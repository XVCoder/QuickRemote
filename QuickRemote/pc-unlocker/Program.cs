// QuickRemote.Unlocker — 远程解锁辅助程序。
//
// Windows 锁屏后输入框位于 Winlogon 安全桌面，普通用户态进程无法注入按键，
// 必须由 SYSTEM 权限进程执行。本程序由 PC 客户端通过计划任务
// （schtasks /RU SYSTEM /IT，交互会话内以 SYSTEM 运行）启动，职责：
//   1. 从密码临时文件读取 Windows 登录密码（读完立即删除）
//   2. 切换到 Winlogon 安全桌面
//   3. 注入按键序列：回车（唤出密码框）→ 密码 → 回车（提交解锁）
//
// 用法：QuickRemote.Unlocker.exe <密码文件路径>

using System.Runtime.InteropServices;

namespace QuickRemote.Unlocker;

internal static class Program
{
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

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
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

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_RETURN = 0x0D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Log("usage: QuickRemote.Unlocker.exe <password-file>");
            return 1;
        }

        // 1. 读取密码并立即删除临时文件（密码不落命令行）
        string password;
        try
        {
            var pwFile = args[0];
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
        var winsta = OpenWindowStationW("WinSta0", false, READ_CONTROL | WRITE_DAC | GENERIC_ALL);
        if (winsta != IntPtr.Zero) SetProcessWindowStation(winsta);

        var desktop = OpenDesktopW("Winlogon", 0, false, GENERIC_ALL);
        if (desktop == IntPtr.Zero)
        {
            Log($"OpenDesktop(Winlogon) failed: {Marshal.GetLastWin32Error()}");
            return 3;
        }
        if (!SetThreadDesktop(desktop))
        {
            Log($"SetThreadDesktop failed: {Marshal.GetLastWin32Error()}");
            return 3;
        }

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
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void Log(string message)
    {
        try
        {
            var logFile = Path.Combine(Path.GetTempPath(), "qr-unlock.log");
            File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }
}
