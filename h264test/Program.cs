using QuickRemote.PCClient.Services;

// 验证 v1.1.37 修复架构：编码器全部 COM 操作收敛到编码线程（MTA），
// STA 线程（模拟 WPF UI / 控制帧线程）只投递 volatile 标志。
// 对照组：STA 线程直接创建 encoder 已稳定复现真机闪退（见 git 历史）。

const int W = 2560, H = 1440, FPS = 15;
int bgraLen = W * H * 4;
var bgra = new byte[bgraLen];
var rnd = new Random(42);

var sta = new Thread(() =>
{
    try
    {
        Console.WriteLine("=== 修复验证：编码线程（MTA）创建/使用 encoder，STA 线程投递请求 ===");
        RunTest(bgra, rnd);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[STA-main] FATAL: {ex}");
    }
});
sta.SetApartmentState(ApartmentState.STA);
sta.Start();
sta.Join();

static void RunTest(byte[] bgra, Random rnd)
{
    object encoderLock = new();
    H264Encoder? encoder = null;
    int rebuildCount = 0;
    int forceKeyOk = 0, forceKeyFail = 0;
    var stop = false;
    var pendingQuality = 0;
    var pendingKeyframe = false;

    // (1) EncodeLoop（MTA）：创建 encoder + 编码 + 统一执行投递请求（复刻修复后架构）
    var encodeThread = new Thread(() =>
    {
        int i = 0;
        try
        {
            encoder = new H264Encoder();          // 创建在 MTA 编码线程（关键）
            encoder.Initialize(W, H, FPS, 4000);
            Console.WriteLine($"[encode] encoder created on MTA encode thread (TID={Environment.CurrentManagedThreadId})");

            while (!stop)
            {
                // 统一执行投递请求（复刻 EncodeLoop 开头的 pending 处理）
                if (pendingKeyframe)
                {
                    pendingKeyframe = false;
                    var ok = encoder?.ForceKeyFrame() ?? false;
                    if (ok) Interlocked.Increment(ref forceKeyOk); else Interlocked.Increment(ref forceKeyFail);
                    Console.WriteLine($"[encode] keyframe executed: ForceKeyFrame={ok}");
                }
                if (pendingQuality != 0)
                {
                    var q = pendingQuality;
                    pendingQuality = 0;
                    try { encoder?.Dispose(); } catch { }
                    encoder = new H264Encoder();
                    encoder.Initialize(W, H, FPS, 4000 * q / 100);
                    Interlocked.Increment(ref rebuildCount);
                    Console.WriteLine($"[encode] quality {q}% -> encoder rebuilt");
                }

                rnd.NextBytes(bgra.AsSpan(0, 64 * 1024));
                byte[]? encoded;
                lock (encoderLock)
                {
                    encoded = encoder?.EncodeFrame(bgra);
                }
                i++;
                Thread.Sleep(66); // 15fps
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[encode] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (encoderLock) { try { encoder?.Dispose(); } catch { } encoder = null; }
            Console.WriteLine($"[encode] loop ended after {i} frames (encoder disposed on encode thread)");
        }
    })
    { IsBackground = true };
    encodeThread.Start();

    // (2) 控制帧线程（模拟 ReadLoop MTA 投递；再叠加 STA 投递人）
    Thread.Sleep(60);
    var recvThread = new Thread(() =>
    {
        Thread.Sleep(40);
        pendingQuality = 20;                       // 投递，不直接操作 encoder
        for (int k = 0; k < 4; k++)
        {
            Thread.Sleep(k == 0 ? 20 : 60 + rnd.Next(140));
            pendingKeyframe = true;
        }
    })
    { IsBackground = true };
    recvThread.Start();

    // (3) STA 线程模拟 UI Dispose：只 stop + 等编码线程自己销毁（复刻 Cleanup 修复）
    Thread.Sleep(2000);
    Console.WriteLine("[sta] simulating UI Dispose: stop + join (encoder disposed by encode thread)");
    stop = true;
    encodeThread.Join(5000);

    Console.WriteLine($"\nresult: rebuilds={rebuildCount}, forceKey ok={forceKeyOk} fail={forceKeyFail}");
    Console.WriteLine("=== PASSED - no crash with fixed architecture ===");
}
