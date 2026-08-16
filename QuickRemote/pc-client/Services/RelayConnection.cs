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
    private CancellationTokenSource? _cts;
    private int _rdpPort = 3389;

    private ConnectionStatus _status = ConnectionStatus.Disconnected;
    private string _serverAddress = string.Empty;
    private bool _useTls;
    private string _tlsHost = string.Empty;
    private string _deviceName = string.Empty;
    private string _deviceId = string.Empty;
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

    /// <summary>上次心跳时间。</summary>
    public DateTime LastHeartbeat
    {
        get => _lastHeartbeat;
        private set { _lastHeartbeat = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public RelayConnection(TunnelManager tunnelManager, Logger logger)
    {
        _tunnelManager = tunnelManager;
        _logger = logger;
    }

    /// <summary>启动连接循环。</summary>
    /// <param name="serverAddress">服务器地址 host:port</param>
    /// <param name="preSharedKey">预共享密钥</param>
    /// <param name="machineId">本机唯一 ID</param>
    /// <param name="rdpPort">本地 RDP 端口</param>
    /// <param name="version">客户端版本</param>
    public void Start(string serverAddress, string preSharedKey, string machineId, int rdpPort, string version)
    {
        Stop();
        _rdpPort = rdpPort;
        ServerAddress = serverAddress;
        // 预解析 scheme 和 TLS 主机名
        var (host, _, useTls) = ParseAddress(serverAddress);
        _useTls = useTls;
        _tlsHost = host;
        DeviceName = SystemInfo.Hostname;
        _cts = new CancellationTokenSource();
        _ = RunAsync(serverAddress, preSharedKey, machineId, rdpPort, version, _cts.Token);
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
        int rdpPort, string version, CancellationToken ct)
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

                // 发送注册消息
                var authKey = ComputeAuthKey(preSharedKey);
                var registerMsg = new ControlMessage
                {
                    Type = "register",
                    MachineId = machineId,
                    Hostname = SystemInfo.Hostname,
                    OS = SystemInfo.OsInfo,
                    RDPPort = rdpPort,
                    Version = version,
                    AuthKey = authKey
                };
                await WriteMessage(stream, registerMsg, ct);

                // 读取注册确认
                var ack = await ReadMessage(stream, ct);
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
                Status = ConnectionStatus.Connected;
                LastMessage = $"已连接，设备 ID: {DeviceId}";
                LastHeartbeat = DateTime.Now;
                backoffIndex = 0;
                _logger.Info($"Registered OK, device_id={DeviceId}");

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
                await WriteMessage(stream, new ControlMessage { Type = "heartbeat" }, ct);
                _logger.Info("Heartbeat sent");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Warn($"Heartbeat error: {ex.Message}");
        }
    }

    /// <summary>处理隧道建立请求：交由 TunnelManager 建立数据连接。</summary>
    private void HandleTunnelRequest(ControlMessage msg)
    {
        var sessionId = msg.SessionId ?? string.Empty;
        var tunnelPort = msg.TunnelPort;
        var (host, _, useTls) = ParseAddress(ServerAddress);

        // 隧道连接直连服务器返回的端口（明文，隧道数据不加密）
        // 控制连接已加密，隧道数据明文不影响安全性（RDP 本身有加密）
        _logger.Info($"Tunnel request received: session={sessionId}, tunnel_port={tunnelPort}, tls={useTls}");
        _tunnelManager.EstablishTunnel(sessionId, host, tunnelPort, _rdpPort, false, _tlsHost);
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
    /// </summary>
    private static (string host, int port, bool useTls) ParseAddress(string address)
    {
        var addr = address.Trim();
        var useTls = false;

        if (addr.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            useTls = true;
            addr = addr[8..];
        }
        else if (addr.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            addr = addr[7..];
        }
        addr = addr.TrimEnd('/');

        var idx = addr.LastIndexOf(':');
        if (idx > 0 && int.TryParse(addr[(idx + 1)..], out var port))
        {
            return (addr[..idx], port, useTls);
        }

        // PC 客户端连接控制连接端口（8444），不是 HTTP API 端口（8443）
        return (addr, 8444, useTls);
    }

    /// <summary>指数退避等待。</summary>
    private static async Task DelayBackoff(CancellationToken ct, int backoffIndex)
    {
        var seconds = BackoffSeconds[Math.Min(backoffIndex, BackoffSeconds.Length - 1)];
        try { await Task.Delay(seconds * 1000, ct); }
        catch (OperationCanceledException) { }
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

        [JsonPropertyName("os")]
        public string? OS { get; set; }

        [JsonPropertyName("rdp_port")]
        public int RDPPort { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("auth_key")]
        public string? AuthKey { get; set; }

        [JsonPropertyName("device_id")]
        public string? DeviceId { get; set; }

        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [JsonPropertyName("tunnel_port")]
        public int TunnelPort { get; set; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
    }
}
