using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// JPEG 帧编码器（WPF JpegBitmapEncoder）。
/// 系统无 H.264 编码器时的回退方案（Windows 全版本可用）。
/// </summary>
public sealed class JpegFrameEncoder : IFrameEncoder
{
    private int _quality = 75;

    /// <summary>降采样比例（高分辨率全尺寸 JPEG 单帧数百 KB，公网中继扛不住）。</summary>
    private double _scale = 1.0;

    /// <inheritdoc/>
    public string CodecName => "jpeg";

    /// <inheritdoc/>
    public int Width { get; private set; }

    /// <inheritdoc/>
    public int Height { get; private set; }

    /// <inheritdoc/>
    public void Initialize(int width, int height, int fps = 15, int bitrateKbps = 4000)
    {
        Width = width;
        Height = height;
        // 码率越高 → 质量越高（粗略映射：4Mbps→70, 6Mbps→80, 8Mbps→85）
        _quality = Math.Clamp((int)(bitrateKbps / 60.0), 60, 88);
        // 长边超过 1280 时等比缩小（如 2560x1440 → 1280x720），把单帧体积压到中继带宽可承受范围。
        // 纵横比不变，Android 端归一化坐标不受影响。
        var longEdge = Math.Max(width, height);
        _scale = longEdge > 1280 ? 1280.0 / longEdge : 1.0;
    }

    /// <inheritdoc/>
    public byte[] EncodeFrame(byte[] bgraData)
    {
        if (bgraData.Length != Width * Height * 4)
            return Array.Empty<byte>();

        try
        {
            var encoder = new JpegBitmapEncoder
            {
                QualityLevel = _quality
            };
            var bitmap = BitmapSource.Create(
                Width, Height, 96, 96, PixelFormats.Bgra32, null, bgraData, Width * 4);
            if (_scale < 1.0)
                bitmap = new TransformedBitmap(bitmap, new ScaleTransform(_scale, _scale));
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    /// <inheritdoc/>
    public bool ForceKeyFrame()
    {
        // JPEG 帧内编码，无帧间参考，无需关键帧
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // 无托管资源
    }
}
