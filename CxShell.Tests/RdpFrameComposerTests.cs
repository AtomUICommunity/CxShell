using System;
using CxShell.Models;
using CxShell.Services;
using Xunit;

namespace CxShell.Tests;

public class RdpFrameComposerTests
{
    private static readonly DateTime Start = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Short waits for steady-state tests; production defaults are longer.</summary>
    private static RdpFrameComposer Fast() => new(
        firstFrameTimeout: TimeSpan.FromMilliseconds(3000),
        minFirstFrameCollect: TimeSpan.Zero,
        firstFrameBurstQuiet: TimeSpan.FromMilliseconds(100),
        burstQuiet: TimeSpan.FromMilliseconds(220),
        maxLatency: TimeSpan.FromMilliseconds(3000));

    /// <summary>Long hold values, so the tests do not depend on tuned defaults.</summary>
    private static RdpFrameComposer Hold() => new(
        firstFrameTimeout: TimeSpan.FromMilliseconds(6000),
        minFirstFrameCollect: TimeSpan.FromMilliseconds(1800),
        firstFrameBurstQuiet: TimeSpan.FromMilliseconds(550));

    private static RdpFrameDirtyRect Strip(int index) =>
        new(0, (index % 40) * 27, 1920, 27);

    [Fact]
    public void HoldsFirstFrameUntilServerGoesQuiet()
    {
        var composer = new RdpFrameComposer();
        composer.NoteFrame(Start);
        composer.NoteFrame(Start.AddMilliseconds(50));

        // Even without dirty rects the first frame still waits out the whole
        // repaint wave - a short idle fallback here would show a half desktop.
        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(100)));
        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(699)));
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(750)));
        Assert.Equal(2, composer.MarkPresented(Start.AddMilliseconds(750)));
        Assert.True(composer.HasPresentedFirstFrame);
    }

    [Fact]
    public void FirstFrameTimesOutWhenServerNeverGoesQuiet()
    {
        var composer = new RdpFrameComposer(firstFrameTimeout: TimeSpan.FromMilliseconds(3000));
        var batches = 0;
        for (var ms = 0; ms <= 2900; ms += 50)
        {
            composer.NoteFrame(Start.AddMilliseconds(ms));
            batches++;
        }

        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(2900)));
        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(2950)));
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(3000)));
        Assert.Equal(batches, composer.MarkPresented(Start.AddMilliseconds(3000)));
    }

    [Fact]
    public void FirstFrameWaitsForTheWholeInitialRepaintWave()
    {
        // The real server sends ~50 strips over ~6 seconds, roughly top to
        // bottom. Not one of those intermediate buffers may reach the UI.
        var composer = new RdpFrameComposer();
        var batches = 0;
        for (var ms = 0; ms <= 4000; ms += 100)
        {
            composer.NoteFrame(Start.AddMilliseconds(ms), Strip(batches), 1920, 1080);
            batches++;
            Assert.False(composer.ShouldPresent(Start.AddMilliseconds(ms + 50)));
        }

        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(4600)));
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(4701)));
        Assert.Equal(batches, composer.MarkPresented(Start.AddMilliseconds(4701)));
    }

    [Fact]
    public void FirstFrameHoldKeepsEveryBatchEvenWhenCoverageIsComplete()
    {
        var composer = Hold();
        composer.NoteFrame(Start, new RdpFrameDirtyRect(0, 0, 1920, 540), 1920, 1080);
        Assert.InRange(composer.Coverage, 0.4, 0.7);
        Assert.True(composer.HasDirtyRect);

        composer.NoteFrame(Start.AddMilliseconds(400), new RdpFrameDirtyRect(0, 540, 1920, 540), 1920, 1080);
        Assert.True(composer.Coverage >= 0.95);
        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(401)));
        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(1799)));

        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(1800)));
        Assert.Equal(2, composer.MarkPresented(Start.AddMilliseconds(1800)));
    }

    [Fact]
    public void FirstFrameHoldWaitsForTheWaveToEnd()
    {
        var composer = Hold();
        composer.NoteFrame(Start, new RdpFrameDirtyRect(0, 0, 1920, 1080), 1920, 1080);
        composer.NoteFrame(Start.AddMilliseconds(1700), new RdpFrameDirtyRect(0, 0, 400, 300), 1920, 1080);

        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(1800)));
        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(2249)));
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(2251)));
    }

    [Fact]
    public void FullScreenFirstBatchIsStillHeldDuringCollectionPeriod()
    {
        var composer = Hold();
        composer.NoteFrame(Start, new RdpFrameDirtyRect(0, 0, 1920, 1080), 1920, 1080);

        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(1)));
        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(1799)));
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(1800)));
        Assert.Equal(1, composer.MarkPresented(Start.AddMilliseconds(1800)));
    }

    [Fact]
    public void FirstFrameHoldIsCappedByHardTimeout()
    {
        var composer = Hold();
        var batches = 0;
        for (var ms = 0; ms <= 5900; ms += 100)
        {
            composer.NoteFrame(Start.AddMilliseconds(ms), new RdpFrameDirtyRect(0, 0, 100, 100), 1920, 1080);
            batches++;
        }

        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(5999)));
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(6000)));
        Assert.Equal(batches, composer.MarkPresented(Start.AddMilliseconds(6000)));
    }

    [Fact]
    public void CoverageDisabledFallsBackToIdleTiming()
    {
        var composer = new RdpFrameComposer(useCoverage: false);
        composer.NoteFrame(Start, new RdpFrameDirtyRect(0, 0, 100, 100), 1920, 1080);

        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(299)));
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(300)));
    }

    [Fact]
    public void SmallUpdatesStillCoalesceWithoutPeriodicPublishing()
    {
        var composer = new RdpFrameComposer();
        composer.NoteFrame(Start);
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(700)));
        composer.MarkPresented(Start.AddMilliseconds(700));

        composer.NoteFrame(Start.AddMilliseconds(710));
        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(720)));
        composer.NoteFrame(Start.AddMilliseconds(725));
        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(750)));
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(771)));
        Assert.Equal(2, composer.MarkPresented(Start.AddMilliseconds(771)));

        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(900)));
    }

    [Fact]
    public void SmallInteractiveUpdatesStayResponsive()
    {
        var composer = Fast();
        composer.NoteFrame(Start, new RdpFrameDirtyRect(0, 0, 1920, 1080), 1920, 1080);
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(101)));
        composer.MarkPresented(Start.AddMilliseconds(101));

        composer.NoteFrame(Start.AddMilliseconds(110), new RdpFrameDirtyRect(10, 10, 20, 20), 1920, 1080);
        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(140)));
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(156)));
    }

    [Fact]
    public void LargeRepaintWavesUseLongerDebounce()
    {
        var composer = Fast();
        composer.NoteFrame(Start, new RdpFrameDirtyRect(0, 0, 1920, 1080), 1920, 1080);
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(101)));
        composer.MarkPresented(Start.AddMilliseconds(101));

        composer.NoteFrame(Start.AddMilliseconds(110), new RdpFrameDirtyRect(0, 0, 1920, 400), 1920, 1080);
        Assert.True(composer.InRepaintWave);
        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(200)));
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(331)));
    }

    [Fact]
    public void ContinuousRepaintWaveIsNeverSlicedIntoVisibleFrames()
    {
        // Regression for the reported "screen fills in from the top": MaxLatency
        // used to force a publish every 600 ms while the server was still
        // painting, so ~50 batches became ~8 visible half-finished screens.
        var composer = new RdpFrameComposer();
        composer.NoteFrame(Start, new RdpFrameDirtyRect(0, 0, 1920, 1080), 1920, 1080);
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(701)));
        composer.MarkPresented(Start.AddMilliseconds(701));

        var batches = 0;
        for (var ms = 750; ms <= 4750; ms += 100)
        {
            var now = Start.AddMilliseconds(ms);
            composer.NoteFrame(now, Strip(batches), 1920, 1080);
            batches++;
            Assert.False(composer.ShouldPresent(now));
            Assert.False(composer.ShouldPresent(now.AddMilliseconds(60)));
        }

        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(5400)));
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(5451)));
        Assert.Equal(batches, composer.MarkPresented(Start.AddMilliseconds(5451)));
    }

    [Fact]
    public void HardCapPublishesWhenAWaveNeverStops()
    {
        var composer = new RdpFrameComposer(
            firstFrameTimeout: TimeSpan.FromMilliseconds(3000),
            firstFrameBurstQuiet: TimeSpan.FromMilliseconds(100),
            burstQuiet: TimeSpan.FromMilliseconds(500),
            maxLatency: TimeSpan.FromMilliseconds(1000));

        composer.NoteFrame(Start, new RdpFrameDirtyRect(0, 0, 1920, 1080), 1920, 1080);
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(101)));
        composer.MarkPresented(Start.AddMilliseconds(101));

        for (var ms = 150; ms <= 1400; ms += 50)
            composer.NoteFrame(Start.AddMilliseconds(ms), new RdpFrameDirtyRect(0, 0, 300, 200), 1920, 1080);

        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(1099)));
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(1450)));
    }

    [Fact]
    public void NoPendingFrameNeverPresents()
    {
        var composer = new RdpFrameComposer();
        Assert.False(composer.ShouldPresent(Start.AddSeconds(10)));
        composer.NoteFrame(Start);
        composer.MarkPresented(Start);
        Assert.False(composer.ShouldPresent(Start.AddSeconds(10)));
    }

    [Fact]
    public void ResetStartsAFreshFirstFrameWait()
    {
        var composer = new RdpFrameComposer();
        composer.NoteFrame(Start);
        composer.MarkPresented(Start.AddMilliseconds(300));

        composer.Reset();
        Assert.False(composer.HasPresentedFirstFrame);
        composer.NoteFrame(Start.AddSeconds(5));
        Assert.False(composer.ShouldPresent(Start.AddSeconds(5).AddMilliseconds(10)));
        Assert.True(composer.ShouldPresent(Start.AddSeconds(5).AddMilliseconds(700)));
    }

    [Fact]
    public void ResetClearsCoverage()
    {
        var composer = new RdpFrameComposer();
        composer.NoteFrame(Start, new RdpFrameDirtyRect(0, 0, 1920, 540), 1920, 1080);
        Assert.True(composer.Coverage > 0);

        composer.Reset();
        Assert.Equal(0, composer.Coverage);
        Assert.False(composer.HasDirtyRect);
    }

    [Fact]
    public void DisabledComposerPresentsEveryBatch()
    {
        var composer = new RdpFrameComposer(enabled: false);
        composer.NoteFrame(Start);
        Assert.True(composer.ShouldPresent(Start));
        Assert.Equal(1, composer.MarkPresented(Start));

        composer.NoteFrame(Start.AddMilliseconds(1));
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(1)));
    }

    [Fact]
    public void BoundaryModePresentsImmediately()
    {
        var composer = new RdpFrameComposer();
        composer.Configure(timeCoalescingEnabled: true, boundaryMode: true);
        composer.NoteFrame(Start);

        Assert.True(composer.BoundaryMode);
        Assert.True(composer.ShouldPresent(Start));

        composer.MarkPresented(Start);
        composer.NoteFrame(Start.AddMilliseconds(1));
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(1)));

        composer.SetBoundaryMode(false);
        composer.NoteFrame(Start.AddMilliseconds(2));
        Assert.False(composer.ShouldPresent(Start.AddMilliseconds(2)));
    }

    [Fact]
    public void ResetClearsBoundaryMode()
    {
        var composer = new RdpFrameComposer();
        composer.Configure(true, true);
        composer.Reset();
        Assert.False(composer.BoundaryMode);
        composer.NoteFrame(Start);
        Assert.False(composer.ShouldPresent(Start));
    }

    [Fact]
    public void ResizeRestartsCoverageAccumulation()
    {
        var composer = new RdpFrameComposer();
        composer.NoteFrame(Start, new RdpFrameDirtyRect(0, 0, 1920, 1080), 1920, 1080);
        Assert.True(composer.Coverage >= 0.95);

        composer.NoteFrame(Start.AddMilliseconds(1), new RdpFrameDirtyRect(0, 0, 100, 100), 1280, 720);
        Assert.True(composer.Coverage < 0.2);
    }

    [Fact]
    public void ResolveFramePresentationModeHandlesUnknownValues()
    {
        Assert.Equal(RdpFramePresentationMode.Auto, RdpFramePresentationModes.Resolve(null));
        Assert.Equal(RdpFramePresentationMode.Auto, RdpFramePresentationModes.Resolve("garbage"));
        Assert.Equal(RdpFramePresentationMode.Coalesce, RdpFramePresentationModes.Resolve("coalesce"));
        Assert.Equal(RdpFramePresentationMode.FrameMarker, RdpFramePresentationModes.Resolve("FrameMarker"));
        Assert.Equal(RdpFramePresentationMode.Legacy, RdpFramePresentationModes.Resolve("legacy"));
        Assert.Equal("Auto", RdpFramePresentationModes.Normalize("garbage"));
        Assert.True(RdpFramePresentationModes.UsesFrameMarkers(RdpFramePresentationMode.Auto));
        Assert.True(RdpFramePresentationModes.UsesFrameMarkers(RdpFramePresentationMode.FrameMarker));
        Assert.False(RdpFramePresentationModes.UsesFrameMarkers(RdpFramePresentationMode.Coalesce));
    }

    [Fact]
    public void LocalInputPublishesImmediatelyDuringRepaintWave()
    {
        // Regression: pointer motion produces full-width diff rectangles, so
        // the wave debounce held every mouse-driven batch for 700 ms (and hit
        // the 8 s hard cap on a continuous stream). The mouse felt frozen.
        var composer = new RdpFrameComposer();
        composer.NoteFrame(Start, new RdpFrameDirtyRect(0, 0, 1920, 1080), 1920, 1080);
        Assert.True(composer.ShouldPresent(Start.AddMilliseconds(701)));
        composer.MarkPresented(Start.AddMilliseconds(701));

        var t = Start.AddMilliseconds(800);
        composer.NoteFrame(t, Strip(0), 1920, 1080);
        composer.NoteFrame(t.AddMilliseconds(10), Strip(1), 1920, 1080);
        Assert.True(composer.InRepaintWave);
        Assert.False(composer.ShouldPresent(t.AddMilliseconds(20)));

        composer.NoteInteraction(t.AddMilliseconds(20));
        Assert.True(composer.IsInteractive(t.AddMilliseconds(30)));
        Assert.True(composer.ShouldPresent(t.AddMilliseconds(30)));
        Assert.Equal(2, composer.MarkPresented(t.AddMilliseconds(30)));

        composer.NoteFrame(t.AddMilliseconds(40), Strip(2), 1920, 1080);
        Assert.True(composer.ShouldPresent(t.AddMilliseconds(41)));
    }

    [Fact]
    public void WaveHoldResumesAfterInteractionGoesIdle()
    {
        var composer = new RdpFrameComposer();
        composer.NoteFrame(Start, new RdpFrameDirtyRect(0, 0, 1920, 1080), 1920, 1080);
        composer.MarkPresented(Start.AddMilliseconds(701));

        var t = Start.AddMilliseconds(800);
        composer.NoteInteraction(t);
        composer.NoteFrame(t, Strip(0), 1920, 1080);
        Assert.True(composer.ShouldPresent(t));
        composer.MarkPresented(t);

        var idle = t.AddMilliseconds(3100);
        composer.NoteFrame(idle, Strip(1), 1920, 1080);
        Assert.False(composer.IsInteractive(idle));
        Assert.True(composer.InSettlingWindow(idle));
        Assert.False(composer.ShouldPresent(idle.AddMilliseconds(10)));
        Assert.False(composer.ShouldPresent(idle.AddMilliseconds(699)));
        Assert.True(composer.ShouldPresent(idle.AddMilliseconds(701)));
    }

    [Fact]
    public void SettledSessionPublishesContinuousWavesPromptly()
    {
        var composer = new RdpFrameComposer(settlingWindow: TimeSpan.FromSeconds(2));
        composer.NoteFrame(Start, new RdpFrameDirtyRect(0, 0, 1920, 1080), 1920, 1080);
        composer.MarkPresented(Start.AddMilliseconds(701));

        var t = Start.AddSeconds(3);
        composer.NoteFrame(t, Strip(0), 1920, 1080);
        Assert.False(composer.InSettlingWindow(t));
        Assert.True(composer.InRepaintWave);
        Assert.True(composer.ShouldPresent(t));
        composer.MarkPresented(t);

        composer.NoteFrame(t.AddMilliseconds(16), Strip(1), 1920, 1080);
        Assert.True(composer.ShouldPresent(t.AddMilliseconds(16)));
    }

    [Fact]
    public void DirtyRectDiffIsSkippedOutsideWaveClassification()
    {
        var composer = new RdpFrameComposer(settlingWindow: TimeSpan.FromSeconds(2));
        Assert.True(composer.NeedsDirtyRect(Start));

        composer.NoteFrame(Start, new RdpFrameDirtyRect(0, 0, 1920, 1080), 1920, 1080);
        composer.MarkPresented(Start.AddMilliseconds(701));
        Assert.True(composer.NeedsDirtyRect(Start.AddMilliseconds(800)));

        var interactiveAt = Start.AddMilliseconds(900);
        composer.NoteInteraction(interactiveAt);
        Assert.False(composer.NeedsDirtyRect(interactiveAt));
        Assert.False(composer.NeedsDirtyRect(Start.AddSeconds(3)));

        composer.Reset();
        Assert.True(composer.NeedsDirtyRect(Start.AddSeconds(3)));
        Assert.False(composer.IsInteractive(Start.AddSeconds(3)));
    }

    [Fact]
    public void BoundaryModeSkipsWaveClassification()
    {
        var composer = new RdpFrameComposer();
        composer.Configure(timeCoalescingEnabled: true, boundaryMode: true);
        Assert.False(composer.NeedsDirtyRect(Start));
    }

    [Fact]
    public void FrameEventArgsReleasesPooledBufferOnlyOnce()
    {
        var released = 0;
        var pixels = new byte[32];
        var args = new RdpFramebufferEventArgs(4, 2, 16, pixels, 24, () => released++);

        Assert.Equal(24, args.PixelLength);
        Assert.Same(pixels, args.Pixels);
        args.ReleasePixels();
        args.ReleasePixels();
        Assert.Equal(1, released);
    }

    [Fact]
    public void FrameEventArgsDefaultsPixelLengthToArrayLength()
    {
        var args = new RdpFramebufferEventArgs(4, 2, 16, new byte[32]);
        Assert.Equal(32, args.PixelLength);
        args.ReleasePixels();
    }
}
