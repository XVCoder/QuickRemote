using QuickRemote.PCClient.Services;

// 最终端到端验证：产品类 H264Encoder → H264Decoder 全链路。
// 验证点：Decode() 逐帧返回非 null、分辨率正确、BGRA 内容正确（左蓝右橙）。

const int W = 1280, H = 720, FPS = 15;
var log = new List<string>();
void L(string s) { log.Add(s); Console.WriteLine(s); }

var enc = new H264Encoder();
enc.Initialize(W, H, FPS, 4000);
var frames = new List<byte[]>();
for (int f = 0; f < 40; f++)
{
    var bgra = new byte[W * H * 4];
    for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int o = (y * W + x) * 4;
            bool left = x < W / 2;
            bgra[o] = (byte)(left ? 30 : 220);   // B：左暗右亮
            bgra[o + 1] = (byte)(40 + (f * 20) % 200); // G：随帧变化
            bgra[o + 2] = (byte)(left ? 200 : 30); // R：左亮右暗
            bgra[o + 3] = 255;
        }
    var nal = enc.EncodeFrame(bgra);
    if (nal is { Length: > 0 }) frames.Add(nal);
}
L($"encoded {frames.Count} non-empty frames");
enc.Dispose();

var dec = new H264Decoder();
dec.Initialize(W, H);
L($"decoder initialized, available={dec.IsAvailable}");

int decoded = 0;
H264Decoder.DecodedFrame? last = null;
int firstDecodedIndex = -1;
for (int f = 0; f < frames.Count; f++)
{
    var frame = dec.Decode(frames[f]);
    if (frame != null)
    {
        decoded++;
        last = frame;
        if (firstDecodedIndex < 0) firstDecodedIndex = f;
    }
}
L($"decoded {decoded}/{frames.Count} frames, first output at input #{firstDecodedIndex}");
L($"decoder size: {dec.Width}x{dec.Height}");

if (last != null)
{
    L($"last frame: {last.Width}x{last.Height}, {last.Bgra.Length}B BGRA");
    // 内容校验：左半 B 低 R 高（蓝），右半 B 高 R 低（橙）
    int mid = last.Width / 2;
    int oL = (last.Height / 2 * last.Width + mid / 2) * 4; // 左中
    int oR = (last.Height / 2 * last.Width + mid + mid / 2) * 4; // 右中
    L($"left  pixel: B={last.Bgra[oL]} G={last.Bgra[oL + 1]} R={last.Bgra[oL + 2]} (expect B<100 R>120)");
    L($"right pixel: B={last.Bgra[oR]} G={last.Bgra[oR + 1]} R={last.Bgra[oR + 2]} (expect B>150 R<100)");
    bool ok = last.Bgra[oL] < 100 && last.Bgra[oL + 2] > 120 &&
              last.Bgra[oR] > 150 && last.Bgra[oR + 2] < 100;
    L($"content check: {(ok ? "PASS" : "FAIL")}");
}
else
{
    L("NO FRAMES DECODED - FAIL");
}

dec.Dispose();
File.WriteAllLines(@"e:\000_AI\QuickRemote\verify-h264dec\result.txt", log);
Console.WriteLine("DONE");
