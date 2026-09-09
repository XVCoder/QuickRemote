using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using QuickRemote.PCClient.ViewModels;

namespace QuickRemote.PCClient;

/// <summary>
/// 主窗口。自定义标题栏，关闭按钮隐藏到托盘。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isPinned;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // 初始化托盘（需在窗口句柄创建后）
        _viewModel.InitTray();

        // 启动状态点脉冲动画
        if (Resources["PulseStoryboard"] is Storyboard sb)
        {
            sb.Begin();
        }

        // 初始化服务连接
        _viewModel.Initialize();
    }

    /// <summary>标题栏拖动。</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }

    /// <summary>验证码输入框仅允许数字（6 位验证码）。</summary>
    private void AccessCodeBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    /// <summary>最小化按钮。</summary>
    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    /// <summary>置顶按钮（图钉图标）：切换窗口是否始终置顶，置顶时图标高亮为强调色。</summary>
    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        Topmost = _isPinned;
        BtnPin.Foreground = _isPinned
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("TextSecondaryBrush");
    }

    /// <summary>关闭按钮：隐藏到托盘而非退出。</summary>
    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _viewModel.Tray?.ShowBalloon("QuickRemote PC", "程序仍在后台运行");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 除非是真正的退出（来自托盘"退出"），否则隐藏到托盘
        if (!_viewModel.IsExiting)
        {
            e.Cancel = true;
            Hide();
            _viewModel.Tray?.ShowBalloon("QuickRemote PC", "程序仍在后台运行");
            return;
        }

        _viewModel.Dispose();
        base.OnClosing(e);
    }
}
