using System.Windows;
using QuickRemote.PCClient.Services;

namespace QuickRemote.PCClient;

/// <summary>
/// 应用程序入口。
/// </summary>
public partial class App : Application
{
    public static readonly string Version =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static ConfigService ConfigService { get; private set; } = null!;
    public static Logger Logger { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 初始化日志（必须在创建主窗口之前）
        Logger = new Logger("App");
        Logger.Info($"QuickRemote PC Client v{Version} starting up");

        // 加载配置
        ConfigService = new ConfigService();
        ConfigService.Load();

        // 应用开机自启设置
        AutoStartHelper.Apply(ConfigService.Config.AutoStart);

        base.OnStartup(e);

        // 手动创建并显示主窗口（确保服务已初始化）
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger?.Info("Application exiting");
        base.OnExit(e);
    }
}
