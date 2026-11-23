using CxShell.Services;

namespace CxShell.Tests;

public sealed class RdpSecurityProfileCacheTests
{
    [Theory]
    [InlineData("RDP security profile selected mode=negotiate.", "negotiate")]
    [InlineData("RDP security profile selected mode=NLA.", "nla")]
    [InlineData("RDP security profile selected mode=tls.", "tls")]
    [InlineData("RDP security profile selected mode=rdp.", "rdp")]
    public void TryParseProfile_ReadsTheWinningHandshake(string status, string expected)
    {
        Assert.True(RdpSecurityProfileCache.TryParseProfile(status, out var profile));
        Assert.Equal(expected, profile);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RDP connected.")]
    [InlineData("RDP security attempt mode=nla")]
    [InlineData("RDP security profile selected mode=unknown.")]
    public void TryParseProfile_IgnoresOtherStatusLines(string? status)
    {
        Assert.False(RdpSecurityProfileCache.TryParseProfile(status, out var profile));
        Assert.Empty(profile);
    }

    [Fact]
    public void Get_WithNoHost_ReturnsNull()
    {
        Assert.Null(RdpSecurityProfileCache.Get(string.Empty, 3389));
        Assert.Null(RdpSecurityProfileCache.Get("   ", 3389));
    }
}
