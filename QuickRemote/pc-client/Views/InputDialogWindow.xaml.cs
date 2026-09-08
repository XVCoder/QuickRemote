using System.Windows;
using System.Windows.Input;

namespace QuickRemote.PCClient.Views;

/// <summary>
/// 单行文本输入弹窗（深色主题，与 DialogWindow 风格一致）。
/// 用于设备重命名等轻量输入场景。
/// </summary>
public partial class InputDialogWindow : Window
{
    private bool _confirmed;

    private InputDialogWindow(string title, string message, string defaultValue, int maxLength)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        InputBox.Text = defaultValue;
        InputBox.MaxLength = maxLength;
        InputBox.SelectAll();
        Loaded += (_, _) => InputBox.Focus();
    }

    /// <summary>显示输入弹窗，返回用户输入（取消返回 null）。线程安全（调度到 UI 线程）。</summary>
    public static string? Show(string title, string message, string defaultValue = "", int maxLength = 64)
    {
        string? result = null;
        RunOnUi(() =>
        {
            var win = new InputDialogWindow(title, message, defaultValue, maxLength);
            win.ShowDialog();
            if (win._confirmed)
                result = win.InputBox.Text;
        });
        return result;
    }

    private static void RunOnUi(Action action)
    {
        var app = Application.Current;
        if (app == null || app.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            app.Dispatcher.Invoke(action);
        }
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

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _confirmed = true;
            DialogResult = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }
}
