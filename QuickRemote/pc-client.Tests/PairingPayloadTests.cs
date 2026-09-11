using QuickRemote.PCClient.Interop;
using Xunit;

namespace QuickRemote.PCClient.Tests;

public class PairingPayloadTests
{
    [Fact]
    public void RoundTrip()
    {
        var url = PairingPayload.Encode("relay.example.com:8444", "abc123", "书房主机");
        Assert.True(PairingPayload.TryParse(url, out var info, out var err), err);
        Assert.Equal(1, info.Version);
        Assert.Equal("relay.example.com:8444", info.Addr);
        Assert.Equal("abc123", info.Psk);
        Assert.Equal("书房主机", info.Name);
    }

    [Fact]
    public void NoBase64Padding()
    {
        var url = PairingPayload.Encode("a:1", "b", "c");
        var d = url[(url.IndexOf("d=", StringComparison.Ordinal) + 2)..];
        Assert.DoesNotContain("=", d);
        Assert.DoesNotContain("+", d);
        Assert.DoesNotContain("/", d);
    }

    [Theory]
    [InlineData("https://pair?d=eyJ2IjoxfQ")]
    [InlineData("quickremote://pair")]
    [InlineData("quickremote://pair?d=!!!bad!!!")]
    [InlineData("")]
    public void InvalidUrlRejected(string url)
    {
        Assert.False(PairingPayload.TryParse(url, out _, out _));
    }

    [Fact]
    public void VersionMismatchRejected()
        => Assert.False(PairingPayload.TryParse(
            "quickremote://pair?d=" + PairingPayload.EncodeRaw("""{"v":2,"addr":"a:1","psk":"b"}"""),
            out _, out _));

    [Fact]
    public void AddrWithoutPortRejected()
        => Assert.False(PairingPayload.TryParse(
            "quickremote://pair?d=" + PairingPayload.EncodeRaw("""{"v":1,"addr":"nohost","psk":"b"}"""),
            out _, out _));

    [Fact]
    public void EmptyPskRejected()
        => Assert.False(PairingPayload.TryParse(
            "quickremote://pair?d=" + PairingPayload.EncodeRaw("""{"v":1,"addr":"a:1","psk":""}"""),
            out _, out _));

    /// <summary>
    /// 跨端互通护栏：本用例固化 Android 端生成的确切 URL 形态。
    /// 若此用例失败，说明两端 codec 已不一致，扫码配对会失效。
    /// </summary>
    [Fact]
    public void ParsesAndroidStyleUrl()
    {
        var url = "quickremote://pair?d=" + PairingPayload.EncodeRaw(
            """{"v":1,"addr":"relay.example.com:8444","psk":"abc123","name":"书房主机"}""");
        Assert.True(PairingPayload.TryParse(url, out var info, out var err), err);
        Assert.Equal("relay.example.com:8444", info.Addr);
        Assert.Equal("书房主机", info.Name);
    }
}
