using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuickRemote.PCClient.Models;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 配置管理服务。加载/保存 appsettings.json，首次运行创建默认配置。
/// machine_id 持久化于配置文件中。
///
/// 配置迁移：新版本若新增了配置项，启动时用内置默认值补齐缺失键，
/// 用户已有的配置值（服务器地址/密钥/machine_id 等）全部保留。
/// 更新程序（update.exe）不再覆盖 appsettings.json，只做 .bak 备份。
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

    /// <summary>加载配置。若文件不存在则使用默认值并保存；存在则做新配置项迁移。</summary>
    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                Config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                // 迁移：用内置默认值补齐新版本新增的配置项（保留用户已有值）
                var migrated = MigrateWithDefaults(json);
                if (migrated != null)
                {
                    Config = migrated;
                    Save();
                }
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

    /// <summary>
    /// 配置迁移：以内置默认配置（new AppConfig()，包含最新字段）为模板，
    /// 与现有配置 JSON 深度合并——用户已有的键保留原值，缺失的键（新版本新增）用默认值补齐。
    /// 仅在确有缺失键时返回合并结果，否则返回 null（避免无谓写盘）。
    /// </summary>
    private AppConfig? MigrateWithDefaults(string existingJson)
    {
        try
        {
            var defaultsNode = JsonNode.Parse(JsonSerializer.Serialize(new AppConfig(), JsonOptions))?.AsObject();
            var existingNode = JsonNode.Parse(existingJson)?.AsObject();
            if (defaultsNode == null || existingNode == null) return null;

            var merged = MergeJson(defaultsNode, existingNode);
            // 与旧配置语义相同（无新键）则不动
            if (JsonNode.DeepEquals(existingNode, merged)) return null;

            return JsonSerializer.Deserialize<AppConfig>(merged.ToJsonString(), JsonOptions);
        }
        catch
        {
            // 迁移失败不阻塞启动（沿用已加载的配置）
            return null;
        }
    }

    /// <summary>
    /// 递归合并：以 defaults 为模板，existing 的键值优先；
    /// defaults 有而 existing 没有的键（新增配置项）用默认值补齐。
    /// existing 中 defaults 没有的旧键保留（不删用户数据）。
    /// </summary>
    private static JsonObject MergeJson(JsonObject defaults, JsonObject existing)
    {
        var result = defaults.DeepClone().AsObject();
        foreach (var (key, value) in existing)
        {
            if (value != null && result.TryGetPropertyValue(key, out var defVal) &&
                defVal is JsonObject defObj && value is JsonObject exObj)
            {
                result[key] = MergeJson(defObj, exObj);
            }
            else
            {
                result[key] = value?.DeepClone();
            }
        }
        return result;
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
