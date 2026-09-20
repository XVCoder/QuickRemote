using System.ComponentModel;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickRemote.PCClient.Models;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 与中转服务器的控制连接。
/// - TLS TCP 连接
/// - 发送注册消息（auth_key = SHA256(pre_shared_key) 的 hex）
/// - JSON 帧协议: [4 字节 big-endian 长度][JSON 负载]
/// - 每 30 秒发送心跳
/// - 接收 tunnel_request，交由 TunnelManager 建立数据连接
/// - 断线重连（指数退避: 1s→2s→4s→8s→16s→30s）
/// </summary>
public sealed class RelayConnection : INotifyPropertyChanged, IDisposable
{
    private const int HeartbeatIntervalMs = 30_000;
    private static readonly int[] BackoffSeconds = { 1, 2, 4, 8, 16, 30 };
    private const int MaxMessageSize = 65_536;

    private readonly Logger _logger;
    private readonly TunnelManager _tunnelManager;
    private readonly RemoteSessionManager _remoteSessionManager;
    private CancellationTokenSource? _cts;
    private int _rdpPort = 3389;
    // 控制连接写锁：心跳任务（MessageLoop 内）与 UI 触发的 tunnel_open/rename 并发写
    // 同一流会交错帧（WriteMessage 分两次 WriteAsync），必须串行化
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    // 当前已连接的控制流（UI 触发请求发送用；断开时置 null）
    private volatile Stream? _stream;
    // 待响应的隧道请求（同一时刻仅一个查看器连接流程在等）
    private TaskCompletionSource<ControlMessage>? _pendingTunnelAck;

    private ConnectionStatus _status = ConnectionStatus.Disconnected;
    private string _serverAddress = string.Empty;
    private bool _useTls;
    private string _tlsHost = string.Empty;
    private string _deviceName = string.Empty;
    private string _deviceId = string.Empty;
    private string _myDisplayName = string.Empty;
    private DateTime _lastHeartbeat;
    private string _lastMessage = string.Empty;

    /// <summary>当前连接状态。</summary>
    public ConnectionStatus Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    /// <summary>最近一条连接状态消息（错误原因等）。</summary>
    public string LastMessage
    {
        get => _lastMessage;
        private set { _lastMessage = value; OnPropertyChanged(); }
    }

    /// <summary>状态文本。</summary>
    public string StatusText => _status switch
    {
        ConnectionStatus.Connected => "已连接",
        ConnectionStatus.Connecting => "连接中",
        ConnectionStatus.Reconnecting => "重连中",
        _ => "未连接"
    };

    /// <summary>服务器地址。</summary>
    public string ServerAddress
    {
        get => _serverAddress;
        private set { _serverAddress = value; OnPropertyChanged(); }
    }

    /// <summary>设备 ID（注册成功后由服务器分配）。</summary>
    public string DeviceName
    {
        get => _deviceName;
        private set { _deviceName = value; OnPropertyChanged(); }
    }

    /// <summary>设备 ID。</summary>
    public string DeviceId
    {
        get => _deviceId;
        private set { _deviceId = value; OnPropertyChanged(); }
    }

    /// <summary>本机生效的显示名称（register_ack 由服务器返回：自定义名或默认分配名）。</summary>
    public string MyDisplayName
    {
        get => _myDisplayName;
        private set { _myDisplayName = value; OnPropertyChanged(); }
    }

    /// <summary>上次心跳时间。</summary>
    public DateTime LastHeartbeat
    {
        get => _lastHeartbeat;
        private set { _lastHeartbeat = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>服务器推送设备列表（全部设备，含离线；任何设备上下线/改名后触发）。</summary>
    public event Action<System.Collections.Generic.List<Models.RemoteDeviceInfo>>? DeviceListUpdated;

    /// <summary>改名结果（成功时 name 为新名称，失败时 message 为原因）。</summary>
    public event Action<bool, string>? RenameResult;

    public RelayConnection(TunnelManager tunnelManager, RemoteSessionManager remoteSessionManager, Logger logger)
    {
        _tunnelManager = tunnelManager;
        _remoteSessionManager = remoteSessionManager;
        _logger = logger;
    }

    /// <summary>启动连接循环。</summary>
    /// <param name="serverAddress">服务器地址 host:port</param>
    /// <param name="preSharedKey">预共享密钥</param>
    /// <param name="machineId">本机唯一 ID</param>
    /// <param name="rdpPort">本地 RDP 端口</param>
    /// <param name="version">客户端版本</param>
    /// <param name="deviceName">本机设备显示名称（空 = 服务器分配默认名）</param>
    public void Start(string serverAddress, string preSharedKey, string machineId, int rdpPort, string version,
        string deviceName = "")
    {
        Stop();
        _rdpPort = rdpPort;
        ServerAddress = serverAddress;
        // 预解析 scheme 和 TLS 主机名
        var (host, _, useTls) = ParseAddress(serverAddress);
        _useTls = useTls;
        _tlsHost = host;
        DeviceName = SystemInfo.Hostname;
        _pendingTunnelAck?.TrySetException(new InvalidOperationException("连接重启，隧道请求已取消"));
        _pendingTunnelAck = null;
        _cts = new CancellationTokenSource();
        _ = RunAsync(serverAddress, preSharedKey, machineId, rdpPort, version, deviceName, _cts.Token);
    }

    /// <summary>停止连接。</summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        Status = ConnectionStatus.Disconnected;
    }

    private async Task RunAsync(string serverAddress, string preSharedKey, string machineId,
        int rdpPort, string version, string deviceName, CancellationToken ct)
    {
        int backoffIndex = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Status = backoffIndex == 0 ? ConnectionStatus.Connecting : ConnectionStatus.Reconnecting;
                var (host, port, useTls) = ParseAddress(serverAddress);
                var schemeDesc = useTls ? "TLS" : "plain";
                LastMessage = $"正在连接 {host}:{port} ({schemeDesc})...";
                _logger.Info($"Connecting to {host}:{port} ({schemeDesc}, attempt, backoff index={backoffIndex})");

                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port, ct);

                Stream stream = tcp.GetStream();

                // 如果配置了 https://，包装 TLS 流
                if (useTls)
                {
                    LastMessage = $"已连接，正在 TLS 握手 ({host})...";
                    var sslStream = new SslStream(stream, false,
                        (_, _, _, _) => true, // 信任所有证书（自签名/nginx 代理场景）
                        null);
                    await sslStream.AuthenticateAsClientAsync(host, null, SslProtocols.Tls12 | SslProtocols.Tls13, false);
                    stream = sslStream;
                    _logger.Info($"TLS handshake OK, cipher={sslStream.CipherAlgorithm}");
                }

                LastMessage = "已连接，正在注册...";

                _logger.Info("Connected, sending register");

                // 发送注册消息（display_name 非空 = 用户自定义名；空 = 保留服务器侧现名/默认分配）
                var authKey = ComputeAuthKey(preSharedKey);
                var registerMsg = new ControlMessage
                {
                    Type = "register",
                    MachineId = machineId,
                    Hostname = SystemInfo.Hostname,
                    DisplayName = deviceName ?? string.Empty,
                    OS = SystemInfo.OsInfo,
                    LanIp = SystemInfo.GetLanIp(),
                    RDPPort = rdpPort,
                    Version = version,
                    AuthKey = authKey
                };
                await WriteMessageLocked(stream, registerMsg, ct);

                // 读取注册确认（带 10 秒超时，防止连错端口时无限等待）
                ControlMessage? ack;
                using (var regCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    regCts.CancelAfter(TimeSpan.FromSeconds(10));
                    try
                    {
                        ack = await ReadMessage(stream, regCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _logger.Warn("Register timeout: no register_ack within 10s (wrong port? control port = HTTP port + 1)");
                        LastMessage = "注册超时：服务器未响应，请检查端口（控制连接应为 8444，而非 HTTP 端口 8443）";
                        Status = ConnectionStatus.Disconnected;
                        await DelayBackoff(ct, backoffIndex);
                        backoffIndex = Math.Min(backoffIndex + 1, BackoffSeconds.Length - 1);
                        continue;
                    }
                }

                if (ack == null || ack.Type != "register_ack" || ack.Status != "ok")
                {
                    var st = ack?.Status ?? "no_response";
                    LastMessage = $"注册失败：{st}";
                    _logger.Warn($"Register failed: {st}");
                    Status = ConnectionStatus.Disconnected;
                    await DelayBackoff(ct, backoffIndex);
                    backoffIndex = Math.Min(backoffIndex + 1, BackoffSeconds.Length - 1);
                    continue;
                }

                DeviceId = ack.DeviceId ?? string.Empty;
                MyDisplayName = ack.DisplayName ?? string.Empty;
                Status = ConnectionStatus.Connected;
                LastMessage = $"已连接，设备 ID: {DeviceId}";
                LastHeartbeat = DateTime.Now;
                backoffIndex = 0;
                _logger.Info($"Registered OK, device_id={DeviceId}, display_name={MyDisplayName}");
                _stream = stream;

                // 进入消息循环（含心跳）
                await MessageLoop(stream, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                LastMessage = $"连接失败：错误 {ex.ErrorCode} - {ex.Message}";
                _logger.Warn($"Connection error: {ex.Message}");
            }
            catch (Exception ex)
            {
                LastMessage = $"连接错误：{ex.Message}";
                _logger.Warn($"Connection error: {ex.Message}");
            }
            finally
            {
                // 连接断开（任何路径）：清空控制流引用并唤醒等待隧道响应的调用方
                _stream = null;
                _pendingTunnelAck?.TrySetException(new InvalidOperationException("控制连接已断开"));
                _pendingTunnelAck = null;
            }

            if (ct.IsCancellationRequested) break;

            Status = ConnectionStatus.Reconnecting;
            await DelayBackoff(ct, backoffIndex);
            backoffIndex = Math.Min(backoffIndex + 1, BackoffSeconds.Length - 1);
        }

        Status = ConnectionStatus.Disconnected;
    }

    /// <summary>消息循环：定时心跳 + 接收服务器消息。</summary>
    private async Task MessageLoop(Stream stream, CancellationToken ct)
    {
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = SendHeartbeats(stream, heartbeatCts.Token);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await ReadMessage(stream, ct);
                if (msg == null)
                {
                    _logger.Warn("Server closed control connection");
                    break;
                }

                switch (msg.Type)
                {
                    case "heartbeat_ack":
                        LastHeartbeat = DateTime.Now;
                        break;

                    case "tunnel_request":
                        HandleTunnelRequest(msg);
                        break;

                    case "device_list":
                        HandleDeviceList(msg);
                        break;

                    case "tunnel_ack":
                        // tunnel_open 的响应（PC→PC 远程控制）
                        _pendingTunnelAck?.TrySetResult(msg);
                        break;

                    case "rename_ack":
                        if (msg.Status == "ok")
                        {
                            MyDisplayName = msg.DisplayName ?? MyDisplayName;
                            RenameResult?.Invoke(true, msg.DisplayName ?? string.Empty);
                        }
                        else
                        {
                            RenameResult?.Invoke(false, MapTunnelAckError(msg.Status));
                        }
                        break;

                    default:
                        _logger.Info($"Received message type: {msg.Type}");
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Warn($"Message loop error: {ex.Message}");
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch { }
        }
    }

    /// <summary>定时发送心跳。</summary>
    private async Task SendHeartbeats(Stream stream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatIntervalMs, ct);
                await WriteMessageLocked(stream, new ControlMessage { Type = "heartbeat" }, ct);
                _logger.Info("Heartbeat sent");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Warn($"Heartbeat error: {ex.Message}");
        }
    }

    /// <summary>处理服务器设备列表广播（全部设备含离线，主线程外触发，订阅方自行调度 UI）。</summary>
    private void HandleDeviceList(ControlMessage msg)
    {
        var devices = new System.Collections.Generic.List<Models.RemoteDeviceInfo>();
        if (msg.Devices != null)
        {
            foreach (var d in msg.Devices)
            {
                devices.Add(new Models.RemoteDeviceInfo
                {
                    DeviceId = d.DeviceId ?? string.Empty,
                    Hostname = d.Hostname ?? string.Empty,
                    DisplayName = d.DisplayName ?? string.Empty,
                    LanIp = d.LanIp ?? string.Empty,
                    Version = d.Version ?? string.Empty,
                    Status = d.Status == "online" ? "online" : "offline",
                });
            }
        }
        DeviceListUpdated?.Invoke(devices);
    }

    /// <summary>修改本机设备显示名称（即时生效，服务器广播新列表）。</summary>
    public async Task SendRenameAsync(string deviceName)
    {
        var name = (deviceName ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > 64)
        {
            RenameResult?.Invoke(false, "设备名称长度需为 1-64 个字符");
            return;
        }
        var stream = _stream;
        if (stream == null || Status != ConnectionStatus.Connected)
        {
            RenameResult?.Invoke(false, "未连接服务器，改名将在下次连接时生效");
            return;
        }
        await WriteMessageLocked(stream, new ControlMessage { Type = "rename", DisplayName = name }, CancellationToken.None);
        _logger.Info($"Rename request sent: {name}");
    }

    /// <summary>
    /// PC→PC 远程控制：请求建立到目标设备的隧道。
    /// 服务器创建会话并通知被控端连入，返回 (session_id, tunnel_port) 供主控端连接。
    /// </summary>
    public async Task<(string SessionId, int TunnelPort)> RequestTunnelAsync(string targetDeviceId)
    {
        var stream = _stream;
        if (stream == null || Status != ConnectionStatus.Connected)
            throw new InvalidOperationException("未连接服务器，无法建立远程控制");

        var tcs = new TaskCompletionSource<ControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingTunnelAck?.TrySetException(new InvalidOperationException("新的隧道请求已取代前一个"));
        _pendingTunnelAck = tcs;

        try
        {
            await WriteMessageLocked(stream, new ControlMessage
            {
                Type = "tunnel_open",
                TargetDevice = targetDeviceId
            }, CancellationToken.None);
            _logger.Info($"Tunnel open request sent: target={targetDeviceId}");

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            if (completed != tcs.Task)
                throw new TimeoutException("请求隧道超时（服务器未响应）");

            var ack = await tcs.Task;
            if (ack.Status != "ok")
                throw new InvalidOperationException(MapTunnelAckError(ack.Status));

            return (ack.SessionId ?? string.Empty, ack.TunnelPort);
        }
        finally
        {
            if (ReferenceEquals(_pendingTunnelAck, tcs))
                _pendingTunnelAck = null;
        }
    }

    /// <summary>隧道/改名应答错误码 → 用户可读消息。</summary>
    private static string MapTunnelAckError(string? status) => status switch
    {
        "device_offline" => "目标设备离线",
        "device_not_found" => "目标设备不存在",
        "target_unreachable" => "无法通知目标设备（其控制连接可能已断开）",
        "tunnel_creation_failed" => "服务器创建隧道失败",
        "tunnel_unavailable" => "服务器隧道服务不可用",
        "bad_request" => "无效的请求参数",
        "invalid_name" => "设备名称无效（1-64 个字符）",
        "rename_failed" => "服务器改名失败",
        _ => $"服务器返回错误：{status ?? "unknown"}"
    };

    /// <summary>处理隧道建立请求：启动远程会话（截屏方案）。</summary>
    private void HandleTunnelRequest(ControlMessage msg)
    {
        var sessionId = msg.SessionId ?? string.Empty;
        var tunnelPort = msg.TunnelPort;
        var (host, _, useTls) = ParseAddress(ServerAddress);

        // 隧道连接直连服务器返回的端口（明文，隧道数据不加密）
        // 控制连接已加密，隧道数据明文不影响安全性（H.264 视频流本身敏感度低，
        // 且隧道只在会话期间开放）
        _logger.Info($"Tunnel request received: session={sessionId}, tunnel_port={tunnelPort}, tls={useTls}");

        // 远程模式：截屏方案（不再连接本地 RDP 3389）
        _ = _remoteSessionManager.StartAsync(sessionId, host, tunnelPort);
    }

    /// <summary>计算 auth_key = SHA256(preSharedKey) 的 hex 小写。</summary>
    private static string ComputeAuthKey(string preSharedKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(preSharedKey));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>解析服务器地址，返回 (host, port, useTls)。
    /// PC 客户端连接的是控制连接端口（8444），不是 HTTP API 端口（8443）。
    /// https://  → TLS，默认 8444
    /// http://   → 明文，默认 8444
    /// 无 scheme → 明文，默认 8444
    ///
    /// 实现已迁到 <see cref="Interop.RelayAddress.ParseAddress"/>（纯逻辑、可单测），
    /// 这里仅保留旧签名转发，避免调用点被迫改名。
    /// </summary>
    public static (string host, int port, bool useTls) ParseAddress(string address)
        => Interop.RelayAddress.ParseAddress(address);

    /// <summary>指数退避等待。</summary>
    private static async Task DelayBackoff(CancellationToken ct, int backoffIndex)
    {
        var seconds = BackoffSeconds[Math.Min(backoffIndex, BackoffSeconds.Length - 1)];
        try { await Task.Delay(seconds * 1000, ct); }
        catch (OperationCanceledException) { }
    }

    /// <summary>带写锁写入一条 JSON 帧消息（心跳任务与 UI 触发请求并发安全）。</summary>
    private async Task WriteMessageLocked(Stream stream, ControlMessage msg, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await WriteMessage(stream, msg, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>写入一条 JSON 帧消息。</summary>
    private static async Task WriteMessage(Stream stream, ControlMessage msg, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(msg);
        var lenBuf = new byte[4];
        lenBuf[0] = (byte)(json.Length >> 24);
        lenBuf[1] = (byte)(json.Length >> 16);
        lenBuf[2] = (byte)(json.Length >> 8);
        lenBuf[3] = (byte)json.Length;
        await stream.WriteAsync(lenBuf, ct);
        await stream.WriteAsync(json, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>读取一条 JSON 帧消息。</summary>
    private static async Task<ControlMessage?> ReadMessage(Stream stream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        if (!await ReadExactAsync(stream, lenBuf, ct)) return null;

        var len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
        if (len <= 0 || len > MaxMessageSize) return null;

        var data = new byte[len];
        if (!await ReadExactAsync(stream, data, ct)) return null;

        return JsonSerializer.Deserialize<ControlMessage>(data);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    public void Dispose()
    {
        Stop();
    }

    /// <summary>控制连接消息（字段名与服务器 control.Message 一致）。</summary>
    private sealed class ControlMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("machine_id")]
        public string? MachineId { get; set; }

        [JsonPropertyName("hostname")]
        public string? Hostname { get; set; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("os")]
        public string? OS { get; set; }

        [JsonPropertyName("lan_ip")]
        public string? LanIp { get; set; }

        [JsonPropertyName("rdp_port")]
        public int RDPPort { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("auth_key")]
        public string? AuthKey { get; set; }

        [JsonPropertyName("device_id")]
        public string? DeviceId { get; set; }

        [JsonPropertyName("target_device")]
        public string? TargetDevice { get; set; }

        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [JsonPropertyName("tunnel_port")]
        public int TunnelPort { get; set; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        /// <summary>device_list 广播携带的设备数组。</summary>
        [JsonPropertyName("devices")]
        public List<DeviceListEntry>? Devices { get; set; }
    }

    /// <summary>device_list 广播条目（服务器 registry.Device JSON 字段）。</summary>
    private sealed class DeviceListEntry
    {
        [JsonPropertyName("device_id")]
        public string? DeviceId { get; set; }

        [JsonPropertyName("hostname")]
        public string? Hostname { get; set; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("lan_ip")]
        public string? LanIp { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }
}
