using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 机器信息收集服务。获取机器名、操作系统信息，并生成/持久化唯一 machine_id。
/// </summary>
public static class SystemInfo
{
    /// <summary>机器名</summary>
    public static string Hostname => Environment.MachineName;

    /// <summary>操作系统信息</summary>
    public static string OsInfo
    {
        get
        {
            var os = Environment.OSVersion;
            return $"Windows {os.Version.Major}.{os.Version.Minor} (Build {os.Version.Build}) {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}";
        }
    }

    /// <summary>
    /// 获取本机局域网 IPv4 地址（优先以太网/Wi-Fi 的私有网段地址）。
    /// 用于注册上报，Android 端据此判断设备是否在同一局域网。
    /// 获取失败时返回空字符串。
    /// </summary>
    public static string GetLanIp()
    {
        try
        {
            var candidates = new List<string>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = addr.Address.ToString();
                    // 私有网段：10.x / 172.16-31.x / 192.168.x
                    if (IsPrivateIp(ip)) candidates.Add(ip);
                }
            }
            if (candidates.Count > 0)
            {
                // 优先 192.168.x / 10.x 常见家庭局域网
                return candidates
                    .OrderByDescending(ip => ip.StartsWith("192.168.") || ip.StartsWith("10."))
                    .First();
            }
            // 无私有 IP 时回退到默认路由接口的 IP
            using var udp = new UdpClient();
            udp.Connect("8.8.8.8", 80);
            var local = udp.Client.LocalEndPoint as IPEndPoint;
            return local?.Address.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool IsPrivateIp(string ip)
    {
        return ip.StartsWith("10.") ||
               ip.StartsWith("192.168.") ||
               (ip.StartsWith("172.") && int.TryParse(ip.Split('.')[1], out var b) && b is >= 16 and <= 31);
    }

    /// <summary>
    /// 获取或生成唯一 machine_id。
    /// machine_id 为一个 UUID 字符串，保存于配置文件中（由 ConfigService 管理）。
    /// 此方法仅生成新的 UUID，持久化由调用方完成。
    /// </summary>
    public static string EnsureMachineId()
    {
        return Guid.NewGuid().ToString("N");
    }
}
