using QuickRemote.PCClient.Services;
using Xunit;

namespace QuickRemote.PCClient.Tests;

/// <summary>
/// 开机自启任务规格的测试。
///
/// 背景：v1.1.81 自启失效的根因是 app.manifest 声明 requireAdministrator，
/// 而 Explorer 执行 HKCU\...\Run 时不弹 UAC，CreateProcess 直接返回 WinError 740，
/// 进程起不来且无任何日志（实测已复现 740）。修复改为 Task Scheduler 任务
/// （InteractiveToken + HighestAvailable）。
///
/// 这些用例锁住「不能再退回 Run 键 / 不能挑错主机进程」等关键决策，
/// 避免后续有人图省事改回注册表实现。
/// </summary>
public class AutoStartTaskSpecTests
{
    [Fact]
    public void TaskName_IsStableNonEmptyIdentifier()
    {
        // 改名会让旧任务残留成孤儿，且新任务名会被用户看到
        Assert.False(string.IsNullOrWhiteSpace(AutoStartTaskSpec.TaskName));
        Assert.Equal("QuickRemote-PCClient-Autostart", AutoStartTaskSpec.TaskName);
    }

    [Fact]
    public void ExpectedExeName_MatchesRealClientExe()
    {
        Assert.Equal("QuickRemote.PCClient.exe", AutoStartTaskSpec.ExpectedExeName);
    }

    [Fact]
    public void LogonDelay_IsShortButNonZero()
    {
        // 0 延迟会撞开机启动风暴；过长则用户以为没自启
        Assert.InRange(AutoStartTaskSpec.LogonDelay.TotalSeconds, 1, 60);
    }

    [Fact]
    public void SelectExecutablePath_PrefersRealClientExe()
    {
        // dotnet xxx.dll 启动时 MainModule 是 dotnet.exe，
        // 把主机写进任务等于开机拉起一个空进程
        var picked = AutoStartTaskSpec.SelectExecutablePath(new[]
        {
            @"C:\Program Files\dotnet\dotnet.exe",
            @"E:\Apps\QuickRemote\QuickRemote.PCClient.exe",
        });

        Assert.Equal(@"E:\Apps\QuickRemote\QuickRemote.PCClient.exe", picked);
    }

    [Fact]
    public void SelectExecutablePath_CaseInsensitiveMatch()
    {
        var picked = AutoStartTaskSpec.SelectExecutablePath(new[]
        {
            @"E:\Apps\quickremote.pcclient.exe",
        });
        Assert.Equal(@"E:\Apps\quickremote.pcclient.exe", picked);
    }

    [Fact]
    public void SelectExecutablePath_TrimsWhitespace()
    {
        var picked = AutoStartTaskSpec.SelectExecutablePath(new[]
        {
            @"  E:\Apps\QuickRemote\QuickRemote.PCClient.exe  ",
        });
        Assert.Equal(@"E:\Apps\QuickRemote\QuickRemote.PCClient.exe", picked);
    }

    [Fact]
    public void SelectExecutablePath_FallsBackToFirstNonEmpty()
    {
        var picked = AutoStartTaskSpec.SelectExecutablePath(new[]
        {
            null,
            "   ",
            @"C:\host\other.exe",
        });
        Assert.Equal(@"C:\host\other.exe", picked);
    }

    [Fact]
    public void SelectExecutablePath_DefaultsToExpectedNameWhenNoCandidates()
    {
        var picked = AutoStartTaskSpec.SelectExecutablePath(Array.Empty<string?>());
        Assert.Equal(AutoStartTaskSpec.ExpectedExeName, picked);
    }

    [Fact]
    public void SelectExecutablePath_IgnoresNullCandidateList()
    {
        IEnumerable<string?>? empty = null;
        var picked = AutoStartTaskSpec.SelectExecutablePath(empty!);
        Assert.Equal(AutoStartTaskSpec.ExpectedExeName, picked);
    }

    [Theory]
    [InlineData(@"E:\Apps\QuickRemote\QuickRemote.PCClient.exe", @"E:\Apps\QuickRemote\QuickRemote.PCClient.exe", true)]
    [InlineData(@"E:\Apps\QuickRemote\QuickRemote.PCClient.exe", @"e:\apps\quickremote\quickremote.pcclient.exe", true)]
    [InlineData(@"E:\Apps\QuickRemote\QuickRemote.PCClient.exe", @"  E:\Apps\QuickRemote\QuickRemote.PCClient.exe  ", true)]
    [InlineData(@"E:\Apps\QuickRemote\QuickRemote.PCClient.exe", @"E:\Old\QuickRemote.PCClient.exe", false)]
    public void IsSameTarget_ComparesPathsLeniently(string? existing, string exePath, bool expected)
    {
        Assert.Equal(expected, AutoStartTaskSpec.IsSameTarget(existing, exePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsSameTarget_TreatsMissingCommandAsDifferent(string? existing)
    {
        // 读不到命令 ⇒ 判定需要重建任务，不能当作「已配置」
        Assert.False(AutoStartTaskSpec.IsSameTarget(existing, @"E:\a\QuickRemote.PCClient.exe"));
    }

    [Fact]
    public void CurrentUserId_HasDomainPrefixOrBareUser()
    {
        var id = AutoStartTaskSpec.CurrentUserId();
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Contains(Environment.UserName, id);
    }
}