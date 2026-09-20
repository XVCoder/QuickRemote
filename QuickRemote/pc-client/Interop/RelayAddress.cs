using System;

namespace QuickRemote.PCClient.Interop;

/// <summary>
/// 中继服务器地址的规范化，以及两端端口换算。
///
/// 纯逻辑、无 WPF 依赖 —— 被 pc-client.Tests 直接编译，便于单元测试。
/// 与 Android 端 com.quickremote.app.data.RelayAddress 保持同一套规则，改动务必同步。
///
/// 中继的端口约定（见 `relay-server/deploy/install.sh` 与 `cmd/server/main.go`）：
/// - **HTTP/API 端口**（默认 8443）：**只有 Android 用** —— 登录认证 / 设备列表 / 隧道请求
/// - **控制连接端口 = HTTP 端口 + 1**（默认 8444）：**只有 PC 用** —— 远程控制连接
/// - 隧道数据端口（默认 8445）：两端共用
/// </summary>
public static class RelayAddress
{
    /// <summary>中继 HTTP/API 端口（Android 专用）。</summary>
    public const int HttpApiPort = 8443;

    /// <summary>中继控制连接端口（PC 专用，= HTTP 端口 + 1）。</summary>
    public const int ControlPort = 8444;

    /// <summary>
    /// 解析服务器地址，返回 (host, port, useTls)。
    /// PC 客户端连接的是控制连接端口（8444），不是 HTTP API 端口（8443）。
    /// https://  → TLS，默认 8444
    /// http://   → 明文，默认 8444
    /// 无 scheme → 明文，默认 8444
    /// </summary>
    public static (string host, int port, bool useTls) ParseAddress(string address)
    {
        var addr = (address ?? string.Empty).Trim();
        var useTls = false;

        if (addr.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            useTls = true;
            addr = addr[8..];
        }
        else if (addr.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            addr = addr[7..];
        }
        addr = addr.TrimEnd('/');

        var idx = addr.LastIndexOf(':');
        if (idx > 0 && int.TryParse(addr[(idx + 1)..], out var port))
        {
            return (addr[..idx], port, useTls);
        }

        // PC 客户端连接控制连接端口（8444），不是 HTTP API 端口（8443）
        return (addr, ControlPort, useTls);
    }

    /// <summary>
    /// 把用户填写的服务器地址规范化成配对载荷用的 `host:端口` 形式。
    ///
    /// 配对载荷（二维码 / 明文配置串）固定携带 **PC 的控制连接端口**（缺省 8444），
    /// 由 Android 端导入时换算成自己的 HTTP/API 端口（−1）。这样：
    /// - 用户只填 `relay.example.com` 时，载荷也是 `relay.example.com:8444`，
    ///   不会生成一个缺少端口、Android 端判定为「服务器地址无效」的二维码；
    /// - 显式写了 `https://` 时保留前缀，表示控制连接走 TLS。
    /// </summary>
    public static string NormalizeForPairing(string address)
    {
        var (host, port, useTls) = ParseAddress(address);
        if (string.IsNullOrWhiteSpace(host))
        {
            return string.Empty;
        }
        return useTls ? $"https://{host}:{port}" : $"{host}:{port}";
    }
}
