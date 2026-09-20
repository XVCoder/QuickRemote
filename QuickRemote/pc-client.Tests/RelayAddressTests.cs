using QuickRemote.PCClient.Interop;
using Xunit;

namespace QuickRemote.PCClient.Tests;

/// <summary>
/// 中继地址规范化 / 配对载荷端口换算的测试。
/// 与 Android 端 RelayAddressTest.kt 覆盖同一组规则，两端必须一致。
/// </summary>
public class RelayAddressTests
{
    [Theory]
    [InlineData("relay.example.com")]          // 缺端口 → 8444
    [InlineData("relay.example.com:8444")]     // 显式控制端口
    public void NormalizeForPairing_DefaultsToControlPort(string input)
    {
        Assert.Equal("relay.example.com:8444", RelayAddress.NormalizeForPairing(input));
    }

    [Theory]
    [InlineData("http://relay.example.com")]
    [InlineData("http://relay.example.com:8444")]
    public void NormalizeForPairing_PlainHttpDropsScheme(string input)
    {
        Assert.Equal("relay.example.com:8444", RelayAddress.NormalizeForPairing(input));
    }

    [Theory]
    [InlineData("https://relay.example.com")]
    [InlineData("https://relay.example.com:8444")]
    public void NormalizeForPairing_KeepsHttpsScheme(string input)
    {
        Assert.Equal("https://relay.example.com:8444", RelayAddress.NormalizeForPairing(input));
    }

    [Fact]
    public void NormalizeForPairing_KeepsCustomPort()
    {
        Assert.Equal("relay.example.com:9444", RelayAddress.NormalizeForPairing("relay.example.com:9444"));
    }

    [Fact]
    public void NormalizeForPairing_EmptyReturnsEmpty()
    {
        Assert.Equal(string.Empty, RelayAddress.NormalizeForPairing(""));
        Assert.Equal(string.Empty, RelayAddress.NormalizeForPairing("   "));
    }

    [Fact]
    public void NormalizeForPairing_TrimsTrailingSlash()
    {
        Assert.Equal("relay.example.com:8444", RelayAddress.NormalizeForPairing("relay.example.com:8444/"));
    }

    [Theory]
    [InlineData("relay.example.com", "relay.example.com", 8444, false)]
    [InlineData("relay.example.com:9444", "relay.example.com", 9444, false)]
    [InlineData("http://relay.example.com:9444", "relay.example.com", 9444, false)]
    [InlineData("https://relay.example.com", "relay.example.com", 8444, true)]
    public void ParseAddress_Cases(string input, string host, int port, bool useTls)
    {
        var (h, p, tls) = RelayAddress.ParseAddress(input);
        Assert.Equal(host, h);
        Assert.Equal(port, p);
        Assert.Equal(useTls, tls);
    }

    /// <summary>
    /// 端到端：PC 规范化 → 编码 → 载荷里带控制端口（Android 端据此 −1 换算）。
    /// 旧行为下用户只填裸 host 会产出一个无端口的载荷，Android 端判定「服务器地址无效」。
    /// </summary>
    [Theory]
    [InlineData("relay.example.com", "relay.example.com:8444")]
    [InlineData("https://relay.example.com", "https://relay.example.com:8444")]
    public void PairingPayload_AlwaysCarriesPort(string input, string expectedAddr)
    {
        var addr = RelayAddress.NormalizeForPairing(input);
        var url = PairingPayload.Encode(addr, "psk", "书房主机");

        Assert.True(PairingPayload.TryParse(url, out var info, out var err), err);
        Assert.Equal(expectedAddr, info.Addr);
    }
}
