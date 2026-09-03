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

        // 3. 唤出密码框（回车）→ 输入密码 → 提交（回车）
        Log("typing unlock sequence on Winlogon desktop");
        PressEnter();
        Thread.Sleep(800);
        foreach (var ch in password) TypeUnicode(ch);
        Thread.Sleep(300);
        PressEnter();

        Log("unlock sequence sent");
        return 0;
    }

    // ======================================================================
    // agent 模式：锁屏输入代理
    // ======================================================================

    // 帧类型（0x02/0x03/0x04 与主程序 RemoteFrameProtocol 一致）
    private const byte TYPE_MOUSE = 0x02;
    private const byte TYPE_KEY = 0x03;
    private const byte TYPE_WHEEL = 0x04;
    private const byte TYPE_SETUP = 0x10; // [宽 2B][高 2B] 归一化用分辨率
    private const byte TYPE_TOKEN = 0x11; // [token 文本] 连接身份校验

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

        // 3. 网络线程：accept（120 秒无连接自杀）→ 校验 token → 读帧入队
        var queue = new BlockingCollection<(byte type, byte[] data)>(256);
        var netThread = new Thread(() => NetworkLoop(listener, token, queue))
        {
            IsBackground = false
        };
        netThread.Start();

        // 4. 主线程（已切 Winlogon 桌面）：消费帧队列并注入
        int videoW = 1920, videoH = 1080;
        try
        {
            foreach (var (type, data) in queue.GetConsumingEnumerable())
            {
                switch (type)
                {
                    case TYPE_SETUP when data.Length >= 4:
                        videoW = Math.Max(1, data[0] | data[1] << 8);
                        videoH = Math.Max(1, data[2] | data[3] << 8);
                        Log($"agent video size: {videoW}x{videoH}");
                        break;
                    case TYPE_MOUSE when data.Length >= 5:
                        InjectMouse(data, videoW, videoH);
                        break;
                    case TYPE_KEY when data.Length >= 3:
                        InjectKey(data);
                        break;
                    case TYPE_WHEEL when data.Length >= 6:
                        InjectWheel(data, videoW, videoH);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"agent inject loop error: {ex.Message}");
        }

        Log("input agent exiting");
        return 0;
    }

    /// <summary>网络线程：accept 一个连接 → 校验 token 帧 → 循环读帧入队。</summary>
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
        var (dx, dy) = Normalize(x, y, videoW, videoH);
        SendMouseInput(dx, dy, unchecked((uint)delta),
            MOUSEEVENTF_WHEEL | MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
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

    private static void PressEnter()
    {
        SendKey(VK_RETURN, 0, 0);
        SendKey(VK_RETURN, 0, KEYEVENTF_KEYUP);
    }

    private static void TypeUnicode(char ch)
    {
        SendKey(0, ch, KEYEVENTF_UNICODE);
        SendKey(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
    }

    private static void SendKey(ushort vk, ushort scan, uint flags)
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
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
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
        try
        {
            var logFile = Path.Combine(Path.GetTempPath(), "qr-agent.log");
            File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
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
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_RETURN = 0x0D;
}
