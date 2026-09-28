namespace QuickRemote.PCClient.Services;

/// <summary>
/// 帧编码器抽象：输入 BGRA 帧，输出编码后的数据（H.264 或 JPEG）。
/// 便于 H.264 不可用时回退到 JPEG（系统兼容性兜底）。
/// </summary>
public interface IFrameEncoder : IDisposable
{
    /// <summary>编码器标识（控制帧 codec 字段用）。</summary>
    string CodecName { get; }

    /// <summary>输入帧宽度。</summary>
    int Width { get; }

    /// <summary>输入帧高度。</summary>
    int Height { get; }

    /// <summary>初始化编码器。</summary>
    void Initialize(int width, int height, int fps = 15, int bitrateKbps = 4000);

    /// <summary>编码一帧 BGRA 数据，返回编码结果（可能为空数组）。</summary>
    byte[] EncodeFrame(byte[] bgraData);

    /// <summary>强制下一帧输出为关键帧（丢帧后快速恢复画面）。JPEG 无帧间参考，恒返回 true。</summary>
    bool ForceKeyFrame();
}
