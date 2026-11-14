using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    private TerminalTabViewModel? _tab;
    private Action<TerminalTabViewModel>? _closeTab;
    private WindowState _windowStateBeforeFullScreen = WindowState.Normal;
    private bool _disposed;

    public DetachedTabWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
        PropertyChanged += OnWindowPropertyChanged;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
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

    public static DetachedTabWindow ShowFor(
        TerminalTabViewModel tab,
        Action<TerminalTabViewModel>? closeTab = null,
        PixelPoint? position = null)
    {
        var window = new DetachedTabWindow(tab, closeTab);
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
        if (state != WindowState.FullScreen)
            _windowStateBeforeFullScreen = state;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            ExitFullScreen();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.F11)
            return;

        ToggleFullScreen();
        e.Handled = true;
    }

    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            ExitFullScreen();
            return;
        }

        WindowState = WindowState.FullScreen;
    }

    private void ExitFullScreen()
    {
        if (WindowState != WindowState.FullScreen)
            return;

        WindowState = _windowStateBeforeFullScreen == WindowState.FullScreen
            ? WindowState.Normal
            : _windowStateBeforeFullScreen;
    }
}
