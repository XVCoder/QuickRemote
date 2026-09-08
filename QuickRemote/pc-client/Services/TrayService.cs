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
        // 打开时把菜单裁剪为圆角（腾讯管家风格）
        _menu.Opened += (_, _) =>
        {
            if (_menu.Width > 0 && _menu.Height > 0)
            {
                _menu.Region = new Region(CreateRoundedPath(new Rectangle(0, 0, _menu.Width, _menu.Height), 8));
            }
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

    /// <summary>创建圆角矩形路径（供菜单 Region 裁剪与圆角绘制）。</summary>
    internal static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>深色主题配色表（与 App.xaml 色板一致：卡片底 #25262C、边框 #3A3C45、强调蓝 #3D8BFF）。</summary>
    private sealed class DarkColorTable : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(0x25, 0x26, 0x2C);
        public override Color ImageMarginGradientBegin => Color.FromArgb(0x25, 0x26, 0x2C);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(0x25, 0x26, 0x2C);
        public override Color ImageMarginGradientEnd => Color.FromArgb(0x25, 0x26, 0x2C);
        public override Color MenuBorder => Color.FromArgb(0x3A, 0x3C, 0x45);
        public override Color MenuItemBorder => Color.FromArgb(0x3D, 0x8B, 0xFF);
        public override Color MenuItemSelected => Color.FromArgb(0x31, 0x33, 0x3B);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(0x31, 0x33, 0x3B);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(0x31, 0x33, 0x3B);
        public override Color SeparatorDark => Color.FromArgb(0x3A, 0x3C, 0x45);
        public override Color SeparatorLight => Color.FromArgb(0x3A, 0x3C, 0x45);
    }

    /// <summary>深色主题菜单渲染器：深色背景、浅色文字、圆角高亮选中项。</summary>
    private sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Color.White : Color.FromArgb(0xF2, 0xF3, 0xF5);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected)
            {
                // 整块圆角高亮（腾讯管家风格），左右留 4px 边距
                var rc = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
                using var path = CreateRoundedPath(rc, 5);
                using var brush = new SolidBrush(Color.FromArgb(0x31, 0x33, 0x3B));
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
            }
            else
            {
                base.OnRenderMenuItemBackground(e);
            }
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(0x3A, 0x3C, 0x45));
            e.Graphics.DrawLine(pen, 34, e.Item.Height / 2, e.Item.Width - 12, e.Item.Height / 2);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            // 菜单整体已由圆角 Region 裁剪，不再绘制直角边框
        }
    }
}
