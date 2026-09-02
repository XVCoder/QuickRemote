using System.Net.Sockets;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 局域网直连传输实现。包装 PC 端 accept 的 TCP 连接（由 LanListener 建立），
/// 不做隧道握手头（客户端已认证），直接按帧协议收发数据。
/// </summary>
public sealed class LocalRemoteTransport : IRemoteTransport
{
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

    /// <inheritdoc/>
    public long BytesSent => _bytesSent;

    /// <inheritdoc/>
    public long BytesReceived => _bytesReceived;

    private long _bytesSent;
    private long _bytesReceived;

    private LocalRemoteTransport(TcpClient client, NetworkStream stream)
    {
        _client = client;
        _stream = stream;
        _readThread = new Thread(ReadLoop) { IsBackground = true };
    }

    /// <summary>包装已接受（已认证）的客户端连接，启动接收线程。</summary>
    public static LocalRemoteTransport FromClient(TcpClient client)
    {
        var transport = new LocalRemoteTransport(client, client.GetStream());
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
                if (len < 0 || len > RemoteFrameProtocol.MAX_PAYLOAD) break;

                var data = new byte[len];
                if (len > 0 && !ReadExactly(data, len)) break;

                _bytesReceived += header.Length + len;
                FrameReceived?.Invoke(type, data);
            }
        }
        catch (Exception ex)
        {
            LogError?.Invoke($"LocalRemoteTransport read loop error: {ex.Message}");
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
