using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using SharpGen.Runtime;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// DXGI Desktop Duplication 屏幕捕获服务。
/// GPU 加速捕获桌面帧，输出 BGRA 像素数据。
/// 需要 Win10+ (Server 2016+)，运行时需要 DXGI 1.2+。
/// </summary>
public sealed class ScreenCaptureService : IDisposable
{
    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGIOutputDuplication _duplication = null!;
    private ID3D11Texture2D _stagingTexture = null!;
    private int _width;
    private int _height;
    private bool _disposed;

    /// <summary>屏幕宽度（像素）。</summary>
    public int Width => _width;

    /// <summary>屏幕高度（像素）。</summary>
    public int Height => _height;

    /// <summary>初始化 DXGI Desktop Duplication。需要在 UI 线程或有桌面会话的线程调用。</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ScreenCaptureService));

        // 1. 创建 D3D11 设备（硬件驱动）
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null,
            out _device,
            out _,
            out _context
        ).CheckError();

        // 2. 获取主显示器输出
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();
        adapter.EnumOutputs(0, out var output).CheckError();
        using (output)
        {
            var desc = output.Description;
            _width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
            _height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;

            using var output1 = output.QueryInterface<IDXGIOutput1>();
            // 3. 创建 Output Duplication
            _duplication = output1.DuplicateOutput(_device);
        }

        // 4. 创建 staging 纹理（CPU 可读，用于读取 GPU 纹理数据）
        var stagingDesc = new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };
        _stagingTexture = _device.CreateTexture2D(stagingDesc);
    }

    /// <summary>
    /// 捕获一帧桌面画面。
    /// </summary>
    /// <param name="timeoutMs">等待超时（毫秒），0=立即返回，-1=无限等待</param>
    /// <returns>捕获的帧（BGRA 像素），如果无变化返回 null</returns>
    public CapturedFrame? CaptureFrame(int timeoutMs = 500)
    {
        if (_disposed || _duplication == null)
            throw new InvalidOperationException("ScreenCaptureService not started");

        OutduplFrameInfo frameInfo;
        IDXGIResource desktopResource;

        var result = _duplication.AcquireNextFrame(timeoutMs, out frameInfo, out desktopResource);
        if (result.Failure)
        {
            if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                return null; // 无新帧
            result.CheckError();
            return null;
        }

        try
        {
            // 判断是否有像素变化
            // AccumulatedFrames > 0 表示有新帧；PointerPosition 变化不产生像素变化
            bool hasChanges = frameInfo.AccumulatedFrames > 0;
            if (!hasChanges)
            {
                _duplication.ReleaseFrame();
                return CapturedFrame.NoChanges;
            }

            using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();

            // 复制桌面纹理到 staging（CPU 可读）
            _context.CopyResource(_stagingTexture, desktopTexture);

            // Map 读取像素
            var map = _context.Map(_stagingTexture, 0, MapMode.Read);
            try
            {
                var dataSize = _width * _height * 4; // BGRA = 4 字节/像素
                var data = new byte[dataSize];

                // staging 纹理的行距可能大于 Width*4（对齐），需要逐行复制
                int srcStride = (int)map.RowPitch;
                int dstStride = _width * 4;
                if (srcStride == dstStride)
                {
                    Marshal.Copy(map.DataPointer, data, 0, dataSize);
                }
                else
                {
                    // 逐行复制（处理行距对齐）
                    for (int y = 0; y < _height; y++)
                    {
                        Marshal.Copy(
                            map.DataPointer + y * srcStride,
                            data, y * dstStride, dstStride
                        );
                    }
                }

                return new CapturedFrame
                {
                    Data = data,
                    Width = _width,
                    Height = _height,
                    HasChanges = true,
                    PointerX = frameInfo.PointerPosition.Position.X,
                    PointerY = frameInfo.PointerPosition.Position.Y
                };
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
            }
        }
        finally
        {
            desktopResource?.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stagingTexture?.Dispose();
        _duplication?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}

/// <summary>捕获的桌面帧。</summary>
public sealed class CapturedFrame
{
    /// <summary>像素数据（BGRA 格式）。</summary>
    public byte[] Data = Array.Empty<byte>();

    /// <summary>帧宽度。</summary>
    public int Width;

    /// <summary>帧高度。</summary>
    public int Height;

    /// <summary>是否有像素变化。</summary>
    public bool HasChanges;

    /// <summary>鼠标 X 坐标（桌面坐标）。</summary>
    public int PointerX;

    /// <summary>鼠标 Y 坐标（桌面坐标）。</summary>
    public int PointerY;

    /// <summary>表示"无变化"的帧。</summary>
    public static CapturedFrame NoChanges { get; } = new() { HasChanges = false };
}
