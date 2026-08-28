using System.Diagnostics;
using System.IO;
using QuickRemote.PCClient.Views;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 自动更新服务。
///
/// 采用独立更新程序（update.exe）完成「下载 → 解压 → 覆盖 → 重启」：
/// 主程序只负责把 update.exe 复制到临时目录启动（避免更新时覆盖自身），
/// 传参后立即退出，由 update.exe 独立完成剩余工作。
///
/// 相比旧的 bat + VBS + PowerShell 脚本方案，链路更短、不依赖脚本引擎与
/// PowerShell 执行策略，也不受杀软对脚本的拦截影响。
/// </summary>
public static class AutoUpdater
{
    /// <summary>更新程序文件名（随发布包分发在应用目录）。</summary>
    private const string UpdaterFileName = "update.exe";

    /// <summary>
    /// 执行自动更新：启动 update.exe → 主程序退出。
    /// </summary>
    /// <param name="downloadUrl">zip 下载链接</param>
    /// <param name="targetVersion">目标版本号</param>
    /// <param name="logger">日志</param>
    /// <returns>是否已启动更新流程（true 表示应用即将关闭）。</returns>
    public static bool StartUpdate(string downloadUrl, string targetVersion, Logger logger)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            DialogWindow.Show("下载地址无效", "更新失败", DialogWindow.DialogType.Warning);
            return false;
        }

        var appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var exeName = Process.GetCurrentProcess().ProcessName + ".exe";
        var exePath = Path.Combine(appDir, exeName);
        var updaterPath = Path.Combine(appDir, UpdaterFileName);

        // 1. 检查更新程序是否存在
        if (!File.Exists(updaterPath))
        {
            logger.Warn($"Updater not found: {updaterPath}");
            var openBrowser = DialogWindow.Confirm(
                "未找到更新程序（update.exe），无法自动安装。\n\n" +
                "是否打开浏览器手动下载最新安装包？\n" +
                "下载后请解压覆盖到当前程序目录。",
                "无法自动更新",
                DialogWindow.DialogType.Warning);
            if (openBrowser)
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = downloadUrl, UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    logger.Error("Open download url failed", ex);
                }
            }
            return false;
        }

        // 2. 把 update.exe 复制到临时目录运行（避免更新过程中覆盖自身文件）
        var workDir = Path.Combine(Path.GetTempPath(), "QuickRemoteUpdate");
        string runnerPath;
        try
        {
            Directory.CreateDirectory(workDir);
            runnerPath = Path.Combine(workDir, UpdaterFileName);
            File.Copy(updaterPath, runnerPath, overwrite: true);
            logger.Info($"Updater copied to: {runnerPath}");
        }
        catch (Exception ex)
        {
            // 复制失败时直接运行程序目录内的 update.exe（更新包若不含 update.exe 则无影响）
            logger.Warn($"Copy updater to temp failed, run in-place: {ex.Message}");
            runnerPath = updaterPath;
        }

        // 3. 启动 update.exe（独立进程，主程序退出后继续运行）
        try
        {
            var args = $"--url \"{downloadUrl}\" --dir \"{appDir}\" --exe \"{exePath}\" " +
                       $"--pid {Environment.ProcessId} --version \"{targetVersion}\"";
            logger.Info($"Launching updater: {runnerPath} {args}");

            Process.Start(new ProcessStartInfo
            {
                FileName = runnerPath,
                Arguments = args,
                UseShellExecute = true
            });

            // 确认更新程序已启动（进程出现即可，不必等它干活）
            var started = WaitForUpdaterStart();
            if (!started)
            {
                logger.Warn("Updater process not detected within 5s");
            }

            logger.Info("Exiting process for update");
            Environment.Exit(0);
            return true;
        }
        catch (Exception ex)
        {
            logger.Error("Failed to launch updater", ex);
            DialogWindow.Show($"启动更新程序失败：{ex.Message}", "更新失败", DialogWindow.DialogType.Error);
            return false;
        }
    }

    /// <summary>等待 update.exe 进程出现（最多 5 秒）。</summary>
    private static bool WaitForUpdaterStart()
    {
        for (int i = 0; i < 25; i++)
        {
            try
            {
                var procs = Process.GetProcessesByName("update");
                if (procs.Length > 0)
                {
                    foreach (var p in procs) p.Dispose();
                    return true;
                }
            }
            catch
            {
                // 忽略查询异常，继续重试
            }
            Thread.Sleep(200);
        }
        return false;
    }
}
