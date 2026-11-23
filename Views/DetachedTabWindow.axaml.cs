using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CxShell.ViewModels;

namespace CxShell.Views;

/// <summary>
/// Hosts a single terminal tab in its own top-level window. The tab view model
/// is moved here from the main window, so its connections, buffers, and runtime
/// state survive the move; closing this window closes that session.
/// </summary>
public partial class DetachedTabWindow : AtomUI.Desktop.Controls.Window
{
    private static readonly List<DetachedTabWindow> OpenWindows = [];
    private readonly KeepTaskbarFullScreen _keepTaskbarFullScreen;
    private TerminalTabViewModel? _tab;
    private Action<TerminalTabViewModel>? _closeTab;
    private Func<TerminalTabViewModel, PixelPoint, bool>? _reattach;
    private DispatcherTimer? _dragWatchTimer;
    private PixelPoint _lastPosition;
    private bool _hasLastPosition;
    private bool _dragMoved;
    private WindowState _windowStateBeforeFullScreen = WindowState.Normal;
    private bool _restoreFullScreenAfterMinimize;
    private bool _restoreKeepTaskbarAfterMinimize;
    private bool _suppressPresentationRestore;
    private bool _disposed;

    public DetachedTabWindow()
    {
        InitializeComponent();
        _keepTaskbarFullScreen = new KeepTaskbarFullScreen(this);
        FullScreenConnectionBar.IsPinned = SessionTreeViewModel.SharedSettings?.FullScreenConnectionBarPinned == true;
        FullScreenConnectionBar.MinimizeRequested += OnConnectionBarMinimize;
        FullScreenConnectionBar.RestoreRequested += OnConnectionBarRestore;
        FullScreenConnectionBar.CloseRequested += OnConnectionBarClose;
        FullScreenConnectionBar.PinnedChanged += OnConnectionBarPinnedChanged;
        KeepTaskbarFullScreenButton.SetValue(
            AtomUI.Desktop.Controls.ToolTip.TipProperty,
            Services.LocalizationService.Shared.Text("Toolbar.FullScreenKeepTaskbarTip"));
        Closed += OnClosed;
        PropertyChanged += OnWindowPropertyChanged;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _dragWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _dragWatchTimer.Tick += OnDragWatchTick;
        _dragWatchTimer.Start();
    }

    public DetachedTabWindow(TerminalTabViewModel tab, Action<TerminalTabViewModel>? closeTab = null)
        : this()
    {
        _closeTab = closeTab;
        Tab = tab;
    }

    public TerminalTabViewModel? Tab
    {
        get => _tab;
        set
        {
            _tab = value;
            DataContext = value;
        }
    }

    public static int OpenWindowCount => OpenWindows.Count;

    /// <summary>
    /// Shows <paramref name="tab"/> in a new window. <paramref name="closeTab"/>
    /// runs when the user closes the window, and <paramref name="reattach"/> is
    /// asked whether a drop at a screen point should hand the tab back to the
    /// main window; returning <c>false</c> keeps the window detached.
    /// </summary>
    public static DetachedTabWindow ShowFor(
        TerminalTabViewModel tab,
        Action<TerminalTabViewModel>? closeTab = null,
        PixelPoint? position = null,
        Func<TerminalTabViewModel, PixelPoint, bool>? reattach = null)
    {
        var window = new DetachedTabWindow(tab, closeTab) { _reattach = reattach };
        OpenWindows.Add(window);

        if (position != null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = position.Value;
        }

        window.Show();
        return window;
    }

    /// <summary>
    /// Closes every detached window, which also releases the sessions they host.
    /// The main window calls this so detaching a tab never keeps a connection
    /// alive after the application window is gone.
    /// </summary>
    public static void CloseAll()
    {
        foreach (var window in OpenWindows.ToArray())
            window.Close();

        OpenWindows.Clear();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopDragWatch();
        OpenWindows.Remove(this);
        DisposeTab();
    }

    private void DisposeTab()
    {
        if (_disposed)
            return;

        _disposed = true;
        var tab = _tab;
        _tab = null;
        DataContext = null;

        if (tab == null)
            return;

        try
        {
            if (_closeTab != null)
                _closeTab(tab);
            else
                tab.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Detached tab cleanup failed: {ex.Message}");
        }
        finally
        {
            _closeTab = null;
        }
    }

    /// <summary>
    /// Remembers the last non-fullscreen window state so leaving fullscreen can
    /// restore whether the window was normal or maximized. The AtomUI caption
    /// button toggles the state itself, so evaluating the current state here also
    /// covers that path.
    /// </summary>
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty)
            return;

        var state = WindowState;

        // Avalonia reveals a full width bar (window logo, title and the full
        // screen/close buttons) from a top level layer above the client area as
        // soon as the pointer touches the top edge of a full screen window.
        // Hide it: this window draws its own connection bar. Posted because the
        // vendor hover handler runs on every pointer move, and repeated once
        // after the decoration template has been built.
        if (state == WindowState.FullScreen)
        {
            Dispatcher.UIThread.Post(
                () => _keepTaskbarFullScreen.SuppressFullscreenPopover(),
                DispatcherPriority.Loaded);
            DispatcherTimer.RunOnce(
                () =>
                {
                    if (WindowState == WindowState.FullScreen)
                        _keepTaskbarFullScreen.SuppressFullscreenPopover();
                },
                TimeSpan.FromMilliseconds(800));
        }

        if (state == WindowState.Minimized)
        {
            UpdateConnectionBarState();
            return;
        }

        if (_suppressPresentationRestore)
        {
            UpdateConnectionBarState();
            return;
        }

        if (RestorePresentationAfterMinimize())
        {
            UpdateConnectionBarState();
            return;
        }

        // Another full screen path (the caption button, F11, or a window manager
        // shortcut) took over, so hand the title bar back instead of leaving the
        // window stuck in a borderless maximized state.
        if (_keepTaskbarFullScreen.IsActive && state != WindowState.Maximized)
        {
            _keepTaskbarFullScreen.Abandon();
        }

        // Same for the immersive mode: if something outside this window ended
        // it, restore the chrome instead of leaving the window without a
        // caption. The live state is checked because a restore above may have
        // changed it.
        if (WindowState != WindowState.FullScreen && !_keepTaskbarFullScreen.IsActive)
        {
            _keepTaskbarFullScreen.RestoreDecorations();
            _keepTaskbarFullScreen.RestoreTitleBar();
            _keepTaskbarFullScreen.ReleaseFullscreenPopoverSuppression();
        }

        if (state != WindowState.FullScreen)
            _windowStateBeforeFullScreen = state;

        UpdateConnectionBarState();
    }

    /// <summary>
    /// Re-applies the presentation that was active when the window was
    /// minimized from the connection bar, once it comes back.
    /// </summary>
    private bool RestorePresentationAfterMinimize()
    {
        if (_restoreKeepTaskbarAfterMinimize)
        {
            _restoreKeepTaskbarAfterMinimize = false;
            _keepTaskbarFullScreen.Enter();
            return true;
        }

        if (_restoreFullScreenAfterMinimize)
        {
            _restoreFullScreenAfterMinimize = false;
            if (WindowState != WindowState.FullScreen)
            {
                _keepTaskbarFullScreen.HideDecorations();
                _keepTaskbarFullScreen.HideTitleBar();
                WindowState = WindowState.FullScreen;
                _keepTaskbarFullScreen.HideDecorations();
                _keepTaskbarFullScreen.SuppressFullscreenPopover();
            }

            return true;
        }

        return false;
    }

    private void UpdateConnectionBarState()
    {
        FullScreenConnectionBar.IsFullScreenActive =
            WindowState == WindowState.FullScreen || _keepTaskbarFullScreen.IsActive;
    }

    private void OnConnectionBarMinimize(object? sender, EventArgs e)
    {
        // Leaving the taskbar-visible presentation changes WindowState, which
        // would otherwise be mistaken for the user restoring the window.
        _suppressPresentationRestore = true;

        _restoreKeepTaskbarAfterMinimize = _keepTaskbarFullScreen.IsActive;
        _restoreFullScreenAfterMinimize = WindowState == WindowState.FullScreen;

        if (_keepTaskbarFullScreen.IsActive)
            _keepTaskbarFullScreen.Leave();

        WindowState = WindowState.Minimized;
        _suppressPresentationRestore = false;
    }

    private void OnConnectionBarRestore(object? sender, EventArgs e)
    {
        if (_keepTaskbarFullScreen.IsActive)
        {
            _keepTaskbarFullScreen.Leave();
            UpdateConnectionBarState();
            return;
        }

        if (WindowState == WindowState.FullScreen)
            ExitFullScreen();

        UpdateConnectionBarState();
    }

    private void OnConnectionBarClose(object? sender, EventArgs e)
    {
        Close();
    }

    private void OnConnectionBarPinnedChanged(object? sender, EventArgs e)
    {
        if (SessionTreeViewModel.SharedSettings is not { } settings)
            return;

        settings.FullScreenConnectionBarPinned = FullScreenConnectionBar.IsPinned;
        SessionTreeViewModel.PersistSharedSettings();
    }

    private void OnKeepTaskbarFullScreenClick(object? sender, RoutedEventArgs e)
    {
        if (_keepTaskbarFullScreen.IsActive)
        {
            _keepTaskbarFullScreen.Leave();
            e.Handled = true;
            return;
        }

        if (WindowState == WindowState.FullScreen)
            ExitFullScreen();

        _keepTaskbarFullScreen.Enter();
        e.Handled = true;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // Escape stays with the remote session; F11 and the connection bar are
        // the ways out of full screen.
        if (e.Key != Key.F11)
            return;

        ToggleFullScreen();
        e.Handled = true;
    }

    private void ToggleFullScreen()
    {
        if (_keepTaskbarFullScreen.IsActive)
            _keepTaskbarFullScreen.Abandon();

        if (WindowState == WindowState.FullScreen)
        {
            ExitFullScreen();
            return;
        }

        // WindowState.FullScreen removes only the native frame; AtomUI draws its
        // title bar inside the client area, and Avalonia reveals a hover popover
        // from the drawn decoration layers. Drop both for an edge-to-edge view.
        _keepTaskbarFullScreen.HideDecorations();
        _keepTaskbarFullScreen.HideTitleBar();
        WindowState = WindowState.FullScreen;
        // Re-assert after the state change: the platform can restore the
        // decorations while entering full screen, which revives the popover.
        _keepTaskbarFullScreen.HideDecorations();
    }

    private void ExitFullScreen()
    {
        if (WindowState != WindowState.FullScreen)
        {
            _keepTaskbarFullScreen.RestoreDecorations();
            _keepTaskbarFullScreen.RestoreTitleBar();
            _keepTaskbarFullScreen.ReleaseFullscreenPopoverSuppression();
            return;
        }

        WindowState = _windowStateBeforeFullScreen == WindowState.FullScreen
            ? WindowState.Normal
            : _windowStateBeforeFullScreen;

        // Restore the chrome only after the window left full screen, so the
        // fullscreen popover can never flash on the way out.
        _keepTaskbarFullScreen.RestoreDecorations();
        _keepTaskbarFullScreen.RestoreTitleBar();
        _keepTaskbarFullScreen.ReleaseFullscreenPopoverSuppression();
    }

    /// <summary>
    /// A native title-bar drag moves the window without delivering pointer
    /// events, so the position is polled: once the window has moved and the
    /// left button is released, the drop point decides whether the tab goes
    /// back to the main window. Releasing anywhere else leaves it detached.
    /// </summary>
    private void OnDragWatchTick(object? sender, EventArgs e)
    {
        if (_disposed || _tab == null || _reattach == null || !OperatingSystem.IsWindows())
            return;

        if (!IsVisible ||
            WindowState != WindowState.Normal ||
            _keepTaskbarFullScreen.IsActive)
        {
            _hasLastPosition = false;
            _dragMoved = false;
            return;
        }

        var position = Position;
        if (!_hasLastPosition)
        {
            _lastPosition = position;
            _hasLastPosition = true;
            return;
        }

        if (position != _lastPosition)
        {
            _lastPosition = position;
            _dragMoved = true;
            return;
        }

        if (!_dragMoved || IsLeftMouseButtonDown())
            return;

        // The window was moved and the button is now up: this is the drop.
        _dragMoved = false;
        TryReattachAtCursor();
    }

    private void StopDragWatch()
    {
        _dragWatchTimer?.Stop();
    }

    private void TryReattachAtCursor()
    {
        var tab = _tab;
        var reattach = _reattach;
        if (tab == null || reattach == null || !TryGetCursorPosition(out var cursor))
            return;

        if (!reattach(tab, cursor))
            return;

        // The main window owns the tab now. Close this window without letting
        // the close path dispose the session.
        _reattach = null;
        _closeTab = null;
        _tab = null;
        DataContext = null;
        _disposed = true;
        Close();
    }

    private const int VkLeftButton = 0x01;

    private static bool IsLeftMouseButtonDown()
    {
        return (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;
    }

    private static bool TryGetCursorPosition(out PixelPoint position)
    {
        if (GetCursorPos(out var point))
        {
            position = new PixelPoint(point.X, point.Y);
            return true;
        }

        position = default;
        return false;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
