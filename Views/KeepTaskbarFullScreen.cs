using Avalonia.Controls;

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
/// <see cref="HideDecorations"/> removes Avalonia's drawn decoration layers.
/// </description></item>
/// <item><description>
/// AtomUI 6.3.0's built-in full-screen title bar (<c>FullScreenTitleBarMode</c>,
/// defaulting to <c>AutoHide</c> inside <c>WindowChromeLayer</c>) is disabled
/// declaratively via <c>FullScreenTitleBarMode="Hidden"</c> on each window XAML
/// root that hosts <see cref="FullScreenConnectionBar"/>.
/// </description></item>
/// </list>
/// <para>
/// All helpers are idempotent and capture the previous value only the first
/// time, so switching between the two modes never loses the original state.
/// </para>
/// </summary>
internal sealed class KeepTaskbarFullScreen
{
    private readonly AtomUI.Desktop.Controls.Window _window;
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
    /// Removes Avalonia's drawn decoration layers for the immersive mode so the
    /// full screen window displays no vendor chrome.
    /// </summary>
    public void HideDecorations()
    {
        if (!_decorationsHidden)
        {
            _decorationsBefore = _window.WindowDecorations;
            _decorationsHidden = true;
        }

        // Re-applied on every call in case entering full screen causes the
        // platform to restore window decorations.
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
}
