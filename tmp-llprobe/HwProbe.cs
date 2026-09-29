using System.Runtime.InteropServices;
using QuickRemote.PCClient.Services;

// ============================================================================
// 硬件编码探针（llprobe hw）：
// MFTEnumEx(HARDWARE) 激活 AMDh264Encoder 等异步硬件 MFT，按事件协议编码
// 2560x1440@15fps 60 帧，验证：
//   ① 系统内存 NV12 输入是否可接受（无 D3D manager）
//   ② 首个输出出现在第几个输入（AVLowLatencyMode 在硬件 MFT 上是否生效）
//   ③ ForceKeyFrame 动态属性是否有效（IDR 是否出现）
//   ④ 每帧编码耗时、输出 NAL 结构（slice 数）
//   ⑤ 产出流用 MS 解码器端到端验证
// ============================================================================

public static class HwProbe
{
    // ============ 异步 MFT 事件互操作（GUID 从本机 SDK 10.0.26100.0 核实） ============

    /// <summary>MF_TRANSFORM_ASYNC（mftransform.h SDK 核实）。</summary>
    public static readonly Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    /// <summary>MF_TRANSFORM_ASYNC_UNLOCK（mftransform.h SDK 核实）。⚠️ 此前误写
    /// da7db1f80e27（凭记忆），真值 da7db1f8e207——错键写入+读回自洽，造成"已解锁"假象，
    /// MFT 实际从未解锁，GetInputStreamAttributes/SET_D3D_MANAGER/SetInputType 全拒
    /// MF_E_TRANSFORM_ASYNC_LOCKED（0xC00D6D77）。v3 属性 dump 暴露此差异。</summary>
    public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");

    // MediaEventType：METransformUnknown=600（mfobjects.h 核实）
    public const uint METransformNeedInput = 601;
    public const uint METransformHaveOutput = 602;
    public const int MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80);
    public const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;

    [ComImport]
    [Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D")] // mfobjects.h MIDL_INTERFACE 核实
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaEventGenerator
    {
        // GetEvent(dwFlags=0) 非阻塞；无事件返回 MF_E_NO_EVENTS_AVAILABLE
        [PreserveSig] int GetEvent(uint dwFlags, out IntPtr ppEvent);
        [PreserveSig] int BeginGetEvent_(IntPtr cb, IntPtr state);
        [PreserveSig] int EndGetEvent_(IntPtr result, out IntPtr ppEvent);
        [PreserveSig] int QueueEvent_();
    }

    /// <summary>IMFMediaEvent = IUnknown(3) + IMFAttributes(30) + GetType/GetExtendedType/GetStatus/GetValue。
    /// IID = DF598932-F10C-4E39-BBA2-C308F101DAA3（mfobjects.h MIDL_INTERFACE 核实）。</summary>
    [ComImport]
    [Guid("DF598932-F10C-4E39-BBA2-C308F101DAA3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaEventView
    {
        // IMFAttributes 30 槽占位
        [PreserveSig] int A01_();
        [PreserveSig] int A02_();
        [PreserveSig] int A03_();
        [PreserveSig] int A04_();
        [PreserveSig] int A05_();
        [PreserveSig] int A06_();
        [PreserveSig] int A07_();
        [PreserveSig] int A08_();
        [PreserveSig] int A09_();
        [PreserveSig] int A10_();
        [PreserveSig] int A11_();
        [PreserveSig] int A12_();
        [PreserveSig] int A13_();
        [PreserveSig] int A14_();
        [PreserveSig] int A15_();
        [PreserveSig] int A16_();
        [PreserveSig] int A17_();
        [PreserveSig] int A18_();
        [PreserveSig] int A19_();
        [PreserveSig] int A20_();
        [PreserveSig] int A21_();
        [PreserveSig] int A22_();
        [PreserveSig] int A23_();
        [PreserveSig] int A24_();
        [PreserveSig] int A25_();
        [PreserveSig] int A26_();
        [PreserveSig] int A27_();
        [PreserveSig] int A28_();
        [PreserveSig] int A29_();
        [PreserveSig] int A30_();
        // IMFMediaEvent（mfobjects.h 顺序：GetType, GetExtendedType, GetStatus, GetValue）
        [PreserveSig] int GetType_(out uint met);
        [PreserveSig] int GetExtendedType_(out Guid g);
        [PreserveSig] int GetStatus_(out int hr);
        [PreserveSig] int GetValue_();
    }

    // ============ 场景 ============

    const int W = 2560, H = 1440;

    static byte[] MakeNv12(int tick)
    {
        // 简化：BGRA 合成帧后软件转 NV12（与产品 H264Encoder.ConvertBgraToNv12 同语义）
        var bgra = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int o = (y * W + x) * 4;
                bgra[o] = 30; bgra[o + 1] = 32; bgra[o + 2] = 38; bgra[o + 3] = 255;
            }
        void Rect(int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y < y1 && y < H; y++)
                for (int x = x0; x < x1 && x < W; x++)
                {
                    int o = (y * W + x) * 4;
                    bgra[o] = 200; bgra[o + 1] = 210; bgra[o + 2] = 230; bgra[o + 3] = 255;
                }
        }
        Rect(100 + (tick * 7) % 800, 100, 700 + (tick * 7) % 800, 600);
        var nv12 = new byte[W * H + (W / 2) * (H / 2) * 2];
        ConvertBgraToNv12(bgra, nv12, W, H);
        return nv12;
    }

    static unsafe void ConvertBgraToNv12(byte[] bgra, byte[] nv12, int w, int h)
    {
        fixed (byte* src = bgra, dst = nv12)
        {
            byte* yP = dst; byte* uvP = dst + w * h;
            for (int row = 0; row < h; row++)
                for (int col = 0; col < w; col++)
                {
                    int o = (row * w + col) * 4;
                    yP[row * w + col] = (byte)((bgra[o + 2] * 77 + bgra[o + 1] * 150 + bgra[o] * 29 + 128) >> 8);
                }
            for (int row = 0; row < h / 2; row++)
                for (int col = 0; col < w / 2; col++)
                {
                    int o = ((row * 2) * w + col * 2) * 4;
                    uvP[row * w + col * 2] = 128;     // U（灰底够用）
                    uvP[row * w + col * 2 + 1] = 128; // V
                }
        }
    }

    public static int Run()
    {
        MfPlatform.EnsureStarted();

        var catEncoder = new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a");
        var inType = new MFT_REGISTER_TYPE_INFO { guidMajorType = MFInterop.MFMediaType_Video, guidSubtype = MFInterop.MFVideoFormat_NV12 };
        var outType = new MFT_REGISTER_TYPE_INFO { guidMajorType = MFInterop.MFMediaType_Video, guidSubtype = MFInterop.MFVideoFormat_H264 };
        IntPtr inPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_REGISTER_TYPE_INFO>());
        IntPtr outPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_REGISTER_TYPE_INFO>());
        Marshal.StructureToPtr(inType, inPtr, false);
        Marshal.StructureToPtr(outType, outPtr, false);

        int hr = MFInterop.MFTEnumEx(catEncoder, MFInterop.MFT_ENUM_FLAG_HARDWARE, inPtr, outPtr, out var activates, out int count);
        Marshal.FreeHGlobal(inPtr);
        Marshal.FreeHGlobal(outPtr);
        Console.WriteLine($"MFTEnumEx(HARDWARE): hr=0x{hr:X8}, {count} 个候选");
        if (count == 0) return 1;

        for (int cand = 0; cand < count; cand++)
        {
            Console.WriteLine($"\n===== 候选 {cand} =====");
            try
            {
                if (TryEncode(cand, Marshal.ReadIntPtr(activates, cand * IntPtr.Size))) return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  失败: {ex.Message}");
            }
        }
        return 1;
    }

    static bool TryEncode(int cand, IntPtr actPtr)
    {
        var act = MFInterop.GetObject<IMFActivate>(actPtr);
        var iidT = MFInterop.IID_IMFTransform;
        if (act.ActivateObject(ref iidT, out IntPtr mftPtr) < 0)
        {
            Console.WriteLine("  ActivateObject 失败");
            return false;
        }
        var mft = MFInterop.GetObject<IMFTransform>(mftPtr);

        // 1. 属性：异步解锁 + GOP + 低延迟
        if (mft.GetAttributes(out var attrs) < 0 || attrs == null)
        {
            Console.WriteLine("  GetAttributes 失败"); return false;
        }
        var asyncKey = MF_TRANSFORM_ASYNC;
        attrs.GetUINT32_(ref asyncKey, out uint isAsync);
        Console.WriteLine($"  MF_TRANSFORM_ASYNC={isAsync}");
        var unlockKey = MF_TRANSFORM_ASYNC_UNLOCK;
        if (isAsync == 1 && attrs.SetUINT32(ref unlockKey, 1) < 0)
        {
            Console.WriteLine("  异步解锁失败"); return false;
        }
        var gopKey = MFInterop.CODECAPI_AVEncMPVGOPSize;
        attrs.SetUINT32(ref gopKey, 45);
        var avllKey = MFInterop.CODECAPI_AVLowLatencyMode;
        bool avll = attrs.SetUINT32(ref avllKey, 1) >= 0;
        var cllKey = MFInterop.CODECAPI_AVEncCommonLowLatency;
        bool cll = attrs.SetUINT32(ref cllKey, 1) >= 0;
        Console.WriteLine($"  AVLowLatencyMode={avll}, AVEncCommonLowLatency={cll}");

        // 2. 输出类型 H.264
        var outM = CreateMediaType();
        MFMediaTypeSet(outM, MFInterop.MF_MT_MAJOR_TYPE, MFInterop.MFMediaType_Video);
        MFMediaTypeSet(outM, MFInterop.MF_MT_SUBTYPE, MFInterop.MFVideoFormat_H264);
        MFMediaTypeSetU64(outM, MFInterop.MF_MT_FRAME_SIZE, ((ulong)(uint)W << 32) | (uint)H);
        MFMediaTypeSetU64(outM, MFInterop.MF_MT_FRAME_RATE, ((ulong)(uint)15 << 32) | 1);
        if (mft.SetOutputType(0, outM, 0) < 0)
        {
            Console.WriteLine("  SetOutputType(H264) 失败（尝试枚举可用类型）");
            for (int i = 0; i < 8 && mft.GetOutputAvailableType(0, i, out var avail) >= 0; i++)
            {
                var subKey = MFInterop.MF_MT_SUBTYPE;
                avail.GetGUID(ref subKey, out var sub);
                Console.WriteLine($"    可用输出[{i}]: {sub}");
            }
            return false;
        }

        // 3. 输入类型 NV12（先枚举 MFT 声明支持的输入子类型，便于诊断）
        Console.WriteLine("  ---- 输入可用类型 ----");
        for (int i = 0; i < 12 && mft.GetInputAvailableType(0, i, out var inAvail) >= 0; i++)
        {
            var subKey = MFInterop.MF_MT_SUBTYPE;
            if (inAvail.GetGUID(ref subKey, out var inSub) >= 0)
                Console.WriteLine($"    输入[{i}]: {inSub}");
        }
        var inM = CreateMediaType();
        MFMediaTypeSet(inM, MFInterop.MF_MT_MAJOR_TYPE, MFInterop.MFMediaType_Video);
        MFMediaTypeSet(inM, MFInterop.MF_MT_SUBTYPE, MFInterop.MFVideoFormat_NV12);
        MFMediaTypeSetU64(inM, MFInterop.MF_MT_FRAME_SIZE, ((ulong)(uint)W << 32) | (uint)H);
        MFMediaTypeSetU64(inM, MFInterop.MF_MT_FRAME_RATE, ((ulong)(uint)15 << 32) | 1);
        MFMediaTypeSet(inM, MFInterop.MF_MT_DEFAULT_STRIDE, (uint)W);
        if (mft.SetInputType(0, inM, 0) < 0)
        {
            Console.WriteLine("  SetInputType(NV12) 失败（系统内存输入可能不被接受）");
            return false;
        }

        // 4. 事件生成器 + 流启动
        var eg = (IMFMediaEventGenerator)mft;
        mft.ProcessMessage(MFHr.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
        mft.ProcessMessage(MFHr.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);

        mft.GetOutputStreamInfo(0, out var outInfo);
        bool provides = (outInfo.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
        Console.WriteLine($"  输出流: cbSize={outInfo.cbSize}, PROVIDES_SAMPLES={provides}");

        // 5. 编码 60 帧
        bool needInput = false, haveOutput = false;
        long sampleTime = 0;
        const long dur = 10_000_000L / 15;
        var chunks = new List<byte[]>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var latencies = new List<long>();
        bool sawIdrAfterForce = false;
        int forceAt = 30;

        for (int i = 1; i <= 60; i++)
        {
            var nv12 = MakeNv12(i);
            if (i == forceAt)
            {
                var fk = MFInterop.CODECAPI_AVEncVideoForceKeyFrame;
                bool ok = attrs.SetUINT32(ref fk, 1) >= 0;
                Console.WriteLine($"  [frame {i}] ForceKeyFrame 设置: {ok}");
            }

            // 等 NeedInput
            var fsw = System.Diagnostics.Stopwatch.StartNew();
            while (!needInput)
            {
                PumpEvents(eg, ref needInput, ref haveOutput);
                if (fsw.ElapsedMilliseconds > 500) { Console.WriteLine($"  [frame {i}] 等 NeedInput 超时"); return false; }
                Thread.Sleep(1);
            }
            needInput = false;

            // ProcessInput
            MFInterop.MFCreateMemoryBuffer(nv12.Length, out var bufPtr);
            var buf = MFInterop.GetObject<IMFMediaBuffer>(bufPtr);
            buf.Lock(out var dst, out _, out _);
            Marshal.Copy(nv12, 0, dst, nv12.Length);
            buf.Unlock();
            buf.SetCurrentLength(nv12.Length);
            MFInterop.MFCreateSample(out var sampPtr);
            var samp = MFInterop.GetObject<IMFSample>(sampPtr);
            samp.AddBuffer(buf);
            sampleTime += dur;
            samp.SetSampleTime(sampleTime);
            samp.SetSampleDuration(dur);
            int hrIn = mft.ProcessInput(0, samp, 0);
            Marshal.ReleaseComObject(samp);
            Marshal.ReleaseComObject(buf);
            if (hrIn < 0)
            {
                Console.WriteLine($"  [frame {i}] ProcessInput 失败 0x{hrIn:X8}");
                return false;
            }

            // 等 HaveOutput
            while (!haveOutput)
            {
                PumpEvents(eg, ref needInput, ref haveOutput);
                if (fsw.ElapsedMilliseconds > 500) break; // 有些帧可能无输出（GOP 前段），继续喂
                Thread.Sleep(1);
            }
            if (!haveOutput) continue;

            // ProcessOutput（可能多个）
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
                int hrOut = mft.ProcessOutput(0, 1, ref dataBuf, out _);
                if (hrOut < 0) break;
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
                        latencies.Add(fsw.ElapsedMilliseconds);
                        bool idr = H264Encoder.ContainsIdrFrame(arr);
                        if (i >= forceAt && idr) sawIdrAfterForce = true;
                        if (i <= 3 || idr)
                            Console.WriteLine($"  [frame {i,2}] 输出 {arr.Length,7}B idr={idr} NAL[{DescribeNals(arr)}] 延迟 {fsw.ElapsedMilliseconds}ms");
                    }
                    ob2.Unlock();
                    Marshal.ReleaseComObject(ob2);
                    Marshal.ReleaseComObject(os2);
                    Marshal.Release(dataBuf.pSample);
                }
            }
        }

        Console.WriteLine($"  总输出 {chunks.Count} 块, 编码延迟 min/avg/max = {latencies.Min()}/{(long)latencies.Average()}/{latencies.Max()} ms");
        Console.WriteLine($"  ForceKeyFrame(frame {forceAt}) 后出过 IDR: {sawIdrAfterForce}");

        // 6. 解码端到端验证
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
                for (int p = 0; p < px.Length; p += 4 * 997) sum += px[p + 1];
                if (sum >= 40 * (px.Length / (4 * 997))) nonBlack++;
            }
        }
        Console.WriteLine($"  >>> 解码验证: {chunks.Count} 块喂入, 解出 {decoded}, 非黑 {nonBlack}");
        dec.Dispose();
        return decoded > 0 && nonBlack > 0;
    }

    static void PumpEvents(IMFMediaEventGenerator eg, ref bool needInput, ref bool haveOutput)
    {
        while (true)
        {
            int hr = eg.GetEvent(0, out var evPtr);
            if (hr < 0) break;
            var ev = MFInterop.GetObject<IMFMediaEventView>(evPtr);
            ev.GetType_(out var met);
            if (met == METransformNeedInput) needInput = true;
            else if (met == METransformHaveOutput) haveOutput = true;
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

    static void MFMediaTypeSet(IMFMediaType t, Guid key, Guid val) => t.SetGUID(ref key, ref val);
    static void MFMediaTypeSet(IMFMediaType t, Guid key, uint val) => t.SetUINT32(ref key, val);
    static void MFMediaTypeSetU64(IMFMediaType t, Guid key, ulong val) => t.SetUINT64(ref key, val);
}
