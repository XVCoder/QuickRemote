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

        _menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = true,
            Font = new Font("Segoe UI", 9f)
        };
        var showItem = new Forms.ToolStripMenuItem("显示主窗口")
        {
            Image = CreateWindowIcon()
        };
        showItem.Click += (_, _) => ShowMainWindowRequested?.Invoke();
        var exitItem = new Forms.ToolStripMenuItem("退出")
        {
            Image = CreateExitIcon()
        };
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

    /// <summary>绘制「显示主窗口」菜单图标（窗口形状，主题蓝）。</summary>
    private static Image CreateWindowIcon()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(0x3B, 0x82, 0xF6), 1.3f);
        g.DrawRectangle(pen, 2, 3, 12, 10);   // 窗口主体
        g.DrawLine(pen, 2, 3, 14, 3);         // 标题栏
        g.DrawLine(pen, 4, 5, 8, 5);          // 标题栏按钮
        return bmp;
    }

    /// <summary>绘制「退出」菜单图标（×，红色）。</summary>
    private static Image CreateExitIcon()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(0xEF, 0x44, 0x44), 1.5f);
        g.DrawLine(pen, 4, 4, 12, 12);
        g.DrawLine(pen, 12, 4, 4, 12);
        return bmp;
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

    /// <summary>深色主题配色表（与主界面 BgCard/Border/Accent 一致）。</summary>
    private sealed class DarkColorTable : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(0x1C, 0x20, 0x30);
        public override Color ImageMarginGradientBegin => Color.FromArgb(0x1C, 0x20, 0x30);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(0x1C, 0x20, 0x30);
        public override Color ImageMarginGradientEnd => Color.FromArgb(0x1C, 0x20, 0x30);
        public override Color MenuBorder => Color.FromArgb(0x2A, 0x2F, 0x3E);
        public override Color MenuItemBorder => Color.FromArgb(0x3B, 0x82, 0xF6);
        public override Color MenuItemSelected => Color.FromArgb(0x23, 0x28, 0x38);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(0x23, 0x28, 0x38);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(0x23, 0x28, 0x38);
        public override Color SeparatorDark => Color.FromArgb(0x2A, 0x2F, 0x3E);
        public override Color SeparatorLight => Color.FromArgb(0x2A, 0x2F, 0x3E);
    }

    /// <summary>深色主题菜单渲染器：深色背景、浅色文字、选中高亮 + 左侧主题色条。</summary>
    private sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Color.White : Color.FromArgb(0xE4, 0xE6, 0xEB);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected)
            {
                var rc = new Rectangle(0, 0, e.Item.Width, e.Item.Height);
                using var brush = new SolidBrush(Color.FromArgb(0x23, 0x28, 0x38));
                e.Graphics.FillRectangle(brush, rc);
                using var accent = new SolidBrush(Color.FromArgb(0x3B, 0x82, 0xF6));
                e.Graphics.FillRectangle(accent, 0, 1, 3, rc.Height - 2);
            }
            else
            {
                base.OnRenderMenuItemBackground(e);
            }
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(0x2A, 0x2F, 0x3E));
            e.Graphics.DrawLine(pen, 30, e.Item.Height / 2, e.Item.Width - 10, e.Item.Height / 2);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is Forms.ContextMenuStrip)
            {
                using var pen = new Pen(Color.FromArgb(0x2A, 0x2F, 0x3E));
                var rc = e.AffectedBounds;
                rc.Width -= 1;
                rc.Height -= 1;
                e.Graphics.DrawRectangle(pen, rc);
            }
            else
            {
                base.OnRenderToolStripBorder(e);
            }
        }
    }
}
