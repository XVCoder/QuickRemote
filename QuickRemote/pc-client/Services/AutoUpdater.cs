using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using QuickRemote.PCClient.Views;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 自动更新服务。下载 zip 安装包，通过外部脚本完成解压覆盖和重启。
/// </summary>
public static class AutoUpdater
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>
    /// 执行自动更新：下载 → 解压覆盖 → 重启。
    /// </summary>
    /// <param name="downloadUrl">zip 下载链接</param>
    /// <param name="targetVersion">目标版本号</param>
    /// <param name="logger">日志</param>
    /// <returns>是否已启动更新流程（true 表示应用即将关闭）。</returns>
    public static async Task<bool> UpdateAsync(string downloadUrl, string targetVersion, Logger logger)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            DialogWindow.Show("下载地址无效", "更新失败", DialogWindow.DialogType.Warning);
            return false;
        }

        // 程序根目录（下载 ZIP 放这里，用户可见）
        var appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        // 脚本目录：系统临时目录（脚本/日志不污染程序目录）
        var scriptDir = Path.Combine(Path.GetTempPath(), "QuickRemoteUpdate");
        if (Directory.Exists(scriptDir)) Directory.Delete(scriptDir, true);
        Directory.CreateDirectory(scriptDir);

        // ZIP 下载位置：优先程序根目录（用户可见），权限不足时回退脚本目录
        string zipPath;
        try
        {
            var testFile = Path.Combine(appDir, ".qremote_write_test");
            File.WriteAllText(testFile, "");
            File.Delete(testFile);
            zipPath = Path.Combine(appDir, $"QuickRemote-{targetVersion}.zip");
        }
        catch
        {
            zipPath = Path.Combine(scriptDir, $"QuickRemote-{targetVersion}.zip");
        }
        // 清理旧的下载文件
        try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }

        var scriptPath = Path.Combine(scriptDir, "update.bat");
        var vbsPath = Path.Combine(scriptDir, "launcher.vbs");

        // 1. 下载
        var progress = new UpdateProgressWindow(targetVersion);
        progress.Show();

        try
        {
            logger.Info($"Downloading update from {downloadUrl}");
            using var resp = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            var totalBytes = resp.Content.Headers.ContentLength ?? -1;
            await using var contentStream = await resp.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(zipPath);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                totalRead += read;
                if (totalBytes > 0)
                {
                    var percent = (int)(totalRead * 100 / totalBytes);
                    progress.UpdateProgress(percent, $"下载中... {percent}%");
                }
                else
                {
                    progress.UpdateProgress(-1, $"下载中... {totalRead / 1024 / 1024} MB");
                }
            }

            logger.Info($"Download complete: {zipPath} ({totalRead} bytes)");
            progress.UpdateProgress(100, "正在准备安装...");
        }
        catch (Exception ex)
        {
            logger.Error("Download failed", ex);
            progress.Close();
            DialogWindow.Show($"下载更新包失败：{ex.Message}", "更新失败", DialogWindow.DialogType.Error);
            return false;
        }

        // 2. 生成更新脚本
        var exeName = Process.GetCurrentProcess().ProcessName + ".exe";
        var exePath = Path.Combine(appDir, exeName);
        var currentPid = Environment.ProcessId;
        var stagingDir = Path.Combine(scriptDir, "staging");
        var logFile = Path.Combine(scriptDir, "update.log");

        // 批处理脚本：等待旧进程退出 → 解压到暂存目录 → 检测嵌套文件夹 → robocopy 覆盖 → 验证 → 启动 → 清理
        var script = $@"@echo off
chcp 65001 >nul 2>&1
set ""LOGFILE={logFile}""
echo [%date% %time%] === QuickRemote Update Script === > ""%LOGFILE%""
echo [%date% %time%] AppDir: {appDir} >> ""%LOGFILE%""
echo [%date% %time%] ZipPath: {zipPath} >> ""%LOGFILE%""
echo [%date% %time%] ExePath: {exePath} >> ""%LOGFILE%""
echo [%date% %time%] TargetVersion: {targetVersion} >> ""%LOGFILE%""
echo [%date% %time%] CurrentPid: {currentPid} >> ""%LOGFILE%""

:: === 1. 等待旧进程退出 ===
:wait
tasklist /fi ""PID eq {currentPid}"" 2>nul | find ""{currentPid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait
)
echo [%date% %time%] Old process {currentPid} exited >> ""%LOGFILE%""

:: 等待文件句柄完全释放（留足时间给句柄释放与杀软扫描）
timeout /t 5 /nobreak >nul
echo [%date% %time%] File release wait complete >> ""%LOGFILE%""

:: === 2. 解压到暂存目录 ===
set ""STAGING={stagingDir}""
if exist ""%STAGING%"" rd /s /q ""%STAGING%""
echo [%date% %time%] Extracting to staging: %STAGING% >> ""%LOGFILE%""
powershell -NoProfile -ExecutionPolicy Bypass -Command ""Expand-Archive -LiteralPath '{zipPath}' -DestinationPath '%STAGING%' -Force""
if errorlevel 1 (
    echo [%date% %time%] ERROR: Expand-Archive failed with errorlevel %errorlevel% >> ""%LOGFILE%""
    copy ""%LOGFILE%"" ""%TEMP%\QuickRemoteUpdate-last.log"" >nul 2>&1
    msg * ""QuickRemote 更新失败：解压更新包失败，请查看日志: %TEMP%\QuickRemoteUpdate-last.log""
    start """" ""{exePath}""
    exit
)
echo [%date% %time%] Extract complete >> ""%LOGFILE%""

:: === 3. 检测 ZIP 是否包含嵌套顶层目录 ===
set ""SOURCE=%STAGING%""
set FILECOUNT=0
set DIRCOUNT=0
for /f %%f in ('dir /b /a-d ""%STAGING%"" 2^>nul ^| find /c /v ""') do set FILECOUNT=%%f
for /f %%d in ('dir /b /ad ""%STAGING%"" 2^>nul ^| find /c /v ""') do set DIRCOUNT=%%d
echo [%date% %time%] Staging root: files=%FILECOUNT%, dirs=%DIRCOUNT% >> ""%LOGFILE%""
if %FILECOUNT% equ 0 if %DIRCOUNT% equ 1 (
    for /f %%d in ('dir /b /ad ""%STAGING%""') do set ""SOURCE=%STAGING%\%%d""
    echo [%date% %time%] Detected nested folder, using: %SOURCE% >> ""%LOGFILE%""
)
dir ""%SOURCE%"" >> ""%LOGFILE%"" 2>&1

:: === 4. 用 robocopy 覆盖文件到应用目录 ===
echo [%date% %time%] Copying files to app directory with robocopy... >> ""%LOGFILE%""
robocopy ""%SOURCE%"" ""{appDir}"" /E /IS /IT /R:5 /W:2 /NFL /NDL /NJH /NJS >> ""%LOGFILE%"" 2>&1
set ROBOEXIT=%errorlevel%
echo [%date% %time%] robocopy exit code: %ROBOEXIT% >> ""%LOGFILE%""
:: robocopy exit codes 0-7 are success, 8+ are errors
if %ROBOEXIT% geq 8 (
    echo [%date% %time%] ERROR: robocopy failed with exit code %ROBOEXIT% >> ""%LOGFILE%""
    copy ""%LOGFILE%"" ""%TEMP%\QuickRemoteUpdate-last.log"" >nul 2>&1
    msg * ""QuickRemote 更新失败：复制文件失败 (robocopy exit %ROBOEXIT%)，请查看日志: %TEMP%\QuickRemoteUpdate-last.log""
    start """" ""{exePath}""
    exit
)
echo [%date% %time%] robocopy completed successfully >> ""%LOGFILE%""

:: === 5. 验证新版本 DLL 是否存在 ===
timeout /t 1 /nobreak >nul
if not exist ""{appDir}\{Path.GetFileNameWithoutExtension(exeName)}.dll"" (
    echo [%date% %time%] ERROR: DLL not found after copy >> ""%LOGFILE%""
    copy ""%LOGFILE%"" ""%TEMP%\QuickRemoteUpdate-last.log"" >nul 2>&1
    msg * ""QuickRemote 更新失败：更新后找不到 DLL，请查看日志: %TEMP%\QuickRemoteUpdate-last.log""
    start """" ""{exePath}""
    exit
)
echo [%date% %time%] DLL verified >> ""%LOGFILE%""

:: === 6. 启动新版本 ===
echo [%date% %time%] Starting new version: {exePath} >> ""%LOGFILE%""
start """" ""{exePath}""

:: === 7. 清理脚本目录（ZIP 在程序根目录，保留供查看） ===
timeout /t 3 /nobreak >nul
copy ""%LOGFILE%"" ""%TEMP%\QuickRemoteUpdate-last.log"" >nul 2>&1
rd /s /q ""{scriptDir}"" 2>nul
echo [%date% %time%] Cleanup complete >> ""%TEMP%\QuickRemoteUpdate-last.log""
exit
";

        await File.WriteAllTextAsync(scriptPath, script);
        logger.Info($"Update script created: {scriptPath}");

        // 创建 VBScript 启动器（完全隐藏窗口，创建独立进程）
        // vbs 用 WshShell.Run 启动 bat，参数 0 = 隐藏窗口，False = 不等待
        // VBS 中双引号用 "" 转义，所以 """path""" 表示字符串 "path"
        var vbsContent = "Set WshShell = CreateObject(\"WScript.Shell\")\r\n" +
                         $"WshShell.Run \"\"\"{scriptPath}\"\"\", 0, False\r\n";
        await File.WriteAllTextAsync(vbsPath, vbsContent);
        logger.Info($"VBS launcher created: {vbsPath}");

        progress.Close();

        // 3. 通过 VBScript 启动器启动更新脚本
        try
        {
            logger.Info("Launching update script via VBS launcher");
            // UseShellExecute=true 确保进程独立，不绑定到父进程作业对象
            // wscript.exe 运行 vbs，vbs 用 WshShell.Run 启动 bat（隐藏窗口），然后 wscript 立即退出
            // bat 作为独立进程继续运行，不受 C# 进程退出影响
            Process.Start(new ProcessStartInfo
            {
                FileName = "wscript.exe",
                Arguments = $"\"{vbsPath}\"",
                UseShellExecute = true
            });

            // 等待 bat 脚本真正启动（update.log 被创建即确认），最多 10 秒
            // 避免 wscript/vbs 被延迟（杀软扫描等）时 C# 进程已退出导致脚本未运行
            for (int i = 0; i < 50; i++)
            {
                if (File.Exists(logFile)) break;
                await Task.Delay(200);
            }
            if (!File.Exists(logFile))
            {
                logger.Warn("Update script may not have started (update.log not created within 10s)");
            }

            // C# 主动退出，bat 脚本会等待本进程退出后继续执行解压覆盖
            logger.Info("Exiting process for update");
            Environment.Exit(0);
            return true;
        }
        catch (Exception ex)
        {
            logger.Error("Failed to launch update script", ex);
            DialogWindow.Show($"启动更新失败：{ex.Message}", "更新失败", DialogWindow.DialogType.Error);
            return false;
        }
    }
}
