using System.Windows;

namespace QuickRemote.PCClient.Views;

/// <summary>
/// 更新进度窗口。显示下载进度和状态。
/// </summary>
public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow(string targetVersion)
    {
        InitializeComponent();
        TitleText.Text = $"正在更新到 v{targetVersion}";
        StatusText.Text = "准备下载...";
    }

    /// <summary>更新进度。percent 为 -1 时显示不确定模式。</summary>
    public void UpdateProgress(int percent, string status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateProgress(percent, status));
            return;
        }

        if (percent < 0)
        {
            ProgressBar.IsIndeterminate = true;
        }
        else
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = percent;
        }
        StatusText.Text = status;
    }
}
