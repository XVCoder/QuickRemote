using System.Net;
using System.Net.Sockets;
using System.Text;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 局域网发现服务：周期性 UDP 广播本机设备信息，
/// 供同一局域网内的 Android 端自动发现并直连（内网 RDP 模式）。
/// </summary>
public sealed class LanDiscoveryService : IDisposable
{
    /// <summary>广播端口（与 Android 端一致）。</summary>
    public const int BroadcastPort = 8446;

    private const int IntervalMs = 5000;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>启动广播（后台任务，失败不影响主功能）。</summary>
    public void Start(string deviceId, string hostname, int rdpPort)
    {
        if (_disposed) return;
        try
        {
            _udp = new UdpClient { EnableBroadcast = true };
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => BroadcastLoop(deviceId, hostname, rdpPort, _cts.Token));
        }
        catch (Exception ex)
        {
            // 广播失败（如无网络）不影响主功能
            System.Diagnostics.Debug.WriteLine($"LanDiscovery start failed: {ex.Message}");
        }
    }

    private async Task BroadcastLoop(string deviceId, string hostname, int rdpPort, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(
            $"{{\"t\":\"qr_discover\",\"id\":\"{deviceId}\",\"host\":\"{hostname}\",\"port\":{rdpPort}}}");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _udp!.SendAsync(payload, payload.Length,
                    new IPEndPoint(IPAddress.Broadcast, BroadcastPort));
            }
            catch
            {
                // 单次广播失败忽略
            }
            try { await Task.Delay(IntervalMs, ct); } catch { break; }
        }
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
