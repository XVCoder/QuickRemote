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
    /// 获取本机局域网 IPv4 地址（优先默认路由接口，排除虚拟网卡）。
    /// 用于注册上报，Android 端据此判断设备是否在同一局域网。
    /// 获取失败时返回空字符串。
    /// </summary>
    public static string GetLanIp()
    {
        try
        {
            // 1. 优先：默认路由接口的源 IP（UDP connect 不发包，仅让系统选路）
            //    虚拟网卡（VMware/WSL/Hyper-V）没有默认路由，天然被排除
            using (var udp = new UdpClient())
            {
                udp.Connect("8.8.8.8", 80);
                if (udp.Client.LocalEndPoint is IPEndPoint ep && IsPrivateIp(ep.Address.ToString()))
                {
                    return ep.Address.ToString();
                }
            }

            // 2. 回退：带网关的物理接口（离线局域网场景），排除常见虚拟网卡
            var gatewayCandidates = new List<string>();
            var otherCandidates = new List<string>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var name = ni.Name.ToLowerInvariant();
                var desc = ni.Description.ToLowerInvariant();
                var isVirtual = name.Contains("vmware") || name.Contains("vmnet") || name.Contains("vethernet") ||
                                name.Contains("wsl") || name.Contains("loopback") ||
                                desc.Contains("vmware") || desc.Contains("virtualbox") || desc.Contains("hyper-v") ||
                                desc.Contains("virtual") || desc.Contains("tap") || desc.Contains("tunnel") || desc.Contains("wsl");
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = addr.Address.ToString();
                    // 私有网段：10.x / 172.16-31.x / 192.168.x
                    if (!IsPrivateIp(ip)) continue;
                    if (isVirtual) continue;
                    var hasGateway = ni.GetIPProperties().GatewayAddresses.Count > 0;
                    if (hasGateway) gatewayCandidates.Add(ip);
                    else otherCandidates.Add(ip);
                }
            }
            // 优先带网关的接口，其次 192.168.x / 10.x 常见家庭局域网
            return gatewayCandidates
                .Concat(otherCandidates)
                .OrderByDescending(ip => ip.StartsWith("192.168.") || ip.StartsWith("10."))
                .FirstOrDefault() ?? "";
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
