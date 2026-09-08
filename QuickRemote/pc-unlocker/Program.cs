// QuickRemote.Agent — SYSTEM 权限辅助程序（两种模式）。
//
// Windows 锁屏/UAC 的输入框位于 Winlogon 安全桌面，普通用户态进程既截不到
// 该桌面画面，注入的 SendInput 也会被系统丢弃（lastError=5 拒绝访问）。
// 必须由 SYSTEM 权限进程切换到 Winlogon 桌面后注入。本程序由 PC 客户端
// 通过计划任务（schtasks /RU SYSTEM /IT，交互会话内以 SYSTEM 运行）启动：
//
//   unlock <密码文件>   远程解锁：读密码（读完立即删文件）→ 切 Winlogon 桌面
//                       → 注入 回车→密码→回车 按键序列
//   agent  <端口文件>   锁屏输入代理（向日葵式体验）：监听 127.0.0.1 随机端口，
//                       把 PC 客户端转发来的鼠标/键盘/滚轮帧注入到 Winlogon
//                       锁屏桌面，让手机端可以直接"点击锁屏页 → 输入密码"。
//                       端口文件内容："<port>\n<token>"（两行），客户端连接后
//                       需先发送 token 帧校验身份，再发 setup 帧（分辨率）。
//                       客户端认证后代理另起捕获线程：GDI BitBlt 抓取 Winlogon
//                       锁屏画面（DXGI 在锁屏下 DuplicateOutput 被拒，唯有
//                       SYSTEM + Winlogon 桌面上的 GDI 能看到安全桌面），按
//                       [0x12][len][宽 2B][高 2B][BGRA] 回传给客户端编码推流，
//                       手机端由此能看到真实锁屏画面。
//
// 帧协议与主程序 RemoteFrameProtocol 一致：[type 1B][len 3B LE] + payload。

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickRemote.Agent;

internal static class Program
{
    // ============ 子命令 ============
    private static int Main(string[] args)
    {
        // GDI 捕获必须拿到物理分辨率（DPI 无感知进程会被系统缩放，画面尺寸错乱）
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // PER_MONITOR_AWARE_V2

        if (args.Length >= 2 && args[0] == "unlock") return RunUnlock(args[1]);
        if (args.Length >= 2 && args[0] == "agent") return RunAgent(args[1]);
        Log("usage: QuickRemote.Agent.exe unlock <password-file> | agent <port-file>");
        return 1;
    }

    // ======================================================================
    // unlock 模式：远程解锁（密码序列注入）
    // ======================================================================
    private static int RunUnlock(string pwFile)
    {
        // 1. 读取密码并立即删除临时文件（密码不落命令行）
        string password;
        try
        {
            if (!File.Exists(pwFile))
            {
                Log($"password file not found: {pwFile}");
                return 2;
            }
            password = File.ReadAllText(pwFile).TrimEnd('\r', '\n');
            File.Delete(pwFile);
        }
        catch (Exception ex)
        {
            Log($"read password failed: {ex.Message}");
            return 2;
        }

        // 2. 切换到交互会话的 WinSta0 + Winlogon 安全桌面（需要 SYSTEM 权限）
        if (!SwitchToWinlogonDesktop()) return 3;

        // 诊断：记录会话 ID 与当前桌面（SendInput 成功但落错桌面时从这里能看出来）
        Log($"unlock start: session={System.Diagnostics.Process.GetCurrentProcess().SessionId}, desktop={GetDesktopName()}");

        // 3. 唤出密码框：Win11 锁屏首次进入显示时钟/壁纸，需点击或回车唤出密码输入框。
        //    Win11 动画慢，点击后等 2.5s 确保密码框获得焦点，否则字符会被丢弃。
        Log("waking lock screen (click center + Enter)");
        ClickCenter();
        Thread.Sleep(500);
        PressEnter();
        Thread.Sleep(2500);

        // 4. 输入密码 → 提交（回车）。共尝试 2 轮：若第 1 轮后桌面仍是 Winlogon
        //    （密码错误提示/时序丢字符），按 ESC 清状态后重试一轮。
        for (var round = 1; round <= 2; round++)
        {
            Log($"typing unlock sequence round {round} ({password.Length} chars)");
            uint ok = 0, fail = 0;
            foreach (var ch in password)
            {
                if (TypeUnicode(ch)) ok++; else fail++;
                Thread.Sleep(40); // 密码框消化速度有限，快速连发可能丢字符
            }
            Thread.Sleep(500);
            var enterOk = PressEnter();
            Log($"round {round} sent (chars ok={ok} fail={fail}, enter={enterOk})");

            // 5. 等待系统处理提交，检测当前输入桌面是否已切回 Default（= 解锁成功）
            for (var i = 0; i < 8; i++)
            {
                Thread.Sleep(500);
                var desk = GetDesktopName();
                if (desk != "Winlogon")
                {
                    Log($"UNLOCK VERIFIED after round {round}: input desktop = {desk}");
                    return 0;
                }
            }
            Log($"round {round} failed: still on Winlogon desktop");
            if (round == 1)
            {
                // 清除可能的"密码错误"提示，重新唤出密码框
                SendKey(0x1B, 0, 0); // ESC
                SendKey(0x1B, 0, KEYEVENTF_KEYUP);
                Thread.Sleep(800);
                ClickCenter();
                Thread.Sleep(1500);
            }
        }
        Log("UNLOCK FAILED after 2 rounds (check password / PIN mode on lock screen)");
        return 5;
    }

    /// <summary>查询当前输入桌面名（Winlogon=锁屏中，Default=已解锁）。</summary>
    private static string GetDesktopName()
    {
        try
        {
            var h = OpenInputDesktop(0, false, 0x00FF); // DESKTOP_READOBJECTS 等组合
            if (h == IntPtr.Zero) return $"(OpenInputDesktop err={Marshal.GetLastWin32Error()})";
            try
            {
                var sb = new StringBuilder(256);
                if (GetUserObjectInformationW(h, 2 /*UOI_NAME*/, sb, 256, out _)) return sb.ToString();
                return "(GetUserObjectInformation failed)";
            }
            finally { CloseDesktop(h); }
        }
        catch (Exception ex) { return $"(err: {ex.Message})"; }
    }

    // ======================================================================
    // agent 模式：锁屏输入代理
    // ======================================================================

    // 帧类型（0x02/0x03/0x04/0x08 与主程序 RemoteFrameProtocol 一致）
    private const byte TYPE_MOUSE = 0x02;
    private const byte TYPE_KEY = 0x03;
    private const byte TYPE_WHEEL = 0x04;
    private const byte TYPE_TEXT = 0x08; // [UTF-8 文本] 锁屏密码框直接打字（Unicode 注入）
    private const byte TYPE_SETUP = 0x10; // [宽 2B][高 2B] 归一化用分辨率
    private const byte TYPE_TOKEN = 0x11; // [token 文本] 连接身份校验
    private const byte TYPE_VIDEO = 0x12; // [宽 2B][高 2B][BGRA top-down] 锁屏画面回传

    /// <summary>代理捕获的锁屏画面尺寸（0=尚未捕获）。输入坐标归一化的权威分辨率：
    /// Android 看到的视频就是代理捕获的画面，用捕获尺寸归一化天然自洽。</summary>
    private static volatile int _captureW, _captureH;

    /// <summary>客户端连接流（捕获线程回传画面；单连接代理，volatile 引用足够）。</summary>
    private static volatile NetworkStream? _clientStream;

    private static int RunAgent(string portFile)
    {
        // 1. 先切 Winlogon 桌面：主线程切桌面后，后续所有注入都在本线程执行
        if (!SwitchToWinlogonDesktop()) return 3;

        // 2. 监听回环随机端口，端口+token 写入端口文件回传给客户端
        TcpListener listener;
        int port;
        string token;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            token = Guid.NewGuid().ToString("N");
            File.WriteAllText(portFile, $"{port}\n{token}");
        }
        catch (Exception ex)
        {
            Log($"agent listen failed: {ex.Message}");
            return 2;
        }

        Log($"input agent listening on 127.0.0.1:{port}");

        // 3. 网络线程：accept（120 秒无连接自杀）→ 校验 token → 读帧入队 →
        //    认证成功后启动锁屏画面捕获线程（GDI 回传）
        var queue = new BlockingCollection<(byte type, byte[] data)>(256);
        var netThread = new Thread(() => NetworkLoop(listener, token, queue))
        {
            IsBackground = false
        };
        netThread.Start();

        // 4. 主线程（已切 Winlogon 桌面）：消费帧队列并注入
        int videoW = 1920, videoH = 1080;
        long mouseFrames = 0, keyFrames = 0, wheelFrames = 0, textFrames = 0;
        var lastStat = DateTime.UtcNow;
        try
        {
            foreach (var (type, data) in queue.GetConsumingEnumerable())
            {
                // 捕获线程报告的锁屏画面尺寸优先（视频画面即捕获画面，坐标自洽）
                if (_captureW > 0 && _captureH > 0)
                {
                    videoW = _captureW;
                    videoH = _captureH;
                }
                switch (type)
                {
                    case TYPE_SETUP when data.Length >= 4:
                        videoW = Math.Max(1, data[0] | data[1] << 8);
                        videoH = Math.Max(1, data[2] | data[3] << 8);
                        Log($"agent video size: {videoW}x{videoH}");
                        break;
                    case TYPE_MOUSE when data.Length >= 5:
                        InjectMouse(data, videoW, videoH);
                        mouseFrames++;
                        break;
                    case TYPE_KEY when data.Length >= 3:
                        InjectKey(data);
                        keyFrames++;
                        break;
                    case TYPE_WHEEL when data.Length >= 6:
                        InjectWheel(data, videoW, videoH);
                        wheelFrames++;
                        break;
                    case TYPE_TEXT when data.Length >= 1:
                        InjectText(data);
                        textFrames++;
                        break;
                }
                // 每 5 秒输出一次注入统计（排查锁屏点击是否到达）
                if ((DateTime.UtcNow - lastStat).TotalSeconds >= 5)
                {
                    Log($"agent injected: mouse={mouseFrames} key={keyFrames} wheel={wheelFrames} text={textFrames}");
                    lastStat = DateTime.UtcNow;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"agent inject loop error: {ex.Message}");
        }

        Log($"input agent exiting (total mouse={mouseFrames} key={keyFrames} wheel={wheelFrames} text={textFrames})");
        return 0;
    }

    /// <summary>网络线程：accept 一个连接 → 校验 token 帧后启动画面捕获线程 → 循环读帧入队。</summary>
    private static void NetworkLoop(TcpListener listener, string token,
        BlockingCollection<(byte, byte[])> queue)
    {
        TcpClient? client = null;
        try
        {
            // 120 秒等不到连接则退出（客户端异常未连接，避免进程泄漏）
            var waitStart = DateTime.UtcNow;
            while (!listener.Pending())
            {
                if ((DateTime.UtcNow - waitStart).TotalSeconds > 120)
                {
                    Log("agent: no client within 120s, exit");
                    return;
                }
                Thread.Sleep(100);
            }
            client = listener.AcceptTcpClient();
            var stream = client.GetStream();

            // 首帧必须是 token 且匹配
            var (tType, tData) = ReadFrame(stream);
            if (tType != TYPE_TOKEN || Encoding.UTF8.GetString(tData) != token)
            {
                Log("agent: token mismatch, closing");
                return;
            }
            Log("agent: client authenticated");

            // 认证通过：启动锁屏画面捕获线程（GDI 抓 Winlogon 桌面回传客户端）
            _clientStream = stream;
            var capThread = new Thread(AgentCaptureLoop) { IsBackground = true };
            capThread.Start();

            // 循环读输入帧
            while (true)
            {
                var (type, data) = ReadFrame(stream);
                queue.Add((type, data));
            }
        }
        catch (Exception ex)
        {
            Log($"agent network loop ended: {ex.Message}");
        }
        finally
        {
            _clientStream = null; // 捕获线程随流断开自然退出
            queue.CompleteAdding();
            try { listener.Stop(); } catch { }
            try { client?.Close(); } catch { }
        }
    }

    /// <summary>读一帧：[type 1B][len 3B LE] + payload。</summary>
    private static (byte, byte[]) ReadFrame(NetworkStream stream)
    {
        var header = new byte[4];
        ReadExactly(stream, header, 4);
        var type = header[0];
        var len = header[1] | header[2] << 8 | header[3] << 16;
        var data = new byte[len];
        if (len > 0) ReadExactly(stream, data, len);
        return (type, data);
    }

    private static void ReadExactly(NetworkStream stream, byte[] buf, int count)
    {
        int off = 0;
        while (off < count)
        {
            var read = stream.Read(buf, off, count - off);
            if (read <= 0) throw new EndOfStreamException();
            off += read;
        }
    }

    // ======================================================================
    // 锁屏画面捕获（GDI）：DXGI DuplicateOutput 在锁屏下被系统拒绝，而 SYSTEM
    // 进程切到 Winlogon 桌面后 GetDC/BitBlt 读到的正是 DWM 合成的安全桌面
    // 画面（物理屏幕上的锁屏壁纸/密码框）。锁屏画面基本静止：变化检测后回传
    // （静止时 1 秒保底重发维持编码器/解码活性），客户端直接喂 H.264 管线。
    // ======================================================================

    private const int CAPTURE_INTERVAL_MS = 250; // 轮询间隔（变化时最高 4fps）
    private const int KEEPALIVE_MS = 1000;       // 静止画面保底重发周期

    private static void AgentCaptureLoop()
    {
        // SetThreadDesktop 是线程级状态，捕获线程需自行切换
        if (!SwitchToWinlogonDesktop())
        {
            Log("capture: switch to Winlogon desktop failed");
            return;
        }

        IntPtr screenDC = IntPtr.Zero, memDC = IntPtr.Zero, bmp = IntPtr.Zero;
        byte[]? prev = null;
        var lastSent = DateTime.MinValue;
        long framesSent = 0, bltFails = 0;
        try
        {
            while (true)
            {
                var stream = _clientStream;
                if (stream == null) return; // 客户端已断开

                // 惰性初始化 GDI 资源；>2560 宽时降采样（协议 len 3B 上限 16MB，4K 原生帧超限）
                if (screenDC == IntPtr.Zero)
                {
                    screenDC = GetDC(IntPtr.Zero);
                    if (screenDC == IntPtr.Zero)
                    {
                        Log($"capture: GetDC failed err={Marshal.GetLastWin32Error()}");
                        return;
                    }
                    var sw = GetDeviceCaps(screenDC, HORZRES);
                    var sh = GetDeviceCaps(screenDC, VERTRES);
                    var scale = sw > 2560 ? 2560.0 / sw : 1.0;
                    var w = Math.Max(1, (int)Math.Round(sw * scale));
                    var h = Math.Max(1, (int)Math.Round(sh * scale));
                    memDC = CreateCompatibleDC(screenDC);
                    bmp = CreateCompatibleBitmap(screenDC, w, h);
                    if (bmp == IntPtr.Zero || SelectObject(memDC, bmp) == IntPtr.Zero)
                    {
                        Log($"capture: bitmap create failed err={Marshal.GetLastWin32Error()}");
                        return;
                    }
                    _captureW = w;
                    _captureH = h;
                    Log($"capture: GDI ready {w}x{h} (screen {sw}x{sh})");
                }

                var cw = _captureW;
                var ch = _captureH;
                if (!BitBlt(memDC, 0, 0, cw, ch, screenDC, 0, 0, SRCCOPY))
                {
                    // 解锁后 Winlogon 桌面不再可见 BitBlt 可能失败；客户端随即会停掉代理。
                    // 节流记录避免刷爆日志。
                    if (bltFails == 0 || bltFails % 20 == 0)
                        Log($"capture: BitBlt failed err={Marshal.GetLastWin32Error()} (#{bltFails + 1})");
                    bltFails++;
                    Thread.Sleep(CAPTURE_INTERVAL_MS);
                    continue;
                }

                var buf = new byte[cw * ch * 4];
                var bi = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = cw,
                    biHeight = -ch, // 负高度 = top-down 行序（与 DXGI 路径一致）
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0 // BI_RGB
                };
                unsafe
                {
                    fixed (byte* p = buf)
                    {
                        if (GetDIBits(memDC, bmp, 0, (uint)ch, p, ref bi, 0) == 0)
                        {
                            Log($"capture: GetDIBits failed err={Marshal.GetLastWin32Error()}");
                            return;
                        }
                    }
                }

                var now = DateTime.UtcNow;
                var changed = prev == null || !buf.AsSpan().SequenceEqual(prev);
                if (changed || (now - lastSent).TotalMilliseconds >= KEEPALIVE_MS)
                {
                    // [type 1B][len 3B LE] + [宽 2B][高 2B][BGRA top-down]
                    var payload = 4 + buf.Length;
                    var frame = new byte[4 + payload];
                    frame[0] = TYPE_VIDEO;
                    frame[1] = (byte)(payload & 0xFF);
                    frame[2] = (byte)(payload >> 8 & 0xFF);
                    frame[3] = (byte)(payload >> 16 & 0xFF);
                    frame[4] = (byte)(cw & 0xFF);
                    frame[5] = (byte)(cw >> 8 & 0xFF);
                    frame[6] = (byte)(ch & 0xFF);
                    frame[7] = (byte)(ch >> 8 & 0xFF);
                    Buffer.BlockCopy(buf, 0, frame, 8, buf.Length);
                    stream.Write(frame, 0, frame.Length);
                    stream.Flush();
                    prev = buf;
                    lastSent = now;
                    framesSent++;
                    if (framesSent == 1 || framesSent % 40 == 0)
                        Log($"capture: sent frame #{framesSent} ({cw}x{ch}, {frame.Length} bytes, changed={changed})");
                }
                Thread.Sleep(CAPTURE_INTERVAL_MS);
            }
        }
        catch (Exception ex)
        {
            // 客户端断开（解锁停止代理）时 Write 抛异常，属正常退出
            Log($"capture: loop ended ({framesSent} frames sent, {ex.Message})");
        }
        finally
        {
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (memDC != IntPtr.Zero) DeleteDC(memDC);
            if (screenDC != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDC);
        }
    }

    // ============ 输入注入（与 RemoteInputHandler 帧格式一致） ============

    private const byte ACTION_MOUSE_MOVE = 0;
    private const byte ACTION_LEFT_DOWN = 1;
    private const byte ACTION_LEFT_UP = 2;
    private const byte ACTION_RIGHT_DOWN = 3;
    private const byte ACTION_RIGHT_UP = 4;
    private const byte ACTION_MIDDLE_DOWN = 5;
    private const byte ACTION_MIDDLE_UP = 6;

    private static void InjectMouse(byte[] data, int videoW, int videoH)
    {
        var action = data[0];
        var x = (ushort)(data[1] | data[2] << 8);
        var y = (ushort)(data[3] | data[4] << 8);
        var (dx, dy) = Normalize(x, y, videoW, videoH);
        uint flags = action switch
        {
            ACTION_MOUSE_MOVE => MOUSEEVENTF_MOVE,
            ACTION_LEFT_DOWN => MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_MOVE,
            ACTION_LEFT_UP => MOUSEEVENTF_LEFTUP | MOUSEEVENTF_MOVE,
            ACTION_RIGHT_DOWN => MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_MOVE,
            ACTION_RIGHT_UP => MOUSEEVENTF_RIGHTUP | MOUSEEVENTF_MOVE,
            ACTION_MIDDLE_DOWN => MOUSEEVENTF_MIDDLEDOWN | MOUSEEVENTF_MOVE,
            ACTION_MIDDLE_UP => MOUSEEVENTF_MIDDLEUP | MOUSEEVENTF_MOVE,
            _ => 0
        };
        if (flags == 0) return;
        SendMouseInput(dx, dy, 0, flags | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
    }

    private static void InjectKey(byte[] data)
    {
        var vkCode = (ushort)(data[0] | data[1] << 8);
        var down = data[2] != 0;
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vkCode,
                    dwFlags = down ? 0u : KEYEVENTF_KEYUP,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void InjectWheel(byte[] data, int videoW, int videoH)
    {
        var delta = (short)(data[0] | data[1] << 8);
        var x = (ushort)(data[2] | data[3] << 8);
        var y = (ushort)(data[4] | data[5] << 8);
        // 水平滚动增量（v1.1.43 起 Android 端追加在帧尾；旧 6B 帧无此字段，视为 0）
        short deltaH = 0;
        if (data.Length >= 8) deltaH = (short)(data[6] | data[7] << 8);
        var (dx, dy) = Normalize(x, y, videoW, videoH);
        // 垂直/水平互斥 dwFlags，分别注入（均带 MOVE 定位光标）
        if (delta != 0)
            SendMouseInput(dx, dy, unchecked((uint)delta),
                MOUSEEVENTF_WHEEL | MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
        if (deltaH != 0)
            SendMouseInput(dx, dy, unchecked((uint)deltaH),
                MOUSEEVENTF_HWHEEL | MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
    }

    /// <summary>文本帧注入：UTF-8 解码后逐字符 Unicode 注入（锁屏密码框打字）。</summary>
    private static void InjectText(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        foreach (var ch in text)
            TypeUnicode(ch);
    }

    private static (int, int) Normalize(int x, int y, int videoW, int videoH)
    {
        var dx = (int)((long)Math.Clamp(x, 0, videoW - 1) * 65535 / videoW);
        var dy = (int)((long)Math.Clamp(y, 0, videoH - 1) * 65535 / videoH);
        return (dx, dy);
    }

    private static void SendMouseInput(int dx, int dy, uint mouseData, uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = mouseData,
                    dwFlags = flags,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // ============ unlock 模式按键注入 ============

    /// <summary>回车（按下+抬起），返回两次 SendInput 是否全部成功。</summary>
    private static bool PressEnter()
    {
        var a = SendKey(VK_RETURN, 0, 0);
        var b = SendKey(VK_RETURN, 0, KEYEVENTF_KEYUP);
        return a && b;
    }

    /// <summary>Unicode 字符注入（按下+抬起），返回是否成功。</summary>
    private static bool TypeUnicode(char ch)
    {
        var a = SendKey(0, ch, KEYEVENTF_UNICODE);
        var b = SendKey(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
        return a && b;
    }

    /// <summary>点击屏幕中心（Win11 锁屏唤出密码框）。</summary>
    private static void ClickCenter()
    {
        SendMouseInput(65535 / 2, 65535 / 2, 0,
            MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
    }

    private static bool SendKey(ushort vk, ushort scan, uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = scan,
                    dwFlags = flags,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        return SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1;
    }

    // ============ 桌面切换（SYSTEM 权限） ============

    private static bool SwitchToWinlogonDesktop()
    {
        var winsta = OpenWindowStationW("WinSta0", false, READ_CONTROL | WRITE_DAC | GENERIC_ALL);
        if (winsta != IntPtr.Zero) SetProcessWindowStation(winsta);

        var desktop = OpenDesktopW("Winlogon", 0, false, GENERIC_ALL);
        if (desktop == IntPtr.Zero)
        {
            Log($"OpenDesktop(Winlogon) failed: {Marshal.GetLastWin32Error()}");
            return false;
        }
        if (!SetThreadDesktop(desktop))
        {
            Log($"SetThreadDesktop failed: {Marshal.GetLastWin32Error()}");
            return false;
        }
        return true;
    }

    private static void Log(string message)
    {
        // SYSTEM 计划任务进程的 GetTempPath 指向 C:\Windows\Temp（普通用户读不到），
        // 统一写 ProgramData 固定路径，客户端/用户可直接查看排查
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n";
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QuickRemote");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "qr-agent.log"), line);
        }
        catch { }
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "qr-agent.log"), line);
        }
        catch { }
    }

    // ============ Win32 P/Invoke ============

    private const uint GENERIC_ALL = 0x10000000;
    private const uint READ_CONTROL = 0x00020000;
    private const uint WRITE_DAC = 0x00040000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenWindowStationW([MarshalAs(UnmanagedType.LPWStr)] string lpszWinSta,
        bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessWindowStation(IntPtr hWinsta);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenDesktopW([MarshalAs(UnmanagedType.LPWStr)] string lpszDesktop,
        uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetUserObjectInformationW(IntPtr hObj, int nIndex,
        [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pvInfo, uint cch, out uint pcchWritten);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    // ============ GDI 捕获（锁屏画面） ============

    private const uint SRCCOPY = 0x00CC0020;
    private const int HORZRES = 8;
    private const int VERTRES = 10;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern unsafe int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        byte* lpBits, ref BITMAPINFOHEADER lpbi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_RETURN = 0x0D;
}
