using System.Text.Json;
using CxShell.Models;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class SessionInfoCompatibilityTests
{
    [Fact]
    public void NewSession_UsesConservativePanelAndRemoteTitleDefaults()
    {
        var session = new SessionInfo();

        Assert.False(session.SshAutoOpenSftpPanel);
        Assert.False(session.SshAutoOpenMonitorPanel);
        Assert.True(session.SshEnableServerMonitoring);
        Assert.False(session.SshEnableMonitorNetworkLatency);
        Assert.False(session.TerminalAdvancedAllowTitleChange);
        Assert.False(session.TerminalAdvancedAllowOsc52Clipboard);
        Assert.False(session.RdpMicrophoneEnabled);
        Assert.Equal("FollowLocal", session.RdpDpiPolicy);
        Assert.True(session.RdpClipboardEnabled);
        Assert.Equal("Auto", session.RdpFrameStrategy);
        Assert.True(session.RdpDisableWallpaper);
        Assert.True(session.RdpGraphicsPipelineEnabled);
        Assert.Equal(SessionTabIconCatalog.Default, session.AppearanceTabIcon);
    }

    [Fact]
    public void RdpFrameStrategy_RoundTripsAndDefaultsForLegacySessions()
    {
        var legacy = JsonSerializer.Deserialize<SessionInfo>("""{"Host":"example"}""");
        Assert.NotNull(legacy);
        Assert.Equal("Auto", legacy.RdpFrameStrategy);
        Assert.Equal(RdpFramePresentationMode.Auto, RdpFramePresentationModes.Resolve(legacy.RdpFrameStrategy));

        var json = JsonSerializer.Serialize(new SessionInfo { RdpFrameStrategy = "FrameMarker" });
        var restored = JsonSerializer.Deserialize<SessionInfo>(json);
        Assert.NotNull(restored);
        Assert.Equal(RdpFramePresentationMode.FrameMarker, RdpFramePresentationModes.Resolve(restored.RdpFrameStrategy));
        Assert.True(RdpFramePresentationModes.UsesFrameMarkers(RdpFramePresentationModes.Resolve(restored.RdpFrameStrategy)));
    }

    [Fact]
    public void RdpClipboardEnabled_RoundTripsAndStaysOnForLegacySessions()
    {
        var legacy = JsonSerializer.Deserialize<SessionInfo>("""{"Host":"example"}""");
        Assert.NotNull(legacy);
        Assert.True(legacy.RdpClipboardEnabled);

        var json = JsonSerializer.Serialize(new SessionInfo { RdpClipboardEnabled = false });
        var restored = JsonSerializer.Deserialize<SessionInfo>(json);
        Assert.NotNull(restored);
        Assert.False(restored.RdpClipboardEnabled);
    }

    [Fact]
    public void RdpDisableWallpaper_DefaultsToEnabledAndRoundTrips()
    {
        // Older session files have no wallpaper setting; the performance flag
        // must stay on for them instead of silently restoring the wallpaper.
        var legacy = JsonSerializer.Deserialize<SessionInfo>("""{"Host":"example"}""");
        Assert.NotNull(legacy);
        Assert.True(legacy.RdpDisableWallpaper);

        var json = JsonSerializer.Serialize(new SessionInfo { RdpDisableWallpaper = false });
        var restored = JsonSerializer.Deserialize<SessionInfo>(json);
        Assert.NotNull(restored);
        Assert.False(restored.RdpDisableWallpaper);
    }

    [Fact]
    public void RdpGraphicsPipelineEnabled_IsOnByDefaultAndRoundTrips()
    {
        // EGFX is the default now that the graphics DVC is registered by its
        // addin name. Session files that never stored the key adopt it, and a
        // server that rejects the pipeline falls back without dropping the tab.
        var legacy = JsonSerializer.Deserialize<SessionInfo>("""{"Host":"example"}""");
        Assert.NotNull(legacy);
        Assert.True(legacy.RdpGraphicsPipelineEnabled);

        var json = JsonSerializer.Serialize(new SessionInfo { RdpGraphicsPipelineEnabled = false });
        var restored = JsonSerializer.Deserialize<SessionInfo>(json);
        Assert.NotNull(restored);
        Assert.False(restored.RdpGraphicsPipelineEnabled);
    }

    [Fact]
    public void RdpDpiPolicy_RoundTripsAndDefaultsForLegacySessions()
    {
        var legacy = JsonSerializer.Deserialize<SessionInfo>("""{"Host":"example"}""");
        Assert.NotNull(legacy);
        Assert.Equal("FollowLocal", legacy.RdpDpiPolicy);

        var json = JsonSerializer.Serialize(new SessionInfo { RdpDpiPolicy = "KeepRemote" });
        var restored = JsonSerializer.Deserialize<SessionInfo>(json);
        Assert.NotNull(restored);
        Assert.Equal("KeepRemote", restored.RdpDpiPolicy);
    }

    [Fact]
    public void SessionTabIconCatalog_NormalizesKnownAndUnknownValues()
    {
        Assert.Equal(SessionTabIconCatalog.Server, SessionTabIconCatalog.Normalize("server"));
        Assert.Equal(SessionTabIconCatalog.Default, SessionTabIconCatalog.Normalize("not-a-tab-icon"));
        Assert.Equal(SessionTabIconCatalog.Default, SessionTabIconCatalog.Normalize(null));
    }

    [Fact]
    public void SessionInfo_DeserializesTabIconSetting()
    {
        var session = JsonSerializer.Deserialize<SessionInfo>(
            """{"AppearanceTabIcon":"Database"}""");

        Assert.NotNull(session);
        Assert.Equal(SessionTabIconCatalog.Database, session.AppearanceTabIcon);
    }

    [Fact]
    public void ApplicationSettings_UsesTheDefaultSftpPanelWidth()
    {
        var settings = new ApplicationSettings();

        Assert.Equal(318, settings.SftpPanelWidth);
        Assert.False(settings.ShowTabBar);
        Assert.True(settings.EnableCommandSuggestions);
        Assert.Equal(ApplicationSettings.DarkThemeMode, settings.ThemeMode);
        Assert.Equal(ApplicationSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public void ApplicationSettings_PreservesThemeMode()
    {
        var settings = JsonSerializer.Deserialize<ApplicationSettings>(
            """{"ThemeMode":"Light"}""");

        Assert.NotNull(settings);
        Assert.Equal(ApplicationSettings.LightThemeMode, settings.ThemeMode);
    }

    [Fact]
    public void SessionInfo_UsesAndClampsServerMonitorDefaults()
    {
        var session = new SessionInfo
        {
            SshMonitorRefreshIntervalSeconds = 0
        };

        Assert.True(session.SshEnableServerMonitoring);
        Assert.Equal(SessionInfo.MinSshMonitorRefreshIntervalSeconds,
            session.SshMonitorRefreshIntervalSeconds);

        session.SshMonitorRefreshIntervalSeconds = 100;

        Assert.Equal(SessionInfo.MaxSshMonitorRefreshIntervalSeconds,
            session.SshMonitorRefreshIntervalSeconds);
    }

    [Fact]
    public void SessionInfo_DeserializesMonitorSettings()
    {
        var session = JsonSerializer.Deserialize<SessionInfo>(
            """{"SshEnableServerMonitoring":false,"SshEnableMonitorNetworkLatency":true,"SshMonitorRefreshIntervalSeconds":10}""");

        Assert.NotNull(session);
        Assert.False(session.SshEnableServerMonitoring);
        Assert.True(session.SshEnableMonitorNetworkLatency);
        Assert.Equal(10, session.SshMonitorRefreshIntervalSeconds);
    }

    [Fact]
    public void ApplicationSettings_PreservesTabBarVisibility()
    {
        var settings = JsonSerializer.Deserialize<ApplicationSettings>(
            """{"ShowTabBar":true}""");

        Assert.NotNull(settings);
        Assert.True(settings.ShowTabBar);
    }

    [Fact]
    public void ApplicationSettings_PreservesCommandSuggestionVisibility()
    {
        var settings = JsonSerializer.Deserialize<ApplicationSettings>(
            """{"EnableCommandSuggestions":false}""");

        Assert.NotNull(settings);
        Assert.False(settings.EnableCommandSuggestions);
    }

    [Fact]
    public void LegacyDisableTitleSetting_DoesNotOptIntoRemoteTabTitles()
    {
        var session = JsonSerializer.Deserialize<SessionInfo>(
            """{"TerminalAdvancedDisableTitleChange":false}""");

        Assert.NotNull(session);
        Assert.False(session.TerminalAdvancedAllowTitleChange);
    }

    [Fact]
    public void ExplicitAllowTitleSetting_RoundTrips()
    {
        var session = JsonSerializer.Deserialize<SessionInfo>(
            """{"TerminalAdvancedAllowTitleChange":true}""");

        Assert.NotNull(session);
        Assert.True(session.TerminalAdvancedAllowTitleChange);
    }

    [Fact]
    public void LegacyAudioCaptureSetting_DoesNotEnableMicrophoneRedirection()
    {
        var session = JsonSerializer.Deserialize<SessionInfo>(
            """{"RdpAudioCapture":true}""");

        Assert.NotNull(session);
        Assert.True(session.RdpAudioCapture);
        Assert.False(session.RdpMicrophoneEnabled);
    }
}
