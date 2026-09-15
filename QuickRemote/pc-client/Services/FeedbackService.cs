using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace QuickRemote.PCClient.Services;

/// <summary>反馈提交结果。</summary>
public sealed record FeedbackResult(bool Success, string FileName, string Message);

/// <summary>
/// 意见反馈上报。
///
/// 把用户反馈（可选附带最近 <see cref="LogTailLines"/> 行运行日志）打包成一个文本文件，
/// 以 <c>设备id_反馈时间.log</c> 命名，上传到 QuickDeploy 的 <c>quickremote/feedback</c> 目录。
///
/// 上传走平台的上传令牌接口（multipart，字段名 <c>file</c>），不依赖中继服务器是否在线 ——
/// 用户反馈的常见场景恰恰是「连不上」，所以直传 QuickDeploy 比经中继转发更可靠。
/// 令牌仅对该目录、仅对 .log 扩展名、不允许覆盖，泄露风险可控。
/// </summary>
public static class FeedbackService
{
    /// <summary>QuickDeploy 上传令牌（目标目录 quickremote/feedback）。</summary>
    private const string UploadUrl =
        "https://qd.solutionx.top/api/upload/lJ7mTnJu0FHkNcx4gOET5NS6IDFDkgE3Ch7FDsN4TNk";

    /// <summary>勾选「附带日志」时截取的日志行数。</summary>
    public const int LogTailLines = 1000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// 提交反馈。上传失败不抛异常，返回带失败原因的结果由调用方提示。
    /// </summary>
    /// <param name="content">用户填写的反馈内容。</param>
    /// <param name="includeLogs">是否附带最近 1000 行运行日志。</param>
    /// <param name="deviceId">设备 ID（PC 端为 machine_id，与中继的 device_id 同源）。</param>
    /// <param name="deviceName">设备名称，可为空。</param>
    public static async Task<FeedbackResult> SubmitAsync(
        string content, bool includeLogs, string deviceId, string deviceName)
    {
        var now = DateTime.Now;
        var fileName = $"{Sanitize(deviceId, "unknown")}_{now:yyyyMMdd_HHmmss}.log";

        try
        {
            var payload = BuildPayload(content, includeLogs, deviceId, deviceName, now);

            using var form = new MultipartFormDataContent();
            var part = new ByteArrayContent(Encoding.UTF8.GetBytes(payload));
            part.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
            form.Add(part, "file", fileName);

            App.Logger.Info($"Submitting feedback: {fileName} ({Encoding.UTF8.GetByteCount(payload)} bytes, logs={includeLogs})");

            using var resp = await Http.PostAsync(UploadUrl, form);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                App.Logger.Warn($"Feedback upload failed: HTTP {(int)resp.StatusCode} {Trim(body)}");
                return new FeedbackResult(false, fileName, $"提交失败（HTTP {(int)resp.StatusCode}）");
            }

            // 平台成功响应形如 {"file_id":"...","name":"...","size":N}，缺 file_id 视为失败
            if (!body.Contains("file_id", StringComparison.OrdinalIgnoreCase))
            {
                App.Logger.Warn($"Feedback upload unexpected response: {Trim(body)}");
                return new FeedbackResult(false, fileName, "提交失败：服务器响应异常");
            }

            App.Logger.Info($"Feedback submitted OK: {fileName}");
            return new FeedbackResult(true, fileName, "反馈已提交，感谢你的反馈！");
        }
        catch (TaskCanceledException)
        {
            App.Logger.Warn("Feedback upload timed out");
            return new FeedbackResult(false, fileName, "提交超时，请检查网络后重试");
        }
        catch (Exception ex)
        {
            App.Logger.Warn($"Feedback upload failed: {ex.Message}");
            return new FeedbackResult(false, fileName, $"提交失败：{ex.Message}");
        }
    }

    /// <summary>拼装提交内容：反馈头信息 + 用户正文 +（可选）日志。</summary>
    private static string BuildPayload(
        string content, bool includeLogs, string deviceId, string deviceName, DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine("========== QuickRemote 意见反馈 ==========");
        sb.AppendLine($"客户端   : PC v{App.Version}");
        sb.AppendLine($"设备 ID  : {Sanitize(deviceId, "unknown")}");
        sb.AppendLine($"设备名称 : {Fallback(deviceName)}");
        sb.AppendLine($"主机名   : {SystemInfo.Hostname}");
        sb.AppendLine($"系统     : {SystemInfo.OsInfo}");
        sb.AppendLine($"提交时间 : {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"附带日志 : {(includeLogs ? $"是（最近 {LogTailLines} 行）" : "否")}");
        sb.AppendLine("==========================================");
        sb.AppendLine();
        sb.AppendLine("【反馈内容】");
        sb.AppendLine(content.Trim());
        sb.AppendLine();

        if (includeLogs)
        {
            var logs = Logger.ReadTail(LogTailLines);
            sb.AppendLine($"---------- 运行日志（最近 {LogTailLines} 行） ----------");
            sb.AppendLine(string.IsNullOrWhiteSpace(logs) ? "（暂无日志）" : logs);
        }

        return sb.ToString();
    }

    /// <summary>把设备 ID 清洗成安全的文件名片段（仅保留字母数字、-、_）。</summary>
    private static string Sanitize(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        var cleaned = new string(value.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    private static string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? "(未设置)" : value.Trim();

    private static string Trim(string body)
        => string.IsNullOrEmpty(body) ? "" : body.Length > 200 ? body[..200] : body;
}
