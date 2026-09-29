using QuickRemote.PCClient.Services;

// ============================================================================
// 生产 HwH264Encoder 端到端验证（tmp-hwtest）：
// 直接链接生产源文件编译，跑 30 帧 2560x1440@15fps，验证：
//   ① TryCreate 成功走硬件路径
//   ② 首帧即出 IDR（硬件无 lookahead）
//   ③ ForceKeyFrame 动态属性生效
//   ④ 每帧延迟、解码非黑（解码用探针同款软验：H264Decoder 逻辑简化为
//      输出非空 + IDR 结构检查——完整解码验证已在 tmp-llprobe hw2 覆盖）
// ============================================================================

unsafe class Program
{
    static int Main()
    {
        int W = 2560, H = 1440, fps = 15;
        var logger = new Logger("hwtest");

        using var enc = HwH264Encoder.TryCreate(W, H, fps, 8000, logger);
        if (enc == null)
        {
            Console.WriteLine(">>> FAIL: TryCreate 返回 null（回退软件路径被触发）");
            return 1;
        }
        Console.WriteLine($">>> TryCreate OK: {enc.Width}x{enc.Height}, IsLowLatency={enc.IsLowLatency}");

        var chunks = new List<byte[]>();
        var lat = new List<long>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int forceAt = 20;
        bool sawIdrFirst = false, sawIdrAfterForce = false;

        for (int i = 1; i <= 45; i++)
        {
            var bgra = MakeBgra(i, W, H);
            if (i == forceAt)
            {
                bool fkOk = enc.ForceKeyFrame();
                Console.WriteLine($"  [frame {i}] ForceKeyFrame 设置返回: {fkOk}");
            }
            sw.Restart();
            var data = enc.EncodeFrame(bgra);
            long ms = sw.ElapsedMilliseconds;
            if (data.Length > 0)
            {
                chunks.Add(data);
                lat.Add(ms);
                bool idr = ContainsIdr(data);
                if (i == 1 && idr) sawIdrFirst = true;
                if (i >= forceAt && idr) sawIdrAfterForce = true;
                if ((i >= forceAt - 2) || i <= 3)
                    Console.WriteLine($"  [frame {i,2}] {data.Length,7}B idr={idr} {ms}ms");
            }
        }

        Console.WriteLine($"  输出 {chunks.Count} 帧非空, 延迟 min/avg/max = {lat.Min()}/{(long)lat.Average()}/{lat.Max()}ms");
        Console.WriteLine($"  首帧 IDR: {sawIdrFirst}, ForceKeyFrame({forceAt}) 后出 IDR: {sawIdrAfterForce}");

        bool ok = chunks.Count >= 25 && sawIdrFirst && sawIdrAfterForce && lat.Average() < 100;
        Console.WriteLine(ok ? ">>> PASS" : ">>> FAIL");
        return ok ? 0 : 1;
    }

    static byte[] MakeBgra(int tick, int w, int h)
    {
        var f = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                f[o] = 30; f[o + 1] = 32; f[o + 2] = 38; f[o + 3] = 255;
            }
        void Rect(int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y < y1 && y < h; y++)
                for (int x = x0; x < x1 && x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    f[o] = 200; f[o + 1] = 210; f[o + 2] = 230; f[o + 3] = 255;
                }
        }
        Rect(100 + (tick * 11) % 1200, 100, 700 + (tick * 11) % 1200, 600);
        return f;
    }

    static bool ContainsIdr(byte[] data)
    {
        for (int i = 2; i < data.Length - 1; i++)
            if (data[i] == 1 && data[i - 1] == 0 && data[i - 2] == 0 && (data[i + 1] & 0x1F) == 5)
                return true;
        return false;
    }
}
