using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using QuickRemote.PCClient.Models;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 数据隧道管理。管理活跃的隧道连接，每个隧道在
/// 服务器数据连接 ↔ 本地 RDP 端口之间双向转发数据，并统计流量。
/// </summary>
public sealed class TunnelManager : IDisposable
{
    /// <summary>隧道连接类型字节（PC 端为 0x01）。</summary>
    public const byte TypePCTunnel = 0x01;

    /// <summary>session_id 字节长度：("sess-" 5 字节 + 32 hex) = 37 字节。</summary>
    public const int SessionIdLen = 37;

    private readonly Logger _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, TunnelSession> _sessions = new();
    private volatile bool _disposed;

    /// <summary>会话列表快照（线程安全复制）。</summary>
    public IReadOnlyList<SessionInfo> Sessions
    {
        get
        {
            lock (_sync)
            {
                return _sessions.Values.Select(s => s.Info).ToList();
            }
        }
    }

    /// <summary>新会话建立时触发。</summary>
    public event Action<SessionInfo>? SessionStarted;

    /// <summary>会话结束时触发。</summary>
    public event Action<string>? SessionEnded;

    public TunnelManager(Logger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 建立一条隧道：连接服务器 tunnel_port 发送握手，同时连接本地 RDP，双向转发。
    /// </summary>
    /// <param name="sessionId">会话 ID（37 字节 ASCII）</param>
    /// <param name="serverHost">中转服务器主机名（用于 TLS/SNI 与连接）</param>
    /// <param name="tunnelPort">服务器隧道端口（TLS 模式下应为 443）</param>
    /// <param name="localRdpPort">本地 RDP 端口（默认 3389）</param>
    /// <param name="useTls">是否启用 TLS</param>
    /// <param name="tlsHost">TLS SNI 主机名</param>
    /// <param name="deviceName">可选客户端设备名</param>
    /// <param name="clientIp">可选客户端 IP</param>
    public void EstablishTunnel(string sessionId, string serverHost, int tunnelPort,
        int localRdpPort, bool useTls = false, string tlsHost = "",
        string deviceName = "", string clientIp = "")
    {
        if (_disposed) return;
        if (sessionId.Length != SessionIdLen)
        {
            _logger.Warn($"Tunnel session_id length mismatch: {sessionId.Length} (expected {SessionIdLen})");
        }

        var info = new SessionInfo
        {
            SessionId = sessionId,
            DeviceName = string.IsNullOrEmpty(deviceName) ? "Remote Device" : deviceName,
            ClientIp = clientIp,
            StartTime = DateTime.Now,
            IsActive = true
        };

        lock (_sync)
        {
            _sessions[sessionId] = new TunnelSession { Info = info };
        }

        SessionStarted?.Invoke(info);
        var schemeDesc = useTls ? "TLS" : "plain";
        _logger.Info($"Tunnel establishing: session={sessionId}, server={serverHost}:{tunnelPort} ({schemeDesc}), localRdp=127.0.0.1:{localRdpPort}");

        // 在后台线程执行隧道建立与转发
        Task.Run(() => RunTunnel(sessionId, serverHost, tunnelPort, localRdpPort, useTls, tlsHost, info));
    }

    private async Task RunTunnel(string sessionId, string serverHost, int tunnelPort,
        int localRdpPort, bool useTls, string tlsHost, SessionInfo info)
    {
        TcpClient? serverClient = null;
        TcpClient? rdpClient = null;
        Stream? serverStream = null;
        Stream? rdpStream = null;

        try
        {
            // 1. 建立到服务器的 TCP 连接
            serverClient = new TcpClient();
            await serverClient.ConnectAsync(serverHost, tunnelPort);
            serverStream = serverClient.GetStream();

            // 如果配置了 https://，包装 TLS 流
            if (useTls && !string.IsNullOrEmpty(tlsHost))
            {
                var sslStream = new SslStream(serverStream, false,
                    (_, _, _, _) => true, // 信任所有证书
                    null);
                await sslStream.AuthenticateAsClientAsync(tlsHost, null, SslProtocols.Tls12 | SslProtocols.Tls13, false);
                serverStream = sslStream;
                _logger.Info($"Tunnel TLS handshake OK: session={sessionId}");
            }

            // 2. 发送握手头：[0x01][session_id 37 字节]
            var header = new byte[1 + SessionIdLen];
            header[0] = TypePCTunnel;
            var idBytes = Encoding.ASCII.GetBytes(sessionId);
            Array.Copy(idBytes, 0, header, 1, Math.Min(idBytes.Length, SessionIdLen));
            await serverStream.WriteAsync(header);
            await serverStream.FlushAsync();
            _logger.Info($"Tunnel handshake sent: session={sessionId}");

            // 3. 连接到本地 RDP 端口
            rdpClient = new TcpClient();
            await rdpClient.ConnectAsync(IPAddress.Loopback, localRdpPort);
            rdpStream = rdpClient.GetStream();
            _logger.Info($"Connected to local RDP 127.0.0.1:{localRdpPort}");

            // 4. 双向转发
            using var cts = new CancellationTokenSource();
            var t1 = CopyAndCount(serverStream, rdpStream, info, isServerToRdp: true, cts.Token);
            var t2 = CopyAndCount(rdpStream, serverStream, info, isServerToRdp: false, cts.Token);

            await Task.WhenAny(t1, t2);
            cts.Cancel();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Tunnel error: session={sessionId}, {ex.Message}");
        }
        finally
        {
            info.IsActive = false;
            try { serverStream?.Dispose(); } catch { }
            try { rdpStream?.Dispose(); } catch { }
            try { serverClient?.Dispose(); } catch { }
            try { rdpClient?.Dispose(); } catch { }

            lock (_sync)
            {
                _sessions.Remove(sessionId);
            }
            SessionEnded?.Invoke(sessionId);
            _logger.Info($"Tunnel closed: session={sessionId}");
        }
    }

    /// <summary>单向复制数据并统计流量。</summary>
    private async Task CopyAndCount(Stream src, Stream dst, SessionInfo info,
        bool isServerToRdp, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await src.ReadAsync(buffer, ct);
                if (read == 0) break;
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                await dst.FlushAsync(ct);

                // server -> rdp 表示 PC 下行接收（客户端从服务器接收）
                // rdp -> server 表示 PC 上行发送
                if (isServerToRdp)
                    info.BytesReceived += read;
                else
                    info.BytesSent += read;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Warn($"Tunnel copy error: {ex.Message}");
        }
    }

    /// <summary>关闭指定会话。</summary>
    public void CloseSession(string sessionId)
    {
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                try { session.Info.IsActive = false; } catch { }
            }
        }
    }

    /// <summary>关闭所有隧道。</summary>
    public void CloseAll()
    {
        lock (_sync)
        {
            foreach (var s in _sessions.Values)
            {
                try { s.Info.IsActive = false; } catch { }
            }
            _sessions.Clear();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        CloseAll();
    }

    private sealed class TunnelSession
    {
        public SessionInfo Info { get; set; } = null!;
    }
}
