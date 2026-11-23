using CxShell.Services;

namespace CxShell.Tests;

public sealed class RdpDpiScaleTests
{
    [Theory]
    [InlineData(1.0, 100)]
    [InlineData(1.25, 125)]
    [InlineData(1.5, 150)]
    [InlineData(1.75, 175)]
    [InlineData(2.0, 200)]
    [InlineData(3.0, 200)]
    [InlineData(0.5, 100)]
    [InlineData(0.0, 100)]
    [InlineData(double.NaN, 100)]
    public void PercentFromRenderScaling_QuantizesToRdpSteps(double renderScaling, int expected)
    {
        Assert.Equal(expected, RdpDpiScale.PercentFromRenderScaling(renderScaling));
    }

    [Fact]
    public void ResolveScalePercent_AppliesPolicy()
    {
        Assert.Equal(150, RdpDpiScale.ResolveScalePercent(RdpDpiPolicy.FollowLocal, 1.5));
        Assert.Equal(100, RdpDpiScale.ResolveScalePercent(RdpDpiPolicy.Fixed100, 1.5));
        Assert.Equal(0, RdpDpiScale.ResolveScalePercent(RdpDpiPolicy.KeepRemote, 1.5));
    }
}
