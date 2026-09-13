using System.Net.Http;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;

namespace QuickRemote.PCClient.Views;

/// <summary>
/// 设置中心弹窗：左侧导航（基本配置/远程配置/被远程安全/版本更新）+ 右侧内容区。
/// DataContext 与主窗口共享 MainViewModel，保存按钮走 SaveSettingsCommand。
/// 「版本更新」页承载原主窗口底部的「检查更新」按钮，以及原独立「更新记录」窗口的
/// CHANGELOG.md 阅读区（Markdown → FlowDocument 渲染见 <see cref="ChangelogRenderer"/>）。
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>更新记录是否已成功加载（切页时不再重复拉取，点「刷新」可强制重载）。</summary>
    private bool _changelogLoaded;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    /// <summary>标题栏/侧边栏拖动。</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }

    /// <summary>关闭按钮。</summary>
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>导航切换：显示对应配置面板并更新内容标题，进入更新页时按需拉取更新记录。</summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;

        var basic = NavBasic.IsChecked == true;
        var remote = NavRemote.IsChecked == true;
        var update = NavUpdate.IsChecked == true;

        PanelBasic.Visibility = basic ? Visibility.Visible : Visibility.Collapsed;
        PanelRemote.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
        PanelSecurity.Visibility = !basic && !remote && !update ? Visibility.Visible : Visibility.Collapsed;
        PanelUpdate.Visibility = update ? Visibility.Visible : Visibility.Collapsed;
        SectionTitle.Text = basic ? "基本配置"
            : remote ? "远程配置"
            : update ? "版本更新"
            : "被远程安全";

        if (update)
        {
            _ = LoadChangelogAsync(force: false);
        }
    }

    /// <summary>「刷新」按钮：强制重新拉取更新记录。</summary>
    private void BtnRefreshChangelog_Click(object sender, RoutedEventArgs e)
        => _ = LoadChangelogAsync(force: true);

    /// <summary>验证码输入框仅允许数字（6 位验证码）。</summary>
    private void AccessCodeBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    /// <summary>
    /// 手机配对：用当前（未保存也可）的服务器地址与密钥生成二维码。
    /// 地址或密钥为空时直接提示，避免生成一个扫了也没用的码。
    /// </summary>
    private void BtnPairing_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm)
        {
            return;
        }

        var addr = vm.ServerAddressInput?.Trim() ?? string.Empty;
        var psk = vm.PreSharedKeyInput?.Trim() ?? string.Empty;

        if (addr.Length == 0 || psk.Length == 0)
        {
            DialogWindow.Show("请先填写服务器地址与预共享密钥，再生成配对二维码。",
                "无法配对", DialogWindow.DialogType.Warning);
            return;
        }

        var window = new PairingWindow(addr, psk, vm.DeviceNameInput?.Trim() ?? string.Empty)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    // ========== 更新记录 ==========

    /// <summary>拉取并渲染更新记录（CHANGELOG.md）。已加载过且非强制时直接复用。</summary>
    private async Task LoadChangelogAsync(bool force)
    {
        if (_changelogLoaded && !force) return;
        if (DataContext is not ViewModels.IChangelogSource source) return;

        var url = source.ChangelogUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowChangelogStatus("未配置更新记录地址");
            return;
        }

        ShowChangelogStatus("加载中...");
        try
        {
            App.Logger.Info($"Loading changelog from {url}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var content = await Http.GetStringAsync(url, cts.Token);

            ChangelogViewer.Document = ChangelogRenderer.Render(content);
            ChangelogViewer.Visibility = Visibility.Visible;
            ChangelogStatusText.Visibility = Visibility.Collapsed;
            _changelogLoaded = true;
        }
        catch (OperationCanceledException)
        {
            App.Logger.Warn("Loading changelog timed out");
            ShowChangelogStatus("加载超时，请稍后重试");
        }
        catch (Exception ex)
        {
            App.Logger.Warn($"Failed to load changelog: {ex.Message}");
            ShowChangelogStatus($"加载失败：{ex.Message}");
        }
    }

    private void ShowChangelogStatus(string text)
    {
        ChangelogStatusText.Text = text;
        ChangelogStatusText.Visibility = Visibility.Visible;
        ChangelogViewer.Visibility = Visibility.Collapsed;
    }
}
