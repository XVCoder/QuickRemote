using System.IO;
using System.Text.Json;
using QuickRemote.PCClient.Models;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 配置管理服务。加载/保存 appsettings.json，首次运行创建默认配置。
/// machine_id 持久化于配置文件中。
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>配置文件路径（应用程序基目录下）。</summary>
    public string ConfigPath { get; }

    public AppConfig Config { get; private set; } = new();

    public ConfigService()
    {
        ConfigPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    /// <summary>加载配置。若文件不存在则使用默认值并保存。</summary>
    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                Config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
            else
            {
                Config = new AppConfig();
                Save();
            }
        }
        catch
        {
            Config = new AppConfig();
        }

        // 确保 machine_id 存在
        if (string.IsNullOrWhiteSpace(Config.MachineId))
        {
            Config.MachineId = SystemInfo.EnsureMachineId();
            Save();
        }
    }

    /// <summary>保存配置到 appsettings.json。</summary>
    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // 保存失败忽略，避免崩溃
        }
    }

    /// <summary>导出当前配置为 JSON 字符串（用于复制到剪贴板迁移）。</summary>
    public string ExportJson()
    {
        return JsonSerializer.Serialize(Config, JsonOptions);
    }
}
