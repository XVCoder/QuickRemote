using System.IO;

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
    /// 获取或生成唯一 machine_id。
    /// machine_id 为一个 UUID 字符串，保存于配置文件中（由 ConfigService 管理）。
    /// 此方法仅生成新的 UUID，持久化由调用方完成。
    /// </summary>
    public static string EnsureMachineId()
    {
        return Guid.NewGuid().ToString("N");
    }
}
