using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using QuickRemote.PCClient.Services;

namespace QuickRemote.PCClient.Views;

/// <summary>
/// 日志查看窗口：在程序内直接弹窗展示日志内容，代替跳转到保存目录。
/// 支持实时刷新（每 2 秒）：在底部时自动跟随最新日志，上翻阅读时保持当前位置。
/// </summary>
public partial class LogViewerWindow : Window
{
    private static LogViewerWindow? _instance;

    /// <summary>单文件读取上限，避免超长日志撑爆内存。</summary>
    private const int MaxBytesPerFile = 512 * 1024;

    /// <summary>实时刷新定时器（2 秒；开关开启时启动）。</summary>
    private readonly DispatcherTimer _autoRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public LogViewerWindow()
    {
        InitializeComponent();
        _autoRefreshTimer.Tick += (_, _) => RefreshPreservePosition();
    }

    /// <summary>显示日志查看窗口（单例，重复点击聚焦并刷新）。</summary>
    public static void ShowWindow()
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (_instance != null && _instance.IsLoaded)
            {
                _instance.Activate();
                _instance.LoadLogs();
                return;
            }

            _instance = new LogViewerWindow();
            _instance.Show();
            _instance.LoadLogs();
        });
    }

    /// <summary>读取并展示日志内容（按文件名升序，新的文件排在后）。
    /// 手动刷新或位于底部时滚到最新，上翻阅读时保持原滚动位置。</summary>
    private void LoadLogs()
    {
        var wasAtBottom = LogTextBox.VerticalOffset + LogTextBox.ViewportHeight >= LogTextBox.ExtentHeight - 8;
        var preserveOffset = LogTextBox.VerticalOffset;

        var files = Logger.GetLogFiles();
        if (files.Count == 0)
        {
            LogTextBox.Text = "暂无日志";
            return;
        }

        var sb = new StringBuilder();
        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var content = File.ReadAllText(file, Encoding.UTF8);
                if (content.Length > MaxBytesPerFile)
                {
                    content = "...（文件过长，仅显示末尾）...\n"
                              + content.Substring(content.Length - MaxBytesPerFile);
                }
                sb.AppendLine($"══════════ {Path.GetFileName(file)} ══════════");
                sb.AppendLine(content.TrimEnd());
            }
            catch (Exception ex)
            {
                sb.AppendLine($"（读取 {Path.GetFileName(file)} 失败：{ex.Message}）");
            }
        }

        LogTextBox.Text = sb.ToString();

        if (wasAtBottom)
        {
            LogTextBox.CaretIndex = LogTextBox.Text.Length;
            LogTextBox.ScrollToEnd();
        }
        else
        {
            // 保持阅读位置（内容顶部变化时偏移可能越界，钳制回合法范围）
            LogTextBox.ScrollToVerticalOffset(Math.Min(preserveOffset, LogTextBox.ExtentHeight));
        }
    }

    /// <summary>实时刷新 tick：保留滚动位置的增量刷新。</summary>
    private void RefreshPreservePosition() => LoadLogs();

    /// <summary>实时刷新开关切换：开启立即刷新一次并启动定时器，关闭停止。</summary>
    private void AutoRefreshToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoRefreshToggle.IsChecked == true)
        {
            RefreshPreservePosition();
            _autoRefreshTimer.Start();
        }
        else
        {
            _autoRefreshTimer.Stop();
        }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadLogs();

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        var ok = DialogWindow.Confirm(
            "确定清空全部日志文件吗？此操作不可恢复。",
            "清空日志",
            DialogWindow.DialogType.Warning);
        if (!ok) return;

        Logger.ClearLogs();
        LoadLogs();
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var logDir = Logger.GetLogDirectory();
            if (Directory.Exists(logDir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = logDir,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // 打开目录失败不影响弹窗本身
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoRefreshTimer.Stop();
        _instance = null;
        base.OnClosed(e);
    }
}
