namespace QuickRemote.PCClient.Services;

/// <summary>
/// 远程桌面传输层抽象。
/// 预留 P2P 扩展：当前实现为中继隧道（RelayRemoteTransport），
/// 未来可增加 P2PTransport（WebRTC/UDP 打洞）实现本接口。
/// </summary>
public interface IRemoteTransport : IDisposable
{
    /// <summary>发送一帧数据。</summary>
    void Send(byte type, byte[] data);

    /// <summary>收到一帧数据（类型 + 载荷）。</summary>
    event Action<byte, byte[]>? FrameReceived;

    /// <summary>连接断开。</summary>
    event Action? Disconnected;

    /// <summary>是否已连接。</summary>
    bool IsConnected { get; }

    /// <summary>已发送字节总数（含帧头，UI 流量统计）。</summary>
    long BytesSent { get; }

    /// <summary>已接收字节总数（含帧头，UI 流量统计）。</summary>
    long BytesReceived { get; }
}
