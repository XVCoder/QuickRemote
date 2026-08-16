using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows;
using QuickRemote.PCClient.Models;
using Forms = System.Windows.Forms;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 系统托盘服务。使用 WinForms NotifyIcon。
/// 托盘图标为应用图标，状态变化通过文本和气泡提示。
/// 右键菜单: 显示主窗口、退出。双击显示主窗口。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _appIcon;
    private bool _disposed;

    /// <summary>用户点击"退出"时触发。</summary>
    public event Action? ExitRequested;

    /// <summary>用户请求显示主窗口时触发。</summary>
    public event Action? ShowMainWindowRequested;

    public TrayService()
    {
        _appIcon = LoadAppIcon();

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "QuickRemote PC - 未连接",
            Visible = true,
            Icon = (Icon)_appIcon.Clone()
        };

        _menu = new Forms.ContextMenuStrip();
        var showItem = new Forms.ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => ShowMainWindowRequested?.Invoke();
        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.Add(showItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = _menu;
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindowRequested?.Invoke();
    }

    /// <summary>根据连接状态更新托盘提示文本，并在状态变化时显示气泡。</summary>
    public void UpdateStatus(ConnectionStatus status)
    {
        if (_disposed) return;
        var text = status switch
        {
            ConnectionStatus.Connected => "QuickRemote PC - 已连接",
            ConnectionStatus.Connecting => "QuickRemote PC - 连接中",
            ConnectionStatus.Reconnecting => "QuickRemote PC - 重连中",
            _ => "QuickRemote PC - 未连接"
        };

        try
        {
            var oldText = _notifyIcon.Text;
            _notifyIcon.Text = text.Length > 63 ? text[..63] : text;

            // 状态变化时显示气泡通知
            if (oldText != _notifyIcon.Text)
            {
                _notifyIcon.BalloonTipTitle = "QuickRemote";
                _notifyIcon.BalloonTipText = text;
                _notifyIcon.ShowBalloonTip(2000);
            }
        }
        catch { }
    }

    /// <summary>显示气泡通知。</summary>
    public void ShowBalloon(string title, string message, int timeoutMs = 3000)
    {
        if (_disposed) return;
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.ShowBalloonTip(timeoutMs);
        }
        catch { }
    }

    /// <summary>从嵌入资源加载应用图标。</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("app.png", StringComparison.OrdinalIgnoreCase));
            if (resourceName != null)
            {
                using var stream = asm.GetManifestResourceStream(resourceName)!;
                using var bmp = new Bitmap(stream);
                var handle = bmp.GetHicon();
                return Icon.FromHandle(handle);
            }
        }
        catch { }

        // 回退：从 exe 提取关联图标
        return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Icon?.Dispose();
            _appIcon.Dispose();
            _menu.Dispose();
            _notifyIcon.Dispose();
        }
        catch { }
    }
}
