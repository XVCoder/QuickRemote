using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 开机自启管理。
///
/// <para><b>主路径 = Task Scheduler 任务</b>：<c>InteractiveToken + HighestAvailable</c>
/// 让进程在用户登录时静默拿到管理员令牌、不需要 UAC 交互；这是本客户端
/// （app.manifest 声明 <c>requireAdministrator</c>）唯一可靠的开机自启方式。</para>
///
/// <para><b>为什么不能只用注册表 Run 键：</b>Explorer 执行 Run 键时不会弹 UAC，
/// <c>CreateProcess</c> 对需要提权的 exe 直接返回 <c>WinError 740</c>，
/// 进程起不来且不留任何日志——这就是 v1.1.81 自启失效的真因。
/// 实测：直接 <c>CreateProcess</c> 该 exe 稳定返回 740。</para>
///
/// <para><b>为什么不用 schtasks.exe：</b>部分环境（安全加固 / EDR / 组策略）会禁用或拦截它，
/// 那会让自启再次静默失效。用库直接调 Task Scheduler COM，无外部进程依赖。</para>
///
/// <para>两条路径都会写日志：自启失败属于「用户看不见但功能不可用」的故障，
/// 静默吞异常只会把排查变成猜谜（旧实现正是如此）。</para>
/// </summary>
public static class AutoStartHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "QuickRemote.PCClient";

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickRemote", "logs");
    private static readonly object LogLock = new();

    /// <summary>应用或移除开机自启。</summary>
    public static void Apply(bool enable)
    {
        try
        {
            if (enable)
            {
                var exePath = ResolveExecutablePath();
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                {
                    WriteLog("WARN", $"可执行文件不存在，跳过自启注册：{exePath}");
                    return;
                }

                if (!TaskAlreadyPointsTo(exePath))
                    RegisterTask(exePath);

                // 清掉历史版本留下的 Run 键残留：在当前清单下它必然导致 740 静默失败，
                // 留着只会让人误以为「注册表里有却不自启」是玄学。
                RemoveRunKeyValue();
            }
            else
            {
                UnregisterTask();
                RemoveRunKeyValue();
            }
        }
        catch (Exception ex)
        {
            WriteLog("ERROR", $"Apply({enable}) 失败：{ex}");
        }
    }

    /// <summary>查询当前是否已设置开机自启（以计划任务为准）。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var service = new TaskService();
            using var task = service.GetTask(AutoStartTaskSpec.TaskName);
            return task != null;
        }
        catch (Exception ex)
        {
            WriteLog("WARN", $"IsEnabled 查询失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>当前客户端 exe 的真实路径。</summary>
    private static string ResolveExecutablePath()
    {
        string? mainModule = null;
        try
        {
            using var proc = Process.GetCurrentProcess();
            mainModule = proc.MainModule?.FileName;
        }
        catch
        {
            // 某些宿主下取 MainModule 会失败，忽略即可，后面还有候选
        }

        return AutoStartTaskSpec.SelectExecutablePath(new[]
        {
            mainModule,
            Environment.ProcessPath,
            Path.Combine(AppContext.BaseDirectory, AutoStartTaskSpec.ExpectedExeName),
        });
    }

    /// <summary>任务已存在且指向同一 exe 时返回 true（避免每次启动都重写任务）。</summary>
    private static bool TaskAlreadyPointsTo(string exePath)
    {
        try
        {
            using var service = new TaskService();
            using var task = service.GetTask(AutoStartTaskSpec.TaskName);
            var existing = task?.Definition.Actions.OfType<ExecAction>().FirstOrDefault()?.Path;
            return AutoStartTaskSpec.IsSameTarget(existing, exePath);
        }
        catch (Exception ex)
        {
            WriteLog("INFO", $"任务查询失败，将重建：{ex.Message}");
            return false;
        }
    }

    private static void RegisterTask(string exePath)
    {
        using var service = new TaskService();
        var definition = service.NewTask();

        // 任务名与描述
        definition.RegistrationInfo.Author = "QuickRemote";
        definition.RegistrationInfo.Description =
            "QuickRemote PC 客户端开机自启动（登录即取得管理员令牌，无需 UAC 交互）";

        // 触发器：用户登录 + 固定延迟。延迟避开开机启动风暴，也给网络栈就绪时间。
        var trigger = new LogonTrigger
        {
            UserId = AutoStartTaskSpec.CurrentUserId(),
            Enabled = true,
            Delay = AutoStartTaskSpec.LogonDelay,
        };
        definition.Triggers.Add(trigger);

        // 主体：这是整个修复的核心。
        // InteractiveToken = 跑在用户自己的交互式会话里（SYSTEM 会话号不同，
        //   锁屏输入代理、屏幕采集、输入注入会全部失效）；
        // HighestAvailable = 登录时静默拿到管理员令牌，不需要 UAC 交互。
        definition.Principal.UserId = AutoStartTaskSpec.CurrentUserId();
        definition.Principal.LogonType = TaskLogonType.InteractiveToken;
        definition.Principal.RunLevel = TaskRunLevel.Highest;

        definition.Actions.Add(new ExecAction
        {
            Path = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
        });

        // 设置：被控端是常驻进程，执行时限必须无限，否则跑满时限会被系统杀掉。
        definition.Settings.Enabled = true;
        definition.Settings.StartWhenAvailable = true;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.RunOnlyIfNetworkAvailable = false;
        definition.Settings.RunOnlyIfIdle = false;
        definition.Settings.WakeToRun = false;
        definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        definition.Settings.Priority = ProcessPriorityClass.AboveNormal;

        service.RootFolder.RegisterTaskDefinition(
            AutoStartTaskSpec.TaskName, definition, TaskCreation.CreateOrUpdate, null, null,
            TaskLogonType.InteractiveToken, null);

        WriteLog("INFO", $"计划任务注册成功：{AutoStartTaskSpec.TaskName} → {exePath}");
    }

    private static void UnregisterTask()
    {
        try
        {
            using var service = new TaskService();
            using var task = service.GetTask(AutoStartTaskSpec.TaskName);
            if (task == null)
            {
                WriteLog("INFO", $"无需删除计划任务（本来就不存在）：{AutoStartTaskSpec.TaskName}");
                return;
            }
            service.RootFolder.DeleteTask(AutoStartTaskSpec.TaskName, exceptionOnNotExists: false);
            WriteLog("INFO", $"计划任务已删除：{AutoStartTaskSpec.TaskName}");
        }
        catch (Exception ex)
        {
            WriteLog("WARN", $"删除计划任务失败：{ex.Message}");
        }
    }

    private static void RemoveRunKeyValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(AppName) == null) return;
            key.DeleteValue(AppName, false);
            WriteLog("INFO", "已清理注册表 Run 键残留（该路径在管理员清单下必然失败）");
        }
        catch (Exception ex)
        {
            WriteLog("WARN", $"清理 Run 键失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 自启日志。不走 <see cref="Logger"/>：AutoStartHelper 是静态工具，
    /// 可能在 Logger 实例化之前被调用，自己写文件更可靠。
    /// </summary>
    private static void WriteLog(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var path = Path.Combine(LogDir, $"quickremote-{DateTime.Now:yyyy-MM-dd}.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] [AutoStart] {message}";
            lock (LogLock)
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}