using QuickRemote.PCClient.Services;

Console.WriteLine("=== JPEG 编码器验证 ===");
try
{
    using var capture = new ScreenCaptureService();
    capture.Start();
    Console.WriteLine($"[OK] 屏幕捕获: {capture.Width}x{capture.Height}");

    // 先捕获一帧有内容的（等一帧变化）
    CapturedFrame? frame = null;
    for (int i = 0; i < 5; i++)
    {
        frame = capture.CaptureFrame(500);
        if (frame != null && frame.HasChanges) break;
    }
    if (frame == null || !frame.HasChanges)
    {
        Console.WriteLine("[WARN] 未捕获到变化帧，用合成帧测试");
        // 合成一个渐变帧
        var fake = new byte[capture.Width * capture.Height * 4];
        for (int y = 0; y < capture.Height; y++)
            for (int x = 0; x < capture.Width; x++)
            {
                int idx = (y * capture.Width + x) * 4;
                fake[idx] = (byte)(x % 256);     // B
                fake[idx+1] = (byte)(y % 256);   // G
                fake[idx+2] = 128;                // R
                fake[idx+3] = 255;                // A
            }
        frame = new CapturedFrame { Data = fake, Width = capture.Width, Height = capture.Height, HasChanges = true };
    }
    Console.WriteLine($"[OK] 获得帧 {frame.Width}x{frame.Height}");

    // JPEG 编码
    using var jpeg = new JpegFrameEncoder();
    jpeg.Initialize(frame.Width, frame.Height, 15, 4000);
    var encoded = jpeg.EncodeFrame(frame.Data);
    Console.WriteLine($"[OK] JPEG 编码输出 {encoded.Length} 字节");
    Console.WriteLine(encoded.Length > 0 ? "[OK] JPEG 编码器工作正常" : "[FAIL] JPEG 编码输出为空");
}
catch (Exception ex)
{
    Console.WriteLine($"[FAIL] {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}
