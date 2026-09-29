using System.Runtime.InteropServices;
using QuickRemote.PCClient.Services;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

// ============================================================================
// GPU 零拷贝管线探针 v3（llprobe hw2）：
//   相对 v2 的改动（针对 MF_E_TRANSFORM_ASYNC_LOCKED 0xC00D6D77 之谜）：
//   ① dump activate / 主属性 / 流属性三个 store 的全部键值（PROPVARIANT）
//   ② 读 MFT_ENUM_ADAPTER_LUID {1D39518C-...}，在与 MFT 绑定相同的 GPU 适配器上
//      创建 D3D11 设备（v2 用 null=默认适配器，混合显卡下可能不匹配）
//   ③ 流属性 store 也设 MF_TRANSFORM_ASYNC_UNLOCK
//   ④ SET_D3D_MANAGER 失败后不中断，在类型设置后重试，观察状态机
//   合成 BGRA 纹理 → ID3D11VideoProcessorBlt 转 NV12 → MFCreateDXGISurfaceBuffer
//   喂 AMD 硬件编码器 → 事件协议收 H.264 → MS 解码器端到端验证
// ============================================================================

public static class HwGpuProbe
{
    // —— MF interop 增补 ——
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateDXGIDeviceManager(out int pResetToken, out IntPtr ppManager);

    // ⚠️ MFCreateDXGISurfaceBuffer 导出自 mfplat.dll（非 mfapi.dll，曾误写导致 DllNotFoundException）
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateDXGISurfaceBuffer(ref Guid riid, IntPtr pUnkSurface,
        uint uSubresourceIndex, int fBottomUpWhenLinear, out IntPtr ppBuffer);

    private const int MFT_MESSAGE_SET_D3D_MANAGER = 0x2; // SDK mftransform.h 核实

    /// <summary>MFT_ENUM_ADAPTER_LUID（mfapi.h RS1+ 核实）：MFTEnumEx 写在 activate 上的绑定适配器 LUID（VT_UI8）。</summary>
    static readonly Guid MFT_ENUM_ADAPTER_LUID = new("1d39518c-e220-4da8-a07f-ba172552d6b1");
    /// <summary>MFT_ENUM FriendlyName（mfapi.h 核实，VT_LPWSTR）。</summary>
    static readonly Guid MFT_FRIENDLY_NAME = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    // IMFDXGIDeviceManager：eb533d5d-2db6-40f8-97a9-494692014f07（mfobjects.h 核实）
    // vtable（按 mfobjects.h 声明序）：Close, GetVideoService, Lock, Open, Reset, Test, Unlock
    [ComImport]
    [Guid("eb533d5d-2db6-40f8-97a9-494692014f07")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFDXGIDeviceManager
    {
        [PreserveSig] int CloseDeviceHandle_(IntPtr h);
        [PreserveSig] int GetVideoService_(IntPtr h, ref Guid riid, out IntPtr pp);
        [PreserveSig] int LockDevice_(IntPtr h, ref Guid riid, out IntPtr pp, int fBlock);
        [PreserveSig] int OpenDeviceHandle_(out IntPtr h);
        [PreserveSig] int ResetDevice(IntPtr pUnkDevice, uint resetToken);
        [PreserveSig] int TestDevice_(IntPtr h);
        [PreserveSig] int UnlockDevice_(IntPtr h);
    }

    const int W = 2560, H = 1440;
    static ID3D11Device? _dev;
    static ID3D11DeviceContext? _ctx;
    static ID3D11VideoDevice? _videoDev;
    static ID3D11VideoContext? _videoCtx;
    static ID3D11VideoProcessor? _vp;
    static ID3D11VideoProcessorEnumerator? _vpe;

    public static int Run()
    {
        MfPlatform.EnsureStarted();

        // ===== 枚举硬件编码器并跑管线 =====
        var catEncoder = new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a");
        var inType = new MFT_REGISTER_TYPE_INFO { guidMajorType = MFInterop.MFMediaType_Video, guidSubtype = MFInterop.MFVideoFormat_NV12 };
        var outType = new MFT_REGISTER_TYPE_INFO { guidMajorType = MFInterop.MFMediaType_Video, guidSubtype = MFInterop.MFVideoFormat_H264 };
        IntPtr inPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_REGISTER_TYPE_INFO>());
        IntPtr outPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_REGISTER_TYPE_INFO>());
        Marshal.StructureToPtr(inType, inPtr, false);
        Marshal.StructureToPtr(outType, outPtr, false);
        int hr = MFInterop.MFTEnumEx(catEncoder, MFInterop.MFT_ENUM_FLAG_HARDWARE, inPtr, outPtr, out var activates, out int count);
        Marshal.FreeHGlobal(inPtr); Marshal.FreeHGlobal(outPtr);
        if (count == 0) { Console.WriteLine("[enum] 无硬件编码器"); return 1; }

        for (int cand = 0; cand < count; cand++)
        {
            try
            {
                if (TryGpuEncode(cand, Marshal.ReadIntPtr(activates, cand * IntPtr.Size))) return 0;
            }
            catch (Exception ex) { Console.WriteLine($"  候选 {cand} 异常: {ex.Message}"); }
        }
        return 1;
    }

    // ================= 属性 store dump =================

    static string FmtPv(PROPVARIANT v)
    {
        // VARENUM 子集（MF 属性只用到这些）
        const ushort VT_EMPTY = 0, VT_I4 = 3, VT_R8 = 5, VT_BOOL = 11,
            VT_UI4 = 19, VT_I8 = 20, VT_UI8 = 21, VT_LPWSTR = 31, VT_CLSID = 72,
            VT_VECTOR = 0x1000;
        if ((v.vt & VT_VECTOR) != 0) return $"(vector vt=0x{v.vt:X})";
        switch (v.vt)
        {
            case VT_EMPTY: return "(空)";
            case VT_I4: return ((int)v.p0.ToInt64()).ToString();
            case VT_UI4: return ((uint)v.p0.ToInt64()).ToString();
            case VT_BOOL: return (v.p0.ToInt64() & 0xFFFF) != 0 ? "true" : "false";
            case VT_I8: return v.p0.ToInt64().ToString();
            case VT_UI8: return $"0x{v.p0.ToInt64():X16}";
            case VT_R8: return BitConverter.Int64BitsToDouble(v.p0.ToInt64()).ToString();
            case VT_LPWSTR: return Marshal.PtrToStringUni(v.p0) ?? "(null)";
            case VT_CLSID:
                var b = new byte[16];
                Marshal.Copy(v.p0, b, 0, 16);
                return new Guid(b).ToString();
            default: return $"(vt={v.vt})";
        }
    }

    static void DumpAttrs(IMFAttributes a, string label)
    {
        if (a == null || a.GetCount(out int n) < 0) { Console.WriteLine($"[{label}] <不可用>"); return; }
        DumpCore(n, i => a.GetItemByIndex(i, out var k, out var v) >= 0 ? (k, v) : default, label);
    }

    /// <summary>IMFActivate 拍平接口不继承 IMFAttributes，单独走一份（槽位相同，逻辑一致）。</summary>
    static void DumpAttrs(IMFActivate a, string label)
    {
        if (a.GetCount(out int n) < 0) { Console.WriteLine($"[{label}] <不可用>"); return; }
        DumpCore(n, i => a.GetItemByIndex(i, out var k, out var v) >= 0 ? (k, v) : default, label);
    }

    static void DumpCore(int n, Func<int, (Guid key, PROPVARIANT val)> get, string label)
    {
        Console.WriteLine($"[{label}] {n} 项:");
        for (int i = 0; i < n; i++)
        {
            var (k, v) = get(i);
            Console.WriteLine($"    {k} = {FmtPv(v)}");
        }
    }

    // ================= D3D 设备（按 LUID 匹配适配器） =================

    static bool CreateDeviceForMft(ulong mftLuid, string candLabel)
    {
        IDXGIAdapter1? match = null;
        string matchName = "";
        var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        int i = 0;
        while (factory.EnumAdapters1(i, out var ad).Success)
        {
            var d = ad.Description1;
            // MF 的 LUID 打包约定：HighPart<<32 | LowPart（另外算一个反向兜底）
            ulong luidA = ((ulong)(uint)d.Luid.HighPart << 32) | d.Luid.LowPart;
            ulong luidB = ((ulong)d.Luid.LowPart << 32) | (uint)d.Luid.HighPart;
            Console.WriteLine($"[dxgi] 适配器{i}: {d.Description} LUID={luidA:X016} flags={d.Flags}");
            if (match == null && (d.Flags & AdapterFlags.Software) == 0 && (luidA == mftLuid || luidB == mftLuid))
            {
                match = ad;
                matchName = d.Description;
            }
            i++;
        }
        if (match == null)
        {
            Console.WriteLine($"[{candLabel}] 未找到 LUID=0x{mftLuid:X016} 的适配器，回退默认适配器");
            var res0 = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null, out _dev, out _ctx);
            Console.WriteLine($"[d3d] 默认设备 hr=0x{res0.Code:X8} ok={res0.Success}");
            return res0.Success;
        }
        Console.WriteLine($"[{candLabel}] MFT 绑定适配器: {matchName}");
        var res = D3D11.D3D11CreateDevice(match, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null, out _dev, out _ctx);
        Console.WriteLine($"[d3d] 指定适配器设备 hr=0x{res.Code:X8} ok={res.Success}");
        return res.Success;
    }

    // ================= 主流程 =================

    static bool TryGpuEncode(int cand, IntPtr actPtr)
    {
        Console.WriteLine($"\n===== GPU 候选 {cand} =====");
        var act = MFInterop.GetObject<IMFActivate>(actPtr);

        // ---- activate store dump：友好名 + 绑定适配器 LUID ----
        DumpAttrs(act, "activate.store");
        var luidKey = MFT_ENUM_ADAPTER_LUID;
        ulong mftLuid = 0;
        bool hasLuid = act.GetUINT64(ref luidKey, out mftLuid) >= 0;
        Console.WriteLine($"  绑定适配器 LUID: {(hasLuid ? $"0x{mftLuid:X016}" : "<无>")}");

        if (!CreateDeviceForMft(hasLuid ? mftLuid : 0, $"候选{cand}")) return false;
        _videoDev = _dev!.QueryInterface<ID3D11VideoDevice>();
        _videoCtx = _ctx!.QueryInterface<ID3D11VideoContext>();

        // ---- 激活 MFT ----
        var iidT = MFInterop.IID_IMFTransform;
        act.ActivateObject(ref iidT, out IntPtr mftPtr);
        var mft = MFInterop.GetObject<IMFTransform>(mftPtr);

        mft.GetAttributes(out var attrs);
        DumpAttrs(attrs, "main.attrs");
        var asyncKey = HwProbe.MF_TRANSFORM_ASYNC;
        attrs!.GetUINT32_(ref asyncKey, out uint isAsync);
        var unlockKey = HwProbe.MF_TRANSFORM_ASYNC_UNLOCK;
        int hrUnlock = attrs.SetUINT32(ref unlockKey, 1);
        attrs.GetUINT32_(ref unlockKey, out uint unlockedBack);
        Console.WriteLine($"  ASYNC={isAsync}, unlock hr=0x{hrUnlock:X8}, readback={unlockedBack}");
        var gopKey = MFInterop.CODECAPI_AVEncMPVGOPSize;
        attrs.SetUINT32(ref gopKey, 45);
        var avllKey = MFInterop.CODECAPI_AVLowLatencyMode;
        attrs.SetUINT32(ref avllKey, 1);

        // ---- 流属性 store：dump + 也设 unlock ----
        int hrInAttrs = mft.GetInputStreamAttributes(0, out var inAttrs);
        if (hrInAttrs >= 0)
        {
            DumpAttrs(inAttrs, "input.stream.attrs");
            inAttrs!.SetUINT32(ref unlockKey, 1);
        }
        else Console.WriteLine($"  GetInputStreamAttributes hr=0x{hrInAttrs:X8}");
        int hrOutAttrs = mft.GetOutputStreamAttributes(0, out var outAttrs);
        if (hrOutAttrs >= 0)
        {
            DumpAttrs(outAttrs, "output.stream.attrs");
            outAttrs!.SetUINT32(ref unlockKey, 1);
        }
        else Console.WriteLine($"  GetOutputStreamAttributes hr=0x{hrOutAttrs:X8}");

        // ---- DXGI device manager（设备已按 MFT 绑定适配器创建）----
        MFCreateDXGIDeviceManager(out int resetToken, out IntPtr mgrPtr);
        var mgr = (IMFDXGIDeviceManager)Marshal.GetObjectForIUnknown(mgrPtr);
        int hrReset = mgr.ResetDevice(_dev!.NativePointer, (uint)resetToken);
        Console.WriteLine($"[mf-dxgi] ResetDevice hr=0x{hrReset:X8}");

        // ---- 尝试 1：SET_D3D_MANAGER 先于类型 ----
        int hrMgrA = mft.ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, mgrPtr);
        Console.WriteLine($"[mf-dxgi] SET_D3D_MANAGER(类型前) hr=0x{hrMgrA:X8}");

        // ---- 输出类型 H264 ----
        var outM = CreateMediaType();
        SetG(outM, MFInterop.MF_MT_MAJOR_TYPE, MFInterop.MFMediaType_Video);
        SetG(outM, MFInterop.MF_MT_SUBTYPE, MFInterop.MFVideoFormat_H264);
        Set64(outM, MFInterop.MF_MT_FRAME_SIZE, ((ulong)(uint)W << 32) | (uint)H);
        Set64(outM, MFInterop.MF_MT_FRAME_RATE, ((ulong)(uint)15 << 32) | 1);
        Set32(outM, MFInterop.MF_MT_AVG_BITRATE, 16_000_000);
        int hrOut = mft.SetOutputType(0, outM, 0);
        Console.WriteLine($"[type] SetOutputType hr=0x{hrOut:X8}");

        // ---- 输入可用类型（MFT 视角，先枚举再决定）----
        Console.WriteLine("  ---- 输入可用类型 ----");
        for (int t = 0; t < 12 && mft.GetInputAvailableType(0, t, out var inAvail) >= 0; t++)
        {
            var subKey = MFInterop.MF_MT_SUBTYPE;
            if (inAvail.GetGUID(ref subKey, out var sub) >= 0)
                Console.WriteLine($"    输入[{t}]: {sub}");
        }

        // ---- 尝试 2：类型后重试 SET_D3D_MANAGER ----
        if (hrMgrA < 0)
        {
            int hrMgrB = mft.ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, mgrPtr);
            Console.WriteLine($"[mf-dxgi] SET_D3D_MANAGER(输出类型后重试) hr=0x{hrMgrB:X8}");
        }

        // ---- 输入类型 NV12（优先 MFT 自报的可用类型，兜底手工类型）----
        int hrIn = mft.SetInputType(0, inAvailType(mft), 0);
        if (hrIn < 0)
        {
            var inM = CreateMediaType();
            SetG(inM, MFInterop.MF_MT_MAJOR_TYPE, MFInterop.MFMediaType_Video);
            SetG(inM, MFInterop.MF_MT_SUBTYPE, MFInterop.MFVideoFormat_NV12);
            Set64(inM, MFInterop.MF_MT_FRAME_SIZE, ((ulong)(uint)W << 32) | (uint)H);
            Set64(inM, MFInterop.MF_MT_FRAME_RATE, ((ulong)(uint)15 << 32) | 1);
            hrIn = mft.SetInputType(0, inM, 0);
        }
        Console.WriteLine($"[type] SetInputType hr=0x{hrIn:X8}");
        if (hrIn < 0) return false;

        // ---- 尝试 3：输入类型后再试 SET_D3D_MANAGER ----
        if (hrMgrA < 0)
        {
            int hrMgrC = mft.ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, mgrPtr);
            Console.WriteLine($"[mf-dxgi] SET_D3D_MANAGER(输入类型后重试) hr=0x{hrMgrC:X8}");
        }

        // ---- VideoProcessor：BGRA → NV12 ----
        // 全零 ContentDescription 会 E_INVALIDARG，填真实输入输出描述
        _vpe = _videoDev!.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputWidth = W, InputHeight = H,
            OutputWidth = W, OutputHeight = H,
            InputFrameRate = new Rational(15, 1),
            OutputFrameRate = new Rational(15, 1),
        });
        _vp = _videoDev.CreateVideoProcessor(_vpe, 0);
        var bgraDesc = new Texture2DDescription
        {
            Width = W, Height = H, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        var bgraTex = _dev.CreateTexture2D(bgraDesc);
        var inView = _videoDev.CreateVideoProcessorInputView(bgraTex, _vpe, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });

        var nv12Desc = new Texture2DDescription
        {
            Width = W, Height = H, MipLevels = 1, ArraySize = 1,
            Format = Format.NV12, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        var nv12Tex = _dev.CreateTexture2D(nv12Desc);
        var outView = _videoDev.CreateVideoProcessorOutputView(nv12Tex, _vpe, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        });
        Console.WriteLine("[vp] VideoProcessor 就绪");

        // 模拟桌面内容：CPU 写入 BGRA 纹理（Product 中来自 DXGI duplication）
        var bgraData = MakeBgra(0);

        // ---- 流启动 + 事件协议 ----
        var eg = (HwProbe.IMFMediaEventGenerator)mft;
        mft.ProcessMessage(MFHr.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
        mft.ProcessMessage(MFHr.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
        mft.GetOutputStreamInfo(0, out var outInfo);
        bool provides = (outInfo.dwFlags & HwProbe.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;

        bool needInput = false, haveOutput = false;
        long sampleTime = 0; const long dur = 10_000_000L / 15;
        var chunks = new List<byte[]>();
        var lat = new List<long>();
        bool sawIdrAfterForce = false; const int forceAt = 30;
        var iidTex = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"); // IID_ID3D11Texture2D

        for (int i = 1; i <= 60; i++)
        {
            unsafe
            {
                var upd = i == 1 ? bgraData : MakeBgra(i);
                fixed (byte* p = upd)
                    _ctx!.UpdateSubresource(bgraTex, 0, null, (IntPtr)p, W * 4, 0);
            }
            if (i == forceAt)
            {
                var fk = MFInterop.CODECAPI_AVEncVideoForceKeyFrame;
                attrs.SetUINT32(ref fk, 1);
            }

            var fsw = System.Diagnostics.Stopwatch.StartNew();
            while (!needInput)
            {
                Pump(eg, ref needInput, ref haveOutput);
                if (fsw.ElapsedMilliseconds > 500) { Console.WriteLine($"[frame {i}] 等 NeedInput 超时"); return false; }
                Thread.Sleep(1);
            }
            needInput = false;

            // GPU 转色：BGRA → NV12
            _videoCtx!.VideoProcessorBlt(_vp, outView, 0, 1, new[]
            {
                new VideoProcessorStream { Enable = true, InputSurface = inView },
            });

            // 包 NV12 纹理为 MF sample
            int hrBuf = MFCreateDXGISurfaceBuffer(ref iidTex, nv12Tex.NativePointer, 0, 0, out var mfbPtr);
            if (hrBuf < 0) { Console.WriteLine($"[frame {i}] MFCreateDXGISurfaceBuffer hr=0x{hrBuf:X8}"); return false; }
            var mfb = MFInterop.GetObject<IMFMediaBuffer>(mfbPtr);
            MFInterop.MFCreateSample(out var sampPtr);
            var samp = MFInterop.GetObject<IMFSample>(sampPtr);
            samp.AddBuffer(mfb);
            sampleTime += dur;
            samp.SetSampleTime(sampleTime);
            samp.SetSampleDuration(dur);
            int hrPi = mft.ProcessInput(0, samp, 0);
            Marshal.ReleaseComObject(samp);
            Marshal.ReleaseComObject(mfb);
            if (hrPi < 0) { Console.WriteLine($"[frame {i}] ProcessInput hr=0x{hrPi:X8}"); return false; }

            while (!haveOutput)
            {
                Pump(eg, ref needInput, ref haveOutput);
                if (fsw.ElapsedMilliseconds > 500) break;
                Thread.Sleep(1);
            }
            if (!haveOutput) continue;
            haveOutput = false;

            while (true)
            {
                var dataBuf = new MFT_OUTPUT_DATA_BUFFER { dwStreamID = 0, pSample = IntPtr.Zero };
                if (provides == false)
                {
                    MFInterop.MFCreateSample(out var osPtr);
                    var os = MFInterop.GetObject<IMFSample>(osPtr);
                    MFInterop.MFCreateMemoryBuffer(Math.Max(outInfo.cbSize, 4 * 1024 * 1024), out var obPtr);
                    os.AddBuffer(MFInterop.GetObject<IMFMediaBuffer>(obPtr));
                    dataBuf.pSample = Marshal.GetIUnknownForObject(os);
                    Marshal.ReleaseComObject(os);
                    Marshal.Release(obPtr);
                }
                int hrPo = mft.ProcessOutput(0, 1, ref dataBuf, out _);
                if (hrPo < 0) break;
                if (dataBuf.pSample != IntPtr.Zero)
                {
                    var os2 = (IMFSample)Marshal.GetObjectForIUnknown(dataBuf.pSample);
                    os2.GetBufferByIndex(0, out var ob2);
                    ob2.Lock(out var p, out _, out var len);
                    if (len > 0)
                    {
                        var arr = new byte[len];
                        Marshal.Copy(p, arr, 0, len);
                        chunks.Add(arr);
                        lat.Add(fsw.ElapsedMilliseconds);
                        bool idr = H264Encoder.ContainsIdrFrame(arr);
                        if (i >= forceAt && idr) sawIdrAfterForce = true;
                        if (i <= 3 || idr)
                            Console.WriteLine($"  [frame {i,2}] {arr.Length,7}B idr={idr} NAL[{DescribeNals(arr)}] {fsw.ElapsedMilliseconds}ms");
                    }
                    ob2.Unlock();
                    Marshal.ReleaseComObject(ob2);
                    Marshal.ReleaseComObject(os2);
                    Marshal.Release(dataBuf.pSample);
                }
            }
        }

        Console.WriteLine($"  输出 {chunks.Count} 块, 延迟 min/avg/max = {lat.Min()}/{(long)lat.Average()}/{lat.Max()}ms");
        Console.WriteLine($"  ForceKeyFrame 后出过 IDR: {sawIdrAfterForce}");

        var dec = new H264Decoder();
        dec.Initialize(W, H);
        int decoded = 0, nonBlack = 0;
        foreach (var c in chunks)
        {
            var df = dec.Decode(c);
            if (df != null)
            {
                decoded++;
                long sum = 0; var px = df.Bgra;
                for (int p2 = 0; p2 < px.Length; p2 += 4 * 997) sum += px[p2 + 1];
                if (sum >= 40 * (px.Length / (4 * 997))) nonBlack++;
            }
        }
        Console.WriteLine($"  >>> 解码验证: {chunks.Count} 喂入, 解出 {decoded}, 非黑 {nonBlack}");
        dec.Dispose();
        return decoded > 0 && nonBlack > 0;
    }

    /// <summary>取 MFT 自报的第一个输入可用类型（保持 MFT 期望的属性组合），失败返回 null。</summary>
    static IMFMediaType? inAvailType(IMFTransform mft)
        => mft.GetInputAvailableType(0, 0, out var t) >= 0 ? t : null;

    static byte[] MakeBgra(int tick)
    {
        var f = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int o = (y * W + x) * 4;
                f[o] = 30; f[o + 1] = 32; f[o + 2] = 38; f[o + 3] = 255;
            }
        void Rect(int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y < y1 && y < H; y++)
                for (int x = x0; x < x1 && x < W; x++)
                {
                    int o = (y * W + x) * 4;
                    f[o] = 200; f[o + 1] = 210; f[o + 2] = 230; f[o + 3] = 255;
                }
        }
        Rect(100 + (tick * 7) % 800, 100, 700 + (tick * 7) % 800, 600);
        return f;
    }

    static void Pump(HwProbe.IMFMediaEventGenerator eg, ref bool needInput, ref bool haveOutput)
    {
        // ⚠️ GetEvent(dwFlags=0) 是阻塞模式：队列空时永久挂起。必须用
        // MF_EVENT_FLAG_NO_WAIT(1) 非阻塞轮询，空队列返回 MF_E_NO_EVENTS_AVAILABLE。
        while (true)
        {
            int hr = eg.GetEvent(1, out var evPtr);
            if (hr < 0) break; // 含 0xC00D3E80 无事件
            var ev = MFInterop.GetObject<HwProbe.IMFMediaEventView>(evPtr);
            ev.GetType_(out var met);
            if (met == HwProbe.METransformNeedInput) needInput = true;
            else if (met == HwProbe.METransformHaveOutput) haveOutput = true;
            Marshal.ReleaseComObject(ev);
        }
    }

    static string DescribeNals(byte[] d)
    {
        var types = new List<byte>();
        for (int i = 2; i < d.Length - 1; i++)
            if (d[i] == 1 && d[i - 1] == 0 && d[i - 2] == 0)
            {
                types.Add((byte)(d[i + 1] & 0x1F));
                if (types.Count >= 20) break;
            }
        return string.Join(",", types);
    }

    static IMFMediaType CreateMediaType()
    {
        MFInterop.MFCreateMediaType(out var ptr);
        return MFInterop.GetObject<IMFMediaType>(ptr);
    }
    static void SetG(IMFMediaType t, Guid k, Guid v) => t.SetGUID(ref k, ref v);
    static void Set32(IMFMediaType t, Guid k, uint v) => t.SetUINT32(ref k, v);
    static void Set64(IMFMediaType t, Guid k, ulong v) => t.SetUINT64(ref k, v);
}
