using System.Net.Sockets;
using System.Text;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 中继隧道传输实现。连接服务器隧道端口（8445），
/// 发送 PC 隧道握手头 [0x01][session_id]，之后按帧协议收发数据。
/// </summary>
public sealed class RelayRemoteTransport : IRemoteTransport
{
    private const int SessionIdLen = 37;
    private const byte TypePCTunnel = 0x01;

    /// <summary>日志回调（由上层注入，用于记录底层连接异常）。</summary>
    public static Action<string>? LogError;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Thread _readThread;
    private volatile bool _running;
    // Dispose 引发的 ReadLoop 退出不再触发 Disconnected（同 LocalRemoteTransport：
    // 防止接管清理旧传输时迟到事件误杀新会话）
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

    private RelayRemoteTransport(TcpClient client, NetworkStream stream)
    {
        _client = client;
        _stream = stream;
        _readThread = new Thread(ReadLoop) { IsBackground = true };
    }

    /// <summary>连接隧道服务器并发送握手头，启动接收线程。</summary>
    public static async Task<RelayRemoteTransport> ConnectAsync(
        string host, int port, string sessionId, CancellationToken ct = default)
    {
        var client = new TcpClient();
        try
        {
            // 禁用 Nagle 算法：视频帧/心跳/控制帧小包立即发出，
            // 否则 Nagle+延迟ACK 交互会给小 P 帧和控制帧带来最多 200ms 额外延迟
            client.NoDelay = true;
            await client.ConnectAsync(host, port, ct);
            var stream = client.GetStream();

            // 发送握手头：[0x01] + session_id（37 字节 ASCII）
            var header = new byte[1 + SessionIdLen];
            header[0] = TypePCTunnel;
            var idBytes = Encoding.ASCII.GetBytes(sessionId);
            Array.Copy(idBytes, 0, header, 1, Math.Min(idBytes.Length, SessionIdLen));
            await stream.WriteAsync(header, ct);
            await stream.FlushAsync(ct);

            var transport = new RelayRemoteTransport(client, stream);
            transport._readThread.Start();
            return transport;
        }
        catch
        {
            client.Dispose();
            throw;
        }
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
                    System.Diagnostics.Debug.WriteLine($"Invalid frame length: {len}");
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
            // 连接异常（网络断开/重置），记录便于排查
            LogError?.Invoke($"RelayRemoteTransport read loop error: {ex.Message}");
        }
        finally
        {
            _running = false;
            try { _client.Dispose(); } catch { }
            // Dispose 主动关闭（会话接管/程序退出）不触发断开事件
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
