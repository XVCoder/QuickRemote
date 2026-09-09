using System.Windows;
using System.Windows.Input;

namespace QuickRemote.PCClient.Views;

/// <summary>
/// 设置中心弹窗：左侧导航（基本配置/远程配置/被远程安全）+ 右侧内容区。
/// DataContext 与主窗口共享 MainViewModel，保存按钮走 SaveSettingsCommand。
/// </summary>
public partial class SettingsWindow : Window
{
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

    /// <summary>导航切换：显示对应配置面板并更新内容标题。</summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;

        var basic = NavBasic.IsChecked == true;
        var remote = NavRemote.IsChecked == true;

        PanelBasic.Visibility = basic ? Visibility.Visible : Visibility.Collapsed;
        PanelRemote.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
        PanelSecurity.Visibility = !basic && !remote ? Visibility.Visible : Visibility.Collapsed;
        SectionTitle.Text = basic ? "基本配置" : remote ? "远程配置" : "被远程安全";
    }

    /// <summary>验证码输入框仅允许数字（6 位验证码）。</summary>
    private void AccessCodeBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }
}
