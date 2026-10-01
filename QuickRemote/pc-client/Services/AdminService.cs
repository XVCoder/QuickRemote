using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 管理员模式 HTTP 客户端（v1.1.77）。
///
/// 与 relay 服务端新增的两个接口交互：
///   POST /api/admin/verify         {password}                    → 200 {ok} / 401
///   POST /api/admin/device/delete  {password, device_id}         → 200 {ok} / 401 / 500
/// 两者都要求 Bearer JWT（requireAuth），所以先走 /api/auth 用预共享密钥换 token
///（与 Android 端 RelayApi 同一套认证：pre_shared_key = SHA256(PSK) 的 hex）。
///
/// 地址换算：PC 配置里存的是**控制连接**地址（端口 = HTTP + 1，默认 8444），
/// HTTP API 在其减一的位置（默认 8443）。scheme 跟随控制地址（https → https）。
/// </summary>
public static class AdminService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>校验管理员密码。成功返回 (true, null, false)。</summary>
    public static async Task<(bool Ok, string? Error, bool PasswordInvalid)> VerifyAsync(string controlAddress, string preSharedKey, string password)
    {
        var (ok, err, pwInvalid) = await PostAsync(controlAddress, preSharedKey, "/api/admin/verify", new { password });
        return (ok, err, pwInvalid);
    }

    /// <summary>物理删除设备。成功返回 (true, null, false)；密码错误时 PasswordInvalid = true
    ///（管理员模式中遇到即视为"密码已被服务器侧更改"，调用方应引导重新登录）。</summary>
    public static async Task<(bool Ok, string? Error, bool PasswordInvalid)> DeleteDeviceAsync(string controlAddress, string preSharedKey, string password, string deviceId)
    {
        return await PostAsync(controlAddress, preSharedKey, "/api/admin/device/delete", new { password, device_id = deviceId });
    }

    private static async Task<(bool Ok, string? Error, bool PasswordInvalid)> PostAsync(string controlAddress, string preSharedKey, string path, object payload)
    {
        try
        {
            var baseUrl = ToHttpBase(controlAddress);
            var token = await GetTokenAsync(baseUrl, preSharedKey);
            if (token == null)
                return (false, "无法通过服务器认证（预共享密钥错误或服务器不可达）", false);

            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + path)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var resp = await Http.SendAsync(req);
            if (resp.IsSuccessStatusCode) return (true, null, false);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return (false, "管理员密码错误", true);
            return (false, $"服务器返回 HTTP {(int)resp.StatusCode}", false);
        }
        catch (Exception ex)
        {
            return (false, $"网络请求失败：{ex.Message}", false);
        }
    }

    /// <summary>用预共享密钥换 JWT（pre_shared_key = SHA256(PSK) hex，与 /api/auth 协议一致）。</summary>
    private static async Task<string?> GetTokenAsync(string baseUrl, string preSharedKey)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(preSharedKey));
        var authKey = Convert.ToHexString(hash).ToLowerInvariant();

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/auth")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { pre_shared_key = authKey }), Encoding.UTF8, "application/json")
        };
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("token", out var token) ? token.GetString() : null;
    }

    /// <summary>
    /// 控制连接地址 → HTTP API 地址（端口 -1，scheme 跟随）。
    /// "192.168.1.10:8444" → "http://192.168.1.10:8443"；
    /// "https://relay.example.com" → "https://relay.example.com:8443"（控制默认 8444，HTTP 即 8443）。
    /// </summary>
    internal static string ToHttpBase(string controlAddress)
    {
        var address = controlAddress.Trim().TrimEnd('/');
        var useTls = address.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) address = address[8..];
        else if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) address = address[7..];

        string host = address;
        int port = useTls ? 8444 : 8444; // 控制连接默认端口（与 RelayAddress.ParseAddress 一致）
        var colon = address.LastIndexOf(':');
        if (colon > 0 && int.TryParse(address[(colon + 1)..], out var p))
        {
            host = address[..colon];
            port = p;
        }

        var httpPort = port > 0 ? port - 1 : 8443;
        var scheme = useTls ? "https" : "http";
        return $"{scheme}://{host}:{httpPort}";
    }
}
