using System.Text.Json.Serialization;

namespace QuickRemote.PCClient.Models;

/// <summary>
/// 应用配置模型，对应 appsettings.json。
/// </summary>
public class AppConfig
{
    [JsonPropertyName("Server")]
    public ServerConfig Server { get; set; } = new();

    [JsonPropertyName("AutoStart")]
    public bool AutoStart { get; set; }

    [JsonPropertyName("CheckUpdateOnStart")]
    public bool CheckUpdateOnStart { get; set; } = true;

    [JsonPropertyName("AutoUploadLogs")]
    public bool AutoUploadLogs { get; set; }

    [JsonPropertyName("QuickDeploy")]
    public QuickDeployConfig QuickDeploy { get; set; } = new();

    [JsonPropertyName("MachineId")]
    public string MachineId { get; set; } = string.Empty;

    /// <summary>本机设备名称（PC 端"远程设备"列表展示用；空 = 由服务器分配默认名"PC客户端N"）。</summary>
    [JsonPropertyName("DeviceName")]
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>远程访问验证码（被控端）：非空时主控端连接本机需输入验证码，验证通过才建立会话；空 = 不验证。</summary>
    [JsonPropertyName("AccessCode")]
    public string AccessCode { get; set; } = string.Empty;

    /// <summary>连接断开时自动锁屏（被控端）：远程会话结束后调用 LockWorkStation 锁定桌面。</summary>
    [JsonPropertyName("LockOnDisconnect")]
    public bool LockOnDisconnect { get; set; } = true;

    /// <summary>远程设备备注（设备 ID → 备注文本；仅本机可见，不上传服务器）。</summary>
    [JsonPropertyName("DeviceRemarks")]
    public Dictionary<string, string> DeviceRemarks { get; set; } = new();

    /// <summary>软删除（从列表隐藏）的远程设备 ID；设备再次上线时自动移除出该列表。</summary>
    [JsonPropertyName("HiddenDevices")]
    public List<string> HiddenDevices { get; set; } = new();

    /// <summary>远程配置：本机作为主控端远程其他主机时的参数（连接后经 configure 控制帧下发给被控端）。</summary>
    [JsonPropertyName("Viewer")]
    public ViewerConfig Viewer { get; set; } = new();

    /// <summary>被控端会话参数默认值（无设置界面：被远程时参数由主控端 configure 帧下发；
    /// 仅作为旧客户端/未下发 configure 时的兜底默认，保留字段兼容已保存的配置文件）。</summary>
    [JsonPropertyName("Host")]
    public HostConfig Host { get; set; } = new();
}

/// <summary>远程配置（主控端：本机远程其他主机时生效）。</summary>
public class ViewerConfig
{
    /// <summary>目标分辨率高度上限（0 = 原始分辨率不缩放；720/1080/1440/2160 = 等比缩放上限）。</summary>
    [JsonPropertyName("TargetMaxHeight")]
    public int TargetMaxHeight { get; set; }

    /// <summary>图像质量百分比（20-100），映射被控端码率缩放。</summary>
    [JsonPropertyName("QualityPercent")]
    public int QualityPercent { get; set; } = 100;

    /// <summary>目标帧率（5-60）。</summary>
    [JsonPropertyName("Fps")]
    public int Fps { get; set; } = 30;

    /// <summary>颜色深度（32 = 真彩色；16 = 高彩色，通道量化提升压缩率）。</summary>
    [JsonPropertyName("ColorDepth")]
    public int ColorDepth { get; set; } = 32;

    /// <summary>局域网直连优先（同网段低延迟；关闭则强制走公网中继）。</summary>
    [JsonPropertyName("PreferLan")]
    public bool PreferLan { get; set; } = true;
}

/// <summary>被远程配置（被控端：本机被其他主机远程连接时的默认参数；主控端下发的 configure 请求会覆盖会话内值）。</summary>
public class HostConfig
{
    /// <summary>默认帧率（5-60）。</summary>
    [JsonPropertyName("Fps")]
    public int Fps { get; set; } = 15;

    /// <summary>基准码率（kbps，200-12000），质量百分比按此基准缩放。</summary>
    [JsonPropertyName("BitrateKbps")]
    public int BitrateKbps { get; set; } = 4000;

    /// <summary>输出分辨率高度上限（0 = 原始分辨率不缩放）。</summary>
    [JsonPropertyName("MaxHeight")]
    public int MaxHeight { get; set; }

    /// <summary>颜色深度（32 = 真彩色；16 = 高彩色）。</summary>
    [JsonPropertyName("ColorDepth")]
    public int ColorDepth { get; set; } = 32;
}

public class ServerConfig
{
    [JsonPropertyName("Address")]
    public string Address { get; set; } = "relay.example.com:8444";

    [JsonPropertyName("PreSharedKey")]
    public string PreSharedKey { get; set; } = "change-me-please";
}

public class QuickDeployConfig
{
    [JsonPropertyName("ManifestUrl")]
    public string ManifestUrl { get; set; } = "https://quickdeploy.example.com/quickremote/manifest.json";

    [JsonPropertyName("ChangelogUrl")]
    public string ChangelogUrl { get; set; } = "https://quickdeploy.example.com/quickremote/CHANGELOG.md";
}
