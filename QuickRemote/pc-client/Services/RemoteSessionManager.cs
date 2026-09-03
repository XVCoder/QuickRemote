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
    private readonly int _fps;
    private readonly int _bitrateKbps;
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
    private volatile bool _running;
    private bool _disposed;

    /** 当前会话信息（启动成功后创建，UI 会话列表展示用）。 */
    private SessionInfo? _sessionInfo;

    /// <summary>编码器重建与编码的互斥锁（压缩率调整时避免竞态）。</summary>
    private readonly object _encoderLock = new();

    /// <summary>初始码率（kbps），压缩率调整时按比例缩放。</summary>
    private readonly int _baseBitrateKbps;

    /// <summary>当前压缩率百分比（Android 端调整后更新，锁屏恢复重建编码器时保持）。</summary>
    private int _qualityPercent = 100;

    // ============ 锁屏输入代理（向日葵式：锁屏时把输入注入 Winlogon 安全桌面） ============

    /// <summary>PC 当前是否处于锁屏/UAC 安全桌面（Winlogon 输入桌面激活）。</summary>
    private volatile bool _pcLocked;

    /// <summary>锁屏输入代理连接（null = 代理未运行）。锁屏期间输入帧转发给它注入。</summary>
    private System.Net.Sockets.TcpClient? _agentClient;
    private System.Net.Sockets.NetworkStream? _agentStream;
    private readonly object _agentWriteLock = new();
    private string _agentTaskName = "";
    private DateTime _lastLockCheck = DateTime.MinValue;

    // 代理专用帧类型（与 QuickRemote.Agent 端约定）
    private const byte TYPE_AGENT_SETUP = 0x10; // [宽 2B][高 2B]
    private const byte TYPE_AGENT_TOKEN = 0x11; // [token 文本] 连接身份校验

    /// <summary>会话 ID。</summary>
    public string SessionId { get; private set; } = "";

    /// <summary>会话建立时触发（UI 会话列表新增）。</summary>
    public event Action<SessionInfo>? SessionStarted;

    /// <summary>会话结束时触发（UI 会话列表移除）。</summary>
    public event Action<string>? SessionEnded;

    public RemoteSessionManager(Logger logger, int fps = 15, int bitrateKbps = 4000)
    {
        _logger = logger;
        _fps = fps;
        _bitrateKbps = bitrateKbps;
        _baseBitrateKbps = bitrateKbps;
    }

    /// <summary>启动远程会话（中继隧道模式）。</summary>
    public async Task<bool> StartAsync(string sessionId, string serverHost, int tunnelPort)
    {
        if (_running) return false;

        SessionId = sessionId;
        _logger.Info($"Remote session starting: session={sessionId}, server={serverHost}:{tunnelPort}");

        try
        {
            // 0. 重建帧队列（上次会话 Cleanup 时已 CompleteAdding，必须新建才能复用）
            try { _frameQueue.Dispose(); } catch { }
            _frameQueue = new BlockingCollection<CapturedFrame>(3);

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
        if (_running) return false;

        SessionId = sessionId;
        _logger.Info($"Remote session starting (LAN direct): session={sessionId}, peer={client.Client.RemoteEndPoint}");

        try
        {
            // 0. 重建帧队列
            try { _frameQueue.Dispose(); } catch { }
            _frameQueue = new BlockingCollection<CapturedFrame>(3);

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
            _transport!.FrameReceived += OnFrameReceived;
            _transport.Disconnected += OnTransportDisconnected;
            _logger.Info("Remote transport connected");

            // 2. 初始化屏幕捕获
            // PC 锁屏时 DXGI DuplicateOutput 被拒绝（E_ACCESSDENIED）：等待解锁期间
            // 保持连接并通知 Android 端展示"PC 已锁屏"提示，解锁后自动恢复。
            bool wasLocked = false;
            while (true)
            {
                try
                {
                    _capture = new ScreenCaptureService();
                    _capture.Start();
                    break;
                }
                catch (Exception ex)
                {
                    // 等待解锁期间客户端断开：客户端会自行重连，属正常流程，安静退出
                    if (_transport?.IsConnected != true)
                    {
                        _logger.Info("Client disconnected while waiting for unlock, abort session start");
                        Cleanup();
                        return false;
                    }
                    if (!ScreenCaptureService.IsAccessDeniedOrLost(ex))
                        throw;
                    // 连接时已锁屏：此阶段 CaptureLoop 尚未启动，在此驱动输入代理
                    UpdateLockState(true);
                    if (!wasLocked)
                    {
                        wasLocked = true;
                        _logger.Warn("Screen capture unavailable (PC locked?), waiting for unlock...");
                        SendStatusControl("locked");
                    }
                    Thread.Sleep(2000);
                }
            }
            if (wasLocked) SendStatusControl("unlocked");
            _logger.Info($"Screen capture started: {_capture.Width}x{_capture.Height}");

            // 2.1 输入处理器需要画面尺寸做坐标归一化
            _inputHandler.VideoWidth = _capture.Width;
            _inputHandler.VideoHeight = _capture.Height;
            RemoteInputHandler.LogError = msg => _logger.Warn(msg);

            // 3. 初始化编码器：优先 H.264（系统支持时），失败回退 JPEG
            var encoderName = "h264";
            try
            {
                var h264 = new H264Encoder();
                h264.Initialize(_capture.Width, _capture.Height, _fps, _bitrateKbps);
                _encoder = h264;
                _logger.Info($"H.264 encoder initialized: {_fps}fps, {_bitrateKbps}kbps");
            }
            catch (Exception ex)
            {
                _logger.Warn($"H.264 encoder unavailable ({ex.Message}), falling back to JPEG");
                var jpeg = new JpegFrameEncoder();
                jpeg.Initialize(_capture.Width, _capture.Height, _fps, _bitrateKbps);
                _encoder = jpeg;
                encoderName = "jpeg";
                _logger.Info($"JPEG encoder initialized (fallback): {_fps}fps");
            }

            // 4. 发送控制帧（握手：分辨率/帧率/编码器信息给 Android 端）
            SendHandshakeControl(encoderName);

            // 5. 启动捕获线程（抓帧入队）与编码线程（消费发送）
            _running = true;
            _captureThread = new Thread(CaptureLoop) { IsBackground = true };
            _captureThread.Start();
            _encodeThread = new Thread(EncodeLoop) { IsBackground = true };
            _encodeThread.Start();

            // 6. 会话建立：通知 UI（会话列表展示）
            _sessionInfo = new SessionInfo
            {
                SessionId = sessionId,
                DeviceName = "Android 客户端",
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
    /// </summary>
    private void CaptureLoop()
    {
        var interval = TimeSpan.FromMilliseconds(1000.0 / _fps);
        var lastControlSent = DateTime.UtcNow;
        var lastRebuildTry = DateTime.MinValue;
        var lockNotified = false;

        while (_running)
        {
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
                        capture.Start();
                        var oldW = _inputHandler.VideoWidth;
                        var oldH = _inputHandler.VideoHeight;
                        _capture = capture;
                        _inputHandler.VideoWidth = capture.Width;
                        _inputHandler.VideoHeight = capture.Height;
                        _logger.Info($"Screen capture restored: {capture.Width}x{capture.Height}");
                        lockNotified = false;
                        SendStatusControl("unlocked");
                        // 分辨率变化（切换显示器/分辨率）：重建编码器并重新握手
                        if (capture.Width != oldW || capture.Height != oldH)
                        {
                            ReconfigureEncoder(_qualityPercent);
                            SendHandshakeControl(_encoder?.CodecName ?? "jpeg");
                        }
                    }
                }
                else
                {
                    var frame = _capture.CaptureFrame(100);
                    if (frame != null && frame.HasChanges)
                    {
                        // 队列满则丢弃最旧的帧（跳帧），避免捕获线程阻塞
                        if (!_frameQueue.TryAdd(frame))
                        {
                            _frameQueue.TryTake(out _);
                            _frameQueue.TryAdd(frame);
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
                _transport?.Send(RemoteFrameProtocol.TYPE_HEARTBEAT, Array.Empty<byte>());
                UpdateTrafficStats();
                lastControlSent = DateTime.UtcNow;
            }

            sw.Stop();
            var remaining = interval - sw.Elapsed;
            if (remaining > TimeSpan.Zero)
                Thread.Sleep(remaining);
        }

        _logger.Info("Capture loop ended");
        Cleanup();
    }

    /// <summary>发送握手控制帧（分辨率/帧率/编码器信息给 Android 端）。</summary>
    private void SendHandshakeControl(string encoderName)
    {
        var control = Encoding.UTF8.GetBytes(
            $"{{\"width\":{_capture?.Width ?? 0},\"height\":{_capture?.Height ?? 0},\"fps\":{_fps},\"codec\":\"{encoderName}\"}}");
        _transport?.Send(RemoteFrameProtocol.TYPE_CONTROL, control);
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

    /// <summary>
    /// 远程解锁：锁屏输入框位于 Winlogon 安全桌面，需要 SYSTEM 权限注入按键。
    /// 通过计划任务（/RU SYSTEM /IT，交互会话）启动 QuickRemote.Agent.exe unlock 完成：
    /// 密码经临时文件传递（不落命令行/任务历史），由代理读取后立即删除。
    /// 创建 SYSTEM 任务需要当前进程已提升（管理员）。
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

            var taskName = $"QuickRemoteUnlock_{Guid.NewGuid():N}";
            var createExit = RunSchtasks("/Create", "/TN", taskName,
                "/TR", $"\"{agentExe}\" unlock \"{pwFile}\"",
                "/SC", "ONCE", "/ST", "23:59", "/RU", "SYSTEM", "/IT", "/F");
            if (createExit != 0)
            {
                _logger.Warn($"Unlock task create failed (exit={createExit}); PC client must run as Administrator");
                try { File.Delete(pwFile); } catch { }
                SendStatusControl("unlock_failed");
                return;
            }

            RunSchtasks("/Run", "/TN", taskName);
            _logger.Info("Unlock task started");

            // 任务执行完毕后清理（解锁器约 2 秒结束；删除失败重试，避免残留任务）
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < 10; i++)
                {
                    await Task.Delay(1000);
                    if (RunSchtasks("/Delete", "/TN", taskName, "/F") == 0) return;
                }
                _logger.Warn($"Unlock task cleanup failed: {taskName}");
            });
        }
        catch (Exception ex)
        {
            _logger.Warn($"Unlock failed: {ex.Message}");
            SendStatusControl("unlock_failed");
        }
    }

    /// <summary>执行 schtasks 并返回退出码。</summary>
    private static int RunSchtasks(params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return -1;
            p.WaitForExit(15000);
            return p.ExitCode;
        }
        catch
        {
            return -1;
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

        var locked = IsSecureDesktopActive();
        if (locked == _pcLocked) return;
        _pcLocked = locked;
        if (locked)
        {
            _logger.Warn("Secure desktop active (locked/UAC), starting locked-screen input agent");
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
    /// 启动锁屏输入代理：计划任务（SYSTEM/IT）运行 QuickRemote.Agent.exe agent，
    /// 代理监听 127.0.0.1 随机端口并把端口+token 写入临时文件；本方法轮询读取
    /// 后连接，先发 token 校验帧，再发 setup 帧（当前分辨率）。
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
            _agentTaskName = $"QuickRemoteAgent_{Guid.NewGuid():N}";
            var createExit = RunSchtasks("/Create", "/TN", _agentTaskName,
                "/TR", $"\"{agentExe}\" agent \"{portFile}\"",
                "/SC", "ONCE", "/ST", "23:59", "/RU", "SYSTEM", "/IT", "/F");
            if (createExit != 0)
            {
                _logger.Warn($"Agent task create failed (exit={createExit}); PC client must run as Administrator");
                _agentTaskName = "";
                return;
            }
            RunSchtasks("/Run", "/TN", _agentTaskName);

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
            // setup 帧：锁屏画面分辨率（捕获分辨率），代理用于坐标归一化
            var setup = new byte[4];
            setup[0] = (byte)(_inputHandler.VideoWidth & 0xFF);
            setup[1] = (byte)(_inputHandler.VideoWidth >> 8 & 0xFF);
            setup[2] = (byte)(_inputHandler.VideoHeight & 0xFF);
            setup[3] = (byte)(_inputHandler.VideoHeight >> 8 & 0xFF);
            stream.Write(MakeAgentFrame(TYPE_AGENT_SETUP, setup));
            stream.Flush();

            _agentClient = client;
            _agentStream = stream;
            _logger.Info($"Locked-screen input agent connected (port {port})");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Input agent start failed: {ex.Message}");
            StopInputAgent();
        }
    }

    /// <summary>停止输入代理：断开连接（代理进程自行退出），终止并删除计划任务。</summary>
    private void StopInputAgent()
    {
        try { _agentStream?.Close(); } catch { }
        try { _agentClient?.Close(); } catch { }
        _agentStream = null;
        _agentClient = null;
        var task = _agentTaskName;
        _agentTaskName = "";
        if (!string.IsNullOrEmpty(task))
        {
            RunSchtasks("/End", "/TN", task);
            RunSchtasks("/Delete", "/TN", task, "/F");
        }
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
    /// 当前输入桌面是否为安全桌面：锁屏/UAC 激活时输入桌面切换为 Winlogon，
    /// 普通权限进程 OpenInputDesktop 失败或桌面名非 Default。
    /// </summary>
    private static bool IsSecureDesktopActive()
    {
        var hDesktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (hDesktop == IntPtr.Zero) return true;
        try
        {
            var sb = new StringBuilder(256);
            if (GetUserObjectInformationW(hDesktop, UOI_NAME, sb, 256, out _))
                return sb.ToString() != "Default";
            return false;
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

    /// <summary>编码线程：消费帧队列 → 编码 → 发送。</summary>
    private void EncodeLoop()
    {
        try
        {
            while (_running)
            {
                var frame = _frameQueue.Take(); // 阻塞等待
                byte[]? encoded = null;
                lock (_encoderLock)
                {
                    encoded = _encoder?.EncodeFrame(frame.Data);
                }
                if (encoded != null && encoded.Length > 0)
                    _transport?.Send(RemoteFrameProtocol.TYPE_VIDEO_FRAME, encoded);
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
        _logger.Info("Encode loop ended");
    }

    /// <summary>接收帧（输入事件等），转发给输入处理器用 SendInput 模拟。</summary>
    private void OnFrameReceived(byte type, byte[] data)
    {
        switch (type)
        {
            case RemoteFrameProtocol.TYPE_INPUT_MOUSE:
            case RemoteFrameProtocol.TYPE_INPUT_KEY:
            case RemoteFrameProtocol.TYPE_INPUT_WHEEL:
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

    /// <summary>处理 Android 端控制帧（目前支持压缩率调整）。</summary>
    private void HandleControlFrame(byte[] data)
    {
        try
        {
            var json = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(data));
            if (json.RootElement.TryGetProperty("action", out var action) &&
                action.GetString() == "quality" &&
                json.RootElement.TryGetProperty("percent", out var percentProp))
            {
                var percent = percentProp.GetInt32();
                _logger.Info($"Quality adjust request: {percent}%");
                _qualityPercent = percent;
                ReconfigureEncoder(percent);
            }
            else if (action.GetString() == "unlock" &&
                     json.RootElement.TryGetProperty("password", out var pwProp))
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
        var width = _capture?.Width ?? 1920;
        var height = _capture?.Height ?? 1080;
        var newBitrate = Math.Max(200, _baseBitrateKbps * percent / 100);

        lock (_encoderLock)
        {
            try
            {
                var isH264 = _encoder?.CodecName == "h264";
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
    }

    private void Cleanup()
    {
        _running = false;
        StopInputAgent();
        try { _frameQueue.CompleteAdding(); } catch { }
        _captureThread = null;
        _encodeThread = null;
        try { _encoder?.Dispose(); } catch { }
        _encoder = null;
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        if (_transport != null)
        {
            _transport.FrameReceived -= OnFrameReceived;
            _transport.Disconnected -= OnTransportDisconnected;
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
    }
}
