using System;

namespace CxShell.Services;

/// <summary>Dirty rectangle of one GDI paint batch, in desktop coordinates.</summary>
public readonly record struct RdpFrameDirtyRect(int X, int Y, int Width, int Height);

/// <summary>
/// Decides when a pending RDP framebuffer snapshot should be handed to the UI.
///
/// Legacy RDP bitmap updates have no frame boundary at all. The server sends a
/// full-screen background first and then keeps overwriting strips of it, often
/// for several seconds, roughly top to bottom. Any policy that publishes on a
/// fixed interval (or on a short idle gap) shows those intermediate snapshots,
/// which is exactly the visible "screen fills in from the top" artefact.
///
/// Three phases, so the fix for the first screen does not ruin the live session:
/// 1. First frame + settling window (no local input): pure debounce. While a
///    repaint wave is arriving publish NOTHING, so the desktop appears once,
///    complete, and the tail of the initial redraw is not sliced up either.
/// 2. Local input (pointer / keyboard): publish on the very next UI tick. A
///    click or a mouse move must never sit behind a 700 ms wave debounce -
///    the diff-based dirty rectangle often spans the whole desktop, so a
///    continuous stream of pointer updates looks exactly like a repaint wave.
/// 3. Settled session (after the window, no input): publish every fresh
///    framebuffer. Background animations, clock ticks and remote video then
///    show at normal frame rate instead of once per MaxLatency (8 seconds).
/// </summary>
public sealed class RdpFrameComposer
{
    // The desktop is split into a coarse grid; a batch "covers" every cell its
    // dirty rectangle intersects. Coverage is a *wave classifier* and a
    // diagnostic, never a frame-boundary signal.
    private const int GridColumns = 16;
    private const int GridRows = 9;
    // A single top-to-bottom strip is only ~2.5% of the screen, so classifying
    // waves by "one big batch" (25%) or by accumulated coverage alone lets the
    // early strips through as if they were typing. Any batch at least this
    // large is part of a repaint wave, not an interactive change.
    private const double LargeBatchThreshold = 0.01;

    /// <summary>Accumulated dirty area above which a run of batches is a repaint wave.</summary>
    public const double WaveCoverageThreshold = 0.06;

    public static readonly TimeSpan DefaultFirstFrameQuiet = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan DefaultFirstFrameTimeout = TimeSpan.FromMilliseconds(8000);
    public static readonly TimeSpan DefaultMinFirstFrameCollect = TimeSpan.Zero;
    public static readonly TimeSpan DefaultFirstFrameBurstQuiet = TimeSpan.FromMilliseconds(700);
    public static readonly TimeSpan DefaultSteadyQuiet = TimeSpan.FromMilliseconds(45);
    public static readonly TimeSpan DefaultBurstQuiet = TimeSpan.FromMilliseconds(700);
    public static readonly TimeSpan DefaultMaxLatency = TimeSpan.FromMilliseconds(8000);
    /// <summary>How long the initial repaint-wave hold stays active after the desktop first appears.</summary>
    public static readonly TimeSpan DefaultSettlingWindow = TimeSpan.FromSeconds(10);
    /// <summary>How long after the last local input the session stays in low-latency mode.</summary>
    public static readonly TimeSpan DefaultInteractiveIdle = TimeSpan.FromSeconds(3);

    private readonly bool[] _covered = new bool[GridColumns * GridRows];
    private DateTime _firstFrameAt;
    private DateTime _lastFrameAt;
    private DateTime _lastPresentedAt;
    private DateTime _settlingUntil;
    private DateTime _lastInteractionAt;
    private bool _hasFrame;
    private bool _hasPending;
    private bool _presentedOnce;
    private bool _boundaryMode;
    private bool _hasDirtyRect;
    private bool _largeBatchSeen;
    private bool _hasInteraction;
    private int _pendingFrames;
    private int _coveredCells;
    private int _consecutiveRapidBatches;
    private int _frameWidth;
    private int _frameHeight;

    public RdpFrameComposer(
        bool enabled = true,
        TimeSpan? firstFrameQuiet = null,
        TimeSpan? firstFrameTimeout = null,
        TimeSpan? minInterval = null,
        bool useCoverage = true,
        TimeSpan? minFirstFrameCollect = null,
        TimeSpan? firstFrameBurstQuiet = null,
        TimeSpan? burstQuiet = null,
        TimeSpan? maxLatency = null,
        TimeSpan? settlingWindow = null,
        TimeSpan? interactiveIdle = null)
    {
        Enabled = enabled;
        FirstFrameQuiet = firstFrameQuiet ?? DefaultFirstFrameQuiet;
        FirstFrameTimeout = firstFrameTimeout ?? DefaultFirstFrameTimeout;
        MinimumFirstFrameCollect = minFirstFrameCollect ?? DefaultMinFirstFrameCollect;
        FirstFrameBurstQuiet = firstFrameBurstQuiet ?? DefaultFirstFrameBurstQuiet;
        SteadyQuiet = minInterval ?? DefaultSteadyQuiet;
        BurstQuiet = burstQuiet ?? DefaultBurstQuiet;
        MaxLatency = maxLatency ?? DefaultMaxLatency;
        SettlingWindow = settlingWindow ?? DefaultSettlingWindow;
        InteractiveIdle = interactiveIdle ?? DefaultInteractiveIdle;
        UseCoverage = useCoverage;
    }

    public bool Enabled { get; private set; }

    /// <summary>
    /// When false the composer falls back to pure idle-time decisions. Useful
    /// for A/B comparisons against the repaint-wave hold.
    /// </summary>
    public bool UseCoverage { get; }

    public TimeSpan FirstFrameQuiet { get; }
    public TimeSpan FirstFrameTimeout { get; }
    public TimeSpan MinimumFirstFrameCollect { get; }
    public TimeSpan FirstFrameBurstQuiet { get; }
    public TimeSpan SteadyQuiet { get; }
    public TimeSpan BurstQuiet { get; }
    public TimeSpan MaxLatency { get; }
    public TimeSpan SettlingWindow { get; }
    public TimeSpan InteractiveIdle { get; }

    /// <summary>Kept for callers that still read the old minimum-interval name.</summary>
    public TimeSpan MinInterval => SteadyQuiet;

    /// <summary>
    /// True when the native bridge already assembled a complete frame, so the
    /// snapshot can go to the UI immediately without any wait.
    /// </summary>
    public bool BoundaryMode => _boundaryMode;

    /// <summary>Fraction of the desktop painted since the last presented frame.</summary>
    public double Coverage => _coveredCells / (double)_covered.Length;

    /// <summary>
    /// True while the pending batches look like a full-screen repaint wave.
    /// When the bridge cannot report dirty rectangles, a run of rapidly
    /// arriving batches is the only remaining signal, so use it as a fallback.
    /// </summary>
    public bool InRepaintWave =>
        _largeBatchSeen ||
        Coverage >= WaveCoverageThreshold ||
        (!_hasDirtyRect && _consecutiveRapidBatches >= 4);

    /// <summary>Number of native updates folded into the pending snapshot.</summary>
    public int PendingFrames => _pendingFrames;

    public bool HasPresentedFirstFrame => _presentedOnce;

    /// <summary>True when at least one usable dirty rectangle arrived.</summary>
    public bool HasDirtyRect => _hasDirtyRect;

    /// <summary>How long the current first-frame collection has been running.</summary>
    public TimeSpan FirstFrameAge(DateTime now) => _hasFrame ? now - _firstFrameAt : TimeSpan.Zero;

    /// <summary>True while local pointer/keyboard input should bypass wave coalescing.</summary>
    public bool IsInteractive(DateTime now) =>
        _hasInteraction && now - _lastInteractionAt < InteractiveIdle;

    /// <summary>True while the tail of the initial desktop redraw may still be arriving.</summary>
    public bool InSettlingWindow(DateTime now) => _presentedOnce && now < _settlingUntil;

    /// <summary>
    /// Dirty-rectangle diffing is only needed while wave classification is in
    /// use. In interactive or settled mode the framebuffer is published as-is,
    /// so the per-batch full-surface diff can be skipped entirely.
    /// </summary>
    public bool NeedsDirtyRect(DateTime now) =>
        !_boundaryMode &&
        (!_presentedOnce || (!IsInteractive(now) && InSettlingWindow(now)));

    /// <summary>Records local input; called from the pointer/keyboard send path.</summary>
    public void NoteInteraction(DateTime now)
    {
        _hasInteraction = true;
        _lastInteractionAt = now;
    }

    /// <summary>Applies a presentation strategy (called once per connection).</summary>
    public void Configure(bool timeCoalescingEnabled, bool boundaryMode)
    {
        Enabled = timeCoalescingEnabled;
        _boundaryMode = boundaryMode;
    }

    /// <summary>
    /// Switches to frame-boundary presentation once the server proves it sends
    /// frame markers. Also used to fall back to time coalescing when it does not.
    /// </summary>
    public void SetBoundaryMode(bool enabled) => _boundaryMode = enabled;

    /// <summary>Records a fresh native frame. Called on the bridge callback thread.</summary>
    public void NoteFrame(DateTime now, RdpFrameDirtyRect? dirty = null, int frameWidth = 0, int frameHeight = 0)
    {
        if (!_hasFrame)
        {
            _hasFrame = true;
            _firstFrameAt = now;
            _consecutiveRapidBatches = 1;
        }
        else
        {
            _consecutiveRapidBatches = now - _lastFrameAt < BurstQuiet
                ? _consecutiveRapidBatches + 1
                : 1;
        }

        if (frameWidth > 0 && frameHeight > 0 &&
            (frameWidth != _frameWidth || frameHeight != _frameHeight))
        {
            _frameWidth = frameWidth;
            _frameHeight = frameHeight;
            ClearCoverage();
        }

        _lastFrameAt = now;
        _hasPending = true;
        _pendingFrames++;

        if (!dirty.HasValue || _frameWidth <= 0 || _frameHeight <= 0)
            return;

        var rect = dirty.Value;
        var left = Math.Clamp(rect.X, 0, _frameWidth);
        var top = Math.Clamp(rect.Y, 0, _frameHeight);
        var right = Math.Clamp(rect.X + rect.Width, 0, _frameWidth);
        var bottom = Math.Clamp(rect.Y + rect.Height, 0, _frameHeight);
        if (right <= left || bottom <= top)
            return;

        _hasDirtyRect = true;
        var columnStart = left * GridColumns / _frameWidth;
        var columnEnd = Math.Min(GridColumns, (right - 1) * GridColumns / _frameWidth + 1);
        var rowStart = top * GridRows / _frameHeight;
        var rowEnd = Math.Min(GridRows, (bottom - 1) * GridRows / _frameHeight + 1);
        for (var row = rowStart; row < rowEnd; row++)
        {
            for (var column = columnStart; column < columnEnd; column++)
            {
                var index = row * GridColumns + column;
                if (_covered[index])
                    continue;
                _covered[index] = true;
                _coveredCells++;
            }
        }

        var area = (double)(right - left) * (bottom - top) / (_frameWidth * (double)_frameHeight);
        if (area >= LargeBatchThreshold)
            _largeBatchSeen = true;
    }

    /// <summary>True when the pending snapshot is ready to be shown.</summary>
    public bool ShouldPresent(DateTime now)
    {
        if (!_hasPending)
            return false;

        // Explicit opt-out keeps the old "show every batch" behaviour available
        // for A/B comparisons without touching the native bridge. Boundary mode
        // means the native side already assembled a whole frame.
        if (!Enabled || _boundaryMode)
            return true;

        if (!_presentedOnce)
            return ShouldPresentFirstFrame(now);

        return ShouldPresentSteady(now);
    }

    private bool ShouldPresentFirstFrame(DateTime now)
    {
        var elapsed = now - _firstFrameAt;
        if (elapsed < MinimumFirstFrameCollect)
            return elapsed >= FirstFrameTimeout;

        // The initial desktop wave can last seconds. Only a real pause in the
        // stream means it is safe to show the screen - do not fall back to the
        // short idle window just because dirty rects were unavailable on this
        // particular connection.
        var quiet = UseCoverage ? FirstFrameBurstQuiet : FirstFrameQuiet;
        if (now - _lastFrameAt >= quiet)
            return true;

        // Hard cap so a server that streams forever cannot hide the desktop.
        return elapsed >= FirstFrameTimeout;
    }

    private bool ShouldPresentSteady(DateTime now)
    {
        // Local input is the strongest signal that the user is watching and
        // waiting. A diff-based dirty rectangle spans the whole desktop for
        // pointer motion (cursor + hover + caret), which the wave classifier
        // mistakes for a full-screen repaint; waiting 700 ms here made the
        // mouse feel frozen, and a never-ending stream hit the 8 s hard cap.
        if (IsInteractive(now))
            return true;

        // Once the desktop has settled, every fresh framebuffer is a real
        // frame. The 16 ms presentation timer caps this at ~60 fps.
        if (!InSettlingWindow(now))
            return true;

        var wave = InRepaintWave;
        var quiet = wave ? BurstQuiet : SteadyQuiet;
        if (now - _lastFrameAt >= quiet)
            return true;

        // Safety valve for a wave that genuinely never stops. It must be much
        // longer than a normal repaint wave, otherwise a long wave gets sliced
        // into several visible half-finished screens. It also requires a brief
        // pause so a burst of back-to-back batches is never interrupted.
        return wave &&
               now - _lastPresentedAt >= MaxLatency &&
               now - _lastFrameAt >= SteadyQuiet;
    }

    /// <summary>
    /// Marks the pending snapshot as consumed and returns how many native
    /// updates were folded into it.
    /// </summary>
    public int MarkPresented(DateTime now)
    {
        var merged = _pendingFrames;
        _hasPending = false;
        _pendingFrames = 0;
        _lastPresentedAt = now;
        if (!_presentedOnce)
            _settlingUntil = now + SettlingWindow;
        _presentedOnce = true;
        ClearCoverage();
        _hasDirtyRect = false;
        _largeBatchSeen = false;
        _consecutiveRapidBatches = 0;
        return merged;
    }

    /// <summary>Starts a fresh first-frame wait (new connection or reconnect).</summary>
    public void Reset()
    {
        _firstFrameAt = default;
        _lastFrameAt = default;
        _lastPresentedAt = default;
        _settlingUntil = default;
        _lastInteractionAt = default;
        _hasInteraction = false;
        _frameWidth = 0;
        _frameHeight = 0;
        _hasFrame = false;
        _hasPending = false;
        _presentedOnce = false;
        _boundaryMode = false;
        _pendingFrames = 0;
        _hasDirtyRect = false;
        _largeBatchSeen = false;
        _consecutiveRapidBatches = 0;
        ClearCoverage();
    }

    private void ClearCoverage()
    {
        Array.Clear(_covered);
        _coveredCells = 0;
    }
}
