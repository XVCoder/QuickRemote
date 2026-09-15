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
    public string Version => "1.1.67";
    public string UpdateStatusText => "当前 v1.1.67 已是最新版本";
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
            if (args.Length > 0 && args[0] == "--test-upload")
            {
                RunUploadTest();
                return;
            }
            Run(args);
        }
        catch (Exception ex)
        {
            Log("EXCEPTION: " + ex);
        }
    }

    /// <summary>
    /// 直连产品代码 <see cref="QuickRemote.PCClient.Services.FeedbackService"/> 跑一次真实上传，
    /// 验证客户端拼装内容 + multipart 提交链路（不只是 curl 探针）。
    /// </summary>
    private static void RunUploadTest()
    {
        _log = Path.Combine(AppContext.BaseDirectory, "upload-test.log");
        try { File.Delete(_log); } catch { }

        // App.Logger 由 OnStartup 初始化，本工作台不跑 OnStartup → 反射注入一个实例
        var prop = typeof(QuickRemote.PCClient.App).GetProperty(
            "Logger",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        prop.SetValue(null, new QuickRemote.PCClient.Services.Logger("UploadTest"));
        Log("logger injected");

        var result = QuickRemote.PCClient.Services.FeedbackService
            .SubmitAsync(
                "【工作台自动化验证】PC 端意见反馈上传链路自测，可忽略。\r\n" +
                "本条由 tmp-settingshot 工作台调用产品 FeedbackService 生成。",
                includeLogs: true,
                deviceId: "pcselftest001",
                deviceName: "自动化验证机")
            .GetAwaiter().GetResult();

        Log($"success = {result.Success}");
        Log($"fileName = {result.FileName}");
        Log($"message = {result.Message}");
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

        // ---- 导出 PNG（版本更新页）----
        Export(win, outPath);
        Log($"saved: {outPath}");

        // ---- 第二张：意见反馈页 ----
        if (win.FindName("NavFeedback") is RadioButton navFb) navFb.IsChecked = true;
        win.UpdateLayout();

        ((TextBlock)win.FindName("FeedbackDeviceIdText")!).Text = "de8919f22c5d42cdb4eed47dea3e5ebe";
        ((TextBox)win.FindName("FeedbackBox")!).Text =
            "手机连上电脑后画面偶尔卡住几秒，切到后台再回来就恢复正常。\r\n\r\n" +
            "复现步骤：\r\n" +
            "1. 手机连接 PC（局域网直连）\r\n" +
            "2. 连续滑动屏幕约 30 秒\r\n" +
            "3. 画面卡顿 2-3 秒后自行恢复";
        win.UpdateLayout();

        var fbScroll = (ScrollViewer)win.FindName("ContentScroll")!;
        var fbPanel = (StackPanel)win.FindName("PanelFeedback")!;
        var fbBox = (TextBox)win.FindName("FeedbackBox")!;
        Log($"panelFeedback  : h={fbPanel.ActualHeight:F1}");
        Log($"feedbackBox    : h={fbBox.ActualHeight:F1}");
        Log($"fb contentScroll: viewport={fbScroll.ViewportHeight:F1} extent={fbScroll.ExtentHeight:F1} " +
            $"scrollable={fbScroll.ScrollableHeight:F1} (0=无溢出)");
        Log($"saveBtnVisible : {((Button)win.FindName("SaveSettingsButton")!).Visibility}");
        Log($"submitBtnVisible: {((Button)win.FindName("SubmitFeedbackButton")!).Visibility}");
        Log($"sectionTitle   : {((TextBlock)win.FindName("SectionTitle")!).Text}");

        var fbOut = Path.Combine(
            Path.GetDirectoryName(outPath)!,
            Path.GetFileNameWithoutExtension(outPath) + "-feedback.png");
        Export(win, fbOut);
        Log($"saved: {fbOut}");

        win.Close();
        Log("done");
    }

    /// <summary>把窗口离屏渲染成 PNG（192 DPI）。</summary>
    private static void Export(Window win, string path)
    {
        const double dpi = 192.0;
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(win.ActualWidth * dpi / 96.0),
            (int)Math.Ceiling(win.ActualHeight * dpi / 96.0),
            dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(win);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
