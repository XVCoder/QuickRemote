using System.Runtime.InteropServices;
using QuickRemote.PCClient.Services;

// ============================================================================
// 探针入口：
//   llprobe          → 黑屏复现 + 编解码端到端验证（默认）
//   llprobe enum     → 枚举本机可用的 H.264 编码器 MFT（硬件/软件/异步）
// ============================================================================

if (args.Length > 0 && args[0] == "enum")
{
    return RunEnumProbe();
}
if (args.Length > 0 && args[0] == "hw")
{
    return HwProbe.Run();
}
if (args.Length > 0 && args[0] == "hw2")
{
    return HwGpuProbe.Run();
}


const int W = 2560, H = 1440;

// 构造"类桌面"帧：深色底 + 窗口色块 + 文字条，每帧白块位置微移（模拟光标/时钟）
byte[] MakeFrame(int tick)
{
    var f = new byte[W * H * 4];
    for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int o = (y * W + x) * 4;
            f[o] = 30; f[o + 1] = 32; f[o + 2] = 38; f[o + 3] = 255; // 深灰蓝底
        }
    void Rect(int x0, int y0, int x1, int y1, byte b, byte g, byte r)
    {
        for (int y = y0; y < y1 && y < H; y++)
            for (int x = x0; x < x1 && x < W; x++)
            {
                int o = (y * W + x) * 4;
                f[o] = b; f[o + 1] = g; f[o + 2] = r; f[o + 3] = 255;
            }
    }
    Rect(100, 80, 1200, 700, 60, 60, 68);      // 窗口
    Rect(1300, 200, 2400, 900, 45, 90, 60);    // 编辑器
    Rect(200, 1000, 2300, 1380, 25, 25, 28);   // 任务栏区
    int cx = 300 + (tick * 7) % (W - 400);     // 移动白块 = 光标
    Rect(cx, 500, cx + 24, 524, 255, 255, 255);
    return f;
}

// 按线上 FastFill 语义喂帧：连投同一帧直到首个非空输出（最多 16 次）
byte[] FastFill(H264Encoder enc, byte[] frame, List<byte[]> sink)
{
    for (int i = 1; i <= 16; i++)
    {
        var enc1 = enc.EncodeFrame(frame);
        if (enc1.Length > 0)
        {
            sink.Add(enc1);
            Console.WriteLine($"  [FF] 第 {i} 次输入出 {enc1.Length}B  NAL[{DescribeNals(enc1)}]");
            return enc1;
        }
    }
    Console.WriteLine("  [FF] 16 次输入无输出");
    return Array.Empty<byte>();
}

string DescribeNals(byte[] d)
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

void RunScenario(string name, int variant, bool keyframeBeforeFirstFrame)
{
    Console.WriteLine($"\n===== {name} (variant={variant}, keyframe先于首帧={keyframeBeforeFirstFrame}) =====");
    var enc = new H264Encoder { LlVariant = variant };
    enc.Initialize(W, H, 15, 16000);
    var chunks = new List<byte[]>();   // 每个 = 一个 TCP 视频帧载荷

    if (keyframeBeforeFirstFrame)
    {
        enc.ForceKeyFrame();
        Console.WriteLine("  [keyframe#1] ForceKeyFrame（首帧之前）");
    }

    var frame0 = MakeFrame(0);
    FastFill(enc, frame0, chunks);

    if (keyframeBeforeFirstFrame)
    {
        enc.ForceKeyFrame();
        Console.WriteLine("  [keyframe#2] ForceKeyFrame");
        FastFill(enc, frame0, chunks);
    }

    // 主循环：30 帧连续编码（白块移动）
    int idrInMain = 0;
    for (int i = 1; i <= 30; i++)
    {
        var out1 = enc.EncodeFrame(MakeFrame(i));
        if (out1.Length > 0)
        {
            chunks.Add(out1);
            bool idr = H264Encoder.ContainsIdrFrame(out1);
            if (idr) idrInMain++;
            if (i <= 5 || idr)
                Console.WriteLine($"  [main {i,2}] {out1.Length,7}B idr={idr} NAL[{DescribeNals(out1)}]");
        }
    }
    Console.WriteLine($"  主循环 30 帧中含 IDR 的输出: {idrInMain}");
    enc.Dispose();

    // ===== 解码验证：按 Android 视角逐 chunk 喂解码器 =====
    var dec = new H264Decoder();
    dec.Initialize(W, H);
    int decoded = 0, nonBlack = 0;
    long firstNonBlackChunk = -1;
    for (int c = 0; c < chunks.Count; c++)
    {
        var df = dec.Decode(chunks[c]);
        if (df != null)
        {
            decoded++;
            // 亮度均值粗判黑屏
            long sum = 0;
            var px = df.Bgra;
            for (int p = 0; p < px.Length; p += 4 * 997) sum += px[p + 1]; // 抽样 G 通道
            bool black = sum < 40 * (px.Length / (4 * 997));
            if (!black)
            {
                nonBlack++;
                if (firstNonBlackChunk < 0) firstNonBlackChunk = c;
            }
        }
    }
    Console.WriteLine($"  >>> 解码验证: {chunks.Count} 个视频帧喂入, 解出 {decoded} 帧, 非黑 {nonBlack} 帧" +
        (firstNonBlackChunk >= 0 ? $", 首个非黑出现在第 {firstNonBlackChunk + 1} 个 chunk" : "  <<< 全程黑屏!"));
    dec.Dispose();
}

if (!H264Decoder.IsH264DecoderAvailable())
{
    Console.WriteLine("系统无 H.264 解码器 MFT，无法做解码验证");
    return 1;
}

RunScenario("线上时序复现", variant: 3, keyframeBeforeFirstFrame: true);
RunScenario("对照：无 keyframe 先行", variant: 3, keyframeBeforeFirstFrame: false);
RunScenario("对照：仅 AVLowLatencyMode + keyframe 先行", variant: 1, keyframeBeforeFirstFrame: true);
return 0;

// ============ 硬件编码器枚举（llprobe enum） ============

static int RunEnumProbe()
{
    MfPlatform.EnsureStarted();

    // MFT_CATEGORY_VIDEO_ENCODER + MFT_FRIENDLY_NAME_Attribute（codecapi.h/mfapi.h）
    var catEncoder = new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a");
    var attrFriendlyName = new Guid("314ffbae-5b41-4c95-9c19-4e7d586face3"); // SDK mfapi.h 核实
    var attrFlags = new Guid("eb3d2b3d-e0d8-45ae-a49c-9990af5d2baf"); // MFT_ENUM_ADAPTER? 无需，仅展示 flags

    // 输入 NV12 → 输出 H264 的类型过滤（与真实使用一致）
    var inType = new MFT_REGISTER_TYPE_INFO { guidMajorType = new Guid("73646976-0000-0010-8000-00aa00389b71"), guidSubtype = new Guid("3231564e-0000-0010-8000-00aa00389b71") };
    var outType = new MFT_REGISTER_TYPE_INFO { guidMajorType = new Guid("73646976-0000-0010-8000-00aa00389b71"), guidSubtype = new Guid("34363248-0000-0010-8000-00aa00389b71") };
    IntPtr inPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_REGISTER_TYPE_INFO>());
    IntPtr outPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_REGISTER_TYPE_INFO>());
    Marshal.StructureToPtr(inType, inPtr, false);
    Marshal.StructureToPtr(outType, outPtr, false);

    string[] flagNames = { "SYNCMFT(软件同步)", "ASYNC(软件异步)", "HARDWARE(硬件)", "FIELDOFUSE" };
    uint[] flags = { MFInterop.MFT_ENUM_FLAG_SYNCMFT, MFInterop.MFT_ENUM_FLAG_ASYNC, MFInterop.MFT_ENUM_FLAG_HARDWARE, MFInterop.MFT_ENUM_FLAG_FIELDOFUSE };

    try
    {
        foreach (var (flag, name) in flags.Zip(flagNames))
        {
            int hr = MFInterop.MFTEnumEx(catEncoder, flag, inPtr, outPtr, out var activates, out int count);
            Console.WriteLine($"\n== {name} (0x{flag:X}): hr=0x{hr:X8}, {count} 个");
            if (hr < 0 || count == 0) continue;
            for (int i = 0; i < count; i++)
            {
                IntPtr actPtr = Marshal.ReadIntPtr(activates, i * IntPtr.Size);
                var act = MFInterop.GetObject<IMFActivate>(actPtr);
                string fname = "?";
                if (act.GetStringLength(ref attrFriendlyName, out uint len) >= 0 && len > 0)
                {
                    var buf = Marshal.AllocHGlobal((int)len * 2 + 2);
                    if (act.GetString(ref attrFriendlyName, buf, (uint)(len * 2 + 2), out _) >= 0)
                        fname = Marshal.PtrToStringUni(buf) ?? "?";
                    Marshal.FreeHGlobal(buf);
                }
                Console.WriteLine($"   [{i}] {fname}");
                Marshal.ReleaseComObject(act);
            }
            Marshal.FreeHGlobal(activates);
        }
    }
    finally
    {
        Marshal.FreeHGlobal(inPtr);
        Marshal.FreeHGlobal(outPtr);
    }
    return 0;
}
