using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;

namespace QuickRemote.Updater;

/// <summary>
/// QuickRemote PC 客户端独立更新程序（update.exe）。
///
/// 由主程序在用户确认更新后启动（主程序会先把本程序复制到临时目录运行，
/// 避免更新过程中覆盖自身文件）。本程序负责：
///   1. 等待主程序进程退出
///   2. 下载更新包（ZIP）
///   3. 解压到暂存目录
///   4. 覆盖到应用目录
///   5. 启动新版本主程序
///
/// 用法：
///   update.exe --url &lt;zip下载链接&gt; --dir &lt;应用目录&gt; --exe &lt;主程序exe路径&gt; --pid &lt;主进程PID&gt; [--version &lt;版本号&gt;]
/// </summary>
internal static class Program
{
    private static string _logFile = "";
    private static readonly List<string> LogBuffer = new();

    private const int WaitProcessTimeoutSec = 60;
    private const int CopyRetryCount = 10;
    private const int CopyRetryDelayMs = 800;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "QuickRemote 更新程序";

        // 解析参数
        var opts = ParseArgs(args);
        if (opts == null)
        {
            PrintUsage();
            return 2;
        }

        // 日志目录
        var workDir = Path.Combine(Path.GetTempPath(), "QuickRemoteUpdate");
        Directory.CreateDirectory(workDir);
        _logFile = Path.Combine(workDir, "update.log");
        Log($"=== QuickRemote Updater === url={opts.Url} dir={opts.AppDir} exe={opts.ExePath} pid={opts.Pid} ver={opts.Version}");

        try
        {
            // 1. 等待主程序退出
            Step("等待主程序关闭...");
            if (!WaitForProcessExit(opts.Pid))
            {
                Log($"WARN: process {opts.Pid} still running after {WaitProcessTimeoutSec}s, continue anyway");
            }
            // 额外等待文件句柄释放
            Thread.Sleep(1500);

            // 2. 下载更新包
            var zipPath = Path.Combine(workDir, $"QuickRemote-{opts.Version}.zip");
            Step("下载更新包...");
            if (!DownloadAsync(opts.Url, zipPath).GetAwaiter().GetResult())
            {
                Fail(opts, "下载更新包失败");
                return 1;
            }
            Log($"Download complete: {zipPath} ({new FileInfo(zipPath).Length} bytes)");

            // 3. 解压到暂存目录
            var staging = Path.Combine(workDir, "staging");
            Step("解压更新包...");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            try
            {
                ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
            }
            catch (Exception ex)
            {
                Log($"ERROR: extract failed: {ex}");
                Fail(opts, "解压更新包失败");
                return 1;
            }

            // 4. 检测嵌套顶层目录（ZIP 含单层目录时取其内部）
            var source = DetectSourceDir(staging);
            Log($"Source dir: {source}");

            // 5. 覆盖到应用目录
            Step("安装更新文件...");
            var (copied, failed, failedFiles) = CopyOverride(source, opts.AppDir);
            Log($"Copy result: copied={copied}, failed={failed}");
            if (failed > 0)
            {
                // 部分文件未更新时不能算成功：新旧文件混合会导致版本不一致等诡异问题，
                // 且会让用户误以为已升级（实际启动的仍是旧版本）。
                Log($"ERROR: {failed} file(s) failed to copy: {string.Join(", ", failedFiles)}");
                Fail(opts,
                    $"有 {failed} 个文件未能覆盖（可能被占用）。\n" +
                    "请确认 QuickRemote 已完全退出（托盘图标右键退出）后重新检查更新。");
                return 1;
            }
            if (copied == 0)
            {
                Fail(opts, "更新包中没有可安装的文件");
                return 1;
            }

            // 6. 启动新版本
            Step("启动新版本...");
            if (!File.Exists(opts.ExePath))
            {
                Log($"ERROR: exe not found after update: {opts.ExePath}");
                Fail(opts, "更新后未找到主程序");
                return 1;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = opts.ExePath,
                UseShellExecute = true,
                WorkingDirectory = opts.AppDir
            });
            Log("New version started");

            // 7. 清理暂存目录（保留日志）
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }

            Step("更新完成！");
            Log("=== Update succeeded ===");
            FlushLog();
            Thread.Sleep(1200);
            return 0;
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
            Fail(opts, $"更新过程异常：{ex.Message}");
            return 1;
        }
    }

    // ==================== 步骤实现 ====================

    private static bool WaitForProcessExit(int pid)
    {
        if (pid <= 0) return true;
        try
        {
            var proc = Process.GetProcessById(pid);
            Log($"Waiting for process {pid} ({proc.ProcessName}) to exit...");
            if (!proc.WaitForExit(WaitProcessTimeoutSec * 1000)) return false;
            Log($"Process {pid} exited");
            return true;
        }
        catch (ArgumentException)
        {
            // 进程已不存在
            Log($"Process {pid} not found (already exited)");
            return true;
        }
        catch (Exception ex)
        {
            Log($"WARN: wait process error: {ex.Message}");
            return true;
        }
    }

    private static async Task<bool> DownloadAsync(string url, string destPath)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(destPath);

            var buffer = new byte[81920];
            long read = 0;
            int n;
            var lastPct = -1;
            while ((n = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n));
                read += n;
                if (total > 0)
                {
                    var pct = (int)(read * 100 / total);
                    if (pct != lastPct && pct % 5 == 0)
                    {
                        lastPct = pct;
                        Progress($"下载中... {pct}%  ({read / 1024 / 1024} MB / {total / 1024 / 1024} MB)");
                    }
                }
                else
                {
                    Progress($"下载中... {read / 1024 / 1024} MB");
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Log($"ERROR: download failed: {ex}");
            return false;
        }
    }

    /// <summary>检测 ZIP 解压后的实际源目录（处理单层嵌套目录）。</summary>
    private static string DetectSourceDir(string staging)
    {
        try
        {
            var files = Directory.GetFiles(staging);
            var dirs = Directory.GetDirectories(staging);
            if (files.Length == 0 && dirs.Length == 1)
            {
                return dirs[0];
            }
        }
        catch (Exception ex)
        {
            Log($"WARN: detect source dir failed: {ex.Message}");
        }
        return staging;
    }

    /// <summary>
    /// 把源目录内容覆盖复制到目标目录。
    /// 跳过自身（update.exe），避免覆盖正在运行的程序文件。
    /// </summary>
    private static (int copied, int failed, List<string> failedFiles) CopyOverride(string source, string target)
    {
        int copied = 0, failed = 0;
        var failedFiles = new List<string>();
        var selfName = Path.GetFileName(Environment.ProcessPath ?? "");

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, rel);

            // 跳过自身（若更新包内包含 update.exe 且正是当前运行的文件）
            if (!string.IsNullOrEmpty(selfName) &&
                string.Equals(Path.GetFileName(dest), selfName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFullPath(dest),
                    Path.GetFullPath(Environment.ProcessPath ?? ""), StringComparison.OrdinalIgnoreCase))
            {
                Log($"Skip self: {dest}");
                continue;
            }

            // 保护用户配置文件：更新不覆盖 appsettings.json，
            // 仅把旧配置备份为 appsettings.json.bak（新配置项由应用启动时自动迁移补齐）。
            if (string.Equals(Path.GetFileName(dest), "appsettings.json", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(dest))
            {
                try
                {
                    var bak = Path.Combine(target, "appsettings.json.bak");
                    File.Copy(dest, bak, overwrite: true);
                    Log($"Backup config: {dest} -> {bak}");
                }
                catch (Exception ex)
                {
                    Log($"WARN: backup config failed: {ex.Message}");
                }
                Log($"Skip overwrite config: {dest} (user config preserved)");
                continue;
            }

            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

            var ok = CopyWithRetry(file, dest);
            if (ok)
            {
                copied++;
            }
            else
            {
                failed++;
                failedFiles.Add(Path.GetFileName(dest));
                Log($"ERROR: copy failed: {file} -> {dest}");
            }
        }
        return (copied, failed, failedFiles);
    }

    private static bool CopyWithRetry(string src, string dest)
    {
        for (int i = 0; i < CopyRetryCount; i++)
        {
            try
            {
                File.Copy(src, dest, overwrite: true);
                return true;
            }
            catch (IOException) when (i < CopyRetryCount - 1)
            {
                Thread.Sleep(CopyRetryDelayMs);
            }
            catch (UnauthorizedAccessException) when (i < CopyRetryCount - 1)
            {
                Thread.Sleep(CopyRetryDelayMs);
            }
            catch (Exception ex)
            {
                Log($"ERROR: copy exception: {ex.Message}");
                return false;
            }
        }
        return false;
    }

    // ==================== 辅助 ====================

    private static void Fail(Options opts, string reason)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"更新失败：{reason}");
        Console.ResetColor();
        Console.WriteLine($"日志：{_logFile}");
        Log($"=== Update failed: {reason} ===");
        FlushLog();

        // 更新失败时尝试恢复启动旧版本，避免用户无程序可用
        try
        {
            if (File.Exists(opts.ExePath))
            {
                Console.WriteLine("正在重新启动 QuickRemote...");
                Thread.Sleep(500);
                Process.Start(new ProcessStartInfo
                {
                    FileName = opts.ExePath,
                    UseShellExecute = true,
                    WorkingDirectory = opts.AppDir
                });
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR: restart failed: {ex.Message}");
            FlushLog();
        }

        Console.WriteLine();
        Console.WriteLine("按任意键关闭...");
        try { Console.ReadKey(true); } catch { }
    }

    private sealed class Options
    {
        public string Url { get; set; } = "";
        public string AppDir { get; set; } = "";
        public string ExePath { get; set; } = "";
        public int Pid { get; set; }
        public string Version { get; set; } = "";
    }

    private static Options? ParseArgs(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                dict[args[i][2..]] = args[i + 1];
                i++;
            }
        }

        if (!dict.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url)) return null;
        if (!dict.TryGetValue("dir", out var dir) || string.IsNullOrWhiteSpace(dir)) return null;
        if (!dict.TryGetValue("exe", out var exe) || string.IsNullOrWhiteSpace(exe)) return null;

        var opts = new Options { Url = url, AppDir = dir, ExePath = exe };
        var parsedPid = 0;
        if (dict.TryGetValue("pid", out var pidStr)) int.TryParse(pidStr, out parsedPid);
        opts.Pid = parsedPid;
        if (dict.TryGetValue("version", out var ver)) opts.Version = ver;
        return opts;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法：");
        Console.WriteLine("  update.exe --url <zip下载链接> --dir <应用目录> --exe <主程序路径> --pid <主进程PID> [--version <版本号>]");
        Console.WriteLine();
        Console.WriteLine("按任意键关闭...");
        try { Console.ReadKey(true); } catch { }
    }

    private static void Step(string text)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[步骤] {text}");
        Console.ResetColor();
        Log($"STEP: {text}");
    }

    private static void Progress(string text)
    {
        Console.Write($"\r{text}   ");
        LogBuffer.Add(text);
    }

    private static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        LogBuffer.Add(line);
        // 每 20 条或错误时刷盘
        if (LogBuffer.Count >= 20) FlushLog();
    }

    private static void FlushLog()
    {
        if (LogBuffer.Count == 0 || string.IsNullOrEmpty(_logFile)) return;
        try
        {
            File.AppendAllLines(_logFile, LogBuffer);
            LogBuffer.Clear();
        }
        catch
        {
            // 日志写入失败不影响更新流程
        }
    }
}
