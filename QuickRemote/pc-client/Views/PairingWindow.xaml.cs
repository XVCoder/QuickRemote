using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace QuickRemote.PCClient.Views;

/// <summary>
/// 手机配对弹窗：展示配对二维码与明文配置串。
///
/// 二维码内容就是明文配置串本身（quickremote://pair?d=...）。
/// ⚠️ 必须用 QuickRemote 安卓端 App 内的「扫码导入」扫描：系统相机与第三方扫码器
/// 都不处理自定义 scheme，扫了不会有任何反应。App 内置扫码器解出文本后，
/// 交给与「粘贴配置导入」完全相同的解析路径写入配置。
/// </summary>
public partial class PairingWindow : Window
{
    /// <summary>AppConfig 里预共享密钥的出厂默认值，仍是它时提示用户先改。</summary>
    private const string DefaultPsk = "change-me-please";

    public PairingWindow(string serverAddress, string preSharedKey, string deviceName)
    {
        InitializeComponent();

        var url = Interop.PairingPayload.Encode(serverAddress, preSharedKey, deviceName);
        PayloadBox.Text = url;

        WarnBar.Visibility = preSharedKey == DefaultPsk ? Visibility.Visible : Visibility.Collapsed;

        QrImage.Source = RenderQr(url);
    }

    /// <summary>
    /// 生成二维码位图。保持默认黑白配色 —— 深色主题下虽然白底略显突兀，
    /// 但扫码成功率优先；若真要改配色，务必用真机复测能否扫出。
    /// </summary>
    private static BitmapImage? RenderQr(string text)
    {
        try
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(png);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PayloadBox.Text);
            BtnCopy.Content = "已复制";
        }
        catch
        {
            // 剪贴板被其他进程占用：静默忽略，用户可手动选中文本复制
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }
}
