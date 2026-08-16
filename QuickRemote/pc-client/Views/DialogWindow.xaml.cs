using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickRemote.PCClient.Views;

/// <summary>
/// 自定义主题弹窗，替代原生 MessageBox，风格与深色主题一致。
/// </summary>
public partial class DialogWindow : Window
{
    /// <summary>弹窗类型（决定图标颜色与符号）。</summary>
    public enum DialogType
    {
        Info,
        Warning,
        Error,
        Success,
        Question
    }

    private bool _confirmed;

    public DialogWindow(string title, string message, DialogType type, bool showCancel)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        BtnCancel.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;

        // 按类型设置图标颜色与符号
        var (color, symbol) = type switch
        {
            DialogType.Warning => (TryBrush("WarningBrush", "#F59E0B"), "!"),
            DialogType.Error => (TryBrush("DangerBrush", "#EF4444"), "\u2715"),
            DialogType.Success => (TryBrush("SuccessBrush", "#10B981"), "\u2713"),
            DialogType.Question => (TryBrush("AccentBrush", "#3B82F6"), "?"),
            _ => (TryBrush("AccentBrush", "#3B82F6"), "i")
        };
        IconBadge.Background = color;
        IconText.Text = symbol;

        BtnOk.Focus();
    }

    /// <summary>从应用资源取画刷，取不到时回退到指定颜色。</summary>
    private static Brush TryBrush(string key, string fallbackHex)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush b) return b;
        }
        catch
        {
            // ignore
        }
        return (Brush)new BrushConverter().ConvertFromString(fallbackHex)!;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        _confirmed = true;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _confirmed = false;
        DialogResult = false;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        _confirmed = false;
        DialogResult = false;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    // ============ 静态便捷方法（线程安全） ============

    /// <summary>显示单按钮提示框。</summary>
    public static void Show(string message, string title = "提示", DialogType type = DialogType.Info)
    {
        RunOnUi(() =>
        {
            var win = Create(title, message, type, showCancel: false);
            win.ShowDialog();
        });
    }

    /// <summary>显示确认框，返回用户是否点击「确定」。</summary>
    public static bool Confirm(string message, string title = "确认", DialogType type = DialogType.Question)
    {
        var result = false;
        RunOnUi(() =>
        {
            var win = Create(title, message, type, showCancel: true);
            win.ShowDialog();
            result = win._confirmed;
        });
        return result;
    }

    private static DialogWindow Create(string title, string message, DialogType type, bool showCancel)
    {
        var win = new DialogWindow(title, message, type, showCancel);
        // 设置 Owner 以居中于主窗口并保持模态
        if (Application.Current?.MainWindow is { } main && main != win && main.IsLoaded)
        {
            win.Owner = main;
        }
        return win;
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            action();
            return;
        }
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }
}
