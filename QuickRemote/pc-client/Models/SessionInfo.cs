using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace QuickRemote.PCClient.Models;

/// <summary>
/// 远程会话信息。
/// </summary>
public class SessionInfo : INotifyPropertyChanged
{
    private long _bytesSent;
    private long _bytesReceived;
    private DateTime _startTime;
    private bool _isActive;
    private string _deviceName = string.Empty;

    /// <summary>会话 ID（来自服务器的 tunnel_request）</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>客户端设备名（来自控制消息或隧道对端；configure 帧携带的 deviceName 到达时可实时更新展示）。</summary>
    public string DeviceName
    {
        get => _deviceName;
        set { if (_deviceName != value) { _deviceName = value; OnPropertyChanged(); } }
    }

    /// <summary>客户端 IP（隧道对端地址，可为空）</summary>
    public string ClientIp { get; set; } = string.Empty;

    /// <summary>连接模式描述（局域网直连 / 公网中继 / RDP 隧道）</summary>
    public string ModeText { get; set; } = "RDP Active";

    public DateTime StartTime
    {
        get => _startTime;
        set { _startTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(DurationText)); }
    }

    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; OnPropertyChanged(); }
    }

    public long BytesSent
    {
        get => _bytesSent;
        set { _bytesSent = value; OnPropertyChanged(); OnPropertyChanged(nameof(TrafficText)); }
    }

    public long BytesReceived
    {
        get => _bytesReceived;
        set { _bytesReceived = value; OnPropertyChanged(); OnPropertyChanged(nameof(TrafficText)); }
    }

    /// <summary>会话持续时长文本（HH:mm）</summary>
    public string DurationText
    {
        get
        {
            var dur = DateTime.Now - _startTime;
            if (dur < TimeSpan.Zero) dur = TimeSpan.Zero;
            return $"{(int)dur.TotalHours:D2}:{dur.Minutes:D2}";
        }
    }

    /// <summary>流量统计文本（↑ 上行 ↓ 下行）</summary>
    public string TrafficText => $"↑ {FormatBytes(BytesSent)} ↓ {FormatBytes(BytesReceived)}";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1}MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2}GB";
    }

    /// <summary>刷新会话时长显示（供定时器周期调用）。</summary>
    public void RefreshDuration() => OnPropertyChanged(nameof(DurationText));

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
