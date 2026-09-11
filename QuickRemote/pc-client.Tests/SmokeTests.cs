using Xunit;

namespace QuickRemote.PCClient.Tests;

/// <summary>
/// 测试脚手架冒烟用例：确认 xunit 测试工程可用。
/// 纯逻辑（配对编解码 / 剪贴板分片）以此为基线走 TDD。
/// 被测源文件通过 csproj 的 &lt;Compile Include&gt; 直接引入，不引用 WinExe 工程。
/// </summary>
public class SmokeTests
{
    [Fact]
    public void Placeholder() => Assert.Equal(2, 1 + 1);
}
