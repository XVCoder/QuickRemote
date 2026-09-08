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

        // 全局异常兜底：后台线程未处理异常默认直接终止进程（表现为无声闪退，日志戛然而止），
        // 注册三个钩子把异常现场写入日志，便于定位；UI 线程异常记录后继续运行避免闪退
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            try
            {
                Logger.Error("FATAL: Unhandled exception (background thread), process will terminate",
                    ex.ExceptionObject as Exception ?? new Exception(ex.ExceptionObject?.ToString() ?? "unknown"));
            }
            catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            try { Logger.Error("Unobserved task exception", ex.Exception); } catch { }
            ex.SetObserved(); // fire-and-forget Task 的异常不再升级为进程崩溃
        };
        DispatcherUnhandledException += (_, ex) =>
        {
            try
            {
                Logger.Error("Unhandled UI thread exception (suppressed, app continues)", ex.Exception);
            }
            catch { }
            ex.Handled = true; // 记录后继续运行，避免闪退
        };

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
