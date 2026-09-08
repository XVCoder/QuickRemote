using System.IO;
using System.Net.Sockets;
using System.Text;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// PC 主控端（查看器）传输层。
/// 两种接入方式，帧协议与被控端（RemoteSessionManager）完全一致：
/// - 中继隧道：连服务器隧道端口，握手头 [0x02 App 角色][session_id 37B]
///   （被控端以 0x01 PC 角色接入同一 session，服务器桥接双向流量）
/// - 局域网直连：连目标 PC 的 LanListener（8447），首帧 TYPE_AUTH + auth_key
/// </summary>
public sealed class ViewerTransport : IRemoteTransport
{
    private const int SessionIdLen = 37;
    private const byte TypeAppTunnel = 0x02;

    /// <summary>日志回调（由上层注入）。</summary>
    public static Action<string>? LogError;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Thread _readThread;
    private volatile bool _running;
    private volatile bool _disposed;
    private readonly object _writeLock = new();

    /// <inheritdoc/>
    public event Action<byte, byte[]>? FrameReceived;

    /// <inheritdoc/>
    public event Action? Disconnected;

    /// <inheritdoc/>
    public bool IsConnected => _running && _client.Connected;

    /// <inheritdoc/>
    public long BytesSent => _bytesSent;

    /// <inheritdoc/>
    public long BytesReceived => _bytesReceived;

    private long _bytesSent;
    private long _bytesReceived;

    private ViewerTransport(TcpClient client, NetworkStream stream)
    {
        _client = client;
        _stream = stream;
        _readThread = new Thread(ReadLoop) { IsBackground = true };
    }

    /// <summary>连接中继隧道（App 角色 0x02），启动接收线程。</summary>
    public static async Task<ViewerTransport> ConnectRelayAsync(
        string host, int port, string sessionId, CancellationToken ct = default)
    {
        var client = new TcpClient();
        try
        {
            client.NoDelay = true;
            await client.ConnectAsync(host, port, ct);
            var stream = client.GetStream();

            // 握手头：[0x02] + session_id（37 字节 ASCII）
            var header = new byte[1 + SessionIdLen];
            header[0] = TypeAppTunnel;
            var idBytes = Encoding.ASCII.GetBytes(sessionId);
            Array.Copy(idBytes, 0, header, 1, Math.Min(idBytes.Length, SessionIdLen));
            await stream.WriteAsync(header, ct);
            await stream.FlushAsync(ct);

            return Start(client, stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>局域网直连目标 PC（LanListener 8447），发送认证帧后启动接收线程。</summary>
    /// <param name="authKey">SHA256(预共享密钥) 的 hex 小写（与被控端 LanListener 校验一致）</param>
    /// <param name="timeoutMs">连接超时（跨网段时快速失败回退中继）</param>
    public static async Task<ViewerTransport> ConnectLanAsync(
        string ip, int port, string authKey, int timeoutMs = 1500)
    {
        var client = new TcpClient();
        try
        {
            client.NoDelay = true;
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(ip, port, cts.Token);
            var stream = client.GetStream();

            // 认证帧：TYPE_AUTH + auth_key（ASCII）
            var keyBytes = Encoding.ASCII.GetBytes(authKey);
            var frame = new byte[RemoteFrameProtocol.HEADER_SIZE + keyBytes.Length];
            var header = RemoteFrameProtocol.MakeHeader(RemoteFrameProtocol.TYPE_AUTH, keyBytes.Length);
            Array.Copy(header, 0, frame, 0, header.Length);
            keyBytes.CopyTo(frame, header.Length);
            await stream.WriteAsync(frame, cts.Token);
            await stream.FlushAsync(cts.Token);

            return Start(client, stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static ViewerTransport Start(TcpClient client, NetworkStream stream)
    {
        var transport = new ViewerTransport(client, stream);
        transport._readThread.Start();
        return transport;
    }

    /// <inheritdoc/>
    public void Send(byte type, byte[] data)
    {
        if (!_running) return;
        var header = RemoteFrameProtocol.MakeHeader(type, data.Length);
        lock (_writeLock)
        {
            _stream.Write(header, 0, header.Length);
            if (data.Length > 0) _stream.Write(data, 0, data.Length);
            _stream.Flush();
            _bytesSent += header.Length + data.Length;
        }
    }

    private void ReadLoop()
    {
        _running = true;
        try
        {
            var header = new byte[RemoteFrameProtocol.HEADER_SIZE];
            while (_running)
            {
                if (!ReadExactly(header, header.Length)) break;
                var type = header[0];
                var len = RemoteFrameProtocol.DecodeLength(header);
                if (len < 0 || len > RemoteFrameProtocol.MAX_PAYLOAD)
                {
                    LogError?.Invoke($"ViewerTransport: invalid frame length {len}");
                    break;
                }

                var data = new byte[len];
                if (len > 0 && !ReadExactly(data, len)) break;

                _bytesReceived += header.Length + len;
                FrameReceived?.Invoke(type, data);
            }
        }
        catch (Exception ex)
        {
            LogError?.Invoke($"ViewerTransport read loop error: {ex.Message}");
        }
        finally
        {
            _running = false;
            try { _client.Dispose(); } catch { }
            if (!_disposed) Disconnected?.Invoke();
        }
    }

    private bool ReadExactly(byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = _stream.Read(buf, offset, count - offset);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
        _running = false;
        try { _client.Dispose(); } catch { }
    }
}
