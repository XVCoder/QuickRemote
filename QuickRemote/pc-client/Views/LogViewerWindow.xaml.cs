using System.IO;
using System.Text;
using System.Windows;
using QuickRemote.PCClient.Services;

namespace QuickRemote.PCClient.Views;

/// <summary>
/// 日志查看窗口：在程序内直接弹窗展示日志内容，代替跳转到保存目录。
/// </summary>
public partial class LogViewerWindow : Window
{
    private static LogViewerWindow? _instance;

    /// <summary>单文件读取上限，避免超长日志撑爆内存。</summary>
    private const int MaxBytesPerFile = 512 * 1024;

    public LogViewerWindow()
    {
        InitializeComponent();
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

    /// <summary>读取并展示日志内容（按文件名升序，新的文件排在后）。</summary>
    private void LoadLogs()
    {
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
        LogTextBox.CaretIndex = LogTextBox.Text.Length;
        LogTextBox.ScrollToEnd();
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadLogs();

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "确定清空全部日志文件吗？此操作不可恢复。",
            "清空日志",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

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
        _instance = null;
        base.OnClosed(e);
    }
}
