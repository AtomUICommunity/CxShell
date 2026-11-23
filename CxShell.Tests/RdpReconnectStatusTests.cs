using CxShell.Services;
using CxShell.ViewModels;

namespace CxShell.Tests;

public sealed class RdpReconnectStatusTests
{
    [Theory]
    [InlineData("RDP connection lost.", "ConnectionLost")]
    [InlineData("RDP reconnecting (attempt 1)", "Reconnecting")]
    [InlineData("RDP reconnecting (attempt 137)", "Reconnecting")]
    [InlineData("RDP reconnected.", "Reconnected")]
    [InlineData("RDP reconnect failed.", "Failed")]
    public void Classify_MapsBridgeStatusLines(string message, string expected)
    {
        Assert.Equal(expected, RdpReconnectStatus.Classify(message).ToString());
    }

    [Fact]
    public void Classify_DoesNotConfuseReconnectPhases()
    {
        Assert.Equal("Reconnecting", RdpReconnectStatus.Classify("RDP reconnecting (attempt 2)").ToString());
        Assert.Equal("Reconnected", RdpReconnectStatus.Classify("RDP reconnected.").ToString());
        Assert.Equal("Failed", RdpReconnectStatus.Classify("RDP reconnect failed.").ToString());
        Assert.Equal("ConnectionLost", RdpReconnectStatus.Classify("RDP connection lost.").ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("RDP connected.")]
    [InlineData("Starting RDP bridge...")]
    [InlineData("RDP clipboard channel ready")]
    public void Classify_IgnoresUnrelatedMessages(string? message)
    {
        Assert.Equal("None", RdpReconnectStatus.Classify(message).ToString());
    }

    [Theory]
    [InlineData("FreeRDP connection failed. code=0x0002000d name=ERRCONNECT_CONNECT_TRANSPORT_FAILED mode=nla")]
    [InlineData("FreeRDP connection failed. mode=tls")]
    [InlineData("RDP display update failed: 1920x1080 @150%.")]
    [InlineData("RDP clipboard channel load failed. code=3 continuing without clipboard.")]
    [InlineData("RDP clipboard channel connect failed; retrying without clipboard.")]
    [InlineData("RDP clipboard channel closed the session early; retrying without clipboard.")]
    public void IsTransientFailure_KeepsBridgeRecoveryFromLookingLikeADisconnect(string message)
    {
        Assert.True(RdpReconnectStatus.IsTransientFailure(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RDP connected.")]
    [InlineData("RDP disconnected.")]
    [InlineData("RDP reconnect failed.")]
    [InlineData("RDP connection lost.")]
    [InlineData("FreeRDP instance initialization failed.")]
    public void IsTransientFailure_LeavesTerminalStatesAlone(string? message)
    {
        Assert.False(RdpReconnectStatus.IsTransientFailure(message));
    }

    [Theory]
    [InlineData("RDP reconnecting (attempt 1)", 1)]
    [InlineData("RDP reconnecting (attempt 27)", 27)]
    [InlineData("RDP reconnecting (attempt 1000)", 1000)]
    public void ParseAttempt_ReadsCounterFromBridgeStatus(string message, int expected)
    {
        Assert.Equal(expected, RdpReconnectStatus.ParseAttempt(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RDP connection lost.")]
    [InlineData("RDP reconnected.")]
    [InlineData("RDP reconnecting")]
    [InlineData("RDP reconnecting (attempt )")]
    public void ParseAttempt_ReturnsZeroForOtherMessages(string? message)
    {
        Assert.Equal(0, RdpReconnectStatus.ParseAttempt(message));
    }

    [Fact]
    public void ReconnectOverlayStrings_AreLocalized()
    {
        var localization = LocalizationService.Shared;
        var original = localization.Language;
        try
        {
            localization.SetLanguage(LocalizationService.English);
            Assert.Equal("Reconnect", localization.Text("Rdp.Reconnect.Button"));
            Assert.Equal("RDP connection lost.", localization.Text("Rdp.Reconnect.Failed"));
            Assert.Equal(
                "RDP reconnecting (attempt 7)",
                string.Format(localization.Text("Rdp.Reconnect.Attempt"), 7));

            localization.SetLanguage(LocalizationService.Chinese);
            Assert.Equal("重新连接", localization.Text("Rdp.Reconnect.Button"));
            Assert.Equal("RDP 连接已断开。", localization.Text("Rdp.Reconnect.Failed"));
            Assert.Equal(
                "RDP 正在重连（第 7 次）",
                string.Format(localization.Text("Rdp.Reconnect.Attempt"), 7));
        }
        finally
        {
            localization.SetLanguage(original);
        }
    }
}
