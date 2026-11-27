using Avalonia;
using CxShell.Models;
using CxShell.Services;
using CxShell.ViewModels;

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

    [Theory]
    [InlineData(RdpDpiPolicy.FollowLocal, 1.0, 100)]
    [InlineData(RdpDpiPolicy.FollowLocal, 1.25, 125)]
    [InlineData(RdpDpiPolicy.FollowLocal, 1.5, 150)]
    [InlineData(RdpDpiPolicy.FollowLocal, 1.75, 175)]
    [InlineData(RdpDpiPolicy.FollowLocal, 2.0, 200)]
    [InlineData(RdpDpiPolicy.Fixed100, 1.5, 100)]
    [InlineData(RdpDpiPolicy.KeepRemote, 1.5, 0)]
    public void ResolveScalePercent_AppliesPolicy(RdpDpiPolicy policy, double scaling, int expected)
    {
        Assert.Equal(expected, RdpDpiScale.ResolveScalePercent(policy, scaling));
    }

    [Theory]
    [InlineData(100, 100u)]
    [InlineData(125, 100u)]
    [InlineData(140, 140u)]
    [InlineData(150, 140u)]
    [InlineData(175, 140u)]
    [InlineData(180, 180u)]
    [InlineData(200, 180u)]
    public void DeviceScaleFactorFromDesktopScale_MapsToProtocolAllowedValues(int desktopScale, uint expected)
    {
        Assert.Equal(expected, RdpDpiScale.DeviceScaleFactorFromDesktopScale(desktopScale));
    }

    [Fact]
    public void ConfigureInitialDisplay_CapturesScalingAndPhysicalViewportBeforeStart()
    {
        var session = new SessionInfo
        {
            Protocol = SessionProtocol.RDP,
            RdpWindowSize = "WorkSpace",
            RdpDpiPolicy = "FollowLocal"
        };
        using var vm = new RdpViewModel(session, "secret");

        vm.ConfigureInitialDisplay(new Size(1200, 800), new PixelSize(2560, 1440), 1.5);

        Assert.Equal(1.5, vm.InitialRenderScaling);
        Assert.Equal(1800, vm.InitialDesktopWidth);
        Assert.Equal(1200, vm.InitialDesktopHeight);
        Assert.Equal(150, RdpDpiScale.ResolveScalePercent(RdpDpiPolicy.FollowLocal, vm.InitialRenderScaling));
    }
}
