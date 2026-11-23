using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace CxShell.Views;

/// <summary>
/// Owns the presentation of every full screen mode of a window.
/// <para>
/// The taskbar-visible mode maximizes the window (instead of using
/// <see cref="WindowState.FullScreen"/>) so the desktop taskbar stays usable,
/// while the immersive mode uses <see cref="WindowState.FullScreen"/> and covers
/// the whole monitor.
/// </para>
/// <para>
/// Both modes must also remove the client-side chrome that AtomUI/Avalonia draw
/// inside the window:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="HideTitleBar"/> hides AtomUI's title bar. <see cref="WindowState.FullScreen"/>
/// removes only the native frame, so the drawn caption row would stay on screen.
/// </description></item>
/// <item><description>
/// <see cref="HideDecorations"/> removes the drawn decoration layers.
/// </description></item>
/// <item><description>
/// <see cref="SuppressFullscreenPopover"/> hides the hover-revealed full
/// screen popover that Avalonia draws in a layer above the client area, which
/// duplicates this application's own connection bar.
/// </description></item>
/// </list>
/// <para>
/// All helpers are idempotent and capture the previous value only the first
/// time, so switching between the two modes never loses the original state.
/// </para>
/// </summary>
internal sealed class KeepTaskbarFullScreen
{
    /// <summary>
    /// Automation id Avalonia's <c>TopLevelHost</c> gives the layer that hosts
    /// the hover-revealed full screen popover. That layer is a sibling of the
    /// window inside the top level's visual tree (the stock layout is
    /// underlay, window, overlay, popover), so it is invisible to a search
    /// started at the window itself.
    /// </summary>
    private const string FullscreenPopoverLayerAutomationId = "PopoverWindowChrome";

    /// <summary>Name AtomUI gives the border hosted by that layer.</summary>
    private const string FullscreenHeaderFrameName = "FullscreenHeaderFrame";

    private readonly AtomUI.Desktop.Controls.Window _window;
    private Control? _fullscreenPopoverLayer;
    private bool _popoverSuppressed;
    private IDisposable? _popoverVisibilitySubscription;
    private WindowState _windowStateBefore = WindowState.Normal;
    private bool _titleBarVisibleBefore = true;
    private bool _titleBarHidden;
    private WindowDecorations _decorationsBefore = WindowDecorations.Full;
    private bool _decorationsHidden;
    private bool _isActive;

    public KeepTaskbarFullScreen(AtomUI.Desktop.Controls.Window window)
    {
        _window = window;
    }

    public bool IsActive => _isActive;

    /// <summary>True while the drawn title bar is hidden by this helper.</summary>
    public bool IsTitleBarHidden => _titleBarHidden;

    public bool Enter()
    {
        if (_isActive)
        {
            return false;
        }

        var currentState = _window.WindowState;
        _windowStateBefore = currentState == WindowState.FullScreen
            ? WindowState.Normal
            : currentState;
        _isActive = true;

        // A previous immersive session may have dropped the decorations; the
        // maximized mode still wants the drawn frame (and its resize grips).
        RestoreDecorations();
        HideTitleBar();
        _window.WindowState = WindowState.Maximized;
        return true;
    }

    public void Leave()
    {
        if (!_isActive)
        {
            return;
        }

        _isActive = false;
        RestoreTitleBar();
        if (_window.WindowState != _windowStateBefore)
        {
            _window.WindowState = _windowStateBefore;
        }
    }

    /// <summary>
    /// Drops the saved state while keeping the current window state, used when
    /// another full screen path (the caption button or F11) took over the window
    /// before this mode was left.
    /// </summary>
    public void Abandon()
    {
        if (!_isActive)
        {
            return;
        }

        _isActive = false;
        RestoreTitleBar();
    }

    /// <summary>
    /// Hides the drawn title bar for the immersive full screen path
    /// (<see cref="WindowState.FullScreen"/>). Safe to call repeatedly; the
    /// original visibility is only captured the first time.
    /// </summary>
    public void HideTitleBar()
    {
        if (_titleBarHidden)
        {
            return;
        }

        _titleBarVisibleBefore = _window.IsTitleBarVisible;
        _titleBarHidden = true;
        _window.IsTitleBarVisible = false;
    }

    /// <summary>Restores the visibility captured by <see cref="HideTitleBar"/>.</summary>
    public void RestoreTitleBar()
    {
        if (!_titleBarHidden)
        {
            return;
        }

        _titleBarHidden = false;
        _window.IsTitleBarVisible = _titleBarVisibleBefore;
    }

    /// <summary>
    /// Removes Avalonia's drawn decoration layers for the immersive mode. While
    /// the window is in <see cref="WindowState.FullScreen"/> those layers provide
    /// the hover-revealed fullscreen popover, which would overlap the connection
    /// bar this application draws itself.
    /// </summary>
    public void HideDecorations()
    {
        if (!_decorationsHidden)
        {
            _decorationsBefore = _window.WindowDecorations;
            _decorationsHidden = true;
        }

        // Re-applied on every call: entering full screen can make the platform
        // put the window decorations back, which would revive the popover.
        _window.WindowDecorations = WindowDecorations.None;
    }

    /// <summary>Restores the decorations captured by <see cref="HideDecorations"/>.</summary>
    public void RestoreDecorations()
    {
        if (!_decorationsHidden)
        {
            return;
        }

        _decorationsHidden = false;
        _window.WindowDecorations = _decorationsBefore;
    }

    /// <summary>True once the popover content has been hidden.</summary>
    public bool IsFullscreenPopoverSuppressed => _popoverSuppressed;

    /// <summary>
    /// Removes the hover-revealed full screen popover that Avalonia draws above
    /// the client area. <c>TopLevelHost</c> inserts a layer with automation id
    /// <c>PopoverWindowChrome</c> as a sibling of the window and shows it as
    /// soon as the pointer touches the top edge of a full screen window; the
    /// content comes from AtomUI's decorations theme
    /// (<c>FullscreenHeaderFrame</c>: a title bar high, window wide strip with
    /// the logo, title and the full screen/close caption buttons). This
    /// application draws its own connection bar there, so that strip has to go.
    /// <para>
    /// Hiding the layer is not enough: the hover handler sets
    /// <see cref="Visual.IsVisible"/> back to true on every pointer move at the
    /// top edge. So the hosted frame is hidden, the layer is taken out of hit
    /// testing, and the layer's visibility is watched: every time the vendor
    /// handler re-shows it, the frame is hidden again before the next render
    /// pass. The layer only exists while drawn decorations exist, hence the
    /// return value: false means nothing was found (yet), and callers retry
    /// once the template has settled.
    /// </para>
    /// </summary>
    public bool SuppressFullscreenPopover()
    {
        var layer = _fullscreenPopoverLayer;
        if (layer is null || !layer.IsAttachedToVisualTree())
        {
            layer = _fullscreenPopoverLayer = FindFullscreenPopoverLayer();
            _popoverVisibilitySubscription?.Dispose();
            _popoverVisibilitySubscription = null;
        }

        if (layer is null)
        {
            return false;
        }

        layer.IsHitTestVisible = false;

        // The hover handler only toggles the layer, so mirroring that toggle on
        // the content keeps the strip off screen for as long as the layer lives.
        _popoverVisibilitySubscription ??= layer
            .GetObservable(Visual.IsVisibleProperty)
            .Subscribe(visible =>
            {
                if (visible)
                {
                    HideFullscreenHeaderFrame(layer);
                }
            });

        if (!HideFullscreenHeaderFrame(layer))
        {
            return false;
        }

        _popoverSuppressed = true;
        return true;
    }

    /// <summary>
    /// Releases the suppression applied by <see cref="SuppressFullscreenPopover"/>.
    /// <para>
    /// The hidden strip belongs to the decoration template, which outlives the
    /// full screen session, so leaving it hidden would make the next entry look
    /// suppressed even if the layer was rebuilt in between. Restoring it also
    /// keeps the diagnostics honest: the log field is only meaningful when each
    /// session reports its own result.
    /// </para>
    /// </summary>
    public void ReleaseFullscreenPopoverSuppression()
    {
        _popoverVisibilitySubscription?.Dispose();
        _popoverVisibilitySubscription = null;

        if (_fullscreenPopoverLayer is { } layer)
        {
            layer.IsHitTestVisible = true;

            if (FindFullscreenHeaderFrame(layer) is { } frame)
            {
                frame.IsVisible = true;
            }
        }

        _popoverSuppressed = false;
    }

    /// <summary>
    /// Hides the title bar strip hosted by the popover layer. Returns true when
    /// the strip was found, so the caller can tell "nothing built yet" from
    /// "already hidden".
    /// </summary>
    private static bool HideFullscreenHeaderFrame(Control layer)
    {
        if (FindFullscreenHeaderFrame(layer) is not { } frame)
        {
            return false;
        }

        frame.IsVisible = false;
        return true;
    }

    private static Control? FindFullscreenHeaderFrame(Control layer) =>
        layer.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control => control.Name == FullscreenHeaderFrameName);

    /// <summary>
    /// Finds the popover layer by walking up from the window: the layer is a
    /// later sibling of the window in the top level host's visual tree.
    /// </summary>
    private Control? FindFullscreenPopoverLayer()
    {
        Visual? root = _window;
        while (root.GetVisualParent() is { } parent)
        {
            root = parent;
        }

        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            if (AutomationProperties.GetAutomationId(control) == FullscreenPopoverLayerAutomationId)
            {
                return control;
            }
        }

        return null;
    }
}
