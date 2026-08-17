using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 局域网发现服务：周期性 UDP 广播本机设备信息，
/// 供同一局域网内的 Android 端自动发现并直连（内网 RDP 模式）。
/// 遍历所有网卡向各子网广播地址发送，避免单网卡全网广播被丢弃。
/// </summary>
public sealed class LanDiscoveryService : IDisposable
{
    /// <summary>广播端口（与 Android 端一致）。</summary>
    public const int BroadcastPort = 8446;

    private const int IntervalMs = 5000;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>日志回调（由上层注入，用于诊断广播状态）。</summary>
    public static Action<string>? LogInfo;

    /// <summary>启动广播（后台任务，失败不影响主功能）。</summary>
    public void Start(string deviceId, string hostname, int rdpPort)
    {
        if (_disposed) return;
        try
        {
            _udp = new UdpClient { EnableBroadcast = true };
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => BroadcastLoop(deviceId, hostname, rdpPort, _cts.Token));
            LogInfo?.Invoke($"LanDiscovery started: {hostname} (port {rdpPort})");
        }
        catch (Exception ex)
        {
            LogInfo?.Invoke($"LanDiscovery start failed: {ex.Message}");
        }
    }

    private async Task BroadcastLoop(string deviceId, string hostname, int rdpPort, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(
            $"{{\"t\":\"qr_discover\",\"id\":\"{deviceId}\",\"host\":\"{hostname}\",\"port\":{rdpPort}}}");

        var loggedTargets = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var targets = GetBroadcastTargets().ToList();
                if (!loggedTargets)
                {
                    LogInfo?.Invoke($"LanDiscovery broadcasting to {targets.Count} target(s): {string.Join(", ", targets.Select(t => t.Address))}");
                    loggedTargets = true;
                }
                foreach (var target in targets)
                {
                    try
                    {
                        await _udp!.SendAsync(payload, payload.Length, target);
                    }
                    catch
                    {
                        // 单个网卡广播失败忽略
                    }
                }
            }
            catch
            {
                // 整体广播失败忽略
            }
            try { await Task.Delay(IntervalMs, ct); } catch { break; }
        }
    }

    /// <summary>
    /// 计算所有网卡的子网广播地址（含全网广播 255.255.255.255 兜底）。
    /// </summary>
    private static IEnumerable<IPEndPoint> GetBroadcastTargets()
    {
        var targets = new HashSet<string>
        {
            IPAddress.Broadcast.ToString() // 兜底：全网广播
        };

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                foreach (var uni in ni.GetIPProperties().UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue; // 仅 IPv4
                    var ip = uni.Address;
                    var mask = uni.IPv4Mask;
                    if (mask == null || mask.Equals(IPAddress.Any)) continue;

                    // 广播地址 = IP | ~Mask
                    var ipBytes = ip.GetAddressBytes();
                    var maskBytes = mask.GetAddressBytes();
                    var bcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                        bcast[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                    targets.Add(new IPAddress(bcast).ToString());
                }
            }
        }
        catch
        {
            // 网卡枚举失败，仅用全网广播
        }

        return targets.Select(t => new IPEndPoint(IPAddress.Parse(t), BroadcastPort));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _cts = null;
        _udp = null;
    }
}
