using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickRemote.PCClient.ViewModels;
using QuickRemote.PCClient.Views;

namespace SettingsShot;

/// <summary>桩数据源：只为离屏渲染提供绑定值，不参与任何真实网络/服务逻辑。</summary>
internal sealed class StubVm : IChangelogSource
{
    public string ChangelogUrl { get; init; } = "";
    public string Version => "1.1.66";
    public string UpdateStatusText => "当前 v1.1.66 已是最新版本";
    public string SaveSettingsText => "保存设置";
    public ICommand CheckUpdateCommand { get; } = new StubCommand();
    public ICommand SaveSettingsCommand { get; } = new StubCommand();
}

internal sealed class StubCommand : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) { }
}

/// <summary>
/// 临时工作台：离屏渲染 SettingsWindow「版本更新」页为 PNG 供视觉校验。
/// 更新记录正文直接读本地 CHANGELOG.md（不走网络，避免沙箱网络/消息泵干扰）。
/// </summary>
internal static class Program
{
    private static string _log = "";

    private static void Log(string msg)
    {
        try { File.AppendAllText(_log, msg + Environment.NewLine); } catch { }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            Log("EXCEPTION: " + ex);
        }
    }

    private static void Run(string[] args)
    {
        var changelogPath = args.Length > 0
            ? args[0]
            : @"E:\000_AI\QuickRemote\QuickRemote\CHANGELOG.md";
        var outPath = args.Length > 1
            ? args[1]
            : Path.Combine(AppContext.BaseDirectory, "settings-update.png");
        _log = outPath + ".log";
        try { File.Delete(_log); } catch { }

        var markdown = File.ReadAllText(changelogPath);
        Log($"changelog: {changelogPath} ({markdown.Length} chars)");

        // 载入产品程序的 App.xaml 资源字典（颜色/样式都在里面）
        var app = new QuickRemote.PCClient.App();
        app.InitializeComponent();
        Log("app resources loaded");

        var win = new SettingsWindow
        {
            DataContext = new StubVm { ChangelogUrl = "" },
            Left = -4000,
            Top = -4000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        win.Show();

        // 切到「版本更新」页（ChangelogUrl 为空 → 异步加载立即返回，正文由本程序注入）
        if (win.FindName("NavUpdate") is RadioButton nav) nav.IsChecked = true;
        win.UpdateLayout();

        var viewer = (FlowDocumentScrollViewer)win.FindName("ChangelogViewer")!;
        var status = (TextBlock)win.FindName("ChangelogStatusText")!;
        viewer.Document = ChangelogRenderer.Render(markdown);
        viewer.Visibility = Visibility.Visible;
        status.Visibility = Visibility.Collapsed;
        win.UpdateLayout();
        Log("content injected");

        // ---- 客观几何数据（判断是否溢出/能否内部滚动）----
        var content = (ScrollViewer)win.FindName("ContentScroll")!;
        var panel = (StackPanel)win.FindName("PanelUpdate")!;
        Log($"window         : {win.ActualWidth} x {win.ActualHeight}");
        Log($"contentScroll  : viewport={content.ViewportHeight:F1} extent={content.ExtentHeight:F1} " +
            $"scrollable={content.ScrollableHeight:F1} (0=无溢出)");
        Log($"panelUpdate    : h={panel.ActualHeight:F1}");
        var host = viewer.Template?.FindName("PART_ContentHost", viewer) as ScrollViewer;
        Log($"docBlocks      : {viewer.Document.Blocks.Count}");
        Log($"changelogViewer: h={viewer.ActualHeight:F1}");
        Log($"changelogScroll: viewport={host?.ViewportHeight:F1} extent={host?.ExtentHeight:F1} " +
            $"scrollable={host?.ScrollableHeight:F1} (>0=内部可滚动)");

        // ---- 导出 PNG ----
        const double dpi = 192.0;
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(win.ActualWidth * dpi / 96.0),
            (int)Math.Ceiling(win.ActualHeight * dpi / 96.0),
            dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(win);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using (var fs = File.Create(outPath)) enc.Save(fs);
        Log($"saved: {outPath}");

        win.Close();
        Log("done");
    }
}
