using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 本机剪贴板监听与读写。仅处理纯文本。
///
/// 回环切断：记录"最后一次由对端写入 / 最后一次已上报"的内容哈希，
/// 剪贴板变化时哈希相同则不上报 —— 否则被控端写入会对端 → 对端再推回来，
/// 形成两端互刷的无限回环。
///
/// 线程模型：WM_CLIPBOARDUPDATE 只会投递到**创建监听窗口的那个线程**的消息队列，
/// 且该线程必须有消息泵。因此监听窗口必须在 WPF UI（Dispatcher/STA）线程创建 ——
/// 若在会话线程（线程池，无消息泵）创建，事件永远不会到达。
/// </summary>
public sealed class ClipboardSync : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private HwndSource? _source;
    private System.Windows.Threading.Dispatcher? _dispatcher;
    private string _lastHash = string.Empty;
    private bool _disposed;

    /// <summary>本机剪贴板文本变化（在 UI 线程触发）。</summary>
    public event Action<string>? TextChanged;

    /// <summary>
    /// 在 WPF UI 线程创建监听窗口并注册剪贴板通知。
    /// 无 WPF Application（如单元测试/无 UI 宿主）时静默降级为不可用实例。
    /// </summary>
    public ClipboardSync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        _dispatcher = dispatcher;
        if (dispatcher.CheckAccess()) CreateListener();
        else dispatcher.Invoke(CreateListener);
    }

    private void CreateListener()
    {
        try
        {
            var parameters = new HwndSourceParameters("QuickRemoteClipboardListener")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            if (!AddClipboardFormatListener(_source.Handle))
            {
                // 注册失败（极少见）：释放窗口，退化为"只能单向推送"（读取仍可用）
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
        }
        catch
        {
            _source = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) TryReadAndRaise();
        return IntPtr.Zero;
    }

    /// <summary>读取本机剪贴板并上报（哈希未变则跳过）。</summary>
    public void TryReadAndRaise()
    {
        var text = ReadText();
        if (text == null) return;
        var hash = Hash(text);
        if (hash == _lastHash) return;
        _lastHash = hash;
        TextChanged?.Invoke(text);
    }

    /// <summary>读取本机剪贴板文本，无文本返回 null。</summary>
    public static string? ReadText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : null;
        }
        catch
        {
            // 剪贴板被其它进程独占锁定（OpenClipboard 失败）时放弃本次读取
            return null;
        }
    }

    /// <summary>
    /// 由对端推送来的文本写入本机剪贴板。
    ///
    /// 两点必须做对：
    /// 1. **先记哈希再写**：写完后本机监听器会收到 WM_CLIPBOARDUPDATE，
    ///    哈希已匹配 → 不上报，切断"对端推来 → 又推回对端"的回环。
    /// 2. **切回 UI（STA）线程写**：本方法由会话 ReadLoop（MTA 线程）调用，
    ///    而 WPF Clipboard.SetText 要求 STA，直接调用会抛 ThreadStateException。
    /// </summary>
    public void ApplyRemote(string text)
    {
        _lastHash = Hash(text);

        var dispatcher = _dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            WriteText(text);
            return;
        }

        try
        {
            dispatcher.BeginInvoke(new Action(() => WriteText(text)));
        }
        catch
        {
            // Dispatcher 已关闭（应用退出中），丢弃本次同步
        }
    }

    /// <summary>写入本机剪贴板；剪贴板被占用时短暂重试一次。</summary>
    public static void WriteText(string text)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch
            {
                if (attempt == 0) System.Threading.Thread.Sleep(60);
            }
        }
    }

    private static string Hash(string s)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        void Teardown()
        {
            try
            {
                if (_source == null) return;
                RemoveClipboardFormatListener(_source.Handle);
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
            catch
            {
                // ignore
            }
        }

        var dispatcher = _source?.Dispatcher ?? _dispatcher;
        if (dispatcher == null) { Teardown(); return; }

        // HwndSource 只能在创建它的线程销毁。
        // 用 BeginInvoke 而非 Invoke：Cleanup 可能由后台线程触发，而 UI 线程此刻
        // 可能在等待该后台线程 —— Invoke 会同步阻塞直到 UI 线程空闲，形成死锁。
        if (dispatcher.CheckAccess()) Teardown();
        else
        {
            try { dispatcher.BeginInvoke(new Action(Teardown)); }
            catch { /* Dispatcher 已关闭，交由 AppDomain 回收 */ }
        }
    }
}
