using System.Diagnostics;
using System.Text;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// PC 端远程会话管理器（截屏方案）。
/// 流程：连接中继隧道 → DXGI 捕获屏幕 → H.264 编码 → 帧协议发送；
/// 接收 Android 输入事件（阶段 5 实现 SendInput 模拟）。
/// </summary>
public sealed class RemoteSessionManager : IDisposable
{
    private readonly Logger _logger;
    private readonly int _fps;
    private readonly int _bitrateKbps;

    private IRemoteTransport? _transport;
    private ScreenCaptureService? _capture;
    private H264Encoder? _encoder;
    private readonly RemoteInputHandler _inputHandler = new();
    private Thread? _captureThread;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>会话 ID。</summary>
    public string SessionId { get; private set; } = "";

    /// <summary>会话结束时触发。</summary>
    public event Action? SessionEnded;

    public RemoteSessionManager(Logger logger, int fps = 15, int bitrateKbps = 4000)
    {
        _logger = logger;
        _fps = fps;
        _bitrateKbps = bitrateKbps;
    }

    /// <summary>启动远程会话。</summary>
    public async Task<bool> StartAsync(string sessionId, string serverHost, int tunnelPort)
    {
        if (_running) return false;

        SessionId = sessionId;
        _logger.Info($"Remote session starting: session={sessionId}, server={serverHost}:{tunnelPort}");

        try
        {
            // 1. 连接中继隧道（传输层抽象，未来可换 P2P）
            _transport = await RelayRemoteTransport.ConnectAsync(serverHost, tunnelPort, sessionId);
            _transport.FrameReceived += OnFrameReceived;
            _transport.Disconnected += OnTransportDisconnected;
            _logger.Info("Remote transport connected");

            // 2. 初始化屏幕捕获
            _capture = new ScreenCaptureService();
            _capture.Start();
            _logger.Info($"Screen capture started: {_capture.Width}x{_capture.Height}");

            // 3. 初始化编码器
            _encoder = new H264Encoder();
            _encoder.Initialize(_capture.Width, _capture.Height, _fps, _bitrateKbps);
            _logger.Info($"H.264 encoder initialized: {_fps}fps, {_bitrateKbps}kbps");

            // 4. 发送控制帧（握手：分辨率/帧率信息给 Android 端）
            var control = Encoding.UTF8.GetBytes(
                $"{{\"width\":{_capture.Width},\"height\":{_capture.Height},\"fps\":{_fps}}}");
            _transport.Send(RemoteFrameProtocol.TYPE_CONTROL, control);

            // 5. 启动捕获循环
            _running = true;
            _captureThread = new Thread(CaptureLoop) { IsBackground = true };
            _captureThread.Start();

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

    /// <summary>捕获循环：抓帧 → 编码 → 发送。</summary>
    private void CaptureLoop()
    {
        var interval = TimeSpan.FromMilliseconds(1000.0 / _fps);
        var lastControlSent = DateTime.UtcNow;

        while (_running)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var frame = _capture?.CaptureFrame(100);
                if (frame != null && frame.HasChanges && _encoder != null)
                {
                    var encoded = _encoder.EncodeFrame(frame.Data);
                    if (encoded.Length > 0)
                        _transport?.Send(RemoteFrameProtocol.TYPE_VIDEO_FRAME, encoded);
                }

                // 周期性心跳（5 秒），保持连接活性
                if ((DateTime.UtcNow - lastControlSent).TotalSeconds >= 5)
                {
                    _transport?.Send(RemoteFrameProtocol.TYPE_HEARTBEAT, Array.Empty<byte>());
                    lastControlSent = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Capture loop error: {ex.Message}");
                break;
            }

            sw.Stop();
            var remaining = interval - sw.Elapsed;
            if (remaining > TimeSpan.Zero)
                Thread.Sleep(remaining);
        }

        _logger.Info("Capture loop ended");
        Cleanup();
        SessionEnded?.Invoke();
    }

    /// <summary>接收帧（输入事件等），转发给输入处理器用 SendInput 模拟。</summary>
    private void OnFrameReceived(byte type, byte[] data)
    {
        switch (type)
        {
            case RemoteFrameProtocol.TYPE_INPUT_MOUSE:
            case RemoteFrameProtocol.TYPE_INPUT_KEY:
            case RemoteFrameProtocol.TYPE_INPUT_WHEEL:
                _inputHandler.HandleFrame(type, data);
                break;
            case RemoteFrameProtocol.TYPE_CONTROL:
                _logger.Info($"Control frame received: {Encoding.UTF8.GetString(data)}");
                break;
            case RemoteFrameProtocol.TYPE_HEARTBEAT:
                break; // 心跳，无需处理
            default:
                _logger.Warn($"Unknown frame type: 0x{type:X2}");
                break;
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
    }
}
