using System.Runtime.InteropServices;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 远程输入处理器：解析 Android 端输入帧，用 SendInput 模拟鼠标/键盘/滚轮。
///
/// 帧格式（与 Android 端约定一致）：
///   INPUT_MOUSE (0x02): [action 1B][x 2B][y 2B]
///     action: 0=移动 1=左按下 2=左释放 3=右按下 4=右释放 5=中按下 6=中释放
///   INPUT_KEY   (0x03): [vkCode 2B][down 1B]
///   INPUT_WHEEL (0x04): [delta 2B(有符号)][x 2B][y 2B]
/// </summary>
public sealed class RemoteInputHandler
{
    // ============ 帧类型 ============
    public const byte ACTION_MOUSE_MOVE = 0;
    public const byte ACTION_LEFT_DOWN = 1;
    public const byte ACTION_LEFT_UP = 2;
    public const byte ACTION_RIGHT_DOWN = 3;
    public const byte ACTION_RIGHT_UP = 4;
    public const byte ACTION_MIDDLE_DOWN = 5;
    public const byte ACTION_MIDDLE_UP = 6;

    private readonly object _lock = new();

    /// <summary>日志回调（由上层注入，SendInput 失败时记录）。</summary>
    public static Action<string>? LogError;

    /// <summary>远程画面宽度（像素），用于坐标归一化。</summary>
    public int VideoWidth { get; set; } = 1920;

    /// <summary>远程画面高度（像素），用于坐标归一化。</summary>
    public int VideoHeight { get; set; } = 1080;

    /// <summary>处理一帧输入数据。</summary>
    public void HandleFrame(byte type, byte[] data)
    {
        try
        {
            switch (type)
            {
                case RemoteFrameProtocol.TYPE_INPUT_MOUSE when data.Length >= 5:
                    HandleMouse(data);
                    break;
                case RemoteFrameProtocol.TYPE_INPUT_KEY when data.Length >= 3:
                    HandleKey(data);
                    break;
                case RemoteFrameProtocol.TYPE_INPUT_WHEEL when data.Length >= 6:
                    HandleWheel(data);
                    break;
            }
        }
        catch
        {
            // 输入模拟失败不影响会话
        }
    }

    // ============ 鼠标 ============

    private void HandleMouse(byte[] data)
    {
        var action = data[0];
        var x = (ushort)(data[1] | data[2] << 8);
        var y = (ushort)(data[3] | data[4] << 8);

        switch (action)
        {
            case ACTION_MOUSE_MOVE:
                MoveMouse(x, y);
                break;
            case ACTION_LEFT_DOWN:
                SendMouse(x, y, MOUSEEVENTF_LEFTDOWN);
                break;
            case ACTION_LEFT_UP:
                SendMouse(x, y, MOUSEEVENTF_LEFTUP);
                break;
            case ACTION_RIGHT_DOWN:
                SendMouse(x, y, MOUSEEVENTF_RIGHTDOWN);
                break;
            case ACTION_RIGHT_UP:
                SendMouse(x, y, MOUSEEVENTF_RIGHTUP);
                break;
            case ACTION_MIDDLE_DOWN:
                SendMouse(x, y, MOUSEEVENTF_MIDDLEDOWN);
                break;
            case ACTION_MIDDLE_UP:
                SendMouse(x, y, MOUSEEVENTF_MIDDLEUP);
                break;
        }
    }

    /// <summary>像素坐标 → SendInput 归一化坐标（0-65535，配合 MOUSEEVENTF_ABSOLUTE）。</summary>
    private (int dx, int dy) Normalize(int x, int y)
    {
        var vw = VideoWidth > 0 ? VideoWidth : 1920;
        var vh = VideoHeight > 0 ? VideoHeight : 1080;
        var dx = (int)((long)Math.Clamp(x, 0, vw - 1) * 65535 / vw);
        var dy = (int)((long)Math.Clamp(y, 0, vh - 1) * 65535 / vh);
        return (dx, dy);
    }

    /// <summary>绝对坐标移动鼠标（像素坐标 → 归一化）。</summary>
    private void MoveMouse(int x, int y)
    {
        var (dx, dy) = Normalize(x, y);
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInputInternal(ref input);
    }

    private void SendMouse(int x, int y, uint flags)
    {
        var (dx, dy) = Normalize(x, y);
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    // 必须带 MOVE：不带时 SendInput 忽略 dx/dy，点击会落在旧光标位置
                    dwFlags = flags | MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInputInternal(ref input);
    }

    // ============ 键盘 ============

    private void HandleKey(byte[] data)
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
        SendInputInternal(ref input);
    }

    // ============ 滚轮 ============

    private void HandleWheel(byte[] data)
    {
        var delta = (short)(data[0] | data[1] << 8);
        var x = (ushort)(data[2] | data[3] << 8);
        var y = (ushort)(data[4] | data[5] << 8);
        var (dx, dy) = Normalize(x, y);

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = unchecked((uint)delta),
                    // 带 MOVE 保证滚轮事件同时把光标定位到指定坐标
                    dwFlags = MOUSEEVENTF_WHEEL | MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInputInternal(ref input);
    }

    private void SendInputInternal(ref INPUT input)
    {
        lock (_lock)
        {
            var ok = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
            if (ok != 1)
            {
                // 返回 0 = 注入被拦截（UIPI/权限/会话问题），远程点击将完全无反应
                LogError?.Invoke($"SendInput failed: returned {ok}, lastError={Marshal.GetLastWin32Error()}");
            }
        }
    }

    // ============ SendInput P/Invoke ============

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
}
