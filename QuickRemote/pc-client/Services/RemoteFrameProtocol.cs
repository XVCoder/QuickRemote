namespace QuickRemote.PCClient.Services;

/// <summary>
/// 远程桌面帧协议。
/// 帧格式：[类型 1字节][长度 4字节(小端)][载荷 N字节]
/// </summary>
public static class RemoteFrameProtocol
{
    // ============ 帧类型 ============

    /// <summary>视频帧（H.264 NAL unit），PC→Android。</summary>
    public const byte TYPE_VIDEO_FRAME = 0x01;

    /// <summary>鼠标事件，Android→PC。</summary>
    public const byte TYPE_INPUT_MOUSE = 0x02;

    /// <summary>键盘事件，Android→PC。</summary>
    public const byte TYPE_INPUT_KEY = 0x03;

    /// <summary>滚轮事件，Android→PC。</summary>
    public const byte TYPE_INPUT_WHEEL = 0x04;

    /// <summary>控制帧（握手/分辨率/帧率），双向。</summary>
    public const byte TYPE_CONTROL = 0x05;

    /// <summary>心跳，双向。</summary>
    public const byte TYPE_HEARTBEAT = 0x06;

    /// <summary>局域网直连认证，Android→PC：[auth_key 64B hex ASCII]。</summary>
    public const byte TYPE_AUTH = 0x07;

    /// <summary>帧头长度：1 类型 + 4 长度。</summary>
    public const int HEADER_SIZE = 5;

    /// <summary>单帧最大载荷（16MB，H.264 关键帧足够）。</summary>
    public const int MAX_PAYLOAD = 16 * 1024 * 1024;

    /// <summary>编码帧头。</summary>
    public static byte[] MakeHeader(byte type, int length)
    {
        var header = new byte[HEADER_SIZE];
        header[0] = type;
        header[1] = (byte)(length & 0xFF);
        header[2] = (byte)((length >> 8) & 0xFF);
        header[3] = (byte)((length >> 16) & 0xFF);
        header[4] = (byte)((length >> 24) & 0xFF);
        return header;
    }

    /// <summary>解析帧头长度（header 至少 5 字节）。</summary>
    public static int DecodeLength(byte[] header)
    {
        return header[1] | header[2] << 8 | header[3] << 16 | header[4] << 24;
    }
}
