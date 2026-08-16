using System.Diagnostics;
using Microsoft.Win32;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// RDP 配置检查与启用服务。
/// 通过注册表查询/修改 RDP 服务状态与端口，通过 netsh 管理防火墙规则。
/// </summary>
public static class RdpConfigurator
{
    private const string TerminalServerKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    private const string RdpTcpKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";
    private const string FirewallRuleName = "QuickRemote RDP";

    // 网络级认证（NLA）：启用后远程连接必须先通过身份认证，认证在创建会话之前完成。
    // 关键作用：FreeRDP 未提供正确凭据时会被干净拒绝，不会创建会话、不会抢占本地控制台，
    // 从而避免「本地黑屏 + 远程连不上」的卡死问题。
    private const string UserAuthenticationValue = "UserAuthentication";
    private const string SecurityLayerValue = "SecurityLayer";

    /// <summary>RDP 是否已启用（fDenyTSConnections == 0 表示启用）。</summary>
    public static bool IsRdpEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(TerminalServerKey, writable: false);
            if (key == null) return false;
            var value = key.GetValue("fDenyTSConnections");
            return value is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>NLA（网络级认证）是否已启用（UserAuthentication == 1）。</summary>
    public static bool IsNlaEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RdpTcpKey, writable: false);
            if (key == null) return false;
            var value = key.GetValue(UserAuthenticationValue);
            return value is int i && i == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 启用 NLA 并将安全层设为 TLS。
    /// 这是避免「远程连接导致本地黑屏」的关键：NLA 让认证在会话创建前完成，
    /// 认证失败则直接拒绝连接，本地控制台会话不会被抢占。
    /// 需要管理员权限。
    /// </summary>
    public static bool EnableNla()
    {
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(RdpTcpKey, writable: true))
            {
                if (key == null) return false;
                key.SetValue(UserAuthenticationValue, 1, RegistryValueKind.DWord);
                key.SetValue(SecurityLayerValue, 2, RegistryValueKind.DWord);
            }
            return IsNlaEnabled();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>获取 RDP 端口（默认 3389）。</summary>
    public static int GetRdpPort()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RdpTcpKey, writable: false);
            if (key == null) return 3389;
            var value = key.GetValue("PortNumber");
            return value is int i ? i : 3389;
        }
        catch
        {
            return 3389;
        }
    }

    /// <summary>检查防火墙是否已放行远程桌面规则。</summary>
    public static bool IsFirewallRuleEnabled()
    {
        try
        {
            var (stdout, stderr, exitCode) = RunNetShWithResult($"advfirewall firewall show rule name=\"{FirewallRuleName}\"");
            if (exitCode != 0) return false;
            // 中文系统输出 "规则名称"，英文系统输出 "Rule Name"
            // 只要不是 "No rules" / "没有" 就认为规则存在
            var combined = stdout + stderr;
            if (string.IsNullOrWhiteSpace(stdout)) return false;
            var hasNoRules = combined.Contains("No rules", StringComparison.OrdinalIgnoreCase)
                             || combined.Contains("没有", StringComparison.OrdinalIgnoreCase)
                             || combined.Contains("未找到", StringComparison.OrdinalIgnoreCase);
            return !hasNoRules;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 一键启用 RDP：修改注册表开启 RDP，并添加防火墙规则。
    /// 需要管理员权限。
    /// </summary>
    /// <returns>是否成功。</returns>
    public static bool EnableRdp(int port)
    {
        // 开启 RDP（fDenyTSConnections = 0）
        using (var key = Registry.LocalMachine.OpenSubKey(TerminalServerKey, writable: true))
        {
            if (key != null) key.SetValue("fDenyTSConnections", 0, RegistryValueKind.DWord);
        }

        // 设置端口
        using (var key = Registry.LocalMachine.OpenSubKey(RdpTcpKey, writable: true))
        {
            if (key != null) key.SetValue("PortNumber", port, RegistryValueKind.DWord);
        }

        // 添加防火墙规则（TCP 入站），使用自定义名称避免与系统规则冲突
        // 先删除旧规则（如果存在），再添加
        RunNetShWithResult($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");
        var (_, stderr, _) = RunNetShWithResult(
            $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={port}");

        // 同时确保系统内置远程桌面组规则也启用
        RunNetShWithResult("advfirewall firewall set rule group=\"远程桌面\" new enable=yes");
        RunNetShWithResult("advfirewall firewall set rule group=\"Remote Desktop\" new enable=yes");

        // 启用 NLA + TLS 安全层：避免无凭据连接抢占本地控制台导致黑屏
        EnableNla();

        return IsRdpEnabled() && IsFirewallRuleEnabled();
    }

    /// <summary>
    /// 紧急恢复本地控制台：注销所有卡住的远程 RDP 会话（rdp-tcp#N），
    /// 让 Windows 自动切回本地控制台会话，无需重启电脑。
    /// 需要管理员权限。
    /// </summary>
    /// <returns>注销的远程会话数量（-1 表示执行失败）。</returns>
    public static int RecoverConsoleSession()
    {
        try
        {
            var (stdout, _, exitCode) = RunCmdWithResult("query session");
            if (exitCode != 0 && string.IsNullOrWhiteSpace(stdout)) return -1;

            int loggedOff = 0;
            foreach (var line in stdout.Split('\n'))
            {
                var trimmed = line.Trim();
                // 匹配 "rdp-tcp#N" 开头的远程会话行，提取会话 ID
                if (trimmed.StartsWith("rdp-tcp#", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("rdp-tcp", StringComparison.OrdinalIgnoreCase))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        trimmed, @"rdp-tcp#?\d*\s+\S*\s+(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var sessionId))
                    {
                        RunCmdWithResult($"logoff {sessionId}");
                        loggedOff++;
                    }
                }
            }
            return loggedOff;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>运行 netsh 命令并返回 stdout、stderr 和退出码。</summary>
    private static (string stdout, string stderr, int exitCode) RunNetShWithResult(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return (string.Empty, string.Empty, -1);
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(5000);
        return (stdout, stderr, proc.ExitCode);
    }

    /// <summary>运行 cmd 命令（query session / logoff 等）并返回 stdout、stderr 和退出码。</summary>
    private static (string stdout, string stderr, int exitCode) RunCmdWithResult(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {command}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return (string.Empty, string.Empty, -1);
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(5000);
        return (stdout, stderr, proc.ExitCode);
    }
}
