using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 版本更新检查服务。从配置的 quickdeploy manifest URL 下载 manifest.json，
/// 解析最新版本号，对比当前版本。
/// </summary>
public sealed class UpdateChecker : INotifyPropertyChanged, IDisposable
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly Logger _logger;
    private bool _isUpdateAvailable;
    private string _latestVersion = string.Empty;
    private string _downloadUrl = string.Empty;
    private string _changelogUrl = string.Empty;
    private string _lastErrorMessage = string.Empty;

    public string CurrentVersion { get; }

    /// <summary>上次检查的错误信息，检查成功时为空字符串。</summary>
    public string LastErrorMessage
    {
        get => _lastErrorMessage;
        private set { _lastErrorMessage = value; OnPropertyChanged(); }
    }

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set { _isUpdateAvailable = value; OnPropertyChanged(); }
    }

    public string LatestVersion
    {
        get => _latestVersion;
        private set { _latestVersion = value; OnPropertyChanged(); }
    }

    public string DownloadUrl => _downloadUrl;
    public string ChangelogUrl => _changelogUrl;

    public UpdateChecker(string currentVersion, Logger logger)
    {
        CurrentVersion = currentVersion;
        _logger = logger;
    }

    /// <summary>检查更新。manifestUrl 为空或请求失败时安全返回。</summary>
    public async Task CheckAsync(string manifestUrl)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            LastErrorMessage = "未配置更新地址";
            IsUpdateAvailable = false;
            return;
        }

        try
        {
            LastErrorMessage = string.Empty;
            _logger.Info($"Checking update from {manifestUrl}");
            using var resp = await Http.GetAsync(manifestUrl);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            // 尝试解析多应用 manifest 格式：{ "pc-client": { "latest_version": "...", "versions": {...} } }
            string? latestVer = null;
            string? dlUrl = null;
            string? clUrl = null;

            var multi = JsonSerializer.Deserialize<MultiAppManifest>(json);
            if (multi?.PcClient != null && !string.IsNullOrWhiteSpace(multi.PcClient.LatestVersion))
            {
                latestVer = multi.PcClient.LatestVersion;
                // 从 versions 字典中获取最新版本的下载链接
                if (multi.PcClient.Versions != null &&
                    multi.PcClient.Versions.TryGetValue(latestVer, out var verInfo))
                {
                    dlUrl = verInfo.Zip ?? verInfo.Apk ?? string.Empty;
                }
                clUrl = multi.PcClient.Changelog;
            }
            else
            {
                // 回退到简单格式：{ "version": "...", "download_url": "...", "changelog_url": "..." }
                var simple = JsonSerializer.Deserialize<Manifest>(json);
                if (simple != null && !string.IsNullOrWhiteSpace(simple.Version))
                {
                    latestVer = simple.Version;
                    dlUrl = simple.DownloadUrl;
                    clUrl = simple.ChangelogUrl;
                }
            }

            if (string.IsNullOrWhiteSpace(latestVer))
            {
                LastErrorMessage = "更新清单格式无效";
                IsUpdateAvailable = false;
                return;
            }

            LatestVersion = latestVer.TrimStart('v', 'V');
            _downloadUrl = dlUrl ?? string.Empty;
            _changelogUrl = clUrl ?? string.Empty;

            IsUpdateAvailable = CompareVersions(LatestVersion, CurrentVersion) > 0;
            _logger.Info($"Update check done: latest={LatestVersion}, current={CurrentVersion}, available={IsUpdateAvailable}");
        }
        catch (TaskCanceledException)
        {
            LastErrorMessage = "检查更新超时，请稍后重试";
            _logger.Warn("Update check timed out");
            IsUpdateAvailable = false;
        }
        catch (HttpRequestException ex)
        {
            LastErrorMessage = $"网络请求失败：{ex.Message}";
            _logger.Warn($"Update check failed: {ex.Message}");
            IsUpdateAvailable = false;
        }
        catch (Exception ex)
        {
            LastErrorMessage = $"检查更新失败：{ex.Message}";
            _logger.Warn($"Update check failed: {ex.Message}");
            IsUpdateAvailable = false;
        }
    }

    /// <summary>对比语义化版本号，返回 1 表示 a 更新，-1 表示 b 更新，0 表示相同。</summary>
    private static int CompareVersions(string a, string b)
    {
        var pa = ParseVersion(a);
        var pb = ParseVersion(b);
        for (int i = 0; i < 3; i++)
        {
            if (pa[i] > pb[i]) return 1;
            if (pa[i] < pb[i]) return -1;
        }
        return 0;
    }

    private static int[] ParseVersion(string v)
    {
        var parts = (v ?? string.Empty).TrimStart('v', 'V').Split('.');
        var result = new int[3];
        for (int i = 0; i < 3 && i < parts.Length; i++)
        {
            int.TryParse(parts[i], out result[i]);
        }
        return result;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose() { }

    private sealed class Manifest
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("changelog_url")]
        public string? ChangelogUrl { get; set; }
    }

    /// <summary>多应用 manifest 格式（quickdeploy 上存储的完整清单）。</summary>
    private sealed class MultiAppManifest
    {
        [JsonPropertyName("pc-client")]
        public AppManifestEntry? PcClient { get; set; }
    }

    private sealed class AppManifestEntry
    {
        [JsonPropertyName("latest_version")]
        public string? LatestVersion { get; set; }

        [JsonPropertyName("changelog")]
        public string? Changelog { get; set; }

        [JsonPropertyName("versions")]
        public Dictionary<string, VersionEntry>? Versions { get; set; }
    }

    private sealed class VersionEntry
    {
        [JsonPropertyName("zip")]
        public string? Zip { get; set; }

        [JsonPropertyName("apk")]
        public string? Apk { get; set; }

        [JsonPropertyName("amd64")]
        public string? Amd64 { get; set; }

        [JsonPropertyName("arm64")]
        public string? Arm64 { get; set; }
    }
}
