using CxShell.Services;

namespace CxShell.Tests;

public sealed class RdpWheelInputTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(-3)]
    public void GetNotchFlags_AlwaysCarriesOneNotchOfRotation(int notches)
    {
        // Regression guard: without the 0x78 rotation field the remote scrolls
        // by zero notches and the wheel direction looks dead.
        Assert.Equal(
            RdpWheelInput.NotchRotation,
            RdpWheelInput.GetNotchFlags(notches) & RdpWheelInput.NotchRotation);
    }

    [Fact]
    public void GetNotchFlags_ScrollUpUsesPositiveRotation()
    {
        Assert.Equal(0x0278, RdpWheelInput.GetNotchFlags(1));
    }

    [Fact]
    public void GetNotchFlags_ScrollDownUsesNegativeRotation()
    {
        Assert.Equal(0x0378, RdpWheelInput.GetNotchFlags(-1));
    }

    [Fact]
    public void GetNotchFlags_HorizontalWheelUsesHorizontalFlag()
    {
        Assert.Equal(0x0478, RdpWheelInput.GetNotchFlags(1, horizontal: true));
        Assert.Equal(0x0578, RdpWheelInput.GetNotchFlags(-1, horizontal: true));
    }

    [Fact]
    public void TakeNotches_AccumulatesFractionalDeltas()
    {
        var remainder = 0d;

        Assert.Equal(0, RdpWheelInput.TakeNotches(ref remainder, 0.4));
        Assert.Equal(0, RdpWheelInput.TakeNotches(ref remainder, 0.4));
        Assert.Equal(1, RdpWheelInput.TakeNotches(ref remainder, 0.4));
        Assert.Equal(0.2, remainder, 3);
    }

    [Fact]
    public void TakeNotches_KeepsRemainderForPartialNegatives()
    {
        var remainder = 0d;

        Assert.Equal(0, RdpWheelInput.TakeNotches(ref remainder, -0.3));
        Assert.Equal(-0.3, remainder, 3);
        Assert.Equal(-1, RdpWheelInput.TakeNotches(ref remainder, -0.7));
        Assert.Equal(0.0, remainder, 3);
    }

    [Fact]
    public void TakeNotches_ReportsEveryWholeNotch()
    {
        var remainder = 0d;

        Assert.Equal(3, RdpWheelInput.TakeNotches(ref remainder, 3));
        Assert.Equal(0.0, remainder, 3);
        Assert.Equal(-2, RdpWheelInput.TakeNotches(ref remainder, -2));
    }
}
