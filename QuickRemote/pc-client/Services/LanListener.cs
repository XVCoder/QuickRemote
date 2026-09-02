using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 局域网直连监听器。PC 监听固定端口（默认 8447），
/// Android 端与 PC 同网段时直接连接，首帧发送 TYPE_AUTH + auth_key 完成认证，
/// 认证通过后启动截屏远程会话（不经中继服务器，低延迟）。
/// 与中继会话互斥：已有会话运行时拒绝新连接。
/// </summary>
public sealed class LanListener : IDisposable
{
    /// <summary>默认监听端口。</summary>
    public const int DefaultPort = 8447;

    private readonly Logger _logger;
    private readonly RemoteSessionManager _sessionManager;
    private readonly Func<string> _authKeyProvider;
    private TcpListener? _listener;
    private volatile bool _running;
    private bool _disposed;

    public LanListener(Logger logger, RemoteSessionManager sessionManager, Func<string> authKeyProvider)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _authKeyProvider = authKeyProvider;
    }

    /// <summary>启动监听（非阻塞）。失败仅记录，不抛出。</summary>
    public void Start(int port = DefaultPort)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _running = true;
            _logger.Info($"LAN listener started on port {port}");
            Task.Run(AcceptLoop);
        }
        catch (Exception ex)
        {
            _logger.Warn($"LAN listener start failed (port {port}): {ex.Message}");
            _running = false;
        }
    }

    private async Task AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClient(client));
            }
            catch
            {
                if (_running) await Task.Delay(200);
            }
        }
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            stream.ReadTimeout = 5000;

            // 1. 读取首帧：必须 TYPE_AUTH + auth_key（64B hex ASCII）
            var header = new byte[RemoteFrameProtocol.HEADER_SIZE];
            if (!ReadExactly(stream, header, header.Length, 5000))
            {
                _logger.Warn("LAN client: auth frame header timeout");
                client.Dispose();
                return;
            }
            if (header[0] != RemoteFrameProtocol.TYPE_AUTH)
            {
                _logger.Warn($"LAN client: expected AUTH frame, got 0x{header[0]:X2}");
                client.Dispose();
                return;
            }
            var keyLen = RemoteFrameProtocol.DecodeLength(header);
            if (keyLen is < 8 or > 128)
            {
                _logger.Warn($"LAN client: invalid auth key length {keyLen}");
                client.Dispose();
                return;
            }
            var keyBytes = new byte[keyLen];
            if (!ReadExactly(stream, keyBytes, keyLen, 5000))
            {
                client.Dispose();
                return;
            }
            var authKey = Encoding.ASCII.GetString(keyBytes).Trim();
            var expected = _authKeyProvider();
            if (authKey.Length < 8 || !string.Equals(authKey, expected, StringComparison.Ordinal))
            {
                _logger.Warn("LAN client: auth key mismatch, rejected");
                client.Dispose();
                return;
            }

            // 2. 认证通过：启动本地会话（与中继会话互斥，StartLocalAsync 内部判断）
            _logger.Info($"LAN client authenticated: {client.Client.RemoteEndPoint}");
            // 关键：恢复无限读超时。认证用的 5 秒超时若泄漏到 LocalRemoteTransport 的
            // 长连接读循环，Android 端 5 秒无上行数据（无点击/心跳）就会读超时断连，
            // 表现为"连接上了但点击没反应"（点击发往已死连接）。
            stream.ReadTimeout = Timeout.Infinite;
            var sessionId = Guid.NewGuid().ToString("N");
            _ = _sessionManager.StartLocalAsync(sessionId, client);
        }
        catch (Exception ex)
        {
            _logger.Warn($"LAN client handle error: {ex.Message}");
            try { client.Dispose(); } catch { }
        }
    }

    private static bool ReadExactly(Stream stream, byte[] buf, int count, int timeoutMs)
    {
        try
        {
            var old = stream.ReadTimeout;
            stream.ReadTimeout = timeoutMs;
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buf, offset, count - offset);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        try { _listener?.Stop(); } catch { }
    }
}
