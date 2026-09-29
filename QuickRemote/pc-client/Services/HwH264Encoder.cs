using System.IO;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// H.264 硬件编码器（GPU MFT + D3D11 零拷贝管线，v1.1.76 第二步优化）。
///
/// 管线（tmp-llprobe hw2 探针在本机 AMD Radeon RX 5500 XT 全链路实测通过）：
///   BGRA 字节 → GPU 纹理(UpdateSubresource)
///   → ID3D11VideoProcessorBlt GPU 色彩转换 BGRA→NV12
///   → MFCreateDXGISurfaceBuffer 包 NV12 纹理
///   → 异步硬件 MFT（AMDh264Encoder / Intel / NVIDIA 同协议）事件驱动编码
///   → H.264 Annex-B 输出
///
/// 对比软件编码器（H264Encoder）：省掉 CPU BGRA→NV12 转换（~13ms/帧）与
/// CPU 编码开销；本机实测 2560x1440@15fps 编码延迟 min/avg/max=11/30/53ms，
/// 首帧 IDR 41ms（软件 LL 模式首帧 78ms），解码端到端验证 60/60 非黑。
///
/// 硬件 MFT 是异步 MFT：必须先在属性 store 设 MF_TRANSFORM_ASYNC_UNLOCK=1
/// 再调 IMFTransform（否则全部调用返回 MF_E_TRANSFORM_ASYNC_LOCKED），
/// 并按事件协议（NeedInput/HaveOutput）驱动，与同步软件 MFT 完全不同。
///
/// 任意初始化失败由 TryCreate 捕获返回 null，调用方回退软件 H264Encoder。
/// </summary>
public sealed class HwH264Encoder : IFrameEncoder
{
    /// <inheritdoc/>
    public string CodecName => "h264";

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsAvailable => _initialized;

    /// <summary>硬件编码器无 lookahead 缓冲，延迟特性与低延迟模式等价（恒 true）。</summary>
    public bool IsLowLatency => _initialized;

    private bool _initialized;
    private bool _disposed;
    private long _sampleTime = 0;
    private long _sampleDuration;

    // MF 侧
    private IMFTransform? _encoder;
    private IMFAttributes? _encAttrs;
    private IntPtr _mgrPtr = IntPtr.Zero;
    private int _outputBufferSize = 4 * 1024 * 1024;
    private bool _providesSamples;

    // D3D 侧（Vortice）
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private ID3D11VideoProcessorEnumerator? _vpEnum;
    private ID3D11VideoProcessor? _vp;
    private ID3D11Texture2D? _bgraTex;
    private ID3D11Texture2D? _nv12Tex;
    private ID3D11VideoProcessorInputView? _inView;
    private ID3D11VideoProcessorOutputView? _outView;

    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    /// <summary>
    /// 创建硬件编码器；本机无硬件 MFT 或初始化失败时返回 null（调用方回退软件编码）。
    /// </summary>
    public static HwH264Encoder? TryCreate(int width, int height, int fps, int bitrateKbps, Logger logger)
    {
        try
        {
            var enc = new HwH264Encoder();
            enc.Initialize(width, height, fps, bitrateKbps);
            return enc;
        }
        catch (Exception ex)
        {
            logger.Warn($"Hardware H.264 encoder unavailable ({ex.Message}), falling back to software");
            return null;
        }
    }

    public void Initialize(int width, int height, int fps = 15, int bitrateKbps = 4000)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HwH264Encoder));
        if (_initialized) throw new InvalidOperationException("Encoder already initialized");

        Width = width;
        Height = height;
        _sampleDuration = 10_000_000L / fps;

        MfPlatform.EnsureStarted();

        try
        {
            // ===== 1. D3D11 设备（BGRA 支持 + 视频接口；默认适配器，采集也在其上）=====
            var res = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null,
                out _device, out _context);
            if (res.Failure) throw new COMException($"D3D11CreateDevice failed: 0x{res.Code:X8}");
            _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
            _videoContext = _context.QueryInterface<ID3D11VideoContext>();

            // ===== 2. 枚举硬件编码 MFT（NV12 → H264），逐候选尝试 =====
            var inType = new MFT_REGISTER_TYPE_INFO { guidMajorType = MFInterop.MFMediaType_Video, guidSubtype = MFInterop.MFVideoFormat_NV12 };
            var outType = new MFT_REGISTER_TYPE_INFO { guidMajorType = MFInterop.MFMediaType_Video, guidSubtype = MFInterop.MFVideoFormat_H264 };
            IntPtr inPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_REGISTER_TYPE_INFO>());
            IntPtr outPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_REGISTER_TYPE_INFO>());
            Marshal.StructureToPtr(inType, inPtr, false);
            Marshal.StructureToPtr(outType, outPtr, false);
            int hr = MFInterop.MFTEnumEx(MFInterop.MFT_CATEGORY_VIDEO_ENCODER, MFInterop.MFT_ENUM_FLAG_HARDWARE,
                inPtr, outPtr, out var activates, out int count);
            Marshal.FreeHGlobal(inPtr);
            Marshal.FreeHGlobal(outPtr);
            if (MFHr.Succeeded(hr) == false || count == 0)
                throw new COMException($"No hardware H.264 encoder MFT (hr=0x{hr:X8}, count={count})");

            bool activated = false;
            for (int cand = 0; cand < count && !activated; cand++)
            {
                try
                {
                    ActivateCandidate(Marshal.ReadIntPtr(activates, cand * IntPtr.Size), width, height, fps, bitrateKbps);
                    activated = true;
                }
                catch
                {
                    CleanupMf();
                    if (cand == count - 1) throw;
                }
            }
            if (MFHr.Succeeded(hr) == false || !activated)
                throw new COMException("No hardware encoder MFT could be activated");

            // ===== 3. GPU 转色资源：BGRA 纹理 → NV12 纹理 =====
            _vpEnum = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
            {
                InputWidth = width, InputHeight = height,
                OutputWidth = width, OutputHeight = height,
                InputFrameRate = new Rational(fps, 1),
                OutputFrameRate = new Rational(fps, 1),
            });
            _vp = _videoDevice.CreateVideoProcessor(_vpEnum, 0);

            var bgraDesc = new Texture2DDescription
            {
                Width = width, Height = height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            };
            _bgraTex = _device.CreateTexture2D(bgraDesc);
            _inView = _videoDevice.CreateVideoProcessorInputView(_bgraTex, _vpEnum, new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });

            var nv12Desc = new Texture2DDescription
            {
                Width = width, Height = height, MipLevels = 1, ArraySize = 1,
                Format = Format.NV12, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            };
            _nv12Tex = _device.CreateTexture2D(nv12Desc);
            _outView = _videoDevice.CreateVideoProcessorOutputView(_nv12Tex, _vpEnum, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });

            _initialized = true;
        }
        catch
        {
            CleanupAll();
            throw;
        }
    }

    /// <summary>激活单个硬件 MFT 候选：解锁 → D3D manager → 类型协商 → 起流。</summary>
    private void ActivateCandidate(IntPtr actPtr, int width, int height, int fps, int bitrateKbps)
    {
        var act = MFInterop.GetObject<IMFActivate>(actPtr);
        var iidT = MFInterop.IID_IMFTransform;
        if (MFHr.Succeeded(act.ActivateObject(ref iidT, out IntPtr mftPtr)) == false)
            throw new COMException("ActivateObject failed");
        _encoder = MFInterop.GetObject<IMFTransform>(mftPtr);

        // 1. 异步解锁（⚠️ 必须 BEFORE 一切 IMFTransform 调用，GUID 见 MFInterop 注释）
        if (MFHr.Succeeded(_encoder.GetAttributes(out var attrs)) == false || attrs == null)
            throw new COMException("GetAttributes failed");
        _encAttrs = attrs;
        var asyncKey = MFInterop.MF_TRANSFORM_ASYNC;
        attrs.GetUINT32_(ref asyncKey, out uint isAsync);
        if (isAsync != 1) throw new COMException("MFT is not async");
        var unlockKey = MFInterop.MF_TRANSFORM_ASYNC_UNLOCK;
        if (MFHr.Succeeded(attrs.SetUINT32(ref unlockKey, 1)) == false)
            throw new COMException("Async unlock failed");

        // 2. GOP（3 秒一个 IDR 自愈，与软件编码器一致）+ 低延迟
        var gopKey = MFInterop.CODECAPI_AVEncMPVGOPSize;
        attrs.SetUINT32(ref gopKey, (uint)Math.Max(1, fps * 3));
        var avllKey = MFInterop.CODECAPI_AVLowLatencyMode;
        attrs.SetUINT32(ref avllKey, 1);

        // 3. D3D device manager（硬件 MFT 要求 GPU 内存输入）
        MFInterop.MFCreateDXGIDeviceManager(out int resetToken, out _mgrPtr);
        var mgr = (IMFDXGIDeviceManager)Marshal.GetObjectForIUnknown(_mgrPtr);
        try
        {
            if (MFHr.Succeeded(mgr.ResetDevice(_device!.NativePointer, (uint)resetToken)) == false)
                throw new COMException("DXGI device manager ResetDevice failed");
            int hrMgr = _encoder.ProcessMessage(MFHr.MFT_MESSAGE_SET_D3D_MANAGER, _mgrPtr);
            if (MFHr.Succeeded(hrMgr) == false)
                throw new COMException($"SET_D3D_MANAGER failed: 0x{hrMgr:X8}");
        }
        finally
        {
            Marshal.ReleaseComObject(mgr);
        }

        // 4. 类型协商：输出 H264 先，输入 NV12 后（探针实测顺序）
        var outM = CreateMediaType();
        try
        {
            SetGuid(outM, MFInterop.MF_MT_MAJOR_TYPE, MFInterop.MFMediaType_Video);
            SetGuid(outM, MFInterop.MF_MT_SUBTYPE, MFInterop.MFVideoFormat_H264);
            SetU64(outM, MFInterop.MF_MT_FRAME_SIZE, MakeFrameSize(width, height));
            SetU64(outM, MFInterop.MF_MT_FRAME_RATE, MakeFrameRate(fps));
            SetUInt(outM, MFInterop.MF_MT_AVG_BITRATE, (uint)(bitrateKbps * 1000));
            int hr = _encoder.SetOutputType(0, outM, 0);
            if (MFHr.Succeeded(hr) == false) throw new COMException($"SetOutputType failed: 0x{hr:X8}");
        }
        finally
        {
            Marshal.ReleaseComObject(outM);
        }

        var inM = CreateMediaType();
        try
        {
            SetGuid(inM, MFInterop.MF_MT_MAJOR_TYPE, MFInterop.MFMediaType_Video);
            SetGuid(inM, MFInterop.MF_MT_SUBTYPE, MFInterop.MFVideoFormat_NV12);
            SetU64(inM, MFInterop.MF_MT_FRAME_SIZE, MakeFrameSize(width, height));
            SetU64(inM, MFInterop.MF_MT_FRAME_RATE, MakeFrameRate(fps));
            int hr = _encoder.SetInputType(0, inM, 0);
            if (MFHr.Succeeded(hr) == false) throw new COMException($"SetInputType failed: 0x{hr:X8}");
        }
        finally
        {
            Marshal.ReleaseComObject(inM);
        }

        // 5. 起流 + 查询输出流要求
        _encoder.ProcessMessage(MFHr.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
        _encoder.ProcessMessage(MFHr.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
        if (MFHr.Succeeded(_encoder.GetOutputStreamInfo(0, out var outInfo)) && outInfo.cbSize > 0)
            _outputBufferSize = Math.Max(_outputBufferSize, outInfo.cbSize);
        _providesSamples = (outInfo.dwFlags & MFInterop.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
    }

    /// <summary>编码一帧 BGRA 数据，返回 H.264 数据（可能为空数组）。</summary>
    public byte[] EncodeFrame(byte[] bgraData)
    {
        if (!_initialized) throw new InvalidOperationException("Encoder not initialized");
        if (bgraData.Length != Width * Height * 4)
            throw new ArgumentException($"BGRA data size mismatch: {bgraData.Length} != {Width * Height * 4}");

        // 1. CPU → GPU 上传
        unsafe
        {
            fixed (byte* p = bgraData)
                _context!.UpdateSubresource(_bgraTex!, 0, null, (IntPtr)p, Width * 4, 0);
        }

        // 2. 等 NeedInput（异步 MFT 节流；超时则本帧跳过，不阻塞编码线程）
        bool needInput = false, haveOutput = false;
        var eg = (IMFMediaEventGenerator)_encoder!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!needInput)
        {
            PumpEvents(eg, ref needInput, ref haveOutput);
            if (sw.ElapsedMilliseconds > 500) return Array.Empty<byte>();
            Thread.Sleep(0);
        }

        // 3. GPU 色彩转换 BGRA → NV12
        _videoContext!.VideoProcessorBlt(_vp!, _outView!, 0, 1, new[]
        {
            new VideoProcessorStream { Enable = true, InputSurface = _inView! },
        });

        // 4. 包 NV12 纹理为 MF sample 并入队（时间戳非 0：部分 MFT 把 0 当未设置）
        // ⚠️ static readonly 字段不能作 ref 实参（CS0199），先拷到局部变量
        var iidTex = IID_ID3D11Texture2D;
        int hrBuf = MFInterop.MFCreateDXGISurfaceBuffer(ref iidTex, _nv12Tex!.NativePointer, 0, 0, out var mfbPtr);
        if (MFHr.Succeeded(hrBuf) == false) throw new COMException($"MFCreateDXGISurfaceBuffer failed: 0x{hrBuf:X8}");
        var mfb = MFInterop.GetObject<IMFMediaBuffer>(mfbPtr);
        MFInterop.MFCreateSample(out var sampPtr);
        var samp = MFInterop.GetObject<IMFSample>(sampPtr);
        try
        {
            samp.AddBuffer(mfb);
            _sampleTime += _sampleDuration;
            samp.SetSampleTime(_sampleTime);
            samp.SetSampleDuration(_sampleDuration);
            int hrPi = _encoder!.ProcessInput(0, samp, 0);
            if (MFHr.Succeeded(hrPi) == false)
            {
                if (hrPi == MFHr.MF_E_NOTACCEPTING)
                    return DrainOutput(); // 先拉输出，本帧丢弃，下帧重试（与软件编码器同策略）
                throw new COMException($"ProcessInput failed: 0x{hrPi:X8}");
            }
        }
        finally
        {
            Marshal.ReleaseComObject(samp);
            Marshal.ReleaseComObject(mfb);
        }

        // 5. 等 HaveOutput 并拉取输出
        sw.Restart();
        while (!haveOutput)
        {
            PumpEvents(eg, ref needInput, ref haveOutput);
            if (sw.ElapsedMilliseconds > 500) break; // 编码可能慢一拍，返回已得数据
            Thread.Sleep(0);
        }
        return DrainOutput();
    }

    /// <summary>冲刷编码器缓冲，返回剩余编码数据（会话结束前调用）。</summary>
    public byte[] Flush()
    {
        if (!_initialized) return Array.Empty<byte>();
        return DrainOutput();
    }

    /// <summary>
    /// 强制下一帧输出为 IDR 关键帧（与软件编码器同款动态属性）。
    /// 实测（tmp-hwtest，RX 5500 XT）：SetUINT32 返回 True，但 AMD 硬件管线对
    /// force 请求有内部延迟——force@frame20 → IDR@frame31（~11 帧 ≈733ms @15fps；
    /// 探针 hw2 中 force@30 → IDR@31）。生效但不是"下一帧"，落在 IDR 看门狗
    /// 1500ms 窗口内不会误触发重建；个别机器更慢时看门狗重建编码器兜底。
    /// </summary>
    public bool ForceKeyFrame()
    {
        if (!_initialized || _encAttrs == null) return false;
        try
        {
            var key = MFInterop.CODECAPI_AVEncVideoForceKeyFrame;
            return MFHr.Succeeded(_encAttrs.SetUINT32(ref key, 1));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>非阻塞事件泵（⚠️ GetEvent(0) 是阻塞模式会永久挂起，必须 NO_WAIT）。</summary>
    private static void PumpEvents(IMFMediaEventGenerator eg, ref bool needInput, ref bool haveOutput)
    {
        while (true)
        {
            int hr = eg.GetEvent(MFInterop.MF_EVENT_FLAG_NO_WAIT, out var evPtr);
            if (hr < 0) break; // 含 MF_E_NO_EVENTS_AVAILABLE（队列空）
            var ev = MFInterop.GetObject<IMFMediaEventView>(evPtr);
            ev.GetType_(out var met);
            if (met == MFInterop.ME_TRANSFORM_NEED_INPUT) needInput = true;
            else if (met == MFInterop.ME_TRANSFORM_HAVE_OUTPUT) haveOutput = true;
            Marshal.ReleaseComObject(ev);
        }
    }

    private byte[] DrainOutput()
    {
        using var result = new MemoryStream();
        var encoder = _encoder!;
        while (true)
        {
            var dataBuf = new MFT_OUTPUT_DATA_BUFFER { dwStreamID = 0, pSample = IntPtr.Zero };
            if (_providesSamples == false)
            {
                // MFT 不提供 sample：调用方分配（与软件编码器 DrainOutput 同）
                MFInterop.MFCreateSample(out var osPtr);
                var os = MFInterop.GetObject<IMFSample>(osPtr);
                MFInterop.MFCreateMemoryBuffer(_outputBufferSize, out var obPtr);
                os.AddBuffer(MFInterop.GetObject<IMFMediaBuffer>(obPtr));
                dataBuf.pSample = Marshal.GetIUnknownForObject(os);
                Marshal.ReleaseComObject(os);
                Marshal.Release(obPtr);
            }

            int hr = encoder.ProcessOutput(0, 1, ref dataBuf, out _);
            if (MFHr.Succeeded(hr) == false)
            {
                if (dataBuf.pSample != IntPtr.Zero) Marshal.Release(dataBuf.pSample);
                break; // NEED_MORE_INPUT / 无更多输出
            }
            if (dataBuf.pSample == IntPtr.Zero) continue;

            var os2 = (IMFSample)Marshal.GetObjectForIUnknown(dataBuf.pSample);
            try
            {
                if (MFHr.Succeeded(os2.GetBufferByIndex(0, out var ob2)))
                {
                    try
                    {
                        ob2.Lock(out var p, out _, out var len);
                        if (len > 0)
                        {
                            var data = new byte[len];
                            Marshal.Copy(p, data, 0, len);
                            result.Write(data, 0, data.Length);
                        }
                        ob2.Unlock();
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(ob2);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(os2);
                Marshal.Release(dataBuf.pSample);
            }
        }
        return result.ToArray();
    }

    // ============ 辅助（与 H264Encoder 同款） ============

    private static IMFMediaType CreateMediaType()
    {
        int hr = MFInterop.MFCreateMediaType(out var ptr);
        if (MFHr.Succeeded(hr) == false) throw new COMException($"CreateMediaType failed: 0x{hr:X8}");
        return MFInterop.GetObject<IMFMediaType>(ptr);
    }

    private static void SetGuid(IMFMediaType attrs, Guid key, Guid value)
    {
        int hr = attrs.SetGUID(ref key, ref value);
        if (MFHr.Succeeded(hr) == false) throw new COMException($"SetGUID failed: 0x{hr:X8}");
    }

    private static void SetUInt(IMFMediaType attrs, Guid key, uint value)
    {
        int hr = attrs.SetUINT32(ref key, value);
        if (MFHr.Succeeded(hr) == false) throw new COMException($"SetUINT32 failed: 0x{hr:X8}");
    }

    private static void SetU64(IMFMediaType attrs, Guid key, ulong value)
    {
        int hr = attrs.SetUINT64(ref key, value);
        if (MFHr.Succeeded(hr) == false) throw new COMException($"SetUINT64 failed: 0x{hr:X8}");
    }

    private static ulong MakeFrameSize(int width, int height) => ((ulong)(uint)width << 32) | (uint)height;
    private static ulong MakeFrameRate(int fps) => ((ulong)(uint)fps << 32) | 1;

    // ============ 清理 ============

    private void CleanupMf()
    {
        if (_encAttrs != null) { try { Marshal.ReleaseComObject(_encAttrs); } catch { } _encAttrs = null; }
        if (_encoder != null)
        {
            try { _encoder.ProcessMessage(MFHr.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero); } catch { }
            try { Marshal.ReleaseComObject(_encoder); } catch { }
            _encoder = null;
        }
        if (_mgrPtr != IntPtr.Zero) { Marshal.Release(_mgrPtr); _mgrPtr = IntPtr.Zero; }
    }

    private void CleanupAll()
    {
        CleanupMf();
        _inView?.Dispose(); _inView = null;
        _outView?.Dispose(); _outView = null;
        _bgraTex?.Dispose(); _bgraTex = null;
        _nv12Tex?.Dispose(); _nv12Tex = null;
        _vp?.Dispose(); _vp = null;
        _vpEnum?.Dispose(); _vpEnum = null;
        _videoContext?.Dispose(); _videoContext = null;
        _videoDevice?.Dispose(); _videoDevice = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CleanupAll();
    }
}
