using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace QuickRemote.PCClient.Models;

/// <summary>
/// 远程设备信息（服务器 device_list 广播条目）。
/// 用于 PC 端"远程设备"列表展示与点击连接。
/// </summary>
public class RemoteDeviceInfo : INotifyPropertyChanged
{
    private string _displayName = string.Empty;
    private string _status = "offline";

    /// <summary>设备 ID（服务器主键，machine_id）。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>主机名。</summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>显示名称（用户自定义或服务器默认分配"PC客户端N"）。</summary>
    public string DisplayName
    {
        get => string.IsNullOrEmpty(_displayName) ? Hostname : _displayName;
        set { _displayName = value; OnPropertyChanged(); }
    }

    /// <summary>局域网 IP（同网段时主控端直连用）。</summary>
    public string LanIp { get; set; } = string.Empty;

    /// <summary>本机私有备注（仅本机可见，本地持久化；由 ViewModel 在合并设备列表时写入）。</summary>
    public string Remark
    {
        get => _remark;
        set { _remark = value; OnPropertyChanged(); }
    }
    private string _remark = string.Empty;

    /// <summary>客户端版本。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>在线状态（online/offline）。</summary>
    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOnline));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusDotBrushKey));
        }
    }

    public bool IsOnline => Status == "online";

    public string StatusText => IsOnline ? "在线" : "离线";

    /// <summary>状态点画刷资源键（在线绿/离线灰）。</summary>
    public string StatusDotBrushKey => IsOnline ? "SuccessBrush" : "TextMutedBrush";

    /// <summary>副标题文本：IP + 版本。</summary>
    public string SubtitleText => string.IsNullOrEmpty(LanIp) ? $"v{Version}" : $"{LanIp} · v{Version}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
