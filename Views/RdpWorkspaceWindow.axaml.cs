using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class RdpWorkspaceTabItem : ObservableObject
{
    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.Parse("#2B3440"));
    private static readonly IBrush FloatingSelectedBrush = new SolidColorBrush(Color.Parse("#4464D8FF"));
    private static readonly IBrush FloatingUnselectedBrush = new SolidColorBrush(Color.Parse("#1FFFFFFF"));
    private static readonly IBrush UnselectedBrush = Brushes.Transparent;

    public RdpWorkspaceTabItem(TerminalTabViewModel tab)
    {
        Tab = tab;
    }

    public TerminalTabViewModel Tab { get; }
    public string Title => Tab.Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabBackground))]
    [NotifyPropertyChangedFor(nameof(FloatingTabBackground))]
    [NotifyPropertyChangedFor(nameof(TitleFontWeight))]
    private bool _isSelected;

    public IBrush TabBackground => IsSelected ? SelectedBrush : UnselectedBrush;
    public IBrush FloatingTabBackground => IsSelected ? FloatingSelectedBrush : FloatingUnselectedBrush;
    public FontWeight TitleFontWeight => IsSelected ? FontWeight.SemiBold : FontWeight.Normal;
}

/// <summary>
/// Standalone multi-tab window dedicated to hosting RDP sessions outside the main window.
/// </summary>
public partial class RdpWorkspaceWindow : AtomUI.Desktop.Controls.Window
{
    private static RdpWorkspaceWindow? _instance;
    private readonly ObservableCollection<RdpWorkspaceTabItem> _tabs = [];
    private WindowState _windowStateBeforeFullScreen = WindowState.Normal;
    private RdpViewModel? _selectedRdpVm;

    public RdpWorkspaceWindow()
    {
        InitializeComponent();
        TabStripItems.ItemsSource = _tabs;
        RdpViewsItems.ItemsSource = _tabs;

        FloatingBar.TabItemsSource = _tabs;
        FloatingBar.TabSelected += item =>
        {
            if (item is RdpWorkspaceTabItem tabItem)
                SelectTab(tabItem);
        };
        FloatingBar.TabCloseRequested += item =>
        {
            if (item is RdpWorkspaceTabItem tabItem)
                CloseTabItem(tabItem);
        };
        FloatingBar.MultiMonitorRequested += anchor =>
            GetSelectedRdpView()?.ToggleMultiMonitorPopup(anchor, PlacementMode.Bottom, FloatingBar.SetExternalPopupOpen);
        FloatingBar.MinimizeRequested += (_, _) => WindowState = WindowState.Minimized;
        FloatingBar.RestoreRequested += (_, _) => WindowState = WindowState.Normal;
        FloatingBar.ToggleFullScreenRequested += ToggleWindowFullScreen;
        FloatingBar.CloseRequested += (_, _) => Close();
        FloatingBar.DragRestoreRequested += e =>
        {
            if (WindowState is WindowState.Maximized or WindowState.FullScreen)
            {
                WindowState = WindowState.Normal;
                BeginMoveDrag(e);
                e.Handled = true;
            }
        };

        Closed += OnClosed;
        PropertyChanged += OnWindowPropertyChanged;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public static void OpenTab(TerminalTabViewModel tab)
    {
        var window = _instance;
        if (window == null)
        {
            window = new RdpWorkspaceWindow();
            _instance = window;
            window.Show();
        }

        window.AddAndSelectTab(tab);

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
    }

    public static void CloseAll()
    {
        var window = _instance;
        _instance = null;
        window?.Close();
    }

    private void AddAndSelectTab(TerminalTabViewModel tab)
    {
        var item = new RdpWorkspaceTabItem(tab);
        _tabs.Add(item);
        SelectTab(item);
    }

    private void SelectTab(RdpWorkspaceTabItem item)
    {
        GetSelectedRdpView()?.CloseMultiMonitorPopup();

        foreach (var existing in _tabs)
            existing.IsSelected = ReferenceEquals(existing, item);

        if (_selectedRdpVm != null)
        {
            _selectedRdpVm.PropertyChanged -= OnSelectedRdpPropertyChanged;
            _selectedRdpVm = null;
        }

        if (item.Tab.Rdp is { } rdp)
        {
            _selectedRdpVm = rdp;
            rdp.PropertyChanged += OnSelectedRdpPropertyChanged;
        }

        UpdateMultiMonitorButtonState();
    }

    private RdpView? GetSelectedRdpView()
    {
        var selected = _tabs.FirstOrDefault(t => t.IsSelected);
        if (selected?.Tab.Rdp is not { } rdp)
            return null;

        return RdpViewsItems.GetVisualDescendants()
            .OfType<RdpView>()
            .FirstOrDefault(v => ReferenceEquals(v.DataContext, rdp));
    }

    private void OnSelectedRdpPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RdpViewModel.IsConnected) or nameof(RdpViewModel.IsMultiMonitorActive))
            UpdateMultiMonitorButtonState();
    }

    private void UpdateMultiMonitorButtonState()
    {
        var connected = _selectedRdpVm?.IsConnected == true;
        var active = _selectedRdpVm?.IsMultiMonitorActive == true;
        FloatingBar.IsMultiMonitorEnabled = connected;
        FloatingBar.IsMultiMonitorActive = active;
        if (TitleBarMultiMonitorButton != null)
            TitleBarMultiMonitorButton.IsEnabled = connected;
    }

    private void OnTitleBarMultiMonitorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control anchor)
            GetSelectedRdpView()?.ToggleMultiMonitorPopup(anchor, PlacementMode.Bottom);
    }

    private void DetachTabItem(RdpWorkspaceTabItem item)
    {
        var wasSelected = item.IsSelected;
        var index = _tabs.IndexOf(item);
        if (index < 0)
            return;

        _tabs.RemoveAt(index);
        DetachedTabWindow.ShowFor(item.Tab);

        if (_tabs.Count == 0)
        {
            Close();
            return;
        }

        if (wasSelected)
        {
            var nextIndex = Math.Min(index, _tabs.Count - 1);
            SelectTab(_tabs[nextIndex]);
        }
    }

    private void CloseTabItem(RdpWorkspaceTabItem item)
    {
        var wasSelected = item.IsSelected;
        var index = _tabs.IndexOf(item);
        if (index < 0)
            return;

        _tabs.RemoveAt(index);
        try
        {
            item.Tab.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RDP workspace tab cleanup failed: {ex.Message}");
        }

        if (_tabs.Count == 0)
        {
            Close();
            return;
        }

        if (wasSelected)
        {
            var nextIndex = Math.Min(index, _tabs.Count - 1);
            SelectTab(_tabs[nextIndex]);
        }
    }

    private void TabItem_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control source && IsInsideButton(source))
            return;

        if (sender is Control { DataContext: RdpWorkspaceTabItem item })
        {
            SelectTab(item);
            e.Handled = true;
        }
    }

    private static bool IsInsideButton(Control? control)
    {
        while (control != null)
        {
            if (control is Button)
                return true;
            control = control.Parent as Control;
        }

        return false;
    }

    private void DetachTabButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RdpWorkspaceTabItem item })
        {
            e.Handled = true;
            DetachTabItem(item);
        }
    }

    private void CloseOtherTabsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RdpWorkspaceTabItem keep })
        {
            e.Handled = true;
            foreach (var other in _tabs.Where(t => !ReferenceEquals(t, keep)).ToList())
                CloseTabItem(other);
        }
    }

    private void CloseTabButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RdpWorkspaceTabItem item })
        {
            e.Handled = true;
            CloseTabItem(item);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        FloatingBar?.StopTimer();
        if (_selectedRdpVm != null)
        {
            _selectedRdpVm.PropertyChanged -= OnSelectedRdpPropertyChanged;
            _selectedRdpVm = null;
        }
        if (ReferenceEquals(_instance, this))
            _instance = null;

        foreach (var item in _tabs.ToArray())
        {
            try
            {
                item.Tab.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RDP workspace tab cleanup failed: {ex.Message}");
            }
        }

        _tabs.Clear();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty)
            return;

        var state = WindowState;
        if (state != WindowState.FullScreen)
            _windowStateBeforeFullScreen = state;

        var isImmersive = state is WindowState.Maximized or WindowState.FullScreen;
        IsTitleBarVisible = !isImmersive;
        WindowDecorations = state == WindowState.FullScreen
            ? WindowDecorations.None
            : WindowDecorations.Full;
        FloatingBar?.SetImmersiveActive(isImmersive);
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

        ToggleWindowFullScreen();
        e.Handled = true;
    }

    private void ToggleWindowFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
            ExitFullScreen();
        else
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
