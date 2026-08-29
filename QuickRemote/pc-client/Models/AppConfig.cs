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
