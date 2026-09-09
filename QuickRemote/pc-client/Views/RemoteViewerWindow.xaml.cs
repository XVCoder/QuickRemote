using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickRemote.PCClient.Models;
using QuickRemote.PCClient.Services;

namespace QuickRemote.PCClient.Views;

/// <summary>
/// 远程查看窗口：渲染目标 PC 画面（WriteableBitmap），
/// 捕获本机鼠标/键盘/滚轮/文本输入并转发到远程（RemoteFrameProtocol）。
/// 文本类按键走 TextInput → TYPE_INPUT_TEXT（中文 IME/符号统一 Unicode 注入）；
/// 控制/组合键走 PreviewKeyDown → TYPE_INPUT_KEY（VK 码）。
/// </summary>
public partial class RemoteViewerWindow : Window
{
    private readonly RemoteDeviceInfo _device;
    private readonly Func<Task<RemoteViewerClient>> _connectFunc;
    private RemoteViewerClient? _client;
    private WriteableBitmap? _bitmap;
    private bool _windowClosed;
    private bool _fitted;

    public RemoteViewerWindow(RemoteDeviceInfo device, Func<Task<RemoteViewerClient>> connectFunc)
    {
        InitializeComponent();
        _device = device;
        _connectFunc = connectFunc;
        Title = $"远程控制 - {device.DisplayName}";
        Loaded += (_, _) => _ = ConnectAsync();
    }

    // ============ 连接管理 ============

    private async Task ConnectAsync()
    {
        if (_windowClosed) return;
        ShowStatus($"正在连接 {_device.DisplayName} ...", string.Empty, showRetry: false);

        RemoteViewerClient client;
        try
        {
            client = await _connectFunc();
        }
        catch (Exception ex)
        {
            if (_windowClosed) return;
            ShowStatus("连接失败", ex.Message, showRetry: true);
            return;
        }

        if (_windowClosed)
        {
            client.Dispose();
            return;
        }

        _client = client;
        client.FrameDecoded += OnFrameDecoded;
        client.StatusMessage += msg => OnStatusMessage(client, msg);
        client.Disconnected += reason => OnDisconnected(client, reason);
        client.AuthRequired += () => OnAuthRequired(client);
        client.AuthFailed += () => OnAuthFailed(client);

        ShowStatus($"已连接（{client.ModeText}），等待画面...", string.Empty, showRetry: false);
        Focus(); // 连接后聚焦，键盘立即可用
    }

    private void BtnReconnect_Click(object sender, RoutedEventArgs e)
    {
        CloseClient();
        _ = ConnectAsync();
    }

    private void OnStatusMessage(RemoteViewerClient client, string message)
    {
        if (_windowClosed || !ReferenceEquals(_client, client)) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_windowClosed || !ReferenceEquals(_client, client)) return;
            ShowStatus(message, string.Empty, showRetry: false);
        });
    }

    private void OnDisconnected(RemoteViewerClient client, string reason)
    {
        if (_windowClosed || !ReferenceEquals(_client, client)) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_windowClosed || !ReferenceEquals(_client, client)) return;
            ShowStatus("连接已断开", reason, showRetry: true);
        });
    }

    // ============ 访问验证码（v1.1.54：被控端配置验证码时连接需先验证） ============

    /// <summary>弹窗收集验证码并提交（isRetry 时提示上次输入错误）。取消则断开连接。</summary>
    private void PromptAuthCode(RemoteViewerClient client, bool isRetry)
    {
        if (_windowClosed || !ReferenceEquals(_client, client)) return;

        ShowStatus(isRetry ? "验证码错误，请重新输入" : "等待验证码验证...", string.Empty, showRetry: false);
        var code = InputDialogWindow.Show(
            "访问验证",
            isRetry
                ? $"「{_device.DisplayName}」要求访问验证码，上次输入错误（3 次失败将断开）："
                : $"「{_device.DisplayName}」要求输入访问验证码：",
            "", 32);
        if (_windowClosed || !ReferenceEquals(_client, client)) return;

        if (string.IsNullOrWhiteSpace(code))
        {
            // 取消输入：主动断开
            CloseClient();
            ShowStatus("已取消验证", "用户取消了验证码输入", showRetry: true);
            return;
        }
        client.SendAuthCode(code);
    }

    private void OnAuthRequired(RemoteViewerClient client)
    {
        if (_windowClosed || !ReferenceEquals(_client, client)) return;
        // 接收线程回调 → 调度 UI；InputDialogWindow.Show 内部在 UI 线程直接模态执行，
        // 不阻塞接收线程（弹窗期间心跳/断开事件仍能到达）
        Dispatcher.InvokeAsync(() => PromptAuthCode(client, isRetry: false));
    }

    private void OnAuthFailed(RemoteViewerClient client)
    {
        if (_windowClosed || !ReferenceEquals(_client, client)) return;
        Dispatcher.InvokeAsync(() => PromptAuthCode(client, isRetry: true));
    }

    // ============ 画面渲染 ============

    private void OnFrameDecoded(int width, int height, byte[] bgra)
    {
        if (_windowClosed) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_windowClosed) return;

            if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            {
                _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                VideoImage.Source = _bitmap;
                if (!_fitted)
                {
                    _fitted = true;
                    FitWindowToVideo(width, height);
                }
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
            StatusOverlay.Visibility = Visibility.Collapsed; // 有画面即隐藏覆盖层
        });
    }

    /// <summary>首帧后按视频分辨率调整窗口（限制在工作区 85% 内）。</summary>
    private void FitWindowToVideo(int videoW, int videoH)
    {
        try
        {
            var work = SystemParameters.WorkArea;
            double maxW = work.Width * 0.85;
            double maxH = work.Height * 0.85;
            double scale = Math.Min(Math.Min(maxW / videoW, maxH / videoH), 1.0);

            // 窗口客户区 ≈ 视频缩放尺寸（Image 填满客户区，Stretch=Uniform 留边）
            Width = Math.Max(MinWidth, Math.Min(maxW, videoW * scale) + SystemParameters.ResizeFrameVerticalBorderWidth * 2);
            Height = Math.Max(MinHeight, Math.Min(maxH, videoH * scale) + SystemParameters.CaptionHeight + SystemParameters.ResizeFrameHorizontalBorderHeight * 2);
        }
        catch
        {
            // 尺寸计算失败保持默认
        }
    }

    /// <summary>显示状态覆盖层（有新画面时自动隐藏）。</summary>
    private void ShowStatus(string main, string sub, bool showRetry)
    {
        StatusOverlay.Visibility = Visibility.Visible;
        StatusText.Text = main;
        StatusSubText.Text = sub;
        StatusSubText.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
        BtnReconnect.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============ 鼠标输入 ============

    /// <summary>窗口坐标 → 视频像素坐标（Image 为 Stretch=Uniform 居中，按内容矩形换算）。</summary>
    private Point ToVideoCoords(Point p)
    {
        var bitmap = _bitmap;
        if (bitmap == null) return new Point(0, 0);

        double iw = VideoImage.ActualWidth, ih = VideoImage.ActualHeight;
        if (iw <= 0 || ih <= 0) return new Point(0, 0);

        double vw = bitmap.PixelWidth, vh = bitmap.PixelHeight;
        double scale = Math.Min(iw / vw, ih / vh);
        double ox = (iw - vw * scale) / 2, oy = (ih - vh * scale) / 2;
        double x = (p.X - ox) / scale, y = (p.Y - oy) / scale;
        return new Point(Math.Clamp(x, 0, vw - 1), Math.Clamp(y, 0, vh - 1));
    }

    private void Grid_MouseMove(object sender, MouseEventArgs e)
    {
        if (_client == null) return;
        var p = ToVideoCoords(e.GetPosition(VideoImage));
        _client.SendMouseMove((int)p.X, (int)p.Y);
    }

    private void Grid_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ActivateForInput();
        ContentGrid.CaptureMouse();
        if (_client == null) return;
        var action = e.ChangedButton switch
        {
            MouseButton.Left => RemoteInputHandler.ACTION_LEFT_DOWN,
            MouseButton.Right => RemoteInputHandler.ACTION_RIGHT_DOWN,
            MouseButton.Middle => RemoteInputHandler.ACTION_MIDDLE_DOWN,
            _ => byte.MaxValue // XButton1/2：暂不转发
        };
        if (action == byte.MaxValue) return;
        var p = ToVideoCoords(e.GetPosition(VideoImage));
        _client.SendMouseButton(action, (int)p.X, (int)p.Y);
        e.Handled = true;
    }

    private void Grid_MouseUp(object sender, MouseButtonEventArgs e)
    {
        ContentGrid.ReleaseMouseCapture();
        if (_client == null) return;
        var action = e.ChangedButton switch
        {
            MouseButton.Left => RemoteInputHandler.ACTION_LEFT_UP,
            MouseButton.Right => RemoteInputHandler.ACTION_RIGHT_UP,
            MouseButton.Middle => RemoteInputHandler.ACTION_MIDDLE_UP,
            _ => byte.MaxValue
        };
        if (action == byte.MaxValue) return;
        var p = ToVideoCoords(e.GetPosition(VideoImage));
        _client.SendMouseButton(action, (int)p.X, (int)p.Y);
        e.Handled = true;
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_client == null) return;
        var p = ToVideoCoords(e.GetPosition(VideoImage));
        var delta = (short)Math.Clamp(e.Delta, short.MinValue, short.MaxValue);
        _client.SendWheel(delta, 0, (int)p.X, (int)p.Y);
        e.Handled = true;
    }

    /// <summary>鼠标按下时激活窗口并取键盘焦点（键盘输入立即作用于远程）。</summary>
    private void ActivateForInput()
    {
        if (!IsActive) Activate();
        Keyboard.Focus(this);
    }

    // ============ 键盘输入 ============

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var client = _client;
        if (client == null) return;

        // Alt 组合键以 SystemKey 形式上报
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ctrl/Alt/Win 组合键强制原始 VK 转发（远程快捷键需要，如 Ctrl+C）
        var mod = Keyboard.Modifiers;
        var forceRaw = (mod & ModifierKeys.Control) != 0 ||
                       (mod & ModifierKeys.Alt) != 0 ||
                       (mod & ModifierKeys.Windows) != 0;

        if (!forceRaw && IsTextProducingKey(key))
            return; // 交给 TextInput → Unicode 注入（中文 IME/标点统一）

        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        client.SendKey((ushort)vk, down: true);
        e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var client = _client;
        if (client == null) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 与 KeyDown 对称：文本键走 TextInput（KeyUp 无文本语义，但按下未转发的键
        // 不应补发 KeyUp——远程未收到 down，多余 up 会破坏按键状态机）
        var mod = Keyboard.Modifiers;
        var wasRaw = (mod & ModifierKeys.Control) != 0 ||
                     (mod & ModifierKeys.Alt) != 0 ||
                     (mod & ModifierKeys.Windows) != 0;
        // 注意：KeyUp 时修饰键已释放的场合无法精确还原 KeyDown 时刻的状态，
        // 对文本键统一不补发（远程注入的文本自带 down/up，状态自洽）
        if (!wasRaw && IsTextProducingKey(key) && !IsModifierKey(key))
            return;

        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        client.SendKey((ushort)vk, down: false);
        e.Handled = true;
    }

    private void Window_TextInput(object sender, TextCompositionEventArgs e)
    {
        var client = _client;
        if (client == null || string.IsNullOrEmpty(e.Text)) return;
        client.SendText(e.Text);
        e.Handled = true;
    }

    /// <summary>产生文本的按键（无修饰键时由 TextInput 统一处理，不直接发 VK）。</summary>
    private static bool IsTextProducingKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => true,
        >= Key.D0 and <= Key.D9 => true,
        >= Key.NumPad0 and <= Key.NumPad9 => true,
        Key.Space => true,
        Key.OemMinus => true,
        Key.OemPlus => true,
        Key.OemOpenBrackets => true,
        Key.OemPipe => true,
        Key.OemCloseBrackets => true,
        Key.OemSemicolon => true,
        Key.OemQuotes => true,
        Key.OemComma => true,
        Key.OemPeriod => true,
        Key.OemQuestion => true,
        Key.OemTilde => true,
        Key.OemBackslash => true,
        Key.AbntC1 => true,
        Key.AbntC2 => true,
        _ => false
    };

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin or Key.System;

    // ============ 清理 ============

    private void CloseClient()
    {
        var client = _client;
        _client = null;
        client?.Dispose();
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _windowClosed = true;
        CloseClient();
    }
}
