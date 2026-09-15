using System.IO;
using System.Text;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 日志服务。按天滚动日志文件，保留 7 天。
/// 格式: [2026-08-01 10:00:00] [INFO] [Component] Message
/// </summary>
public sealed class Logger : IDisposable
{
    private static readonly object FileLock = new();
    private static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickRemote", "logs");

    private readonly string _component;

    public Logger(string component)
    {
        _component = component;
        Directory.CreateDirectory(LogDir);
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);
    public void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] [{_component}] {message}";
        System.Diagnostics.Debug.WriteLine(line);

        try
        {
            lock (FileLock)
            {
                CleanupOldLogs();
                var path = Path.Combine(LogDir, $"quickremote-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不应影响主流程
        }
    }

    /// <summary>清理 7 天前的日志文件。</summary>
    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var file in Directory.GetFiles(LogDir, "quickremote-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>获取所有日志文件路径（用于上传）。</summary>
    public static IReadOnlyList<string> GetLogFiles()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            return Directory.GetFiles(LogDir, "quickremote-*.log");
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>获取日志目录。</summary>
    public static string GetLogDirectory() => LogDir;

    /// <summary>
    /// 读取最近 <paramref name="maxLines"/> 行日志（用于意见反馈附带日志）。
    /// 按文件名（即日期）从新到旧读取，凑满行数即停止，不会把 7 天日志全量载入内存。
    /// </summary>
    public static string ReadTail(int maxLines)
    {
        if (maxLines <= 0) return string.Empty;

        try
        {
            var files = GetLogFiles()
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal);

            var tail = new List<string>();
            foreach (var file in files)
            {
                List<string> dayLines;
                try { dayLines = File.ReadAllLines(file).ToList(); }
                catch { continue; }

                if (tail.Count == 0) tail.AddRange(dayLines);
                else tail.InsertRange(0, dayLines);   // 更早的一天插到前面，保持时间升序

                if (tail.Count >= maxLines) break;
            }

            if (tail.Count > maxLines) tail = tail.GetRange(tail.Count - maxLines, maxLines);
            return string.Join(Environment.NewLine, tail);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>清空全部日志文件（下次写入时自动重建当天文件）。</summary>
    public static void ClearLogs()
    {
        try
        {
            foreach (var file in Directory.GetFiles(LogDir, "quickremote-*.log"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
            // 清空失败不影响主流程
        }
    }

    public void Dispose()
    {
    }
}
