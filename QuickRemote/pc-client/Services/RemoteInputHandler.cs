using System.Runtime.InteropServices;
using System.Text;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 远程输入处理器：解析 Android 端输入帧，用 SendInput 模拟鼠标/键盘/滚轮。
///
/// 帧格式（与 Android 端约定一致）：
///   INPUT_MOUSE (0x02): [action 1B][x 2B][y 2B]
///     action: 0=移动 1=左按下 2=左释放 3=右按下 4=右释放 5=中按下 6=中释放
///   INPUT_KEY   (0x03): [vkCode 2B][down 1B]
///   INPUT_WHEEL (0x04): [delta 2B(有符号)][x 2B][y 2B]
///   INPUT_TEXT  (0x08): [UTF-8 文本]（中文/emoji，KEYEVENTF_UNICODE 注入）
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

    // ============ 多显示器映射（视频像素 → 虚拟桌面物理像素） ============
    // 视频画面只是被捕获的那一块显示器，而 MOUSEEVENTF_VIRTUALDESK 的 0..65535
    // 覆盖整个虚拟桌面。缺少这两个偏移/尺寸时，双屏下点按会被压缩到另一块屏。

    /// <summary>被捕获显示器在虚拟桌面中的左上角（物理像素，可为负）。</summary>
    public int MonitorLeft { get; private set; }

    /// <summary>被捕获显示器在虚拟桌面中的顶边（物理像素，可为负）。</summary>
    public int MonitorTop { get; private set; }

    /// <summary>被捕获显示器物理宽（0=未知，按视频宽处理）。</summary>
    public int MonitorWidth { get; private set; }

    /// <summary>被捕获显示器物理高（0=未知，按视频高处理）。</summary>
    public int MonitorHeight { get; private set; }

    /// <summary>虚拟桌面左上角 X（物理像素）。</summary>
    public int VirtualLeft { get; private set; }

    /// <summary>虚拟桌面左上角 Y（物理像素）。</summary>
    public int VirtualTop { get; private set; }

    /// <summary>虚拟桌面宽（物理像素，0=未知，按显示器宽处理）。</summary>
    public int VirtualWidth { get; private set; }

    /// <summary>虚拟桌面高（物理像素）。</summary>
    public int VirtualHeight { get; private set; }

    /// <summary>
    /// 设置多显示器坐标映射（会话启动/捕获重建时更新）。
    /// 输入帧在线程池线程处理，整体加锁避免读到半更新的映射。
    /// </summary>
    public void SetDesktopMapping(
        int monitorLeft, int monitorTop, int monitorWidth, int monitorHeight,
        int virtualLeft, int virtualTop, int virtualWidth, int virtualHeight)
    {
        lock (_lock)
        {
            MonitorLeft = monitorLeft;
            MonitorTop = monitorTop;
            MonitorWidth = monitorWidth;
            MonitorHeight = monitorHeight;
            VirtualLeft = virtualLeft;
            VirtualTop = virtualTop;
            VirtualWidth = virtualWidth;
            VirtualHeight = virtualHeight;
        }
    }

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
                case RemoteFrameProtocol.TYPE_INPUT_TEXT when data.Length >= 1:
                    HandleText(data);
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

    /// <summary>
    /// 视频像素 → SendInput 归一化坐标（0-65535，配合 MOUSEEVENTF_ABSOLUTE|VIRTUALDESK）。
    /// 先映射到被捕获显示器的桌面物理像素，再在整个虚拟桌面上归一化：
    /// 双屏时画面只覆盖其中一块屏，直接按视频宽高归一化会把点按压缩/平移到另一块屏。
    /// </summary>
    private (int dx, int dy) Normalize(int x, int y)
    {
        int vw, vh, mLeft, mTop, mw, mh, vLeft, vTop, vW, vH;
        lock (_lock)
        {
            vw = VideoWidth > 0 ? VideoWidth : 1920;
            vh = VideoHeight > 0 ? VideoHeight : 1080;
            mLeft = MonitorLeft;
            mTop = MonitorTop;
            mw = MonitorWidth > 0 ? MonitorWidth : vw;
            mh = MonitorHeight > 0 ? MonitorHeight : vh;
            vLeft = VirtualLeft;
            vTop = VirtualTop;
            vW = VirtualWidth > 0 ? VirtualWidth : mw;
            vH = VirtualHeight > 0 ? VirtualHeight : mh;
        }

        // 视频像素 → 被捕获显示器桌面像素（物理）
        var px = mLeft + (long)Math.Clamp(x, 0, vw - 1) * mw / vw;
        var py = mTop + (long)Math.Clamp(y, 0, vh - 1) * mh / vh;

        // 桌面像素 → 虚拟桌面归一化坐标（65535 = 虚拟桌面右下角最后一个像素）
        var dx = (int)Math.Round((px - vLeft) * 65535.0 / Math.Max(1, vW - 1));
        var dy = (int)Math.Round((py - vTop) * 65535.0 / Math.Max(1, vH - 1));
        return (Math.Clamp(dx, 0, 65535), Math.Clamp(dy, 0, 65535));
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

    /// <summary>
    /// Unicode 文本注入：逐 UTF-16 码元以 KEYEVENTF_UNICODE 模拟按键
    /// （wVk=0 + wScan=码元；代理对由系统按相邻事件组合成完整字符）。
    /// </summary>
    private void HandleText(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        foreach (var ch in text)
        {
            SendUnicodeKey(ch, down: true);
            SendUnicodeKey(ch, down: false);
        }
    }

    private void SendUnicodeKey(char ch, bool down)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = (down ? 0u : KEYEVENTF_KEYUP) | KEYEVENTF_UNICODE,
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
        // 水平滚动增量（v1.1.43 起 Android 端追加在帧尾；旧 6B 帧无此字段，视为 0）
        short deltaH = 0;
        if (data.Length >= 8) deltaH = (short)(data[6] | data[7] << 8);
        var (dx, dy) = Normalize(x, y);

        // 垂直/水平是互斥的 dwFlags，各自构造输入分别提交
        if (delta != 0) SendWheelInput(dx, dy, delta, MOUSEEVENTF_WHEEL);
        if (deltaH != 0) SendWheelInput(dx, dy, deltaH, MOUSEEVENTF_HWHEEL);
    }

    /// <summary>滚轮输入：wheelFlag 选 WHEEL(垂直)/HWHEEL(水平)，带 MOVE 定位光标到指定坐标。</summary>
    private void SendWheelInput(int dx, int dy, int delta, uint wheelFlag)
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
                    mouseData = unchecked((uint)delta),
                    // 带 MOVE 保证滚轮事件同时把光标定位到指定坐标
                    dwFlags = wheelFlag | MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
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
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

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
