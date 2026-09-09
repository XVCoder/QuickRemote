using QuickRemote.PCClient.Services;

// 真实桌面模拟：大面积静态色块（低复杂度，B 帧收益大）+ 每帧少量矩形变化。
// 对比低延迟模式开/关的码率与 NAL 结构，确认 v1.1.56 低延迟模式在公网场景的副作用。

const int W = 2560, H = 1440, FPS = 15, BITRATE = 4000;
int bgraLen = W * H * 4;
var bgra = new byte[bgraLen];
var rnd = new Random(42);

// 静态桌面底：渐变 + 色块（模拟窗口/桌面，低复杂度，可被 B/P 帧高效压缩）
for (int y = 0; y < H; y++)
{
    int row = y * W * 4;
    for (int x = 0; x < W; x++)
    {
        int o = row + x * 4;
        byte shade = (byte)((x / 8 + y / 8) % 4 * 40 + 60);
        bgra[o] = (byte)(shade + (x / 16) % 16);       // B
        bgra[o + 1] = (byte)(shade + (y / 16) % 16);   // G
        bgra[o + 2] = shade;                            // R
        bgra[o + 3] = 255;
    }
}
// 顶部"任务栏"深色条 + 中部"窗口"白色块
for (int y = H - 40; y < H; y++)
    for (int x = 0; x < W; x++)
    {
        int o = (y * W + x) * 4;
        bgra[o] = 20; bgra[o + 1] = 20; bgra[o + 2] = 20; bgra[o + 3] = 255;
    }
for (int y = 300; y < 800; y++)
    for (int x = 400; x < 1800; x++)
    {
        int o = (y * W + x) * 4;
        bgra[o] = 240; bgra[o + 1] = 240; bgra[o + 2] = 240; bgra[o + 3] = 255;
    }
var baseFrame = new byte[bgraLen];
Array.Copy(bgra, baseFrame, bgraLen);

var lines = new List<string>();
void Log(string s) { lines.Add(s); Console.WriteLine(s); }

foreach (var lowLatency in new[] { false, true })
{
    H264Encoder.LowLatency = lowLatency;
    try
    {
        var enc = new H264Encoder();
        enc.Initialize(W, H, FPS, BITRATE);
        Array.Copy(baseFrame, bgra, bgraLen);

        long totalBytes = 0;
        int idrNalCount = 0, spsCount = 0;
        int maxFrameBytes = 0, maxIdrBytes = 0;
        int firstOutputAt = -1;
        var perFrameBytes = new List<int>();

        for (int i = 0; i < 90; i++)
        {
            // 模拟桌面活动：每帧移动一个 120x40 的"光标/窗口拖动"块
            int cx = 200 + (i * 40) % (W - 200);
            for (int yy = 600; yy < 640; yy++)
                for (int xx = cx; xx < cx + 120 && xx < W; xx++)
                {
                    int o = (yy * W + xx) * 4;
                    bgra[o] = 60; bgra[o + 1] = 140; bgra[o + 2] = 200; bgra[o + 3] = 255;
                }

            var encoded = enc.EncodeFrame(bgra);
            if (encoded.Length > 0 && firstOutputAt < 0) firstOutputAt = i;
            totalBytes += encoded.Length;
            perFrameBytes.Add(encoded.Length);
            if (encoded.Length > maxFrameBytes) maxFrameBytes = encoded.Length;

            // NAL 解析
            var nalTypes = new List<int>();
            int nalIdr = 0;
            for (int p = 0; p < encoded.Length - 4; p++)
            {
                if (encoded[p] == 0 && encoded[p + 1] == 0 && encoded[p + 2] == 0 && encoded[p + 3] == 1)
                {
                    int nalType = encoded[p + 4] & 0x1F;
                    nalTypes.Add(nalType);
                    if (nalType == 5) { nalIdr++; idrNalCount++; if (encoded.Length > maxIdrBytes) maxIdrBytes = encoded.Length; }
                    if (nalType == 7) spsCount++;
                }
            }
            if (i < 3 || nalIdr > 0)
                Log($"  frame#{i:D2}: {encoded.Length,8} bytes  NAL=[{string.Join(",", nalTypes)}]");
        }

        enc.Dispose();
        double secs = 90 / (double)FPS;
        var sorted = perFrameBytes.OrderByDescending(v => v).ToArray();
        Log($"=== LowLatency={lowLatency} ===");
        Log($"  total={totalBytes} bytes  avg={totalBytes * 8.0 / secs / 1000.0:F0} kbps  (设定 {BITRATE} kbps)");
        Log($"  max frame={maxFrameBytes}  max IDR frame={maxIdrBytes}  IDR NAL total={idrNalCount}  SPS={spsCount}");
        Log($"  top5 frames: {string.Join(", ", sorted.Take(5))}");
        Log($"  first output after input frame #{firstOutputAt}");
        Log("");
    }
    catch (Exception ex)
    {
        Log($"=== LowLatency={lowLatency} FAILED: {ex.Message} ===");
    }
}

// ============ 聚焦测试：低码率 + 静态桌面下首帧输出结构（复现真实会话 182 字节首帧） ============
// 真实会话日志：LL 模式下 FastFill 首帧只输出 182 字节（疑似仅 SPS/PPS），
// 而 Android 需要完整 IDR 才能出画——FastFill 提前 return 把真正的 IDR 吞了。
Log("=== 聚焦：1200kbps 静态桌面 首帧输出结构（LL 模式，复现 v1.1.56 会话） ===");
H264Encoder.LowLatency = true;
try
{
    var enc = new H264Encoder();
    enc.Initialize(W, H, FPS, 1200);
    Array.Copy(baseFrame, bgra, bgraLen);

    // 静态桌面（无活动块）：模拟连接时桌面静止
    for (int i = 0; i < 6; i++)
    {
        var out1 = enc.EncodeFrame(bgra);
        var nals = ParseNalTypes(out1);
        Log($"  input#{i} -> {out1.Length,8} bytes  NAL=[{string.Join(",", nals)}]");
        if (out1.Length > 0) break; // 与 FastFillEncoder 相同：首个非空输出即停止
    }
    // ForceKeyFrame 后第二帧（复现 Android keyframe 请求时序）
    enc.ForceKeyFrame();
    var out2 = enc.EncodeFrame(bgra);
    Log($"  ForceKeyFrame -> {out2.Length,8} bytes  NAL=[{string.Join(",", ParseNalTypes(out2))}]");
    var out3 = enc.EncodeFrame(bgra);
    Log($"  next          -> {out3.Length,8} bytes  NAL=[{string.Join(",", ParseNalTypes(out3))}]");
    enc.Dispose();
}
catch (Exception ex)
{
    Log($"  1200kbps LL FAILED: {ex.Message}");
}

// 对照：非 LL 模式（v1.1.54 行为）
Log("=== 聚焦：1200kbps 静态桌面 首帧输出结构（非 LL，v1.1.54 行为） ===");
H264Encoder.LowLatency = false;
try
{
    var enc = new H264Encoder();
    enc.Initialize(W, H, FPS, 1200);
    Array.Copy(baseFrame, bgra, bgraLen);
    for (int i = 0; i < 20; i++)
    {
        var out1 = enc.EncodeFrame(bgra);
        var nals = ParseNalTypes(out1);
        if (out1.Length > 0 || i == 19)
            Log($"  input#{i} -> {out1.Length,8} bytes  NAL=[{string.Join(",", nals)}]");
        if (out1.Length > 0) break;
    }
    // 非 LL：ForceKeyFrame 是否有效？
    enc.ForceKeyFrame();
    for (int i = 0; i < 4; i++)
    {
        var outk = enc.EncodeFrame(bgra);
        Log($"  nonLL ForceKeyFrame #{i} -> {outk.Length,8} bytes  NAL=[{string.Join(",", ParseNalTypes(outk))}]");
        if (ParseNalTypes(outk).Contains(5)) break;
    }
    enc.Dispose();
}
catch (Exception ex)
{
    Log($"  1200kbps non-LL FAILED: {ex.Message}");
}

// ============ 决定性测试：动态桌面 + ForceKeyFrame（模拟真实 keyframe 请求） ============
// 真实场景：桌面有活动（鼠标/窗口），Android 请求 keyframe → ForceKeyFrame → FastFill 连投缓存帧
Log("=== 决定性：动态桌面(移动块) + ForceKeyFrame 连投 5 帧（非 LL = v1.1.54 行为） ===");
H264Encoder.LowLatency = false;
try
{
    var enc = new H264Encoder();
    enc.Initialize(W, H, FPS, 1200);
    Array.Copy(baseFrame, bgra, bgraLen);
    // 先跑 20 帧建立参考链（非 LL 首帧 IDR 在第 13 帧左右输出）
    for (int i = 0; i < 20; i++)
    {
        int cx = 200 + (i * 40) % (W - 200);
        for (int yy = 600; yy < 640; yy++)
            for (int xx = cx; xx < cx + 120 && xx < W; xx++)
            {
                int o = (yy * W + xx) * 4;
                bgra[o] = 60; bgra[o + 1] = 140; bgra[o + 2] = 200; bgra[o + 3] = 255;
            }
        enc.EncodeFrame(bgra);
    }
    // 模拟 Android keyframe 请求：ForceKeyFrame + FastFill 连投（上限 16 帧，与 FastFillEncoder 一致）
    enc.ForceKeyFrame();
    for (int i = 0; i < 16; i++)
    {
        var outk = enc.EncodeFrame(bgra);
        var nals = ParseNalTypes(outk);
        Log($"  keyframe#{i} -> {outk.Length,8} bytes  NAL=[{string.Join(",", nals)}]{(nals.Contains(5) ? "  <-- IDR" : "")}");
        if (nals.Contains(5)) break;
    }
    enc.Dispose();
}
catch (Exception ex)
{
    Log($"  dynamic non-LL FAILED: {ex.Message}");
}

File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "latency_compare.txt"), lines);
Console.WriteLine("done -> " + Path.Combine(AppContext.BaseDirectory, "latency_compare.txt"));

static List<int> ParseNalTypes(byte[] data)
{
    // 兼容 3 字节(00 00 01)与 4 字节(00 00 00 01)起始码
    var types = new List<int>();
    for (int p = 0; p < data.Length - 4; p++)
    {
        if (data[p] == 0 && data[p + 1] == 0 && data[p + 2] == 1)
        {
            // 4 字节起始码：p 指向第 3 个 00（00 00 00 01 的 index+2）；3 字节：p 指向 00 00 01
            bool is4Byte = p > 0 && data[p - 1] == 0;
            if (is4Byte)
            {
                types.Add(data[p + 3] & 0x1F);
                p += 3;
            }
            else
            {
                types.Add(data[p + 3] & 0x1F);
                p += 2;
            }
        }
    }
    return types;
}
