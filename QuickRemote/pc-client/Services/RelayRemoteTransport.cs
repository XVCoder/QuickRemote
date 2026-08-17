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
    private readonly object _writeLock = new();

    /// <inheritdoc/>
    public event Action<byte, byte[]>? FrameReceived;

    /// <inheritdoc/>
    public event Action? Disconnected;

    /// <inheritdoc/>
    public bool IsConnected => _running && _client.Connected;

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
            Disconnected?.Invoke();
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
        _running = false;
        try { _client.Dispose(); } catch { }
    }
}
