using System.IO;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 开机自启任务规格（纯逻辑，无副作用，可单测）。
/// 只放「与 Task Scheduler API 无关」的常量与决策，实际注册动作在 <see cref="AutoStartHelper"/>。
///
/// <para><b>为什么用计划任务而不是 HKCU\...\Run：</b>本客户端 app.manifest 声明
/// <c>requireAdministrator</c>，而 Explorer 执行 Run 键时**不会弹 UAC**，
/// <c>CreateProcess</c> 直接返回 <c>WinError 740（请求的操作需要提升）</c>，
/// 进程根本起不来，且不留任何日志——表现就是「注册表里有、就是不自启」。</para>
///
/// <para><b>为什么必须 InteractiveToken 而不是 SYSTEM：</b>锁屏输入代理、屏幕采集、
/// 输入注入都依赖当前用户的交互式桌面会话，用 SYSTEM 会话号不同，全部失效。</para>
///
/// <para><b>为什么不用 schtasks.exe：</b>部分环境（安全加固 / EDR / 组策略）会禁用或拦截它，
/// 那会让自启再次静默失效。改用 Task Scheduler COM，无外部进程依赖。</para>
/// </summary>
public static class AutoStartTaskSpec
{
    /// <summary>计划任务名称。改名会导致旧任务残留。</summary>
    public const string TaskName = "QuickRemote-PCClient-Autostart";

    /// <summary>本客户端可执行文件名。用于从候选路径里挑出真正的 exe（见 <see cref="SelectExecutablePath"/>）。</summary>
    public const string ExpectedExeName = "QuickRemote.PCClient.exe";

    /// <summary>登录触发延迟。给网络栈和桌面一点就绪时间，避开开机启动风暴。</summary>
    public static readonly TimeSpan LogonDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 从候选路径里挑出真正的客户端 exe。
    /// <para>必须按<b>文件名精确匹配</b>而不是「后缀是 .exe」：以 <c>dotnet QuickRemote.PCClient.dll</c>
    /// 方式启动时，主机进程是 <c>dotnet.exe</c>，把主机写进任务就等于开机拉起一个空进程。</para>
    /// </summary>
    /// <param name="candidates">候选路径，按优先级排列（通常为 MainModule → ProcessPath → BaseDirectory 拼接）。</param>
    /// <returns>匹配到则返回该路径；全不匹配时回落到首个非空候选；无一可用则返回 <see cref="ExpectedExeName"/>。</returns>
    public static string SelectExecutablePath(IEnumerable<string?> candidates)
    {
        var list = (candidates ?? Array.Empty<string?>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .ToList();

        var exact = list.FirstOrDefault(c =>
            string.Equals(Path.GetFileName(c), ExpectedExeName, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        return list.FirstOrDefault() ?? ExpectedExeName;
    }

    /// <summary>
    /// 判断已注册任务是否已指向目标 exe（避免每次启动都重写任务）。
    /// </summary>
    public static bool IsSameTarget(string? existingCommand, string exePath)
    {
        if (string.IsNullOrWhiteSpace(existingCommand)) return false;
        return string.Equals(existingCommand.Trim(), exePath?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>当前用户的任务计划程序身份串（<c>域\用户</c> 或 <c>计算机\用户</c>）。</summary>
    public static string CurrentUserId()
    {
        var user = Environment.UserName;
        var domain = Environment.UserDomainName;
        return string.IsNullOrWhiteSpace(domain) ? user : domain + "\\" + user;
    }
}