using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickRemote.PCClient.Interop;

/// <summary>
/// 配对载荷编解码，与 Android 端 com.quickremote.app.data.PairingPayload 共享同一契约。
///
/// 格式：quickremote://pair?d=&lt;base64url_no_padding(UTF-8 JSON)&gt;
/// JSON：{"v":1,"addr":"host:port","psk":"...","name":"设备名"}
///
/// 两端实现必须逐字节一致。改动任一端务必同步另一端，并跑通
/// pc-client.Tests 的 PairingPayloadTests 与 Android 的 PairingPayloadTest。
/// </summary>
public static class PairingPayload
{
    public const string Scheme = "quickremote";
    public const string Host = "pair";
    public const int Version = 1;

    public sealed class PairInfo
    {
        [JsonPropertyName("v")] public int Version { get; set; } = PairingPayload.Version;
        [JsonPropertyName("addr")] public string Addr { get; set; } = string.Empty;
        [JsonPropertyName("psk")] public string Psk { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }

    private static readonly JsonSerializerOptions Opts = new()
    {
        // 中文设备名不做 \uXXXX 转义，载荷更短、二维码更稀疏、更好扫
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>生成完整配对 URL（二维码内容与明文配置串都是它）。</summary>
    public static string Encode(string addr, string psk, string name)
    {
        var payload = new PairInfo { Addr = addr, Psk = psk, Name = name };
        var raw = JsonSerializer.Serialize(payload, Opts);
        return $"{Scheme}://{Host}?d={EncodeRaw(raw)}";
    }

    /// <summary>仅编码原始 JSON 串（供测试构造非法载荷用）。</summary>
    public static string EncodeRaw(string rawJson)
    {
        var bytes = Encoding.UTF8.GetBytes(rawJson);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>解析配对 URL。失败返回 false 并给出原因，绝不抛异常。</summary>
    public static bool TryParse(string? url, out PairInfo info, out string error)
    {
        info = new PairInfo();
        error = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(url)) { error = "空输入"; return false; }

            var trimmed = url.Trim();
            if (!trimmed.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
            {
                error = "不是 QuickRemote 配对链接";
                return false;
            }

            var q = trimmed.IndexOf('?');
            if (q < 0) { error = "缺少参数"; return false; }
            var query = trimmed[(q + 1)..];

            string? d = null;
            foreach (var pair in query.Split('&'))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0] == "d") { d = kv[1]; break; }
            }
            if (string.IsNullOrEmpty(d)) { error = "缺少 d 参数"; return false; }

            var b64 = d.Replace('-', '+').Replace('_', '/');
            switch (b64.Length % 4)
            {
                case 2: b64 += "=="; break;
                case 3: b64 += "="; break;
                case 1: error = "base64 长度非法"; return false;
            }

            var rawText = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            var parsed = JsonSerializer.Deserialize<PairInfo>(rawText, Opts);
            if (parsed == null) { error = "载荷解析失败"; return false; }
            if (parsed.Version != Version) { error = $"版本不支持：{parsed.Version}"; return false; }
            if (string.IsNullOrWhiteSpace(parsed.Addr) || !parsed.Addr.Contains(':'))
            {
                error = "服务器地址无效";
                return false;
            }
            if (string.IsNullOrWhiteSpace(parsed.Psk)) { error = "预共享密钥为空"; return false; }

            info = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = "载荷损坏：" + ex.Message;
            return false;
        }
    }
}
