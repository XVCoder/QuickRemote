using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using QuickRemote.PCClient.Models;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// PC 端远程会话管理器（截屏方案）。
/// 流程：连接中继隧道 → DXGI 捕获屏幕 → H.264 编码 → 帧协议发送；
/// 接收 Android 输入事件 → SendInput 模拟。
/// 捕获与编码分线程：捕获线程只抓帧入队，编码线程消费并发送，避免编码阻塞降低帧率。
/// </summary>
public sealed class RemoteSessionManager : IDisposable
{
    private readonly Logger _logger;

    // ============ 会话参数（volatile：主控 configure 帧运行中可覆盖；配置保存即时生效） ============
    // 帧率/码率基准/输出分辨率上限/颜色深度。默认值与旧版行为一致（15fps/4Mbps/不缩放/32 位），
    // 未收到 configure 帧的客户端（Android、旧版 PC）连接行为完全不变。
    // host 前缀 = 被远程配置的默认值；会话值在会话启动时复位到 host 值，
    // 主控端 configure 帧（{"action":"configure",...}）可覆盖会话值，会话结束再次复位。
    private volatile int _fps;
    /// <summary>基准码率（kbps），质量百分比按此缩放（Android quality 帧 / 主控 configure 帧均复用）。</summary>
    private volatile int _baseBitrateKbps;
    /// <summary>配置默认的帧率。</summary>
    private volatile int _hostFps;
    /// <summary>配置默认的基准码率（kbps）。</summary>
    private volatile int _hostBitrateKbps;
    /// <summary>配置默认的输出分辨率高度上限（会话启动时会话值复位到此）。</summary>
    private volatile int _hostMaxHeight;
    /// <summary>配置默认的颜色深度（会话启动时会话值复位到此）。</summary>
    private volatile int _hostColorDepth = 32;
    /// <summary>本会话输出分辨率高度上限（0 = 不缩放；主控 configure 帧可覆盖，会话结束复位）。</summary>
    private volatile int _sessionMaxHeight;
    /// <summary>本会话颜色深度（32 / 16：BGRA 通道量化；主控 configure 帧可覆盖，会话结束复位）。</summary>
    private volatile int _sessionColorDepth = 32;
    /// <summary>局域网直连会话（带宽充裕，码率无省流量必要，见 ComputeBitrateKbps）。</summary>
    private volatile bool _isLanDirect;

    // ============ 分辨率基准（volatile：源=桌面/锁屏代理帧尺寸；输出=编码尺寸） ============
    // 输入归一化（_inputHandler.VideoWidth/Height）与握手帧报的都是输出尺寸；
    // 源尺寸仅供锁屏代理 setup 帧（输入注入按桌面坐标归一化）使用。
    private volatile int _sourceWidth;
    private volatile int _sourceHeight;
    /// <summary>当前编码输出宽度（0 = 待定，首帧驱动重建）。</summary>
    private volatile int _outWidth;
    /// <summary>当前编码输出高度。</summary>
    private volatile int _outHeight;

    // 注意：帧队列不能跨会话复用——Cleanup 会 CompleteAdding 永久关闭队列，
    // 复用会导致下一次会话 TryAdd 立即抛 "marked as complete" 而秒断。
    // 每次会话启动时在 StartAsync 中重建。
    private BlockingCollection<CapturedFrame> _frameQueue = new(3);

    private IRemoteTransport? _transport;
    private ScreenCaptureService? _capture;
    private IFrameEncoder? _encoder;
    private readonly RemoteInputHandler _inputHandler = new();
    private Thread? _captureThread;
    private Thread? _encodeThread;
    private Thread? _sendThread;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>
    /// 会话代数（v1.1.38 会话接管配套）：每次新会话启动时递增。
    /// 旧会话线程若在 Cleanup 的 Join(3000) 超时后仍未退出，仅靠 _running 无法
    /// 自我识别（新会话已把它置回 true）——旧 EncodeLoop 会复用新会话的编码器/
    /// 队列/传输，MFT 非线程安全直接 AccessViolation。工作线程启动时捕获本代
    /// 代数，循环条件比对当前代数，过期即退出且不得触碰共享字段。
    /// </summary>
    private int _sessionGeneration;

    /// <summary>
    /// 发送队列（编码线程 → 网络发送线程，容量 2）。
    /// 公网中继带宽不足时 TCP 写阻塞会传导到编码线程，帧无限积压 → 延迟持续增长
    /// （画面"加载很慢"的根源）。独立发送线程 + 有界队列：拥塞时丢最旧帧，
    /// 延迟封顶在 2 帧 + TCP 缓冲，同时强制 IDR 刷新保证画面可解。
    /// </summary>
    private BlockingCollection<(byte Type, byte[] Data)> _sendQueue = new(2);

    /// <summary>上次因丢帧强制 IDR 的时间（节流，避免反复重建编码器）。</summary>
    private DateTime _lastKeyframeForce = DateTime.MinValue;

    /** 当前会话信息（启动成功后创建，UI 会话列表展示用）。 */
    private SessionInfo? _sessionInfo;

    /// <summary>主控端设备名（configure 帧携带的 deviceName；空 = 未收到，兼容 Android/旧版 PC）。
    /// 会话启动时复位，configure 帧到达后更新并实时反映到 _sessionInfo.DeviceName。</summary>
    private volatile string _peerDeviceName = string.Empty;

    /// <summary>编码器重建与编码的互斥锁（压缩率调整时避免竞态）。</summary>
    private readonly object _encoderLock = new();

    /// <summary>当前压缩率百分比（Android quality 帧 / 主控 configure 帧调整后更新，重建编码器时保持）。</summary>
    private volatile int _qualityPercent = 100;

    // ============ 编码器请求投递（v1.1.36 闪退根因修复） ============
    // WPF UI 线程（STA）创建的 COM RCW 被编码线程（MTA）调用 ProcessInput 会
    // AccessViolation 硬崩（事件日志三连崩，h264test STA 模式稳定复现）。
    // 因此编码器的创建/编码/强制关键帧/重建/销毁全部收敛到 EncodeLoop 线程执行，
    // 控制帧/捕获线程只投递 volatile 标志。
    /// <summary>待执行的编码器重建请求（压缩率百分比；0=无请求）。</summary>
    private volatile int _pendingQualityPercent;
    /// <summary>待执行的强制关键帧请求。</summary>
    private volatile bool _pendingKeyframe;

    // ============ 静止桌面保底（v1.1.40 黑屏修复） ============
    // MS H.264 编码器 lookahead 深度实测 14 帧：输入不足 14 帧时 ProcessOutput
    // 永远 NEED_MORE_INPUT——静止桌面（无像素/光标更新）下 AcquireNextFrame 一直
    // 超时，编码器零输入即零输出，远程端永久黑屏（2026-09-06 公网连接实测）。
    /// <summary>最近投递的真实帧（快投/保底复用；像素数据捕获后不可变）。</summary>
    private CapturedFrame? _lastDeliveredFrame;
    /// <summary>请求编码线程连投缓存帧快速填满 lookahead（首帧/编码器重建/keyframe 请求后）。</summary>
    private volatile bool _pendingFastFill;

    // ============ 锁屏输入代理（向日葵式：锁屏时把输入注入 Winlogon 安全桌面） ============

    /// <summary>PC 当前是否处于锁屏/UAC 安全桌面（Winlogon 输入桌面激活）。</summary>
    private volatile bool _pcLocked;

    /// <summary>锁屏输入代理连接（null = 代理未运行）。锁屏期间输入帧转发给它注入。</summary>
    private System.Net.Sockets.TcpClient? _agentClient;
    private System.Net.Sockets.NetworkStream? _agentStream;
    private readonly object _agentWriteLock = new();
    private System.Diagnostics.Process? _agentProcess;
    private DateTime _lastLockCheck = DateTime.MinValue;

    // 代理专用帧类型（与 QuickRemote.Agent 端约定）
    private const byte TYPE_AGENT_SETUP = 0x10; // [宽 2B][高 2B]
    private const byte TYPE_AGENT_TOKEN = 0x11; // [token 文本] 连接身份校验

    /// <summary>会话 ID。</summary>
    public string SessionId { get; private set; } = "";

    // ============ 被控端访问安全（v1.1.54：连接验证码 + 断开自动锁屏） ============

    /// <summary>访问验证码（被控端）：非空时主控端连接需先通过验证（auth 帧）才建立会话。
    /// 由 UI 线程在设置加载/保存时更新，会话线程 volatile 读取。空 = 不验证。</summary>
    public volatile string AccessCode = string.Empty;

    /// <summary>访问验证码开关：false = 不启用验证保护（即使 AccessCode 非空也不验证）。</summary>
    public volatile bool AccessCodeEnabled;

    /// <summary>连接断开时自动锁屏（被控端）：会话曾建立且非新连接接管时调用 LockWorkStation。</summary>
    public volatile bool LockOnDisconnect = true;

    /// <summary>等待主控端验证码验证中（StartWithTransportAsync 设置，HandleAuthRequest/超时复位）。</summary>
    private volatile bool _awaitingAuth;

    /// <summary>验证失败次数（同一连接内累计，3 次后断开）。</summary>
    private int _authFailCount;

    /// <summary>验证超时定时器（15s 内未收到正确验证码则断开连接）。</summary>
    private System.Threading.Timer? _authTimer;

    /// <summary>验证状态转换锁（ReadLoop 的 auth 帧与超时定时器回调互斥）。</summary>
    private readonly object _authLock = new();

    /// <summary>验证通过后启动会话核心所需的连接模式描述（验证门暂存）。</summary>
    private string _pendingModeText = string.Empty;

    /// <summary>验证通过后启动会话核心所需的客户端 IP（验证门暂存）。</summary>
    private string _pendingClientIp = string.Empty;

    /// <summary>会话建立时触发（UI 会话列表新增）。</summary>
    public event Action<SessionInfo>? SessionStarted;

    /// <summary>会话结束时触发（UI 会话列表移除）。</summary>
    public event Action<string>? SessionEnded;

    public RemoteSessionManager(Logger logger, int fps = 15, int bitrateKbps = 4000)
    {
        _logger = logger;
        _hostFps = Math.Clamp(fps, 5, 60);
        _hostBitrateKbps = Math.Clamp(bitrateKbps, 200, 12000);
        _fps = _hostFps;
        _baseBitrateKbps = _hostBitrateKbps;
    }

    /// <summary>
    /// 应用被远程配置（保存设置时调用，活跃会话即时生效：帧率下个捕获周期、
    /// 码率/分辨率上限/颜色深度在编码器下次重建时生效，分辨率上限立即触发重建）。
    /// </summary>
    public void UpdateHostSettings(Models.HostConfig cfg)
    {
        _hostFps = Math.Clamp(cfg.Fps, 5, 60);
        _hostBitrateKbps = Math.Clamp(cfg.BitrateKbps, 200, 12000);
        _hostMaxHeight = Math.Max(0, cfg.MaxHeight);
        _hostColorDepth = cfg.ColorDepth == 16 ? 16 : 32;
        _fps = _hostFps;
        _baseBitrateKbps = _hostBitrateKbps;
        _sessionMaxHeight = _hostMaxHeight;
        _sessionColorDepth = _hostColorDepth;
        _outWidth = 0; // 尺寸/fps 可能变化：编码线程按下一帧重建编码器（帧驱动）
        _logger.Info($"Host settings updated: {_fps}fps, {_baseBitrateKbps}kbps, maxHeight={_sessionMaxHeight}, colorDepth={_sessionColorDepth}");
    }

    /// <summary>启动远程会话（中继隧道模式）。</summary>
    public async Task<bool> StartAsync(string sessionId, string serverHost, int tunnelPort)
    {
        // 新连接接管（v1.1.38）：旧会话可能因客户端半死连接成为僵尸——手机切后台被系统
        // 杀掉时 TCP 无 FIN，中继链路无 keepalive 感知不到，PC 端 ReadLoop 永远阻塞，
        // _running 永远为 true，此后所有新连接（中继/局域网）都被静默拒绝（Android 表现
        // 为"已连接但永远等不到握手帧"，2026-09-05 21:30-21:40 实测四次局域网连接全挂）。
        // 单查看器设计：新连接直接终止旧会话并接管（向日葵式会话接管）。
        if (_running)
        {
            _logger.Warn($"Session takeover: new session {sessionId} replaces active session {SessionId}");
            Cleanup(isTakeover: true); // 接管不触发断开锁屏
        }

        SessionId = sessionId;
        _logger.Info($"Remote session starting: session={sessionId}, server={serverHost}:{tunnelPort}");

        try
        {
            // 0. 重建帧队列（上次会话 Cleanup 时已 CompleteAdding，必须新建才能复用）
            try { _frameQueue.Dispose(); } catch { }
            _frameQueue = new BlockingCollection<CapturedFrame>(3);
            try { _sendQueue.Dispose(); } catch { }
            _sendQueue = new BlockingCollection<(byte, byte[])>(2);

            // 1. 连接中继隧道（传输层抽象）
            RelayRemoteTransport.LogError = msg => _logger.Warn(msg);
            _transport = await RelayRemoteTransport.ConnectAsync(serverHost, tunnelPort, sessionId);
            return await StartWithTransportAsync(sessionId, "公网中继", "");
        }
        catch (Exception ex)
        {
            _logger.Error($"Remote session start failed: {ex.Message}", ex);
            Cleanup();
            return false;
        }
    }

    /// <summary>启动远程会话（局域网直连模式，连接已由 LanListener 认证建立）。</summary>
    public async Task<bool> StartLocalAsync(string sessionId, System.Net.Sockets.TcpClient client)
    {
        // 新连接接管（同 StartAsync——僵尸会话不论新旧模式都必须能被新连接替换）
        if (_running)
        {
            _logger.Warn($"Session takeover: new LAN session {sessionId} replaces active session {SessionId}");
            Cleanup();
        }

        SessionId = sessionId;
        _logger.Info($"Remote session starting (LAN direct): session={sessionId}, peer={client.Client.RemoteEndPoint}");

        try
        {
            // 0. 重建帧队列
            try { _frameQueue.Dispose(); } catch { }
            _frameQueue = new BlockingCollection<CapturedFrame>(3);
            try { _sendQueue.Dispose(); } catch { }
            _sendQueue = new BlockingCollection<(byte, byte[])>(2);

            // 1. 包装已接受（已认证）的连接
            LocalRemoteTransport.LogError = msg => _logger.Warn(msg);
            _transport = LocalRemoteTransport.FromClient(client);
            var peerIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
            return await StartWithTransportAsync(sessionId, "局域网直连", peerIp);
        }
        catch (Exception ex)
        {
            _logger.Error($"Remote session (LAN) start failed: {ex.Message}", ex);
            Cleanup();
            try { client.Dispose(); } catch { }
            return false;
        }
    }

    /// <summary>共享启动逻辑：初始化捕获/编码器、发控制帧、启动捕获与编码线程。</summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="modeText">连接模式描述（局域网直连 / 公网中继）</param>
    /// <param name="clientIp">客户端 IP（局域网直连时有值）</param>
    private async Task<bool> StartWithTransportAsync(string sessionId, string modeText, string clientIp)
    {
        try
        {
            // 连接模式标记（码率策略用：LAN 直连带宽充裕，见 ComputeBitrateKbps）
            _isLanDirect = modeText == "局域网直连";
            // 断开事件带传输身份校验：会话接管时旧传输被 Cleanup 关闭，其 ReadLoop
            // 仍可能迟到触发 Disconnected（事件在旧线程 finally 里）——若不校验身份，
            // 会把新会话的 _running 误置 false，新会话秒断（2026-09-07 23:52 LAN
            // 连接 130ms EOF 根因，与 transport 侧 _disposed 守卫双保险）
            var transport = _transport!;
            transport.FrameReceived += OnFrameReceived;
            transport.Disconnected += () =>
            {
                if (ReferenceEquals(_transport, transport)) OnTransportDisconnected();
            };
            _logger.Info("Remote transport connected");

            // 1.5 访问验证门（v1.1.54）：启用验证保护且配置了验证码时先不发画面，
            // 等主控端提交验证码。验证通过前不初始化捕获/编码（BeginSessionCore 延迟执行），
            // 60s 超时断开。_running 提前置 true：验证等待期也是会话生命周期一部分，
            // 新连接可据此接管。
            var accessCode = AccessCode;
            if (AccessCodeEnabled && !string.IsNullOrEmpty(accessCode))
            {
                _running = true;
                _pendingModeText = modeText;
                _pendingClientIp = clientIp;
                lock (_authLock)
                {
                    _awaitingAuth = true;
                    _authFailCount = 0;
                    // 60s 超时（人工输入 6 位码需要时间；旧客户端无交互则超时断开）
                    _authTimer = new System.Threading.Timer(AuthTimeoutCallback, null, 60000, Timeout.Infinite);
                }
                _logger.Info("Access code required, waiting for auth");
                SendActionControl("auth_required");
                return true;
            }

            return BeginSessionCore(sessionId, modeText, clientIp);
        }
        catch (Exception ex)
        {
            _logger.Error($"Remote session start failed: {ex.Message}", ex);
            Cleanup();
            return false;
        }
    }

    /// <summary>
    /// 会话核心启动（原 StartWithTransportAsync 主体）：初始化捕获/编码器、启动捕获
    /// /编码/发送线程并发布 SessionInfo。无验证码时由 StartWithTransportAsync 直接
    /// 调用；有验证码时延迟到 HandleAuthRequest 验证通过后在 ReadLoop 线程调用。
    /// </summary>
    private bool BeginSessionCore(string sessionId, string modeText, string clientIp)
    {
        try
        {
            // 2. 初始化屏幕捕获
            // PC 锁屏时 DXGI DuplicateOutput 被拒绝（E_ACCESSDENIED）：不阻塞等待，
            // 直接带着空捕获进入 CaptureLoop（每秒重建检测解锁），并立即启动锁屏
            // 代理（SYSTEM 进程 GDI 捕获 Winlogon 桌面回传画面 + 输入注入）——
            // Android 端可见真实锁屏画面并可点击/输 PIN 解锁（v1.1.42 修复连接时
            // 已锁屏全程黑屏）。旧实现阻塞在等待循环里：无心跳、无代理视频、
            // CaptureLoop 重建逻辑不可达。
            try
            {
                // 本地变量完全初始化后才发布到 _capture 字段：字段若在半初始化状态
                // 被并发 Cleanup 读到，dispose-置null 两步与赋值竞态会 orphan 实例
                // （内部已创建的 DXGI duplication 未释放，之后所有连接
                // DuplicateOutput 永久 E_INVALIDARG，只能重启进程恢复）
                var capture = new ScreenCaptureService();
                try
                {
                    capture.Start();
                    _sourceWidth = capture.Width;
                    _sourceHeight = capture.Height;
                    _capture = capture; // 所有权转移：此后由字段统一管理生命周期
                }
                finally
                {
                    // 半初始化（Start 中途抛出）时本地销毁，不泄漏 D3D 设备/复制对象
                    if (!ReferenceEquals(_capture, capture))
                    {
                        try { capture.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!ScreenCaptureService.IsAccessDeniedOrLost(ex)) throw;
                _capture = null;
                UpdateLockState(true); // 立即上报 locked 状态并启动锁屏代理（视频+输入）
            }
            _logger.Info(_capture != null
                ? $"Screen capture started: {_capture.Width}x{_capture.Height}"
                : "Screen capture unavailable (PC locked?), lock-screen agent takes over");

            // 2.1 输入处理器需要画面尺寸做坐标归一化（锁屏直连时由代理首帧补齐）
            RemoteInputHandler.LogError = msg => _logger.Warn(msg);

            // 3. 编码器创建挪至 EncodeLoop 线程执行（必须与编码同线程：
            //    UI 线程是 STA，STA 创建的 COM RCW 被 MTA 编码线程调用 ProcessInput
            //    会 AccessViolation 硬崩——v1.1.36 三连崩根因，h264test 已复现验证）
            // 4. 启动捕获线程（抓帧入队）、编码线程（消费发送）与发送线程（网络写）
            // 接管旧会话时残留的 pending 标志必须清零，避免旧请求作用于新编码器
            _pendingKeyframe = false;
            _pendingQualityPercent = 0;
            _pendingFastFill = false;
            _lastDeliveredFrame = null;
            _lastKeyframeForce = DateTime.MinValue;
            // 复位会话参数为被控端配置默认（上次会话的 configure 覆盖值不跨会话残留）
            _fps = _hostFps;
            _baseBitrateKbps = _hostBitrateKbps;
            _sessionMaxHeight = _hostMaxHeight;
            _sessionColorDepth = _hostColorDepth;
            _peerDeviceName = string.Empty; // 主控端设备名：configure 帧到达后填充，会话不跨会话残留
            _outWidth = 0; // 强制编码器首帧重建（读取最新尺寸/色深）
            // 会话代数递增：旧会话线程（若 Join 超时未退出）据此识别自己已过期
            var gen = Interlocked.Increment(ref _sessionGeneration);
            _running = true;
            _captureThread = new Thread(() => CaptureLoop(gen)) { IsBackground = true };
            _captureThread.Start();
            _encodeThread = new Thread(() => EncodeLoop(gen)) { IsBackground = true };
            _encodeThread.Start();
            _sendThread = new Thread(() => SendLoop(gen)) { IsBackground = true };
            _sendThread.Start();

            // 6. 会话建立：通知 UI（会话列表展示）。
            // 设备名优先取 configure 帧携带的 deviceName（PC 主控 v1.1.57+ 会下发，
            // 会话列表据此区分 PC/Android 来源）；未收到（Android/旧版 PC）时保持默认。
            _sessionInfo = new SessionInfo
            {
                SessionId = sessionId,
                DeviceName = string.IsNullOrWhiteSpace(_peerDeviceName) ? "Android 客户端" : _peerDeviceName,
                ClientIp = clientIp,
                ModeText = modeText,
                StartTime = DateTime.Now,
                IsActive = true
            };
            SessionStarted?.Invoke(_sessionInfo);

            _logger.Info("Remote session started");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Remote session start failed: {ex.Message}", ex);
            Cleanup();
            return false;
        }
    }

    /// <summary>
    /// 捕获线程：抓帧 → 入队（不编码，保证帧率）。
    /// PC 锁屏/UAC 安全桌面会导致 DXGI 访问丢失（ACCESS_LOST）：此时销毁捕获并
    /// 每秒尝试重建，期间心跳保持连接，解锁后自动恢复推流（不结束会话）。
    /// 静止桌面保底（v1.1.40 黑屏修复）：完全静止的桌面（无像素/光标更新）
    /// AcquireNextFrame 永远超时——①从未有帧时鼠标微移 1 像素强制桌面产生基准帧；
    /// ②拿到基准帧后通知 EncodeLoop 快投（瞬间填满 lookahead）；③之后每秒重投
    /// 缓存帧维持编码器输入（重复帧编码输出仅约 20 字节）。
    /// </summary>
    private void CaptureLoop(int gen)
    {
        var lastControlSent = DateTime.UtcNow;
        var lastRebuildTry = DateTime.MinValue;
        var lockNotified = false;
        var sessionStart = DateTime.UtcNow;
        var lastNudge = DateTime.MinValue;
        var lastDeliverTime = DateTime.UtcNow;

        while (_running && gen == Volatile.Read(ref _sessionGeneration))
        {
            // 帧率 volatile 读取：主控 configure / 配置保存后无需重启会话即时生效
            var interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _fps));
            var sw = Stopwatch.StartNew();

            // 锁屏/UAC 安全桌面检测（内部节流 1 秒）：驱动输入代理启停。
            // 覆盖 DXGI 正常但实际已锁屏的场景（此时本地 SendInput 被 lastError=5 拒绝）。
            UpdateLockState();

            try
            {
                if (_capture == null)
                {
                    // 捕获失效（锁屏中）：每秒尝试重建一次，等待解锁
                    if ((DateTime.UtcNow - lastRebuildTry).TotalMilliseconds >= 1000)
                    {
                        lastRebuildTry = DateTime.UtcNow;
                        var capture = new ScreenCaptureService();
                        try
                        {
                            capture.Start();
                        }
                        catch
                        {
                            // 锁屏中 DuplicateOutput 仍被拒：销毁半初始化实例再抛出
                            // （内部已创建 D3D11 设备，不销毁则每秒泄漏一组 COM/驱动句柄）
                            capture.Dispose();
                            throw;
                        }
                        var oldW = _sourceWidth;
                        var oldH = _sourceHeight;
                        _capture = capture;
                        _sourceWidth = capture.Width;
                        _sourceHeight = capture.Height;
                        _logger.Info($"Screen capture restored: {capture.Width}x{capture.Height}");
                        lockNotified = false;
                        SendStatusControl("unlocked");
                        // 分辨率变化（切换显示器/分辨率）：编码线程按帧尺寸驱动重建编码器并重新握手
                        // （编码器操作必须收敛到 EncodeLoop 线程，见字段区注释）
                        if (capture.Width != oldW || capture.Height != oldH)
                            _outWidth = 0; // 下一帧触发重建（帧驱动）
                    }
                }
                else
                {
                    var frame = _capture.CaptureFrame(100);
                    if (frame != null)
                    {
                        // Acquire 成功即投递（AccumulatedFrames==0 的鼠标帧也含完整桌面像素）。
                        // 首帧（基准帧，连接后立即返回 / 锁屏恢复后）：先写缓存再置标志，
                        // 避免 EncodeLoop 在两者之间读到 null 白白消费快投请求。
                        var wasFirst = _lastDeliveredFrame == null;
                        _lastDeliveredFrame = frame;
                        if (wasFirst)
                            _pendingFastFill = true;
                        lastDeliverTime = DateTime.UtcNow;
                        EnqueueFrame(frame);
                    }
                    else if (_lastDeliveredFrame != null)
                    {
                        // 超时（桌面完全静止）：每秒重投上一帧维持编码器输入，
                        // 重复帧编码输出极小（skip 宏块，约 20 字节），开销可忽略
                        if ((DateTime.UtcNow - lastDeliverTime).TotalMilliseconds >= 1000)
                        {
                            lastDeliverTime = DateTime.UtcNow;
                            EnqueueFrame(_lastDeliveredFrame);
                        }
                    }
                    else if ((DateTime.UtcNow - sessionStart).TotalMilliseconds >= 250)
                    {
                        // 从未取到帧（连接后桌面零更新，duplication 无基准帧可 Acquire）：
                        // 鼠标微移 1 像素强制桌面产生更新事件，下轮 Acquire 即可成功。
                        // 节流 800ms；双方向各移一次保证光标贴边时至少一个方向有效。
                        //（v1.1.56：250ms/800ms——原 800ms/2s 首画面最坏要等 ~2.8s）
                        if ((DateTime.UtcNow - lastNudge).TotalMilliseconds >= 800)
                        {
                            lastNudge = DateTime.UtcNow;
                            NudgeMouse();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (ScreenCaptureService.IsAccessDeniedOrLost(ex))
                {
                    // 锁屏/安全桌面：销毁捕获等待解锁，会话保持
                    try { _capture?.Dispose(); } catch { }
                    _capture = null;
                    _lastDeliveredFrame = null;
                    if (!lockNotified)
                    {
                        lockNotified = true;
                        _logger.Warn("Screen capture lost (PC locked?), waiting for unlock...");
                        SendStatusControl("locked");
                    }
                }
                else
                {
                    _logger.Warn($"Capture loop error: {ex.Message}");
                    break;
                }
            }

            // 周期性心跳（5 秒），保持连接活性；同时刷新流量统计（锁屏等待期间也保持）
            if ((DateTime.UtcNow - lastControlSent).TotalSeconds >= 5)
            {
                // Send 在连接断开瞬间会抛 IOException（EncodeLoop 已实证），
                // 本线程无 catch 会变成未处理线程异常直接终止进程（闪退无日志），必须保护
                try { _transport?.Send(RemoteFrameProtocol.TYPE_HEARTBEAT, Array.Empty<byte>()); }
                catch (Exception ex) { _logger.Warn($"Heartbeat send failed: {ex.Message}"); }
                UpdateTrafficStats();
                lastControlSent = DateTime.UtcNow;
            }

            sw.Stop();
            var remaining = interval - sw.Elapsed;
            if (remaining > TimeSpan.Zero)
                Thread.Sleep(remaining);
        }

        _logger.Info("Capture loop ended");
        // 代数已过期（会话被新连接接管）：新会话仍在运行，本线程不得触发清理
        if (gen == Volatile.Read(ref _sessionGeneration)) Cleanup();
    }

    /// <summary>帧入队：满则丢最旧（跳帧），避免捕获线程阻塞。</summary>
    private void EnqueueFrame(CapturedFrame frame)
    {
        if (!_frameQueue.TryAdd(frame))
        {
            _frameQueue.TryTake(out _);
            _frameQueue.TryAdd(frame);
        }
    }

    /// <summary>
    /// 鼠标微移 1 像素往返：强制桌面产生更新事件。完全静止的桌面
    /// AcquireNextFrame 永远超时（duplication 依赖桌面更新），编码器
    /// 取不到基准帧 → 远程端永久黑屏。仅在从未捕获到帧时调用（节流 2 秒）。
    /// </summary>
    private static void NudgeMouse()
    {
        try
        {
            mouse_event(MOUSEEVENTF_MOVE, 1, 0, 0, IntPtr.Zero);
            mouse_event(MOUSEEVENTF_MOVE, -1, 0, 0, IntPtr.Zero);
        }
        catch { }
    }

    private const uint MOUSEEVENTF_MOVE = 0x0001;

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy,
        uint dwData, IntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    /// <summary>发送握手控制帧（分辨率/帧率/编码器信息给 Android 端）。</summary>
    private void SendHandshakeControl(string encoderName)
    {
        try
        {
            var control = Encoding.UTF8.GetBytes(
                $"{{\"width\":{_inputHandler.VideoWidth},\"height\":{_inputHandler.VideoHeight},\"fps\":{_fps},\"codec\":\"{encoderName}\"}}");
            _transport?.Send(RemoteFrameProtocol.TYPE_CONTROL, control);
        }
        catch (Exception ex)
        {
            // 连接断开时 Send 抛异常；此方法在 CaptureLoop 内调用，不能让异常逃逸终止进程
            _logger.Warn($"Handshake send failed: {ex.Message}");
        }
    }

    /// <summary>向客户端发送状态通知（locked/unlocked，锁屏提示）。</summary>
    private void SendStatusControl(string status)
    {
        try
        {
            var json = Encoding.UTF8.GetBytes($"{{\"status\":\"{status}\"}}");
            _transport?.Send(RemoteFrameProtocol.TYPE_CONTROL, json);
        }
        catch { }
    }

    /// <summary>发送 action 控制帧（auth_required / auth_failed）。</summary>
    private void SendActionControl(string action)
    {
        try
        {
            _transport?.Send(RemoteFrameProtocol.TYPE_CONTROL,
                Encoding.UTF8.GetBytes($"{{\"action\":\"{action}\"}}"));
        }
        catch { }
    }

    /// <summary>
    /// 处理主控端验证码提交（ReadLoop 线程）。验证通过则同步启动会话核心
    /// （阻塞 ReadLoop 数百 ms 无碍：验证通过前不会有视频/输入帧）；
    /// 失败累计 3 次断开连接（期间主控端可重试）。
    /// </summary>
    private void HandleAuthRequest(string code)
    {
        lock (_authLock)
        {
            if (!_awaitingAuth) return; // 已超时/已断开/已通过

            if (code == AccessCode)
            {
                _awaitingAuth = false;
                _authTimer?.Dispose();
                _authTimer = null;
                _logger.Info("Access code verified");
                SendActionControl("auth_ok"); // 通知主控端验证通过（v1.1.55+，旧版忽略未知 action）
                // 锁外无法调用（已持锁）：BeginSessionCore 内部无 _authLock 依赖，锁内调用安全
                BeginSessionCore(SessionId, _pendingModeText, _pendingClientIp);
                return;
            }

            _authFailCount++;
            _logger.Warn($"Access code rejected (attempt {_authFailCount}/3)");
            if (_authFailCount >= 3)
            {
                _awaitingAuth = false;
                _authTimer?.Dispose();
                _authTimer = null;
                SendActionControl("auth_failed"); // 尽力通知（随后即断开）
            }
            else
            {
                SendActionControl("auth_failed"); // 通知主控端重试
                // 重试期间重置超时计时（给人工输入留足时间）
                _authTimer?.Change(60000, Timeout.Infinite);
            }
        }
        if (_authFailCount >= 3)
        {
            _logger.Warn("Too many auth failures, closing session");
            Cleanup(); // _sessionInfo 为 null → 不触发断开锁屏
        }
    }

    /// <summary>验证超时（15s 未收到正确验证码）：断开连接。兼容不支持验证码的旧客户端。</summary>
    private void AuthTimeoutCallback(object? state)
    {
        lock (_authLock)
        {
            if (!_awaitingAuth) return;
            _awaitingAuth = false;
            _authTimer?.Dispose();
            _authTimer = null;
        }
        _logger.Warn("Access code timeout, closing session");
        Cleanup();
    }

    /// <summary>
    /// 远程解锁：锁屏输入框位于 Winlogon 安全桌面，需要 SYSTEM 权限注入按键。
    /// 通过 SystemSessionLauncher 复制 winlogon 令牌在用户会话内以 SYSTEM 启动
    /// QuickRemote.Agent.exe unlock：密码经临时文件传递（不落命令行），由代理
    /// 读取后立即删除。注意不能用 schtasks /RU SYSTEM——那会在 session 0 运行，
    /// 注入落在服务隔离会话的影子桌面，锁屏毫无反应。
    /// 需要当前进程已提升（管理员）。
    /// </summary>
    private void RequestUnlockScreen(string password)
    {
        try
        {
            var agentExe = Path.Combine(AppContext.BaseDirectory, "QuickRemote.Agent.exe");
            if (!File.Exists(agentExe))
            {
                _logger.Warn($"Agent not found: {agentExe}");
                SendStatusControl("unlock_failed");
                return;
            }

            var pwFile = Path.Combine(Path.GetTempPath(), $"qr-pw-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(pwFile, password);

            var proc = SystemSessionLauncher.Launch(agentExe, $"unlock \"{pwFile}\"");
            if (proc == null)
            {
                _logger.Warn($"Unlock launch failed: {SystemSessionLauncher.LastError}");
                try { File.Delete(pwFile); } catch { }
                SendStatusControl("unlock_failed");
                return;
            }
            _logger.Info($"Unlock process started (pid={proc.Id}, session={proc.SessionId})");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Unlock failed: {ex.Message}");
            SendStatusControl("unlock_failed");
        }
    }

    // ============ 锁屏输入代理 ============

    /// <summary>
    /// 检测当前输入桌面是否为安全桌面（锁屏/UAC 激活时为 Winlogon），
    /// 状态变化时同步启动/停止 SYSTEM 输入代理并通知 Android 端。
    /// 供 CaptureLoop 周期调用（内部节流 1 秒）。
    /// </summary>
    private void UpdateLockState(bool forceCheck = false)
    {
        if (!forceCheck && (DateTime.UtcNow - _lastLockCheck).TotalMilliseconds < 1000) return;
        _lastLockCheck = DateTime.UtcNow;

        var (locked, desktopName) = GetInputDesktopState();
        if (locked == _pcLocked) return;
        _pcLocked = locked;
        if (locked)
        {
            // 记录实际桌面名（Winlogon=锁屏/UAC，Screen-saver=屏保，其他=异常切换），
            // 用于诊断"未锁屏却报 locked"的误判来源
            _logger.Warn($"Secure desktop active (desktop='{desktopName}'), starting locked-screen input agent");
            SendStatusControl("locked");
            // 启动要等端口文件（秒级），放后台避免阻塞捕获循环
            Task.Run(StartInputAgent);
        }
        else
        {
            _logger.Info("Secure desktop inactive, stopping input agent");
            SendStatusControl("unlocked");
            StopInputAgent();
        }
    }

    /// <summary>锁屏期间把输入帧转发给 SYSTEM 代理（注入 Winlogon 桌面）。</summary>
    private bool TryForwardToAgent(byte type, byte[] data)
    {
        var stream = _agentStream;
        if (stream == null) return false;
        try
        {
            var frame = new byte[4 + data.Length];
            frame[0] = type;
            frame[1] = (byte)(data.Length & 0xFF);
            frame[2] = (byte)(data.Length >> 8 & 0xFF);
            frame[3] = (byte)(data.Length >> 16 & 0xFF);
            data.CopyTo(frame, 4);
            lock (_agentWriteLock)
            {
                stream.Write(frame, 0, frame.Length);
                stream.Flush();
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Agent forward failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 启动锁屏输入代理：SystemSessionLauncher 以 SYSTEM 在用户会话内运行
    /// QuickRemote.Agent.exe agent，代理监听 127.0.0.1 随机端口并把端口+token
    /// 写入临时文件；本方法轮询读取后连接，先发 token 校验帧，再发 setup 帧
    /// （当前分辨率）。
    /// </summary>
    private void StartInputAgent()
    {
        if (!_pcLocked) return; // 调用前已解锁（竞态），不启动
        try
        {
            var agentExe = Path.Combine(AppContext.BaseDirectory, "QuickRemote.Agent.exe");
            if (!File.Exists(agentExe))
            {
                _logger.Warn($"Agent not found: {agentExe}");
                return;
            }

            var portFile = Path.Combine(Path.GetTempPath(), $"qr-agent-{Guid.NewGuid():N}.tmp");
            var proc = SystemSessionLauncher.Launch(agentExe, $"agent \"{portFile}\"");
            if (proc == null)
            {
                _logger.Warn($"Input agent launch failed: {SystemSessionLauncher.LastError}");
                return;
            }
            _agentProcess = proc;
            _logger.Info($"Input agent process started (pid={proc.Id}, session={proc.SessionId})");

            // 等代理写端口文件（最多 6 秒）
            string? endpoint = null;
            for (var i = 0; i < 120; i++)
            {
                if (File.Exists(portFile))
                {
                    endpoint = File.ReadAllText(portFile).Trim();
                    try { File.Delete(portFile); } catch { }
                    break;
                }
                Thread.Sleep(50);
            }
            if (!_pcLocked) { StopInputAgent(); return; } // 等待期间已解锁，立即回收
            if (endpoint == null)
            {
                _logger.Warn("Agent port file timeout (6s)");
                StopInputAgent();
                return;
            }

            var lines = endpoint.Split('\n');
            var port = int.Parse(lines[0].Trim());
            var token = lines.Length > 1 ? lines[1].Trim() : "";

            var client = new System.Net.Sockets.TcpClient();
            client.Connect(IPAddress.Loopback, port);
            var stream = client.GetStream();
            // token 校验帧
            stream.Write(MakeAgentFrame(TYPE_AGENT_TOKEN, Encoding.UTF8.GetBytes(token)));
            // setup 帧：锁屏画面分辨率，代理用于坐标归一化。
            // 连接时已锁屏（尚无分辨率信息，值为 0）不发：代理以自身 GDI 捕获尺寸为准
            // （Android 所见视频即代理捕获画面，尺寸天然自洽）
            if (_inputHandler.VideoWidth > 0 && _inputHandler.VideoHeight > 0)
            {
                var setup = new byte[4];
                setup[0] = (byte)(_inputHandler.VideoWidth & 0xFF);
                setup[1] = (byte)(_inputHandler.VideoWidth >> 8 & 0xFF);
                setup[2] = (byte)(_inputHandler.VideoHeight & 0xFF);
                setup[3] = (byte)(_inputHandler.VideoHeight >> 8 & 0xFF);
                stream.Write(MakeAgentFrame(TYPE_AGENT_SETUP, setup));
            }
            stream.Flush();

            _agentClient = client;
            _agentStream = stream;
            _logger.Info($"Locked-screen input agent connected (port {port})");
            // 锁屏画面读取线程：代理 GDI 捕获回传 → 编码管线（DXGI 在锁屏下不可用）
            new Thread(() => AgentVideoReadLoop(stream)) { IsBackground = true }.Start();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Input agent start failed: {ex.Message}");
            StopInputAgent();
        }
    }

    /// <summary>锁屏代理视频帧类型（payload：[宽 2B][高 2B][BGRA top-down]，与 Agent 端一致）。</summary>
    private const byte TYPE_AGENT_VIDEO = 0x12;

    /// <summary>
    /// 锁屏画面读取线程：消费代理回传的 GDI 帧（Winlogon 安全桌面画面），
    /// 直接喂编码管线——DXGI 捕获在锁屏下不可用，Android 端由此看到真实
    /// 锁屏画面并可点击/输 PIN（向日葵式体验）。
    /// </summary>
    private void AgentVideoReadLoop(System.Net.Sockets.NetworkStream stream)
    {
        try
        {
            var header = new byte[4];
            while (true)
            {
                ReadAgentExactly(stream, header, 0, 4);
                var type = header[0];
                var len = header[1] | header[2] << 8 | header[3] << 16;
                if (type != TYPE_AGENT_VIDEO || len < 4 || len > 64 * 1024 * 1024)
                {
                    // 非视频帧（不应出现）：跳过 payload 继续
                    SkipAgentBytes(stream, len);
                    continue;
                }

                var dims = new byte[4];
                ReadAgentExactly(stream, dims, 0, 4);
                var w = dims[0] | dims[1] << 8;
                var h = dims[2] | dims[3] << 8;
                var pixels = new byte[len - 4];
                ReadAgentExactly(stream, pixels, 0, pixels.Length);

                if (!_pcLocked) continue; // 已解锁：DXGI 已接管推流，丢弃残留帧
                if (w <= 0 || h <= 0 || pixels.Length != w * h * 4) continue;

                // 尺寸变化由 EncodeLoop 帧驱动统一处理（重建编码器 + 重发握手 +
                // 同步输入归一化尺寸），此处仅入队
                var frame = new CapturedFrame { Data = pixels, Width = w, Height = h, HasChanges = true };
                var wasFirst = _lastDeliveredFrame == null;
                _lastDeliveredFrame = frame;
                if (wasFirst)
                    _pendingFastFill = true; // 首帧（锁屏恢复/直连锁屏）：立即填满编码器 lookahead
                EnqueueFrame(frame);
            }
        }
        catch (Exception)
        {
            // 代理停止/断开（解锁或会话结束）时读取线程自然退出
        }
    }

    private static void ReadAgentExactly(System.Net.Sockets.NetworkStream stream, byte[] buf, int off, int count)
    {
        while (off < count)
        {
            var read = stream.Read(buf, off, count - off);
            if (read <= 0) throw new EndOfStreamException();
            off += read;
        }
    }

    private static void SkipAgentBytes(System.Net.Sockets.NetworkStream stream, int count)
    {
        var tmp = new byte[4096];
        while (count > 0)
        {
            var read = stream.Read(tmp, 0, Math.Min(tmp.Length, count));
            if (read <= 0) throw new EndOfStreamException();
            count -= read;
        }
    }

    /// <summary>停止输入代理：断开连接（代理进程自行退出兜底 Kill）。</summary>
    private void StopInputAgent()
    {
        try { _agentStream?.Close(); } catch { }
        try { _agentClient?.Close(); } catch { }
        _agentStream = null;
        _agentClient = null;
        try
        {
            if (_agentProcess is { HasExited: false }) _agentProcess.Kill();
        }
        catch { }
        _agentProcess = null;
    }

    private static byte[] MakeAgentFrame(byte type, byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        frame[0] = type;
        frame[1] = (byte)(payload.Length & 0xFF);
        frame[2] = (byte)(payload.Length >> 8 & 0xFF);
        frame[3] = (byte)(payload.Length >> 16 & 0xFF);
        payload.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>
    /// 当前输入桌面状态：锁屏/UAC 激活时输入桌面切换为 Winlogon（桌面名非 Default），
    /// 普通权限进程 OpenInputDesktop 失败也视为锁屏。返回 (是否安全桌面, 桌面名)。
    /// </summary>
    private static (bool Locked, string DesktopName) GetInputDesktopState()
    {
        var hDesktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (hDesktop == IntPtr.Zero) return (true, "(open failed)");
        try
        {
            var sb = new StringBuilder(256);
            if (GetUserObjectInformationW(hDesktop, UOI_NAME, sb, 256, out _))
            {
                var name = sb.ToString();
                return (name != "Default", name);
            }
            return (false, "(query failed)");
        }
        finally { CloseDesktop(hDesktop); }
    }

    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int UOI_NAME = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformationW(IntPtr hObj, int nIndex,
        [Out] System.Text.StringBuilder pvInfo, uint cchInfo, out uint pcchInfo);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    /// <summary>把传输层字节计数同步到会话信息（UI 流量展示）。</summary>
    private void UpdateTrafficStats()
    {
        var info = _sessionInfo;
        var transport = _transport;
        if (info == null || transport == null) return;
        info.BytesSent = transport.BytesSent;
        info.BytesReceived = transport.BytesReceived;
    }

    /// <summary>
    /// 在编码线程初始化编码器（H.264 优先，失败回退 JPEG）并立即发送握手控制帧。
    /// 必须与 EncodeFrame 同线程：UI 线程（STA）创建的 COM RCW 被本线程调用
    /// ProcessInput 会 AccessViolation（v1.1.36 三连崩根因，h264test STA 模式已复现）。
    /// 输出尺寸 = ComputeOutputSize(源尺寸)（会话分辨率上限约束），并同步到
    /// _outWidth/_outHeight 与 _inputHandler（客户端输入坐标按输出尺寸归一化）。
    /// </summary>
    private void InitializeEncoderOnEncodeThread(int srcW, int srcH)
    {
        var (width, height) = ComputeOutputSize(srcW, srcH);
        _outWidth = width;
        _outHeight = height;
        _inputHandler.VideoWidth = width;
        _inputHandler.VideoHeight = height;
        var bitrate = ComputeBitrateKbps(_qualityPercent);
        try
        {
            var h264 = new H264Encoder();
            h264.Initialize(width, height, _fps, bitrate);
            _encoder = h264;
            _logger.Info($"H.264 encoder initialized: {_fps}fps, {bitrate}kbps, {width}x{height}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"H.264 encoder unavailable ({ex.Message}), falling back to JPEG");
            var jpeg = new JpegFrameEncoder();
            jpeg.Initialize(width, height, _fps, bitrate);
            _encoder = jpeg;
            _logger.Info($"JPEG encoder initialized (fallback): {_fps}fps");
        }
        // 握手（分辨率/帧率/编码器信息）在编码器就绪后立即发出
        SendHandshakeControl(_encoder?.CodecName ?? "jpeg");
    }

    // ============ 帧预处理：缩放 + 色深量化（v1.1.48 参数化配置） ============
    // 全部仅在 EncodeLoop 线程调用（_scaleBuffer/权重表为编码线程独占，无需加锁）

    /// <summary>
    /// 会话码率（kbps）= 基准码率 × 质量百分比。
    /// 局域网直连 ×4（上限 50Mbps）：基准 4Mbps 即便 100% 质量对 2560x1440 也明显不足
    /// （v1.1.50 前 LAN 高配置仍模糊的根因）；LAN 带宽充裕无省流量必要。
    /// 公网中继保持原值（VPS 带宽有限，高码率会拥塞丢帧）。
    /// </summary>
    private int ComputeBitrateKbps(int percent)
    {
        var bitrate = Math.Max(200, _baseBitrateKbps * percent / 100);
        if (_isLanDirect)
            bitrate = Math.Min(50000, bitrate * 4);
        return bitrate;
    }

    /// <summary>
    /// 计算输出尺寸：会话分辨率上限约束源帧（等比缩放），宽高偶数化
    /// （H.264 420 chroma 要求）。锁屏代理路径不缩放——代理按 setup 帧的视频
    /// 尺寸归一化输入坐标，缩放会引入首帧早于 setup 的时序错位；锁屏画面
    /// 静态简单，码率压力小，解锁后 DXGI 路径自动恢复缩放。
    /// </summary>
    private (int W, int H) ComputeOutputSize(int srcW, int srcH)
    {
        var maxH = _sessionMaxHeight;
        if (srcW <= 0 || srcH <= 0) return (0, 0);
        if (_pcLocked || maxH <= 0 || srcH <= maxH)
            return (srcW & ~1, srcH & ~1);
        var scale = (double)maxH / srcH;
        var w = Math.Max(2, (int)Math.Round(srcW * scale)) & ~1;
        return (w, maxH & ~1);
    }

    /// <summary>
    /// 帧预处理：缩放到输出尺寸 + 16 位色深量化，返回可直接编码的 BGRA 数据。
    /// 不缩放时返回原帧数据（量化原地应用，mask 幂等，与 FastFill 复用兼容）；
    /// 缩放时返回 _scaleBuffer（编码器同步消费，无持有）。
    /// </summary>
    private byte[] PrepareFrame(CapturedFrame frame, int outW, int outH)
    {
        var data = frame.Data;
        if (outW != frame.Width || outH != frame.Height)
            data = ScaleBgra(data, frame.Width, frame.Height, outW, outH);
        if (_sessionColorDepth == 16)
            QuantizeBgraTo16Bit(data);
        return data;
    }

    /// <summary>缩放中转缓冲（编码线程独占）。</summary>
    private byte[] _scaleBuffer = Array.Empty<byte>();
    /// <summary>水平采样索引表（缓存，分辨率组合变化时重建；编码线程独占）。</summary>
    private int[] _scaleX0 = Array.Empty<int>();
    /// <summary>水平采样权重表（256 定点；编码线程独占）。</summary>
    private int[] _scaleXT = Array.Empty<int>();
    /// <summary>采样表缓存键（srcW 变化但 dstW 相同——锁屏代理→DXGI 源切换——时也必须重建）。</summary>
    private (int SrcW, int DstW) _scaleKey;

    /// <summary>
    /// BGRA 双线性等比缩放（256 定点权重，x 方向采样表预计算）。
    /// 仅在 EncodeLoop 线程调用。
    /// </summary>
    private byte[] ScaleBgra(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        if (_scaleBuffer.Length != dstW * dstH * 4)
            _scaleBuffer = new byte[dstW * dstH * 4];
        if (_scaleX0.Length != dstW || _scaleKey != (srcW, dstW))
        {
            // 源 x 坐标 + 权重表：目标像素中心 (x+0.5)*srcW/dstW-0.5 映射回源空间
            _scaleX0 = new int[dstW];
            _scaleXT = new int[dstW];
            for (int x = 0; x < dstW; x++)
            {
                var sx = (x + 0.5) * srcW / dstW - 0.5;
                var x0 = (int)Math.Floor(sx);
                var tx = Math.Clamp((int)((sx - x0) * 256), 0, 255);
                if (x0 < 0) { x0 = 0; tx = 0; }
                if (x0 >= srcW - 1) { x0 = srcW - 1; tx = 0; }
                _scaleX0[x] = x0;
                _scaleXT[x] = tx;
            }
            _scaleKey = (srcW, dstW);
        }
        var dst = _scaleBuffer;
        for (int y = 0; y < dstH; y++)
        {
            var sy = (y + 0.5) * srcH / dstH - 0.5;
            var y0 = (int)Math.Floor(sy);
            var ty = Math.Clamp((int)((sy - y0) * 256), 0, 255);
            if (y0 < 0) { y0 = 0; ty = 0; }
            if (y0 >= srcH - 1) { y0 = srcH - 1; ty = 0; }
            var y1 = Math.Min(y0 + 1, srcH - 1);
            var row0 = y0 * srcW * 4;
            var row1 = y1 * srcW * 4;
            var dRow = y * dstW * 4;
            var w00y = (256 - ty);
            var w1y = ty;
            for (int x = 0; x < dstW; x++)
            {
                var x0 = _scaleX0[x];
                var x1 = Math.Min(x0 + 1, srcW - 1);
                var tx = _scaleXT[x];
                var w00x = (256 - tx);
                var w1x = tx;
                var w00 = w00x * w00y;  // 16.16 权重（和为 256*256）
                var w01 = w1x * w00y;
                var w10 = w00x * w1y;
                var w11 = w1x * w1y;
                var s00 = row0 + x0 * 4;
                var s01 = row0 + x1 * 4;
                var s10 = row1 + x0 * 4;
                var s11 = row1 + x1 * 4;
                var d = dRow + x * 4;
                for (int c = 0; c < 3; c++)
                {
                    // 双线性插值 BGR；A 直通首采样点（转 NV12 时被忽略）
                    dst[d + c] = (byte)((src[s00 + c] * w00 + src[s01 + c] * w01
                                       + src[s10 + c] * w10 + src[s11 + c] * w11) >> 16);
                }
                dst[d + 3] = src[s00 + 3];
            }
        }
        return dst;
    }

    /// <summary>16 位色深量化（RGB565 风格通道 mask，原地，幂等）：降低 H.264 输入熵提升压缩率。</summary>
    private static void QuantizeBgraTo16Bit(byte[] data)
    {
        for (int i = 0; i < data.Length; i += 4)
        {
            data[i] &= 0xF8;      // B → 5 bit
            data[i + 1] &= 0xFC;  // G → 6 bit
            data[i + 2] &= 0xF8;  // R → 5 bit
        }
    }

    /// <summary>
    /// 应用新的输出尺寸（帧驱动）：同步输入归一化尺寸，重建编码器并重发握手。
    /// 仅在 EncodeLoop 线程调用（编码器 COM 操作收敛于此线程）。
    /// </summary>
    private void ApplyOutputSize(int outW, int outH)
    {
        _outWidth = outW;
        _outHeight = outH;
        _inputHandler.VideoWidth = outW;
        _inputHandler.VideoHeight = outH;
        ReconfigureEncoder(_qualityPercent);
        SendHandshakeControl(_encoder?.CodecName ?? "jpeg");
        // 锁屏代理活跃时同步 setup 帧：代理按视频尺寸归一化输入坐标，
        // 输出尺寸变化（锁屏代理帧→DXGI 源切换/中途 configure）后旧 setup 会错位
        if (_agentStream != null) SendAgentSetup();
        // 新编码器 lookahead 为空且静止桌面无新帧：请求快投缓存帧填充
        _pendingFastFill = true;
    }

    /// <summary>向输入代理发送 setup 帧（当前视频输出尺寸，代理用于输入坐标归一化）。</summary>
    private void SendAgentSetup()
    {
        var stream = _agentStream;
        if (stream == null || _inputHandler.VideoWidth <= 0 || _inputHandler.VideoHeight <= 0) return;
        try
        {
            var setup = new byte[4];
            setup[0] = (byte)(_inputHandler.VideoWidth & 0xFF);
            setup[1] = (byte)(_inputHandler.VideoWidth >> 8 & 0xFF);
            setup[2] = (byte)(_inputHandler.VideoHeight & 0xFF);
            setup[3] = (byte)(_inputHandler.VideoHeight >> 8 & 0xFF);
            lock (_agentWriteLock)
            {
                stream.Write(MakeAgentFrame(TYPE_AGENT_SETUP, setup));
                stream.Flush();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Agent setup send failed: {ex.Message}");
        }
    }

    /// <summary>编码线程：消费帧队列 → 编码 → 入发送队列（网络阻塞不再拖累编码节奏）。
    /// 编码器所有 COM 操作（创建/编码/强制关键帧/重建/销毁）全部在本线程执行，
    /// 其他线程通过 volatile 标志投递请求，消除 apartment 不匹配与跨线程并发两类崩溃。</summary>
    private void EncodeLoop(int gen)
    {
        // 本代队列快照：接管后 _frameQueue/_sendQueue 字段已指向新会话队列，
        // 旧线程必须只碰自己那一代，否则会偷走新 EncodeLoop 的帧
        var frameQueue = _frameQueue;
        var sendQueue = _sendQueue;
        try
        {
            // 正常路径（捕获就绪）立即初始化（按捕获源尺寸→输出尺寸）；
            // 连接时已锁屏（_capture==null）推迟到首个代理锁屏帧按帧尺寸懒初始化
            if (_capture != null) InitializeEncoderOnEncodeThread(_capture.Width, _capture.Height);

            while (_running && gen == Volatile.Read(ref _sessionGeneration))
            {
                // 控制线程（ReadLoop/CaptureLoop）投递的编码器请求，统一在本线程执行
                if (_pendingKeyframe)
                {
                    _pendingKeyframe = false;
                    if (_encoder?.ForceKeyFrame() != true)
                        ReconfigureEncoder(_qualityPercent); // 编码器不支持动态强制时重建兜底
                    // ForceKeyFrame 属性需下一帧输入才生效：请求快投（静止桌面无新帧时
                    // 靠缓存帧立即生效，否则 keyframe 请求无响应）
                    _pendingFastFill = true;
                }
                if (_pendingQualityPercent != 0)
                {
                    var q = _pendingQualityPercent;
                    _pendingQualityPercent = 0;
                    ReconfigureEncoder(q);
                }

                // 快投：连投缓存帧瞬间填满编码器 lookahead（实测深度 14 帧）。
                // 静止桌面下没有真实新帧，不快投则首帧要等 14 个保底帧周期才输出。
                // 缓存帧尚未就绪时保留标志（首帧到达后下一个循环处理）
                if (_pendingFastFill)
                {
                    FastFillEncoder(sendQueue);
                }

                // 带超时轮询：静止桌面下帧投递稀疏（保底 1fps），无限 Take 会卡住
                // pending 标志处理（Android 的 keyframe 请求响应不及时）
                if (!frameQueue.TryTake(out var frame, 100))
                    continue;

                // 帧驱动编码尺寸：源分辨率（显示器切换/锁屏代理→DXGI 切换）或会话参数
                // （configure/保存设置置 _outWidth=0）任一变化 → 输出尺寸变化 →
                // 重建编码器 + 同步输入归一化 + 重发握手。锁屏直连首帧（编码器未建）
                // 也由此统一初始化（ComputeOutputSize 按帧源尺寸计算）。
                var (outW, outH) = ComputeOutputSize(frame.Width, frame.Height);
                if (outW != _outWidth || outH != _outHeight || _encoder == null)
                {
                    _logger.Info($"Output size changed: {_outWidth}x{_outHeight} -> {outW}x{outH} (source {frame.Width}x{frame.Height})");
                    ApplyOutputSize(outW, outH);
                }

                byte[]? encoded = null;
                try
                {
                    encoded = _encoder?.EncodeFrame(PrepareFrame(frame, _outWidth, _outHeight));
                }
                catch (ArgumentException)
                {
                    // 分辨率切换瞬间队列残留旧尺寸帧：跳过（编码器已按新尺寸重建）
                    continue;
                }
                if (encoded != null && encoded.Length > 0)
                {
                    // 发送队列满 = 网络拥塞（SendLoop 阻塞在 TCP 写）：丢最旧帧控制延迟，
                    // 并强制下一个输出为 IDR（丢帧破坏 P 帧参考链，不刷新会花屏到下个 GOP 边界）
                    if (!sendQueue.TryAdd((RemoteFrameProtocol.TYPE_VIDEO_FRAME, encoded)))
                    {
                        sendQueue.TryTake(out _);
                        sendQueue.TryAdd((RemoteFrameProtocol.TYPE_VIDEO_FRAME, encoded));
                        OnFrameDropped();
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            // 队列已 CompleteAdding（清理时），正常退出
        }
        catch (Exception ex)
        {
            _logger.Warn($"Encode loop error: {ex.Message}");
        }
        finally
        {
            // 编码器在本（MTA）线程销毁：STA 线程（UI/Dispose）经 Cleanup 调用
            // COM 方法同样会 AccessViolation，故 Cleanup 只等本线程退出，不直接销毁
            if (gen == Volatile.Read(ref _sessionGeneration))
            {
                lock (_encoderLock)
                {
                    try { _encoder?.Dispose(); } catch { }
                    _encoder = null;
                }
            }
            // 代数已过期（会话被新连接接管）：_encoder 字段属于新会话，本线程不得触碰
        }
        _logger.Info("Encode loop ended");
    }

    /// <summary>
    /// 快速填充：连投缓存帧直到编码器产出（lookahead 实测 14 帧，16 次足够）。
    /// 编码时间戳由 _sampleDuration 人工推进，与真实墙钟无关，瞬间连投无副作用；
    /// 若首帧输出是 IDR（ForceKeyFrame 已设置或编码器首个输出），远程端立即恢复画面。
    /// 仅在 EncodeLoop 线程调用。
    /// </summary>
    private void FastFillEncoder(BlockingCollection<(byte Type, byte[] Data)> sendQueue)
    {
        var frame = _lastDeliveredFrame;
        // 缓存帧未就绪（连接刚建立，基准帧未捕获）：保留标志，等首帧到达再处理
        if (frame == null || _encoder == null) return;
        _pendingFastFill = false;
        try
        {
            // 缓存帧为原始数据（未缩放/未量化）：与常规路径一致走 PrepareFrame
            var prepared = PrepareFrame(frame, _outWidth, _outHeight);
            for (int i = 0; i < 16; i++)
            {
                var encoded = _encoder.EncodeFrame(prepared);
                if (encoded.Length > 0)
                {
                    if (!sendQueue.TryAdd((RemoteFrameProtocol.TYPE_VIDEO_FRAME, encoded)))
                    {
                        sendQueue.TryTake(out _);
                        sendQueue.TryAdd((RemoteFrameProtocol.TYPE_VIDEO_FRAME, encoded));
                    }
                    _logger.Info($"Fast fill: encoder output after {i + 1} frames ({encoded.Length} bytes)");
                    return;
                }
            }
        }
        catch (ArgumentException)
        {
            // 缓存帧与当前编码器尺寸不符（分辨率切换瞬间）：等待真实帧
        }
        catch (Exception ex)
        {
            _logger.Warn($"Fast fill error: {ex.Message}");
        }
    }

    /// <summary>网络发送线程：消费发送队列 → 传输层写出（公网带宽不足时阻塞在此，不拖累编码）。</summary>
    private void SendLoop(int gen)
    {
        // 本代队列/传输快照：接管后字段已指向新会话对象，旧线程不得混用
        var sendQueue = _sendQueue;
        var transport = _transport;
        try
        {
            while (_running && gen == Volatile.Read(ref _sessionGeneration))
            {
                var (type, data) = sendQueue.Take();
                transport?.Send(type, data);
            }
        }
        catch (InvalidOperationException)
        {
            // 队列已 CompleteAdding（清理时），正常退出
        }
        catch (Exception ex)
        {
            _logger.Warn($"Send loop error: {ex.Message}");
        }
        _logger.Info("Send loop ended");
    }

    /// <summary>丢帧后强制 IDR 刷新（2 秒节流；编码器不支持动态强制时重建兜底）。
    /// 仅在 EncodeLoop 线程内调用，与编码天然同线程，无并发。</summary>
    private void OnFrameDropped()
    {
        if ((DateTime.UtcNow - _lastKeyframeForce).TotalSeconds < 2) return;
        _lastKeyframeForce = DateTime.UtcNow;
        _logger.Warn("Network congested: frame dropped, forcing IDR refresh");
        if (_encoder?.ForceKeyFrame() != true)
            ReconfigureEncoder(_qualityPercent);
    }

    /// <summary>接收帧（输入事件等），转发给输入处理器用 SendInput 模拟。</summary>
    private void OnFrameReceived(byte type, byte[] data)
    {
        switch (type)
        {
            case RemoteFrameProtocol.TYPE_INPUT_MOUSE:
            case RemoteFrameProtocol.TYPE_INPUT_KEY:
            case RemoteFrameProtocol.TYPE_INPUT_WHEEL:
            case RemoteFrameProtocol.TYPE_INPUT_TEXT:
                // 仅记录键盘帧（低频且有诊断价值）；鼠标移动帧高频（每秒几十次），逐帧写日志会拖慢输入并撑爆日志文件
                if (type == RemoteFrameProtocol.TYPE_INPUT_KEY)
                    _logger.Info($"Input frame received: type=0x{type:X2} len={data.Length} data=[{string.Join(",", data.Take(8))}]");
                // 锁屏时本地 SendInput 被 Winlogon 安全桌面拒绝（lastError=5），
                // 转发给 SYSTEM 输入代理注入锁屏桌面；转发失败/未锁屏走本地注入
                if (_pcLocked && TryForwardToAgent(type, data)) break;
                _inputHandler.HandleFrame(type, data);
                break;
            case RemoteFrameProtocol.TYPE_CONTROL:
                HandleControlFrame(data);
                break;
            case RemoteFrameProtocol.TYPE_HEARTBEAT:
                break; // 心跳，无需处理
            default:
                _logger.Warn($"Unknown frame type: 0x{type:X2}");
                break;
        }
    }

    /// <summary>处理客户端控制帧（压缩率调整 / 关键帧请求 / 远程解锁 / 主控参数下发）。</summary>
    private void HandleControlFrame(byte[] data)
    {
        try
        {
            var json = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(data));
            var root = json.RootElement;
            var action = root.TryGetProperty("action", out var actionProp) ? actionProp.GetString() : null;

            if (action == "quality" &&
                root.TryGetProperty("percent", out var percentProp))
            {
                var percent = percentProp.GetInt32();
                _logger.Info($"Quality adjust request: {percent}%");
                _qualityPercent = percent;
                // 重建由编码线程统一执行（编码器 COM 操作不能跨线程/跨 apartment）
                _pendingQualityPercent = percent;
            }
            else if (action == "configure")
            {
                // 主控端会话参数下发（PC 主控 v1.1.48+）：分辨率上限/质量百分比/帧率/色深，
                // 会话内生效；会话结束（新连接启动）复位回被控端配置默认值。
                // 字段缺省 = 保持当前值（maxHeight=0 表示不缩放）
                var maxHeight = root.TryGetProperty("maxHeight", out var mh) &&
                                mh.ValueKind == System.Text.Json.JsonValueKind.Number ? mh.GetInt32() : _sessionMaxHeight;
                var percent = root.TryGetProperty("percent", out var pc) &&
                              pc.ValueKind == System.Text.Json.JsonValueKind.Number ? pc.GetInt32() : _qualityPercent;
                var fps = root.TryGetProperty("fps", out var fp) &&
                          fp.ValueKind == System.Text.Json.JsonValueKind.Number ? fp.GetInt32() : _fps;
                var colorDepth = root.TryGetProperty("colorDepth", out var cd) &&
                                 cd.ValueKind == System.Text.Json.JsonValueKind.Number ? cd.GetInt32() : _sessionColorDepth;
                // 主控端设备名（PC 主控 v1.1.57+ 随 configure 下发；缺省 = Android/旧版 PC，保持默认）
                if (root.TryGetProperty("deviceName", out var dn) &&
                    dn.ValueKind == System.Text.Json.JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(dn.GetString()))
                {
                    _peerDeviceName = dn.GetString()!;
                    // 会话已建立（configure 帧在握手后到达）：实时更新 UI 会话列表展示
                    if (_sessionInfo != null) _sessionInfo.DeviceName = _peerDeviceName;
                }

                _sessionMaxHeight = Math.Max(0, maxHeight);
                _sessionColorDepth = colorDepth == 16 ? 16 : 32;
                _fps = Math.Clamp(fps, 5, 60);
                _qualityPercent = Math.Clamp(percent, 20, 100);
                // 分辨率上限/帧率变化需重建编码器（新尺寸/时间戳/GOP）；色深为预处理层即时生效。
                // 置 _outWidth=0 下一帧触发帧驱动重建（配置了 pendingQuality 的场景由 defer 兜底）
                _outWidth = 0;
                _pendingQualityPercent = 0; // 帧驱动重建按最新 _qualityPercent，无需重复重建
                _logger.Info($"Configure request: maxHeight={_sessionMaxHeight}, quality={_qualityPercent}%, fps={_fps}, colorDepth={_sessionColorDepth}, deviceName={_peerDeviceName}");
            }
            else if (action == "keyframe")
            {
                // 解码端（重）启动完成，请求立即出 IDR：中继模式下解码器若晚于首帧
                // IDR 就绪会错过关键帧，无周期 GOP 时将永久黑屏，这里秒级补救。
                // ForceKeyFrame 由编码线程统一执行（失败时由其重建编码器兜底）
                _logger.Info("Keyframe request received");
                _pendingKeyframe = true;
            }
            else if (action == "auth" &&
                     root.TryGetProperty("code", out var codeProp))
            {
                // 主控端提交访问验证码（v1.1.54）：验证通过后建立会话
                HandleAuthRequest(codeProp.GetString() ?? "");
            }
            else if (action == "unlock" &&
                     root.TryGetProperty("password", out var pwProp))
            {
                // 远程解锁：Android 端提交 Windows 登录密码，由 SYSTEM 辅助程序在锁屏桌面注入
                var password = pwProp.GetString() ?? "";
                if (password.Length > 0)
                {
                    _logger.Info("Unlock request received");
                    Task.Run(() => RequestUnlockScreen(password));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Control frame parse failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 按压缩百分比重建编码器（20-100%）：
    /// H.264 → 码率按比例缩放；JPEG → 质量按比例映射。保持当前 codec 类型。
    /// </summary>
    private void ReconfigureEncoder(int percent)
    {
        percent = Math.Clamp(percent, 20, 100);
        // 连接时已锁屏且首帧未到（输出尺寸未知，_outWidth==0）：仅记录压缩率，
        // 首帧到达时帧驱动按最新参数初始化，避免以默认值建错
        if (_encoder == null && _outWidth == 0)
        {
            _logger.Info($"Quality request deferred until first frame ({percent}%)");
            return;
        }
        var width = _outWidth > 0 ? _outWidth : (_inputHandler.VideoWidth > 0 ? _inputHandler.VideoWidth : (_capture?.Width ?? 1920));
        var height = _outHeight > 0 ? _outHeight : (_inputHandler.VideoHeight > 0 ? _inputHandler.VideoHeight : (_capture?.Height ?? 1080));
        var newBitrate = ComputeBitrateKbps(percent);

        lock (_encoderLock)
        {
            try
            {
                // _encoder==null（锁屏直连懒建路径）时默认 H.264；已初始化时保持原类型
                var isH264 = _encoder == null || _encoder?.CodecName == "h264";
                try { _encoder?.Dispose(); } catch { }
                _encoder = null;

                if (isH264)
                {
                    var h264 = new H264Encoder();
                    h264.Initialize(width, height, _fps, newBitrate);
                    _encoder = h264;
                }
                else
                {
                    var jpeg = new JpegFrameEncoder();
                    jpeg.Initialize(width, height, _fps, newBitrate);
                    _encoder = jpeg;
                }
                _logger.Info($"Encoder reconfigured: {(_encoder?.CodecName)} at {newBitrate}kbps ({percent}%)");
                // 新编码器 lookahead 为空，静止桌面下无真实新帧：请求快投缓存帧填充，
                // 否则重建后远程端黑屏直到桌面出现变化（quality 调整后黑屏的同源问题）
                _pendingFastFill = true;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Encoder reconfigure failed: {ex.Message}");
            }
        }
    }

    private void OnTransportDisconnected()
    {
        if (_running)
        {
            _logger.Info("Remote transport disconnected");
            _running = false;
        }
        // 验证等待期断开：取消超时定时器（连接已不存在，无需再等）
        lock (_authLock)
        {
            _awaitingAuth = false;
            _authTimer?.Dispose();
            _authTimer = null;
        }
    }

    /// <summary>
    /// 停止并清理会话。isTakeover=true 表示被新连接接管（不触发断开锁屏）。
    /// </summary>
    private void Cleanup(bool isTakeover = false)
    {
        // 断开自动锁屏（v1.1.54）：会话曾真实建立（_sessionInfo 非空 = 验证通过并推流过）
        // 且非新连接接管时，锁定本机桌面保护隐私。接管场景新连接即将继续控制，不能锁。
        if (!isTakeover && LockOnDisconnect && _sessionInfo != null)
        {
            try
            {
                if (LockWorkStation())
                    _logger.Info("Workstation locked on disconnect");
                else
                    _logger.Warn($"LockWorkStation failed: win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            }
            catch (Exception ex)
            {
                _logger.Warn($"LockWorkStation error: {ex.Message}");
            }
        }
        // 验证状态复位（超时/断开路径兜底；Monitor 可重入，HandleAuthRequest 持锁路径安全）
        lock (_authLock)
        {
            _awaitingAuth = false;
            _authTimer?.Dispose();
            _authTimer = null;
        }
        // 会话代数先行递增（2026-09-07 LAN 接管竞态根因）：Cleanup 不等待捕获/
        // 发送线程退出，旧线程稍后在自己的退出路径比对"代数==当前"决定是否补一次
        // Cleanup——而新会话的代数递增发生在捕获初始化之后（StartWithTransportAsync），
        // 存在窗口期：旧 CaptureLoop 此时退出会误判自己仍是当前代，执行"野 Cleanup"，
        // 其 _capture?.Dispose(); _capture = null 两步与 new 会话的 _capture 赋值竞态，
        // orphan 半初始化的捕获实例（泄漏活的 DXGI duplication）→ 下一次连接
        // DuplicateOutput 永久 E_INVALIDARG（实测 23:52:35 NRE + 23:52:41 E_INVALIDARG）。
        // Cleanup 入口先递增代数：此后任何残留旧线程退出时立即失配，不再触发野清理
        Interlocked.Increment(ref _sessionGeneration);
        _running = false;
        StopInputAgent();
        // 锁屏状态复位：若残留 true，下次"连接时已锁屏"的会话里 UpdateLockState
        // 因状态未变化直接 return，输入代理永不启动（2026-09-06 22:38 LAN 会话实测：
        // 点击全部走本地 SendInput 被拒 lastError=5，锁屏画面/输入均无响应）
        _pcLocked = false;
        _lastLockCheck = DateTime.MinValue;
        try { _frameQueue.CompleteAdding(); } catch { }
        try { _sendQueue.CompleteAdding(); } catch { }
        // 等待工作线程退出（队列已 CompleteAdding / _running=false，正常瞬间退出），
        // 防止线程仍在使用即将销毁的捕获/传输（旧实现只等编码线程，捕获线程
        // 残留导致接管后仍向已销毁传输发心跳、读取已销毁捕获）。
        // 自 join 防护：Cleanup 可能由 CaptureLoop 自身退出路径调用
        var self = Thread.CurrentThread;
        var captureThread = _captureThread;
        if (captureThread != null && captureThread.IsAlive && !ReferenceEquals(captureThread, self))
            captureThread.Join(2000);
        var sendThread = _sendThread;
        if (sendThread != null && sendThread.IsAlive && !ReferenceEquals(sendThread, self))
            sendThread.Join(2000);
        // 编码器由 EncodeLoop 线程在 finally 中自行销毁（STA 线程直接调用其 COM 方法
        // 会 AccessViolation——v1.1.36 崩溃根因同源）。等待其退出，超时才兜底强制销毁
        var encodeThread = _encodeThread;
        if (encodeThread != null && encodeThread.IsAlive && !ReferenceEquals(encodeThread, self))
            encodeThread.Join(3000);
        lock (_encoderLock)
        {
            try { _encoder?.Dispose(); } catch { }
            _encoder = null;
        }
        _captureThread = null;
        _encodeThread = null;
        _sendThread = null;
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        if (_transport != null)
        {
            // Disconnected 为带身份校验的 lambda 订阅（见 StartWithTransportAsync），
            // 迟到事件会被 ReferenceEquals 过滤，无需在此退订
            _transport.FrameReceived -= OnFrameReceived;
            try { _transport.Dispose(); } catch { }
            _transport = null;
        }
        // 会话已对外发布（SessionStarted）时，通知 UI 移除（所有结束路径都经过 Cleanup）
        var info = _sessionInfo;
        _sessionInfo = null;
        if (info != null)
        {
            info.IsActive = false;
            SessionEnded?.Invoke(info.SessionId);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
        try { _frameQueue.Dispose(); } catch { }
        try { _sendQueue.Dispose(); } catch { }
    }
}
