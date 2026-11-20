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

    /// <summary>
    /// 当前捕获的显示器编号（<see cref="EnumerateMonitors"/> 返回列表中的 Index；
    /// -1 = 尚未启动）。多屏切换时客户端据此高亮当前屏幕。
    /// </summary>
    public int MonitorIndex { get; private set; } = -1;

    /// <summary>本次捕获时枚举到的显示器列表（与 <see cref="MonitorIndex"/> 同一编号体系；
    /// 提供给会话层下发 Android 端，避免每次握手重新创建设备枚举）。</summary>
    public IReadOnlyList<MonitorTarget> Monitors { get; private set; } = Array.Empty<MonitorTarget>();

    /// <summary>被捕获显示器在虚拟桌面中的左上角（物理像素；主屏为 0，左/上方副屏可为负）。</summary>
    public int DesktopLeft { get; private set; }

    /// <summary>被捕获显示器在虚拟桌面中的顶边（物理像素）。</summary>
    public int DesktopTop { get; private set; }

    /// <summary>虚拟桌面（所有显示器外接矩形）左上角 X（物理像素）。</summary>
    public int VirtualLeft { get; private set; }

    /// <summary>虚拟桌面左上角 Y（物理像素）。</summary>
    public int VirtualTop { get; private set; }

    /// <summary>虚拟桌面宽度（物理像素，所有显示器外接矩形）。</summary>
    public int VirtualWidth { get; private set; }

    /// <summary>虚拟桌面高度（物理像素）。</summary>
    public int VirtualHeight { get; private set; }

    /// <summary>
    /// 判断异常是否为锁屏/安全桌面导致的 DXGI 访问问题（可等待解锁后恢复）。
    /// - 0x887A0026 DXGI_ERROR_ACCESS_LOST：锁屏/UAC 切换桌面导致复制失效
    /// - 0x80070005 E_ACCESSDENIED：锁屏期间重建 Output Duplication 被拒
    /// - 0x887A0001 DXGI_ERROR_INVALID_CALL：锁屏瞬间 AcquireNextFrame/DuplicateOutput
    ///   状态失效（实测锁屏时部分驱动报此码而非 ACCESS_LOST）
    /// - 0x80070057 E_INVALIDARG：显示器休眠/显示拓扑变化期间 DuplicateOutput/
    ///   AcquireNextFrame 持续失败（2026-09-28 00:19 实测：显示器睡眠后该错误
    ///   持续 13 分钟，期间中继/局域网连接全部秒断——按可恢复处理，销毁捕获
    ///   每秒重建，显示器唤醒后自动恢复，而不是杀死会话进入重连风暴）
    /// </summary>
    public static bool IsAccessDeniedOrLost(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is SharpGenException sge)
            {
                int code = sge.ResultCode.Code;
                if (code == unchecked((int)0x887A0026) ||
                    code == unchecked((int)0x887A0001) ||
                    code == unchecked((int)0x80070005) ||
                    code == unchecked((int)0x80070057))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 枚举本机显示器（默认适配器的全部输出，过滤未接入桌面的输出），
    /// 统一按「主屏优先、再从左到右、从上到下」排序并编号。
    /// <see cref="Start"/> 的 monitorIndex 使用同一套顺序定位目标输出。
    /// </summary>
    public static List<MonitorTarget> EnumerateMonitors()
    {
        try
        {
            // 与 Start 完全一致：设备用默认适配器创建（显式传适配器在部分驱动上
            // 会直接返回 E_INVALIDARG），再枚举该设备所属适配器的输出。
            var hr = D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                null,
                out var device,
                out _,
                out var context
            );
            if (hr.Failure) return new List<MonitorTarget>();

            using (device)
            using (context)
            using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
            using (var adapter = dxgiDevice.GetParent<IDXGIAdapter>())
            {
                var raw = new List<MonitorTarget>();
                for (var oi = 0; ; oi++)
                {
                    var ohr = adapter.EnumOutputs(oi, out var output);
                    if (ohr.Failure) break;
                    using (output)
                    {
                        var r = output.Description.DesktopCoordinates;
                        var w = r.Right - r.Left;
                        var h = r.Bottom - r.Top;
                        if (w <= 0 || h <= 0) continue; // 未接入桌面（如休眠/禁用）的输出
                        raw.Add(new MonitorTarget(0, w, h, r.Left, r.Top,
                            r.Left == 0 && r.Top == 0));
                    }
                }
                return Order(raw);
            }
        }
        catch
        {
            return new List<MonitorTarget>();
        }
    }

    /// <summary>统一排序规则：主屏在最前，其余按虚拟桌面坐标从左到右、从上到下，再重新编号。</summary>
    private static List<MonitorTarget> Order(List<MonitorTarget> raw) =>
        raw.OrderByDescending(m => m.IsPrimary)
           .ThenBy(m => m.Left)
           .ThenBy(m => m.Top)
           .Select((m, i) => m with { Index = i })
           .ToList();

    /// <summary>
    /// 枚举本机所有适配器的所有输出，返回虚拟桌面的外接矩形（物理像素）。
    /// 仅用于输入坐标归一化基准；枚举失败返回空矩形由调用方回退。
    /// </summary>
    private static (int Left, int Top, int Right, int Bottom) EnumerateVirtualDesktopBounds()
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (var ai = 0; ; ai++)
            {
                var adapters = factory.EnumAdapters1(ai, out var adapter);
                if (adapters.Failure) break;
                using (adapter)
                {
                    for (var oi = 0; ; oi++)
                    {
                        var outputs = adapter.EnumOutputs(oi, out var output);
                        if (outputs.Failure) break;
                        using (output)
                        {
                            var r = output.Description.DesktopCoordinates;
                            left = Math.Min(left, r.Left);
                            top = Math.Min(top, r.Top);
                            right = Math.Max(right, r.Right);
                            bottom = Math.Max(bottom, r.Bottom);
                        }
                    }
                }
            }
        }
        catch
        {
            return (0, 0, 0, 0);
        }
        return left == int.MaxValue ? (0, 0, 0, 0) : (left, top, right, bottom);
    }

    /// <summary>
    /// 初始化 DXGI Desktop Duplication。需要在 UI 线程或有桌面会话的线程调用。
    /// </summary>
    /// <param name="monitorIndex">
    /// 目标显示器编号（<see cref="EnumerateMonitors"/> 的 Index）；-1 = 主显示器。
    /// 多屏切换时由会话层传入 Android 端选择的编号。
    /// </param>
    public void Start(int monitorIndex = -1)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ScreenCaptureService));

        // 1. 创建设备。必须传 null（默认适配器）：显式传 IDXGIAdapter 在 AMD 驱动上
        //    实测直接返回 E_INVALIDARG，导致整条推流链路黑屏（v1.1.85 回归根因）。
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null,
            out _device,
            out _,
            out _context
        ).CheckError();

        // 2. 枚举设备所属适配器的全部输出，按与 EnumerateMonitors 一致的规则排序后
        //    按编号选目标（-1 = 主显示器）。输出序号不保证主屏在第一位，必须按坐标判断。
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();

        var outputs = new List<IDXGIOutput>();
        for (var i = 0; ; i++)
        {
            var hr = adapter.EnumOutputs(i, out var candidate);
            if (hr.Failure) break;
            outputs.Add(candidate);
        }
        if (outputs.Count == 0)
            throw new InvalidOperationException("No DXGI output available for desktop duplication");

        try
        {
            var ordered = outputs
                .Select(o =>
                {
                    var r = o.Description.DesktopCoordinates;
                    return (Output: o, Info: new MonitorTarget(0, r.Right - r.Left, r.Bottom - r.Top,
                        r.Left, r.Top, r.Left == 0 && r.Top == 0));
                })
                .Where(x => x.Info.Width > 0 && x.Info.Height > 0)
                .OrderByDescending(x => x.Info.IsPrimary)
                .ThenBy(x => x.Info.Left)
                .ThenBy(x => x.Info.Top)
                .Select((x, i) => (x.Output, Info: x.Info with { Index = i }))
                .ToList();
            if (ordered.Count == 0)
                throw new InvalidOperationException("No active DXGI output available for desktop duplication");

            var sel = monitorIndex >= 0 && monitorIndex < ordered.Count ? monitorIndex : 0;
            var target = ordered[sel];
            MonitorIndex = sel;
            Monitors = ordered.Select(x => x.Info).ToList();

            _width = target.Info.Width;
            _height = target.Info.Height;
            DesktopLeft = target.Info.Left;
            DesktopTop = target.Info.Top;

            using var output1 = target.Output.QueryInterface<IDXGIOutput1>();
            // 3. 创建 Output Duplication
            _duplication = output1.DuplicateOutput(_device);

            // 虚拟桌面 = 所有适配器所有输出的外接矩形（物理像素）。
            // 输入注入按 VIRTUALDESK 归一化，必须用整个虚拟桌面换算，
            // 否则双屏下点按位置会被压缩/平移到另一块屏。
            var (vLeft, vTop, vRight, vBottom) = EnumerateVirtualDesktopBounds();
            if (vRight <= vLeft || vBottom <= vTop)
            {
                vLeft = target.Info.Left;
                vTop = target.Info.Top;
                vRight = target.Info.Left + target.Info.Width;
                vBottom = target.Info.Top + target.Info.Height;
            }
            VirtualLeft = vLeft;
            VirtualTop = vTop;
            VirtualWidth = Math.Max(1, vRight - vLeft);
            VirtualHeight = Math.Max(1, vBottom - vTop);
        }
        finally
        {
            // 选中输出之外的对象必须释放；Duplication 创建后不再依赖 output 实例
            foreach (var o in outputs)
            {
                try { o.Dispose(); } catch { }
            }
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
    /// <returns>捕获的帧（BGRA 像素，Acquire 成功时总是完整桌面快照）；超时无新帧返回 null</returns>
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
            // AccumulatedFrames == 0 表示仅指针位置变化（无像素更新），
            // 但 AcquireNextFrame 成功时 desktopResource 始终是完整桌面快照——
            // 必须照常读取像素投递编码器：静止桌面下若跳过（旧逻辑返回 NoChanges），
            // 编码器永远等不到输入帧，远程端黑屏（2026-09-06 公网连接黑屏根因）。
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
                    HasChanges = frameInfo.AccumulatedFrames > 0,
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

        // 逐个隔离：某个 Dispose 抛出（如并发使用中）不能跳过其余对象——
        // _duplication 未释放会泄漏活的输出复制对象，之后同进程内任何
        // DuplicateOutput 都将永久 E_INVALIDARG（每输出仅允许一个复制）
        try { _stagingTexture?.Dispose(); } catch { }
        try { _duplication?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
    }
}

/// <summary>本机显示器信息（多屏切换用）。</summary>
/// <param name="Index">排序后的编号（0 起，主屏为 0）。</param>
public sealed record MonitorTarget(int Index, int Width, int Height, int Left, int Top, bool IsPrimary);

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
}
