using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickRemote.PCClient.Models;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// PC 主控端远程查看客户端（查看另一台 PC 的屏幕并注入鼠标键盘）。
/// 连接策略：局域网直连优先（低延迟，目标 LanListener 8447 + auth_key 认证），
/// 失败回退公网中继隧道（control 连接 tunnel_open → App 角色 0x02 接入）。
/// 帧协议与 Android 端完全一致（RemoteFrameProtocol）：
/// 收 TYPE_VIDEO_FRAME（H.264/JPEG）解码为 BGRA；TYPE_CONTROL 握手/状态；
/// 发 TYPE_INPUT_MOUSE/KEY/WHEEL/TEXT 输入帧。
/// 事件均在接收线程触发，UI 订阅方需自行调度到 UI 线程。
/// </summary>
public sealed class RemoteViewerClient : IDisposable
{
    private readonly Logger _logger;
    private ViewerTransport? _transport;
    // 解码器互斥锁：创建/解码（接收线程）与销毁（任意线程）互斥，
    // 防止 Dispose 与 Decode 并发触碰同一 COM 对象（MFT 非线程安全）
    private readonly object _decoderLock = new();
    private H264Decoder? _h264Decoder;
    private bool _decoderClosed;
    private string _codec = "h264";
    private int _hintWidth;
    private int _hintHeight;
    private volatile bool _running;
    private volatile bool _disposed;
    private Thread? _watchdogThread;
    private DateTime _lastFrameTime = DateTime.UtcNow;
    private DateTime _lastKeyframeRequest = DateTime.MinValue;
    private DateTime _lastHeartbeat = DateTime.UtcNow;
    /// <summary>主控端远程配置（握手后下发给被控端）。</summary>
    private ViewerConfig _viewerConfig = new();
    /// <summary>configure 已下发标志（仅首次握手后发送一次，避免与被控端重建握手的循环）。</summary>
    private bool _configureSent;

    /// <summary>目标设备信息。</summary>
    public RemoteDeviceInfo Device { get; }

    /// <summary>连接模式描述（局域网直连 / 公网中继）。</summary>
    public string ModeText { get; private set; } = string.Empty;

    /// <summary>会话 ID（中继模式有值）。</summary>
    public string SessionId { get; private set; } = string.Empty;

    /// <summary>是否已连接（传输层存活）。</summary>
    public bool IsConnected => _running && _transport is { IsConnected: true };

    // ============ 事件（接收线程触发） ============

    /// <summary>解码出一帧 BGRA（width/height 可能随分辨率切换变化）。</summary>
    public event Action<int, int, byte[]>? FrameDecoded;

    /// <summary>状态消息（连接过程/锁屏/等待画面等，UI 展示）。</summary>
    public event Action<string>? StatusMessage;

    /// <summary>被控端要求输入访问验证码（UI 弹出输入框后经 SendAuthCode 提交）。</summary>
    public event Action? AuthRequired;

    /// <summary>验证码被拒绝（UI 提示后可重新输入；被控端累计 3 次失败将断开连接）。</summary>
    public event Action? AuthFailed;

    /// <summary>连接已断开（参数为原因描述）。</summary>
    public event Action<string>? Disconnected;

    private RemoteViewerClient(RemoteDeviceInfo device, Logger logger)
    {
        Device = device;
        _logger = logger;
    }

    /// <summary>
    /// 连接目标设备：LAN 直连优先（同网段低延迟），失败回退中继隧道。
    /// 返回已连接的客户端（传输层就绪，画面经 FrameDecoded 事件到达）。
    /// </summary>
    /// <param name="device">目标设备（需在线）</param>
    /// <param name="relay">本机控制连接（中继模式申请隧道用）</param>
    /// <param name="serverAddress">服务器地址（中继模式取 host）</param>
    /// <param name="preSharedKey">预共享密钥（LAN 直连认证）</param>
    /// <param name="viewerConfig">远程配置（连接后下发 configure 帧给被控端；PreferLan=false 时强制走中继）</param>
    /// <param name="logger">日志</param>
    public static async Task<RemoteViewerClient> ConnectAsync(
        RemoteDeviceInfo device, RelayConnection relay, string serverAddress,
        string preSharedKey, ViewerConfig viewerConfig, Logger logger)
    {
        // 1. 局域网直连（有 LanIp 且配置允许时尝试，1.5s 超时快速失败）。
        // 失败缓存（v1.1.56）：LanIp 失效（跨网段/设备换了网络）时每次连接都要白等
        // 1.5s 超时才回退中继——表现为"连接很慢"。缓存 5 分钟，键含 LanIp，
        // 设备换了网段上报新 LanIp 后自动重新尝试。
        if (viewerConfig.PreferLan && !string.IsNullOrEmpty(device.LanIp) &&
            !IsLanRecentlyFailed(device.DeviceId, device.LanIp))
        {
            try
            {
                var authKey = ComputeAuthKey(preSharedKey);
                var transport = await ViewerTransport.ConnectLanAsync(
                    device.LanIp, LanListener.DefaultPort, authKey, 1500);
                return Start(device, logger, transport, "局域网直连", string.Empty, viewerConfig);
            }
            catch (Exception ex)
            {
                logger.Info($"LAN direct to {device.LanIp} failed ({ex.Message}), falling back to relay");
                MarkLanFailed(device.DeviceId, device.LanIp);
            }
        }

        // 2. 中继隧道
        var (sessionId, tunnelPort) = await relay.RequestTunnelAsync(device.DeviceId);
        var (host, _, _) = RelayConnection.ParseAddress(serverAddress);
        var relayTransport = await ViewerTransport.ConnectRelayAsync(host, tunnelPort,
            sessionId);
        return Start(device, logger, relayTransport, "公网中继", sessionId, viewerConfig);
    }

    /// <summary>LAN 直连失败缓存（deviceId+lanIp → 失败时间）。5 分钟内跳过 LAN 直接走中继。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lanFailCache = new();

    private static bool IsLanRecentlyFailed(string deviceId, string lanIp) =>
        _lanFailCache.TryGetValue($"{deviceId}|{lanIp}", out var t) &&
        (DateTime.UtcNow - t).TotalMinutes < 5;

    private static void MarkLanFailed(string deviceId, string lanIp) =>
        _lanFailCache[$"{deviceId}|{lanIp}"] = DateTime.UtcNow;

    private static RemoteViewerClient Start(RemoteDeviceInfo device, Logger logger,
        ViewerTransport transport, string modeText, string sessionId, ViewerConfig viewerConfig)
    {
        var client = new RemoteViewerClient(device, logger)
        {
            _transport = transport,
            ModeText = modeText,
            SessionId = sessionId,
            _running = true,
        };
        ViewerTransport.LogError = msg => logger.Warn(msg);
        transport.FrameReceived += client.OnFrameReceived;
        transport.Disconnected += client.OnTransportDisconnected;
        client._viewerConfig = viewerConfig;
        client._watchdogThread = new Thread(client.WatchdogLoop) { IsBackground = true };
        client._watchdogThread.Start();
        logger.Info($"Viewer connected: device={device.DeviceId}({device.DisplayName}), mode={modeText}");
        return client;
    }

    /// <summary>
    /// 下发主控端远程配置（分辨率上限/质量/帧率/色深）。
    /// 时机：收到被控端首次握手后（会话已完全就绪，参数不会被会话启动复位覆盖）；
    /// 被控端 v1.1.48+ 解析 configure 帧应用；旧版被控端忽略未知 action，无兼容风险。
    /// </summary>
    private void SendConfigureRequest(ViewerConfig cfg)
    {
        try
        {
            var json = $"{{\"action\":\"configure\",\"maxHeight\":{Math.Max(0, cfg.TargetMaxHeight)}," +
                       $"\"percent\":{Math.Clamp(cfg.QualityPercent, 20, 100)}," +
                       $"\"fps\":{Math.Clamp(cfg.Fps, 5, 60)}," +
                       $"\"colorDepth\":{(cfg.ColorDepth == 16 ? 16 : 32)}}}";
            _transport?.Send(RemoteFrameProtocol.TYPE_CONTROL, Encoding.UTF8.GetBytes(json));
            _logger.Info($"Viewer configure sent: maxHeight={cfg.TargetMaxHeight}, quality={cfg.QualityPercent}%, fps={cfg.Fps}, colorDepth={cfg.ColorDepth}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Viewer configure send failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 提交访问验证码（AuthRequired 事件后 UI 收集用户输入调用；UI 线程安全）。
    /// 验证结果经 AuthFailed 事件或握手帧返回。
    /// </summary>
    public void SendAuthCode(string code)
    {
        try
        {
            var json = $"{{\"action\":\"auth\",\"code\":{JsonSerializer.Serialize(code)}}}";
            _transport?.Send(RemoteFrameProtocol.TYPE_CONTROL, Encoding.UTF8.GetBytes(json));
            _logger.Info("Auth code submitted");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Auth code send failed: {ex.Message}");
        }
    }

    // ============ 帧接收处理（接收线程） ============

    private void OnFrameReceived(byte type, byte[] data)
    {
        if (!_running) return;
        try
        {
            switch (type)
            {
                case RemoteFrameProtocol.TYPE_VIDEO_FRAME:
                    HandleVideoFrame(data);
                    break;

                case RemoteFrameProtocol.TYPE_CONTROL:
                    HandleControlFrame(data);
                    break;

                case RemoteFrameProtocol.TYPE_HEARTBEAT:
                    break; // 对端心跳，无需处理

                default:
                    break; // 输入帧是反向的，不会出现在查看器侧
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Viewer frame error: {ex.Message}");
        }
    }

    /// <summary>视频帧解码（H.264/JPEG → BGRA）。解码器创建/解码/销毁统一持锁互斥。</summary>
    private void HandleVideoFrame(byte[] data)
    {
        if (data.Length == 0) return;
        _lastFrameTime = DateTime.UtcNow;

        if (_codec == "jpeg")
        {
            DecodeJpegFrame(data);
            return;
        }

        // H.264：解码器懒创建（握手先行到达时已知分辨率 hint）
        lock (_decoderLock)
        {
            if (_decoderClosed) return;
            if (_h264Decoder == null)
            {
                var decoder = new H264Decoder();
                decoder.Initialize(_hintWidth, _hintHeight);
                _h264Decoder = decoder;
                _logger.Info($"H.264 decoder initialized (hint {_hintWidth}x{_hintHeight})");
            }
        }

        H264Decoder.DecodedFrame? frame;
        lock (_decoderLock)
        {
            if (_decoderClosed || _h264Decoder == null) return;
            frame = _h264Decoder.Decode(data);
        }
        if (frame != null)
            FrameDecoded?.Invoke(frame.Width, frame.Height, frame.Bgra);
    }

    /// <summary>JPEG 帧解码（对端无 H.264 编码器时的回退流）。</summary>
    private void DecodeJpegFrame(byte[] data)
    {
        var decoder = new JpegBitmapDecoder(
            new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        // JPEG 解码通常是 Bgr24，统一转 Bgra32（渲染层格式一致）
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var w = converted.PixelWidth;
        var h = converted.PixelHeight;
        var bgra = new byte[w * h * 4];
        converted.CopyPixels(bgra, w * 4, 0);
        FrameDecoded?.Invoke(w, h, bgra);
    }

    /// <summary>控制帧：握手（分辨率/编码器）与状态（锁屏/解锁）。</summary>
    private void HandleControlFrame(byte[] data)
    {
        try
        {
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(data));
            var root = json.RootElement;

            if (root.TryGetProperty("codec", out var codecProp))
            {
                // 握手：{"width":W,"height":H,"fps":F,"codec":"h264"}
                _codec = codecProp.GetString() == "jpeg" ? "jpeg" : "h264";
                _hintWidth = root.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                _hintHeight = root.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                _logger.Info($"Handshake: {_hintWidth}x{_hintHeight}, codec={_codec}");
                StatusMessage?.Invoke($"已连接（{_hintWidth}x{_hintHeight}），等待画面...");

                // 首次握手 = 被控端会话就绪：下发主控端会话参数（分辨率上限/质量/
                // 帧率/色深，覆盖被控端配置默认值，仅本会话生效）。被控端应用后若
                // 参数与默认不同会重建编码器并重发握手（不触发本分支重复下发）
                if (!_configureSent)
                {
                    _configureSent = true;
                    SendConfigureRequest(_viewerConfig);
                }

                // 解码器就绪前请求 IDR：若中途加入（重连/丢包）错过首关键帧，
                // 无周期 GOP 将永久黑屏，秒级补救（对端 3s GOP 兜底仍在）
                SendKeyframeRequest();
            }
            else if (root.TryGetProperty("action", out var actionProp))
            {
                // 被控端验证码交互（v1.1.54）：auth_required 要求输入 / auth_failed 拒绝重试
                var action = actionProp.GetString();
                if (action == "auth_required")
                {
                    _logger.Info("Auth required by host");
                    AuthRequired?.Invoke();
                }
                else if (action == "auth_failed")
                {
                    _logger.Warn("Auth rejected by host");
                    AuthFailed?.Invoke();
                }
                else if (action == "auth_ok")
                {
                    _logger.Info("Auth accepted by host");
                    StatusMessage?.Invoke("验证通过，等待画面...");
                }
            }
            else if (root.TryGetProperty("status", out var statusProp))
            {
                var status = statusProp.GetString();
                if (status == "locked")
                    StatusMessage?.Invoke("对方电脑已锁屏，仍可查看与输入（可输 PIN 解锁）");
                else if (status == "unlocked")
                    StatusMessage?.Invoke("对方电脑已解锁");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Viewer control frame parse failed: {ex.Message}");
        }
    }

    // ============ 看门狗线程：心跳 + 无帧补救 ============

    private void WatchdogLoop()
    {
        while (_running && !_disposed)
        {
            Thread.Sleep(1000);
            if (!_running) break;

            // 周期心跳（保持 NAT/中继链路活性）
            if ((DateTime.UtcNow - _lastHeartbeat).TotalSeconds >= 5)
            {
                _lastHeartbeat = DateTime.UtcNow;
                try { _transport?.Send(RemoteFrameProtocol.TYPE_HEARTBEAT, Array.Empty<byte>()); }
                catch (Exception ex) { _logger.Warn($"Viewer heartbeat failed: {ex.Message}"); }
            }

            // 无帧补救：连接存活但 5 秒无视频帧（错过 IDR/编码器重建）→ 请求关键帧
            if (_lastKeyframeRequest != DateTime.MinValue &&
                (DateTime.UtcNow - _lastFrameTime).TotalSeconds >= 5 &&
                (DateTime.UtcNow - _lastKeyframeRequest).TotalSeconds >= 2)
            {
                _logger.Warn("Viewer watchdog: no frames for 5s, requesting keyframe");
                SendKeyframeRequest();
            }
        }
    }

    // ============ 输入发送（UI 线程调用，内部无阻塞） ============

    /// <summary>鼠标移动（视频像素坐标）。</summary>
    public void SendMouseMove(int x, int y)
    {
        var data = new byte[5];
        data[0] = 0; // ACTION_MOUSE_MOVE
        WriteU16(data, 1, x);
        WriteU16(data, 3, y);
        SendFrame(RemoteFrameProtocol.TYPE_INPUT_MOUSE, data);
    }

    /// <summary>鼠标按钮（action 见 RemoteInputHandler.ACTION_*，视频像素坐标）。</summary>
    public void SendMouseButton(byte action, int x, int y)
    {
        var data = new byte[5];
        data[0] = action;
        WriteU16(data, 1, x);
        WriteU16(data, 3, y);
        SendFrame(RemoteFrameProtocol.TYPE_INPUT_MOUSE, data);
    }

    /// <summary>键盘事件（Windows VK 码）。</summary>
    public void SendKey(ushort vkCode, bool down)
    {
        var data = new byte[3];
        WriteU16(data, 0, vkCode);
        data[2] = (byte)(down ? 1 : 0);
        SendFrame(RemoteFrameProtocol.TYPE_INPUT_KEY, data);
    }

    /// <summary>滚轮（垂直/水平增量，视频像素坐标）。</summary>
    public void SendWheel(short deltaV, short deltaH, int x, int y)
    {
        var data = new byte[8];
        WriteU16(data, 0, (ushort)deltaV);
        WriteU16(data, 2, x);
        WriteU16(data, 4, y);
        WriteU16(data, 6, (ushort)deltaH);
        SendFrame(RemoteFrameProtocol.TYPE_INPUT_WHEEL, data);
    }

    /// <summary>Unicode 文本输入（中文/emoji 等无法映射 VK 的字符）。</summary>
    public void SendText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        SendFrame(RemoteFrameProtocol.TYPE_INPUT_TEXT, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>请求对端立即输出 IDR 关键帧。</summary>
    private void SendKeyframeRequest()
    {
        _lastKeyframeRequest = DateTime.UtcNow;
        try
        {
            _transport?.Send(RemoteFrameProtocol.TYPE_CONTROL,
                Encoding.UTF8.GetBytes("{\"action\":\"keyframe\"}"));
        }
        catch (Exception ex)
        {
            _logger.Warn($"Keyframe request failed: {ex.Message}");
        }
    }

    private void SendFrame(byte type, byte[] data)
    {
        if (!_running) return;
        try
        {
            _transport?.Send(type, data);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Viewer send failed: {ex.Message}");
        }
    }

    private static void WriteU16(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private void OnTransportDisconnected()
    {
        if (!_running) return;
        _running = false;
        _logger.Info($"Viewer transport disconnected: device={Device.DeviceId}");
        Cleanup();
        Disconnected?.Invoke("连接已断开");
    }

    /// <summary>释放传输与解码器。解码器销毁与解码互斥（MFT 非线程安全）。</summary>
    private void Cleanup()
    {
        try { _transport?.Dispose(); } catch { }
        _transport = null;
        lock (_decoderLock)
        {
            _decoderClosed = true;
            try { _h264Decoder?.Dispose(); } catch { }
            _h264Decoder = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        Cleanup();
    }

    /// <summary>auth_key = SHA256(psk) hex 小写（与 LanListener/RelayConnection 一致）。</summary>
    private static string ComputeAuthKey(string preSharedKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(preSharedKey));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
