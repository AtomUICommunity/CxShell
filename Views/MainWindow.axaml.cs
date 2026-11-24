using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using AtomUI.Desktop.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CxShell.Models;
using CxShell.Services;
using CxShell.ViewModels;
using AtomContextMenu = AtomUI.Desktop.Controls.ContextMenu;
using AtomMenuItem = AtomUI.Desktop.Controls.MenuItem;
using AtomMenuSeparator = AtomUI.Desktop.Controls.MenuSeparator;
using AtomWindow = AtomUI.Desktop.Controls.Window;
using AvaloniaControl = Avalonia.Controls.Control;

namespace CxShell.Views;

public partial class MainWindow : AtomWindow
{
    protected override Type StyleKeyOverride { get; } = typeof(AtomWindow);
    private readonly string[] _startupArgs;
    private readonly CommandLineLaunchOptions _startupLaunchOptions;
    private IDisposable? _commandLineHandoffServer;
    private bool _tabReorderHandlersAttached;
    private SessionInfo? _quickSessionContext;
    private readonly HashSet<Guid> _quickSessionActivationsPending = [];
    private Guid? _quickSessionPointerPressedSessionId;
    private bool _isQuickSessionActivationReady;
    private TerminalTabViewModel? _tabContext;
    private TerminalTabViewModel? _connectionBarMonitorTab;
    private bool _isDraggingSftpSplitter;
    private bool _isSftpPanelWidthApplyQueued;
    private bool _hasSftpSplitterPreviousCursor;
    private double _sftpSplitterStartX;
    private double _sftpSplitterStartWidth;
    private Cursor? _sftpSplitterPreviousCursor;
    private bool _isDraggingAgentSplitter;
    private bool _hasAgentSplitterPreviousCursor;
    private double _agentSplitterStartX;
    private double _agentSplitterStartWidth;
    private Cursor? _agentSplitterPreviousCursor;

    private const double MinimumSftpPanelWidth = 120;
    private const double SftpSplitterHitSlop = 0;
    private const double MinimumTerminalPanelWidth = 320;
    private const double MonitorPanelWidth = 283;
    private const double MinimumAgentPanelWidth = 280;
    private const double MaximumAgentPanelWidth = 600;
    private const double AgentSplitterHitSlop = 5;
    private const double TabTearOutVerticalMargin = 36;
    private const double TabTearOutDragThreshold = 8;
    private const double TabDropHitSlop = 10;
    private TerminalTabViewModel? _tearOutTab;
    private TabStrip? _tearOutStrip;
    private Point _tearOutStart;
    private bool _tearOutArmed;
    private TabDragGhostWindow? _tabDragGhost;
    private readonly KeepTaskbarFullScreen _keepTaskbarFullScreen;
    private Avalonia.Controls.WindowState _windowStateBeforeFullScreen = Avalonia.Controls.WindowState.Normal;
    private bool _restoreFullScreenAfterMinimize;

    public MainWindow()
        : this(Array.Empty<string>())
    {
    }

    public MainWindow(string[] startupArgs)
    {
        _startupArgs = startupArgs;
        _startupLaunchOptions = CommandLineLaunchOptions.Parse(startupArgs);
        InitializeComponent();
        // SelectingItemsControl exposes SelectionMode as a protected CLR
        // property in Avalonia 12. Set the public styled property directly so
        // the quick-session strip can have no persistent selection without
        // making generated XAML call the inaccessible setter.
        var selectionModeProperty = AvaloniaPropertyRegistry.Instance.FindRegistered(
            typeof(Avalonia.Controls.Primitives.SelectingItemsControl),
            "SelectionMode");
        if (selectionModeProperty != null)
            QuickSessionTabStrip.SetValue(selectionModeProperty, Avalonia.Controls.SelectionMode.Single);
        QuickSessionTabStrip.SelectedIndex = -1;
        _keepTaskbarFullScreen = new KeepTaskbarFullScreen(this);

        FullScreenConnectionBar.IsPinned = SessionTreeViewModel.SharedSettings?.FullScreenConnectionBarPinned == true;
        FullScreenConnectionBar.MinimizeRequested += OnFullScreenBarMinimize;
        FullScreenConnectionBar.RestoreRequested += OnFullScreenBarRestore;
        FullScreenConnectionBar.CloseRequested += OnFullScreenBarClose;
        FullScreenConnectionBar.PinnedChanged += OnFullScreenBarPinnedChanged;
        FullScreenConnectionBar.MultiMonitorRequested += OnFullScreenBarMultiMonitorRequested;
        PropertyChanged += OnMainWindowPropertyChanged;

        var vm = new MainWindowViewModel();
        AgentPanelHost.CloseRequested += OnAgentPanelCloseRequested;
        ApplyAgentPanelLayout(vm);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainWindowViewModel.IsTerminalFullScreen) or
                nameof(MainWindowViewModel.IsTerminalFullScreenKeepTaskbar))
            {
                ApplyTerminalFullScreenState(vm);
            }

            if (e.PropertyName == nameof(MainWindowViewModel.SftpPanelWidth) ||
                e.PropertyName == nameof(MainWindowViewModel.IsSftpVisible) ||
                e.PropertyName == nameof(MainWindowViewModel.IsTerminalFullScreen))
            {
                QueueApplySftpPanelWidth(vm);
            }

            if (e.PropertyName is nameof(MainWindowViewModel.IsAgentPanelHostVisible) or
                nameof(MainWindowViewModel.AgentPanelWidth) or
                nameof(MainWindowViewModel.IsTerminalFullScreen))
            {
                ApplyAgentPanelLayout(vm);
            }

            if (e.PropertyName == nameof(MainWindowViewModel.SelectedTab))
            {
                // Defer past the binding update so the tab's RdpView has already
                // picked up the newly selected view model when we look for it.
                Dispatcher.UIThread.Post(
                    () => UpdateConnectionBarMultiMonitorState(vm),
                    DispatcherPriority.Background);
            }
        };
        DataContext = vm;
        LeftToolbarAddOn.DataContext = vm;
        RightToolbarAddOn.DataContext = vm;
        UpdateConnectionBarMultiMonitorState(vm);
        WriteToolbarDiagnostics("MainWindow initialized; toolbar menus use AtomUI ContextMenu.");
        Closed += (_, _) =>
        {
            CloseTabDragGhost();
            DetachedTabWindow.CloseAll();
            vm.Dispose();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property.Name is nameof(WindowState) or nameof(IsVisible))
                UpdateApplicationSuspension(vm);
        };
        Deactivated += (_, _) => ResetTabTearOut();
        MainContentGrid.AddHandler(PointerPressedEvent, OnMainContentGridPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnSftpSplitterPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnSftpSplitterPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnSftpSplitterPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnAgentSplitterPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnAgentSplitterPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnAgentSplitterPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnTabTearOutPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnTabTearOutPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnTabTearOutPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        QueueApplySftpPanelWidth(vm);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Dispatcher.UIThread.Post(
            () => WriteToolbarDiagnostics(
                $"Toolbar layout: left={LeftToolbarAddOn.Bounds}; menu={ToolbarMenu.Bounds}; right={RightToolbarAddOn.Bounds}; " +
                $"leftVisible={LeftToolbarAddOn.IsVisible}; rightVisible={RightToolbarAddOn.IsVisible}."),
            DispatcherPriority.ApplicationIdle);
        StartCommandLineHandoffServer();
        StartRdpSmokeIfRequested();
        HandleCommandLineLaunchIfRequested();
        ShowSessionManagerOnStartupIfNeeded();
        if (DataContext is MainWindowViewModel vm)
        {
            AttachTabReorderHandlers();
            _isQuickSessionActivationReady = false;
            ClearQuickSessionSelection(QuickSessionTabStrip);
            Dispatcher.UIThread.Post(() =>
            {
                if (!this.IsAttachedToVisualTree())
                    return;

                ClearQuickSessionSelection(QuickSessionTabStrip);
                _isQuickSessionActivationReady = true;
            }, DispatcherPriority.ApplicationIdle);
            UpdateApplicationSuspension(vm);
            vm.StartAutomaticUpdateCheck(_startupArgs);
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _isQuickSessionActivationReady = false;
        _quickSessionPointerPressedSessionId = null;
        _quickSessionActivationsPending.Clear();
        DetachTabReorderHandlers();
        if (DataContext is MainWindowViewModel vm)
            vm.SetApplicationSuspended(true);
        _commandLineHandoffServer?.Dispose();
        _commandLineHandoffServer = null;
        base.OnUnloaded(e);
    }

    private void AttachTabReorderHandlers()
    {
        if (_tabReorderHandlersAttached)
            return;

        QuickSessionTabStrip.TabReordered += OnQuickSessionTabReordered;
        QuickSessionTabStrip.LayoutUpdated += OnQuickSessionTabStripLayoutUpdated;
        foreach (var tabStrip in this.GetVisualDescendants().OfType<TabStrip>())
            tabStrip.TabReordered += OnTabReordered;

        _tabReorderHandlersAttached = true;
        ApplyQuickSessionSquareCorners();
    }

    private void DetachTabReorderHandlers()
    {
        if (!_tabReorderHandlersAttached)
            return;

        QuickSessionTabStrip.TabReordered -= OnQuickSessionTabReordered;
        QuickSessionTabStrip.LayoutUpdated -= OnQuickSessionTabStripLayoutUpdated;
        foreach (var tabStrip in this.GetVisualDescendants().OfType<TabStrip>())
            tabStrip.TabReordered -= OnTabReordered;

        _tabReorderHandlersAttached = false;
    }

    private void OnQuickSessionTabStripLayoutUpdated(object? sender, EventArgs e)
    {
        ApplyQuickSessionSquareCorners();
    }

    private void ApplyQuickSessionSquareCorners()
    {
        foreach (var tabItem in QuickSessionTabStrip.GetVisualDescendants().OfType<TabStripItem>())
        {
            if (tabItem.CornerRadius != default)
                tabItem.CornerRadius = default;
        }
    }

    private void UpdateApplicationSuspension(MainWindowViewModel vm)
    {
        vm.SetApplicationSuspended(!IsVisible || WindowState == Avalonia.Controls.WindowState.Minimized);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.CommandPalette.IsOpen)
        {
            if (e.Key == Key.Escape)
            {
                vm.CommandPalette.Close();
                e.Handled = true;
            }

            return;
        }

        if (DataContext is MainWindowViewModel mainWindowVm &&
            (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 &&
            e.Key is Key.P or Key.K)
        {
            mainWindowVm.OpenCommandPalette();
            e.Handled = true;
            return;
        }

        if (TryHandleQuickCommandShortcut(e))
            return;

        // Escape is deliberately not bound to leaving full screen: the key is
        // forwarded to the remote session, and the connection bar's button is
        // the way out (mstsc behaves the same way).
        TryHandleVncPasteShortcut(e);
    }

    private void OnCommandPaletteBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.CommandPalette.Close();

        e.Handled = true;
    }

    private void OnAgentPanelToggleClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ToggleAgentPanelVisibility();
            ApplyAgentPanelLayout(viewModel);
        }

        e.Handled = true;
    }

    private void OnLocalTerminalButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Control anchor ||
            DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var menu = new AtomContextMenu
        {
            Placement = Avalonia.Controls.PlacementMode.Bottom,
            PlacementTarget = anchor
        };

        foreach (var profile in vm.LocalTerminalProfiles)
        {
            var capturedProfile = profile;
            AddMenuItem(
                menu,
                profile.Name,
                () => _ = vm.OpenLocalTerminalAsync(capturedProfile));
        }

        menu.Open(anchor);
        e.Handled = true;
    }

    private void OnLocalTerminalMenuItemLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not AtomMenuItem menuItem ||
            DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        menuItem.Items.Clear();
        foreach (var profile in vm.LocalTerminalProfiles)
        {
            var capturedProfile = profile;
            var item = new AtomMenuItem
            {
                Header = profile.Name
            };
            item.Click += (_, args) =>
            {
                _ = vm.OpenLocalTerminalAsync(capturedProfile);
                args.Handled = true;
            };
            menuItem.Items.Add(item);
        }
    }

    private void OnAgentPanelCloseRequested(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.IsAgentPanelVisible)
        {
            viewModel.ToggleAgentPanelVisibility();
            ApplyAgentPanelLayout(viewModel);
        }
    }

    private void ApplyAgentPanelLayout(MainWindowViewModel viewModel)
    {
        AgentPanelHost.IsVisible = viewModel.IsAgentPanelHostVisible;
        var width = viewModel.IsAgentPanelHostVisible
            ? Math.Clamp(viewModel.AgentPanelWidth.Value, MinimumAgentPanelWidth, MaximumAgentPanelWidth)
            : 0;

        MainContentGrid.ColumnDefinitions[4].Width = viewModel.IsAgentPanelHostVisible
            ? new Avalonia.Controls.GridLength(1)
            : new Avalonia.Controls.GridLength(0);
        MainContentGrid.ColumnDefinitions[5].Width = viewModel.IsAgentPanelHostVisible
            ? new Avalonia.Controls.GridLength(width)
            : new Avalonia.Controls.GridLength(0);
        AgentPanelHost.Width = width;
        AgentPanelHost.MinWidth = width > 0 ? MinimumAgentPanelWidth : 0;
        AgentPanelHost.MaxWidth = width;
        AgentPanelHost.InvalidateMeasure();
        MainContentGrid.InvalidateMeasure();
    }

    private void OnRecentSessionsDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { SelectedRecentSession: { } item } vm)
            return;

        vm.ConnectRecentSessionCommand.Execute(item);
        e.Handled = true;
    }

    private void OnRecentSessionsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter ||
            DataContext is not MainWindowViewModel { SelectedRecentSession: { } item } vm)
        {
            return;
        }

        vm.ConnectRecentSessionCommand.Execute(item);
        e.Handled = true;
    }

    private bool TryHandleQuickCommandShortcut(KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            (e.KeyModifiers & KeyModifiers.Control) == 0 ||
            (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            return false;
        }

        var index = e.Key >= Key.D1 && e.Key <= Key.D9
            ? (int)e.Key - (int)Key.D1
            : e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9
                ? (int)e.Key - (int)Key.NumPad1
                : -1;
        if (index < 0 || !vm.ExecuteQuickCommandByIndex(index))
            return false;

        e.Handled = true;
        return true;
    }

    private bool TryHandleVncPasteShortcut(KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { SelectedTab.Vnc: { } vnc } ||
            e.Key != Key.V ||
            (e.KeyModifiers & KeyModifiers.Control) == 0 ||
            (e.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            return false;
        }

        e.Handled = true;
        _ = PasteLocalClipboardToVncAsync(vnc);
        return true;
    }

    private async Task PasteLocalClipboardToVncAsync(VncViewModel vnc)
    {
        try
        {
            var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(this)?.Clipboard;
            var text = clipboard == null ? null : await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text))
                await vnc.SendClipboardTextAndPasteAsync(text);
        }
        catch
        {
            // Clipboard access can fail on some desktop backends.
        }
    }

    private void OnQuickSessionTagPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Control { DataContext: SessionInfo session } anchor)
            return;

        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed || properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            _quickSessionContext = session;
            ShowQuickSessionContextMenu(anchor);
            e.Handled = true;
            return;
        }

        if (properties.IsLeftButtonPressed || properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
        {
            _quickSessionPointerPressedSessionId = session.Id;
        }
    }

    private void OnQuickSessionTagPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Control { DataContext: SessionInfo session })
            return;

        if (_quickSessionPointerPressedSessionId != session.Id)
            return;

        // TabStripItem processes selection after the child release handler,
        // so defer cleanup until the current pointer route has completed.
        Dispatcher.UIThread.Post(() =>
        {
            if (_quickSessionPointerPressedSessionId == session.Id)
                _quickSessionPointerPressedSessionId = null;
        }, DispatcherPriority.Background);
    }

    private void OnQuickSessionSelectionChanged(
        object? sender,
        Avalonia.Controls.SelectionChangedEventArgs e)
    {
        var session = e.AddedItems.OfType<SessionInfo>().FirstOrDefault();
        if (session == null || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (!_isQuickSessionActivationReady)
        {
            if (sender is Avalonia.Controls.Primitives.SelectingItemsControl initializingTabStrip)
                ClearQuickSessionSelection(initializingTabStrip);
            return;
        }

        // SelectionChanged can also be raised by ItemsSource initialization,
        // keyboard focus, or the reorder implementation. Only a pointer
        // activation from this tab's content is a connect request.
        if (_quickSessionPointerPressedSessionId != session.Id)
        {
            if (sender is Avalonia.Controls.Primitives.SelectingItemsControl nonActivationTabStrip)
                ClearQuickSessionSelection(nonActivationTabStrip);
            return;
        }

        _quickSessionPointerPressedSessionId = null;

        // CardTabStrip activates on pointer release. Clearing the transient
        // selection can make the same routed event surface again, so only
        // accept one activation for a session until the dispatcher is idle.
        if (!_quickSessionActivationsPending.Add(session.Id))
        {
            if (sender is Avalonia.Controls.Primitives.SelectingItemsControl duplicateTabStrip)
                ClearQuickSessionSelection(duplicateTabStrip);
            return;
        }

        vm.ConnectQuickSessionCommand.Execute(session);

        if (sender is Avalonia.Controls.Primitives.SelectingItemsControl tabStrip)
        {
            ClearQuickSessionSelection(tabStrip);
            Dispatcher.UIThread.Post(() =>
            {
                _quickSessionActivationsPending.Remove(session.Id);
                if (tabStrip.IsAttachedToVisualTree())
                    ClearQuickSessionSelection(tabStrip);
            }, DispatcherPriority.ApplicationIdle);
        }
        else
        {
            _quickSessionActivationsPending.Remove(session.Id);
        }
    }

    private static void ClearQuickSessionSelection(
        Avalonia.Controls.Primitives.SelectingItemsControl tabStrip)
    {
        tabStrip.SelectedItem = null;
        tabStrip.SelectedIndex = -1;
    }

    private void OnQuickSessionTabReordered(object? sender, TabReorderedEventArgs e)
    {
        if (e.Item is SessionInfo && DataContext is MainWindowViewModel vm)
            vm.HandleQuickSessionReordered();
    }

    private void OnTabHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var tab = ResolveTabContext(sender as Avalonia.Controls.Control, out var anchor) ??
                  ResolveTabContext(e.Source as Avalonia.Controls.Control, out anchor);
        if (tab == null || anchor == null || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed || properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            _tabContext = tab;
            vm.SelectTabCommand.Execute(tab);
            ShowTabContextMenu(anchor, vm.AddCurrentSessionToQuickBarCommand.CanExecute(null));
            e.Handled = true;
        }
    }

    private static IEnumerable<Avalonia.Controls.Control> EnumerateControlLineage(Avalonia.Controls.Control? source)
    {
        var current = source;
        var seen = new HashSet<Avalonia.Controls.Control>(ReferenceEqualityComparer.Instance);
        while (current != null && seen.Add(current))
        {
            yield return current;
            current = GetParentControl(current);
        }
    }

    private static Avalonia.Controls.Control? GetParentControl(Avalonia.Controls.Control control)
    {
        var visualParent = control.GetVisualParent() as Avalonia.Controls.Control;
        if (visualParent != null && !ReferenceEquals(visualParent, control))
            return visualParent;

        var logicalParent = control.Parent as Avalonia.Controls.Control;
        if (logicalParent != null && !ReferenceEquals(logicalParent, control))
            return logicalParent;

        return null;
    }

    private void OnTabReordered(object? sender, TabReorderedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm &&
            e.Item is TerminalTabViewModel tab)
        {
            vm.HandleTabReordered(tab);
        }
    }

    private void OnTabStripClosing(object? sender, TabStripClosingEventArgs e)
    {
        e.Cancel = true;

        var tab = e.TabStripItem.DataContext as TerminalTabViewModel ??
                  e.TabStripItem.Content as TerminalTabViewModel;
        if (tab != null && DataContext is MainWindowViewModel vm)
            vm.CloseTab(tab);
    }

    private static TerminalTabViewModel? ResolveTabContext(Avalonia.Controls.Control? source, out Avalonia.Controls.Control? anchor)
    {
        anchor = source;
        foreach (var current in EnumerateControlLineage(source))
        {
            if (current.DataContext is TerminalTabViewModel tab)
            {
                anchor = current;
                return tab;
            }
        }

        return null;
    }

    private void ShowQuickSessionContextMenu(Avalonia.Controls.Control anchor)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var menu = CreatePointerContextMenu(anchor);
        AddMenuItem(menu, vm.QuickPropertiesText, () => OnQuickSessionPropertiesClick(anchor, new RoutedEventArgs()));
        menu.Items.Add(new AtomMenuSeparator());
        AddMenuItem(menu, vm.QuickDeleteText, () => OnQuickSessionDeleteClick(anchor, new RoutedEventArgs()));
        menu.Open(anchor);
    }

    private void ShowTabContextMenu(Avalonia.Controls.Control anchor, bool canAddQuick)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var menu = CreatePointerContextMenu(anchor);
        AddMenuItem(menu, vm.TabMoveToNewWindowText, () => OnTabMoveToNewWindowClick(anchor, new RoutedEventArgs()));
        menu.Items.Add(new AtomMenuSeparator());
        AddMenuItem(menu, vm.TabDuplicateText, () => OnTabDuplicateClick(anchor, new RoutedEventArgs()));
        menu.Items.Add(new AtomMenuSeparator());
        AddMenuItem(menu, vm.TabCloseText, () => OnTabCloseClick(anchor, new RoutedEventArgs()));
        AddMenuItem(menu, vm.TabPropertiesText, () => OnTabPropertiesClick(anchor, new RoutedEventArgs()));
        AddMenuItem(menu, vm.TabAddQuickText, () => OnTabAddQuickClick(anchor, new RoutedEventArgs()), canAddQuick);
        menu.Open(anchor);
    }

    private static AtomContextMenu CreatePointerContextMenu(Avalonia.Controls.Control anchor)
    {
        return new AtomContextMenu
        {
            Placement = Avalonia.Controls.PlacementMode.Pointer,
            PlacementTarget = anchor
        };
    }

    private static void AddMenuItem(AtomContextMenu menu, string text, Action action, bool isEnabled = true)
    {
        var item = new AtomMenuItem
        {
            Header = text,
            IsEnabled = isEnabled
        };
        item.Click += (_, _) =>
        {
            menu.Close();
            action();
        };
        menu.Items.Add(item);
    }

    private void OnArrangeVerticalMenuItemClick(object? sender, RoutedEventArgs e) =>
        ExecuteToolbarCommand("arrange vertical", vm => vm.ArrangeTabsVerticalCommand.Execute(null));

    private void OnArrangeHorizontalMenuItemClick(object? sender, RoutedEventArgs e) =>
        ExecuteToolbarCommand("arrange horizontal", vm => vm.ArrangeTabsHorizontalCommand.Execute(null));

    private void OnArrangeTileMenuItemClick(object? sender, RoutedEventArgs e) =>
        ExecuteToolbarCommand("arrange tile", vm => vm.ArrangeTabsTileCommand.Execute(null));

    private void OnArrangeMergeMenuItemClick(object? sender, RoutedEventArgs e) =>
        ExecuteToolbarCommand("arrange merge", vm => vm.MergeTabGroupsCommand.Execute(null));

    private void OnChineseLanguageMenuItemClick(object? sender, RoutedEventArgs e) =>
        ExecuteToolbarCommand("language zh-CN", vm => vm.SetLanguageCommand.Execute("zh-CN"));

    private void OnEnglishLanguageMenuItemClick(object? sender, RoutedEventArgs e) =>
        ExecuteToolbarCommand("language en-US", vm => vm.SetLanguageCommand.Execute("en-US"));

    private void ExecuteToolbarCommand(string name, Action<MainWindowViewModel> execute)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            WriteToolbarDiagnostics($"{name} menu item Click ignored; MainWindow DataContext is unavailable.");
            return;
        }

        try
        {
            execute(vm);
            WriteToolbarDiagnostics($"{name} menu item command executed.");
        }
        catch (Exception ex)
        {
            WriteToolbarDiagnostics($"{name} menu item command failed: {ex.GetType().Name}: {ex.Message}");
        }
    }


    private static void WriteToolbarDiagnostics(string message)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CxShell",
                "Logs");
            Directory.CreateDirectory(logDirectory);
            var path = Path.Combine(logDirectory, "toolbar-menu.log");
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}";
            lock (ToolbarDiagnosticsLock)
                File.AppendAllText(path, line, System.Text.Encoding.UTF8);
            System.Diagnostics.Debug.WriteLine(message);
        }
        catch
        {
            // UI diagnostics must never affect menu interaction.
        }
    }

    private static readonly object ToolbarDiagnosticsLock = new();

    private async void OnSendRemoteClipboardClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { SelectedTab.Vnc: { } vnc })
            return;

        var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(this)?.Clipboard;
        var text = clipboard == null ? null : await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
            await vnc.SendClipboardTextAndPasteAsync(text);
    }

    private async void OnSendRemoteCtrlAltDelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { SelectedTab.Vnc: { } vnc })
            return;

        await vnc.SendCtrlAltDeleteAsync();
    }

    private async void OnSendRdpCtrlAltDelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { SelectedTab.Rdp: { } rdp })
            await rdp.SendCtrlAltDeleteAsync();
    }

    private void OnRdpKeyCombinationsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Control anchor ||
            DataContext is not MainWindowViewModel { SelectedTab.Rdp: { } rdp })
        {
            return;
        }

        var menu = CreatePointerContextMenu(anchor);
        var enabled = rdp.IsConnected;
        AddMenuItem(menu, T("Rdp.Keyboard.AltTab"), () => _ = rdp.SendAltTabAsync(), enabled);
        AddMenuItem(menu, T("Rdp.Keyboard.Windows"), () => _ = rdp.SendWindowsKeyAsync(), enabled);
        AddMenuItem(menu, T("Rdp.Keyboard.CtrlEsc"), () => _ = rdp.SendCtrlEscapeAsync(), enabled);
        AddMenuItem(menu, T("Rdp.Keyboard.AltF4"), () => _ = rdp.SendAltF4Async(), enabled);
        menu.Items.Add(new AtomMenuSeparator());
        AddMenuItem(menu, T("Rdp.Keyboard.TaskManager"), () => _ = rdp.SendTaskManagerAsync(), enabled);
        AddMenuItem(menu, T("Rdp.Keyboard.PrintScreen"), () => _ = rdp.SendPrintScreenAsync(), enabled);
        menu.Open(anchor);
    }

    private static string T(string key)
    {
        return LocalizationService.Shared.Text(key);
    }

    private void OnTabGroupPanePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control { DataContext: TerminalTabGroupViewModel group } &&
            DataContext is MainWindowViewModel vm)
        {
            vm.SelectTabGroupCommand.Execute(group);
        }
    }

    private void OnSftpSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        TryStartSftpSplitterDrag(sender, e);
    }

    private void OnMainContentGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsLeftButtonPress(e))
        {
            return;
        }

        if (DataContext is MainWindowViewModel vm && vm.IsSftpPanelVisible)
        {
            var position = e.GetPosition(MainContentGrid);
            var splitterLeft = SftpPanelHost.Bounds.Right;
            var splitterRight = splitterLeft + Math.Max(vm.SftpSplitterWidth.Value, 1);
            if (position.X >= splitterLeft - SftpSplitterHitSlop &&
                position.X <= splitterRight + SftpSplitterHitSlop)
            {
                TryStartSftpSplitterDrag(MainContentGrid, e);
                return;
            }
        }

        if (DataContext is not MainWindowViewModel agentVm ||
            !agentVm.IsAgentPanelHostVisible ||
            !AgentSplitterHandle.IsVisible)
        {
            return;
        }

        var agentPosition = e.GetPosition(MainContentGrid);
        var agentSplitterLeft = AgentSplitterHandle.Bounds.Left;
        var agentSplitterRight = agentSplitterLeft + Math.Max(agentVm.AgentSplitterWidth.Value, 1);
        if (agentPosition.X < agentSplitterLeft - AgentSplitterHitSlop ||
            agentPosition.X > agentSplitterRight + AgentSplitterHitSlop)
        {
            return;
        }

        TryStartAgentSplitterDrag(MainContentGrid, e);
    }

    private void OnAgentSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        TryStartAgentSplitterDrag(sender, e);
    }

    private void TryStartAgentSplitterDrag(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            !vm.IsAgentPanelHostVisible ||
            !IsLeftButtonPress(e) ||
            _isDraggingAgentSplitter)
        {
            return;
        }

        _isDraggingAgentSplitter = true;
        _agentSplitterStartX = e.GetPosition(MainContentGrid).X;
        _agentSplitterStartWidth = Math.Clamp(
            vm.AgentPanelWidth.Value,
            MinimumAgentPanelWidth,
            MaximumAgentPanelWidth);

        e.Pointer.Capture(this);
        ShowAgentSplitterCursor();
        e.Handled = true;
    }

    private void OnAgentSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingAgentSplitter ||
            DataContext is not MainWindowViewModel vm ||
            !vm.IsAgentPanelHostVisible)
        {
            return;
        }

        var delta = e.GetPosition(MainContentGrid).X - _agentSplitterStartX;
        var maxWidth = GetMaximumAgentPanelWidth(vm);
        var width = Math.Min(
            Math.Max(MinimumAgentPanelWidth, _agentSplitterStartWidth - delta),
            maxWidth);
        vm.AgentPanelWidth = new Avalonia.Controls.GridLength(
            width,
            Avalonia.Controls.GridUnitType.Pixel);
        ShowAgentSplitterCursor();
        e.Handled = true;
    }

    private void OnAgentSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingAgentSplitter)
            return;

        EndAgentSplitterDrag(e.Pointer);
        e.Handled = true;
    }

    private void OnAgentSplitterPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(e.Source, this))
            EndAgentSplitterDrag(null);
    }

    private void EndAgentSplitterDrag(IPointer? pointer)
    {
        _isDraggingAgentSplitter = false;
        pointer?.Capture(null);
        ClearAgentSplitterCursor();

        if (DataContext is MainWindowViewModel vm)
            vm.PersistAgentPanelWidth();
    }

    private void ShowAgentSplitterCursor()
    {
        if (!_hasAgentSplitterPreviousCursor)
        {
            _agentSplitterPreviousCursor = Cursor;
            _hasAgentSplitterPreviousCursor = true;
        }

        Cursor = new Cursor(StandardCursorType.SizeWestEast);
    }

    private void ClearAgentSplitterCursor()
    {
        if (!_hasAgentSplitterPreviousCursor)
            return;

        Cursor = _agentSplitterPreviousCursor;
        _agentSplitterPreviousCursor = null;
        _hasAgentSplitterPreviousCursor = false;
    }

    private void TryStartSftpSplitterDrag(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            !vm.IsSftpPanelVisible ||
            !IsLeftButtonPress(e))
        {
            return;
        }

        if (_isDraggingSftpSplitter)
            return;

        _isDraggingSftpSplitter = true;
        _sftpSplitterStartX = e.GetPosition(MainContentGrid).X;
        _sftpSplitterStartWidth = Math.Max(MinimumSftpPanelWidth, vm.SftpPanelWidth.Value);

        e.Pointer.Capture(this);
        ShowSftpSplitterCursor();

        e.Handled = true;
    }

    private void OnSftpSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingSftpSplitter ||
            DataContext is not MainWindowViewModel vm ||
            !vm.IsSftpPanelVisible)
        {
            return;
        }

        var delta = e.GetPosition(MainContentGrid).X - _sftpSplitterStartX;
        var maxWidth = GetMaximumSftpPanelWidth(vm);
        var width = Math.Min(Math.Max(MinimumSftpPanelWidth, _sftpSplitterStartWidth + delta), maxWidth);
        vm.SftpPanelWidth = new Avalonia.Controls.GridLength(width);
        ShowSftpSplitterCursor();
        e.Handled = true;
    }

    private void OnSftpSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingSftpSplitter)
            return;

        EndSftpSplitterDrag(e.Pointer);
        e.Handled = true;
    }

    private void OnSftpSplitterPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(e.Source, this))
            EndSftpSplitterDrag(null);
    }

    private void EndSftpSplitterDrag(IPointer? pointer)
    {
        _isDraggingSftpSplitter = false;
        pointer?.Capture(null);
        ClearSftpSplitterCursor();

        if (DataContext is MainWindowViewModel vm)
            vm.PersistSftpPanelWidth();
    }

    private void ShowSftpSplitterCursor()
    {
        if (!_hasSftpSplitterPreviousCursor)
        {
            _sftpSplitterPreviousCursor = Cursor;
            _hasSftpSplitterPreviousCursor = true;
        }

        Cursor = new Cursor(StandardCursorType.SizeWestEast);
    }

    private void ClearSftpSplitterCursor()
    {
        if (!_hasSftpSplitterPreviousCursor)
            return;

        Cursor = _sftpSplitterPreviousCursor;
        _sftpSplitterPreviousCursor = null;
        _hasSftpSplitterPreviousCursor = false;
    }

    private bool IsLeftButtonPress(PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        return properties.IsLeftButtonPressed ||
               properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;
    }

    private void QueueApplySftpPanelWidth(MainWindowViewModel vm)
    {
        if (_isSftpPanelWidthApplyQueued)
            return;

        _isSftpPanelWidthApplyQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _isSftpPanelWidthApplyQueued = false;
            ApplySftpPanelWidth(vm);
        }, DispatcherPriority.Render);
    }

    private void ApplySftpPanelWidth(MainWindowViewModel vm)
    {
        var width = vm.IsSftpPanelVisible
            ? Math.Max(MinimumSftpPanelWidth, vm.SftpPanelWidth.Value)
            : 0;

        MainContentGrid.ColumnDefinitions[0].Width = new Avalonia.Controls.GridLength(width);
        SftpPanelHost.Width = width;
        SftpPanelHost.MinWidth = width > 0 ? MinimumSftpPanelWidth : 0;
        SftpPanelHost.MaxWidth = width;
        SftpPanelHost.InvalidateMeasure();
        MainContentGrid.InvalidateMeasure();
    }

    private double GetMaximumSftpPanelWidth(MainWindowViewModel vm)
    {
        var reservedRightWidth = vm.IsMonitorPanelVisible ? MonitorPanelWidth : 0;
        var maxWidth = MainContentGrid.Bounds.Width -
                       reservedRightWidth -
                       MinimumTerminalPanelWidth -
                       vm.SftpSplitterWidth.Value;
        return Math.Max(MinimumSftpPanelWidth, maxWidth);
    }

    private double GetMaximumAgentPanelWidth(MainWindowViewModel vm)
    {
        var reservedLeftWidth = vm.IsSftpPanelVisible
            ? vm.SftpPanelPixelWidth + vm.SftpSplitterWidth.Value
            : 0;
        var reservedRightWidth = vm.IsMonitorPanelVisible ? MonitorPanelWidth : 0;
        var maxWidth = MainContentGrid.Bounds.Width -
                       reservedLeftWidth -
                       reservedRightWidth -
                       MinimumTerminalPanelWidth -
                       vm.AgentSplitterWidth.Value;
        return Math.Max(MinimumAgentPanelWidth, Math.Min(MaximumAgentPanelWidth, maxWidth));
    }

    private async void OnQuickSessionPropertiesClick(object? sender, RoutedEventArgs e)
    {
        if (_quickSessionContext == null || DataContext is not MainWindowViewModel vm)
            return;

        await vm.EditQuickSessionCommand.ExecuteAsync(_quickSessionContext);
    }

    private void OnQuickSessionDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_quickSessionContext == null || DataContext is not MainWindowViewModel vm)
            return;

        vm.RemoveQuickSessionCommand.Execute(_quickSessionContext);
        _quickSessionContext = null;
    }

    private void OnTabMoveToNewWindowClick(object? sender, RoutedEventArgs e)
    {
        if (_tabContext == null)
            return;

        var tab = _tabContext;
        _tabContext = null;
        MoveTabToNewWindow(tab);
    }

    /// <summary>
    /// Moves a tab out of the main window and into its own top-level window.
    /// <paramref name="anchorInWindow"/> is the pointer position that triggered
    /// the move, used to place the new window under the cursor; the context menu
    /// path passes <c>null</c> and lets the window center itself.
    /// </summary>
    private void MoveTabToNewWindow(TerminalTabViewModel? tab, Point? anchorInWindow = null)
    {
        if (tab == null || DataContext is not MainWindowViewModel vm)
            return;

        var position = anchorInWindow == null ? null : ResolveDetachedWindowPosition(anchorInWindow.Value);
        if (!vm.DetachTab(tab))
            return;

        DetachedTabWindow.ShowFor(tab, vm.CloseDetachedTab, position, TryReattachDetachedTab);
    }

    /// <summary>
    /// Accepts a detached tab that was dropped on this window. Only the tab bar
    /// counts as a drop target, so dragging a detached window across the main
    /// window by accident leaves it detached.
    /// </summary>
    private bool TryReattachDetachedTab(TerminalTabViewModel tab, PixelPoint screenPoint)
    {
        if (DataContext is not MainWindowViewModel vm ||
            tab == null ||
            tab.IsDisposed ||
            !IsScreenPointOverTabBar(screenPoint))
        {
            return false;
        }

        if (!vm.AttachTab(tab))
            return false;

        Activate();
        return true;
    }

    private bool IsScreenPointOverTabBar(PixelPoint screenPoint)
    {
        if (!IsVisible || WindowState == Avalonia.Controls.WindowState.Minimized)
            return false;

        var foundStrip = false;
        foreach (var visual in this.GetVisualDescendants())
        {
            // Only the workspace tab strips count; the quick-session strip in
            // the sidebar is a session list, not a drop target for tabs.
            if (visual is not Avalonia.Controls.Control control ||
                control is not (AtomUI.Desktop.Controls.TabStrip or AtomUI.Desktop.Controls.CardTabStrip) ||
                !control.Classes.Contains("session-tab-strip") ||
                !control.IsEffectivelyVisible ||
                control.Bounds.Width <= 0 ||
                control.Bounds.Height <= 0)
            {
                continue;
            }

            foundStrip = true;
            if (IsPointInside(control, screenPoint))
                return true;
        }

        if (foundStrip)
            return false;

        // With no tabs left the header row is hidden, but the strip position is
        // still where a user expects to drop; use a band at the top instead.
        var bandTopLeft = ToScreenPoint(new Point(0, 0));
        var bandBottomRight = ToScreenPoint(new Point(Bounds.Width, 44));
        return screenPoint.X >= bandTopLeft.X &&
               screenPoint.X <= bandBottomRight.X &&
               screenPoint.Y >= bandTopLeft.Y &&
               screenPoint.Y <= bandBottomRight.Y;
    }

    /// <summary>
    /// Avalonia 12 exposes screen coordinates only through the platform
    /// implementation, which the compile-time Avalonia reference here does not
    /// surface, so the window origin plus a DIP offset scaled by
    /// <see cref="TopLevel.RenderScaling"/> reproduces the same mapping.
    /// </summary>
    private PixelPoint ToScreenPoint(Point pointInWindow)
    {
        var scaling = RenderScaling <= 0 ? 1 : RenderScaling;
        return new PixelPoint(
            Position.X + (int)Math.Round(pointInWindow.X * scaling),
            Position.Y + (int)Math.Round(pointInWindow.Y * scaling));
    }

    private bool IsPointInside(Avalonia.Controls.Control control, PixelPoint screenPoint)
    {
        var topLeft = control.TranslatePoint(new Point(0, 0), this);
        var bottomRight = control.TranslatePoint(
            new Point(control.Bounds.Width, control.Bounds.Height),
            this);
        if (topLeft == null || bottomRight == null)
            return false;

        var screenTopLeft = ToScreenPoint(topLeft.Value);
        var screenBottomRight = ToScreenPoint(bottomRight.Value);
        return screenPoint.X >= screenTopLeft.X - TabDropHitSlop &&
               screenPoint.X <= screenBottomRight.X + TabDropHitSlop &&
               screenPoint.Y >= screenTopLeft.Y - TabDropHitSlop &&
               screenPoint.Y <= screenBottomRight.Y + TabDropHitSlop;
    }

    private PixelPoint? ResolveDetachedWindowPosition(Point anchorInWindow)
    {
        var scaling = RenderScaling <= 0 ? 1 : RenderScaling;
        var screenPoint = new PixelPoint(
            Position.X + (int)Math.Round(anchorInWindow.X * scaling),
            Position.Y + (int)Math.Round(anchorInWindow.Y * scaling));
        var screen = Screens.ScreenFromPoint(screenPoint) ?? Screens.Primary;
        if (screen == null)
            return null;

        var area = screen.WorkingArea;
        var width = (int)(1000 * scaling);
        var height = (int)(700 * scaling);
        var x = Math.Clamp(screenPoint.X - width / 4, area.X, Math.Max(area.X, area.Right - width));
        var y = Math.Clamp(screenPoint.Y - 24, area.Y, Math.Max(area.Y, area.Bottom - height));
        return new PixelPoint(x, y);
    }

    private void OnTabTearOutPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ResetTabTearOut();

        if (DataContext is not MainWindowViewModel ||
            e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        var source = e.Source as Avalonia.Controls.Control;
        var tab = ResolveTabContext(source, out var anchor);
        if (tab == null || anchor == null || IsPointerFromButton(source))
            return;

        _tearOutTab = tab;
        _tearOutStrip = FindAncestorTabStrip(anchor);
        _tearOutStart = e.GetPosition(this);
    }

    private void OnTabTearOutPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tearOutTab == null)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ResetTabTearOut();
            return;
        }

        var position = e.GetPosition(this);
        var delta = position - _tearOutStart;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < TabTearOutDragThreshold || _tearOutStrip == null)
            return;

        // AtomUI already owns horizontal drags for tab reordering. Only a drag
        // that leaves the strip vertically arms the tear-out, so the two
        // gestures never fight over the same movement. Once armed the preview
        // stays latched until the pointer is released, even if the pointer
        // wanders back over the strip or the title bar.
        var stripOrigin = _tearOutStrip.TranslatePoint(new Point(0, 0), this);
        if (stripOrigin == null)
            return;

        var stripTop = stripOrigin.Value.Y;
        var stripBottom = stripTop + _tearOutStrip.Bounds.Height;
        if (position.Y > stripBottom + TabTearOutVerticalMargin ||
            position.Y < stripTop - TabTearOutVerticalMargin)
        {
            _tearOutArmed = true;
        }

        UpdateTabTearOutPreview(_tearOutArmed, e.GetPosition(this));
    }

    private void OnTabTearOutPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var tab = _tearOutTab;
        var strip = _tearOutStrip;
        var anchor = e.GetPosition(this);
        // Dropping the tab back onto its own strip cancels the tear-out, which
        // mirrors the usual "put it back" gesture. Anywhere else creates the
        // detached window.
        var shouldDetach = tab != null &&
                           _tearOutArmed &&
                           e.InitialPressMouseButton == MouseButton.Left &&
                           !IsOverTabStrip(strip, anchor);
        ResetTabTearOut();

        if (!shouldDetach || tab == null)
            return;

        // Let AtomUI finish its own pointer-release bookkeeping (it may be
        // mid-reorder-drag) before the tab is pulled out of the strip.
        Dispatcher.UIThread.Post(
            () => MoveTabToNewWindow(tab, anchor),
            DispatcherPriority.Background);
    }

    private bool IsOverTabStrip(TabStrip? strip, Point positionInWindow)
    {
        if (strip == null)
            return false;

        var origin = strip.TranslatePoint(new Point(0, 0), this);
        if (origin == null)
            return false;

        return new Rect(origin.Value, strip.Bounds.Size).Inflate(4).Contains(positionInWindow);
    }

    private void ResetTabTearOut()
    {
        _tearOutTab = null;
        _tearOutStrip = null;
        _tearOutArmed = false;
        HideTabTearOutPreview();
    }

    /// <summary>
    /// Shows a small floating card at the pointer while the dragged tab is far
    /// enough from its strip to become its own window. It is only a preview:
    /// nothing is detached until the pointer is released. The card lives in its
    /// own top-level window so it is not clipped by the main window or hidden
    /// behind its title bar.
    /// </summary>
    private void UpdateTabTearOutPreview(bool visible, Point position)
    {
        if (!visible)
        {
            HideTabTearOutPreview();
            return;
        }

        _tabDragGhost ??= new TabDragGhostWindow();
        _tabDragGhost.ShowAt(_tearOutTab?.Title, this, position);
    }

    private void HideTabTearOutPreview()
    {
        if (_tabDragGhost is { IsVisible: true })
            _tabDragGhost.Hide();
    }

    private void CloseTabDragGhost()
    {
        if (_tabDragGhost == null)
            return;

        _tabDragGhost.Close();
        _tabDragGhost = null;
    }

    private static TabStrip? FindAncestorTabStrip(Avalonia.Controls.Control anchor)
    {
        foreach (var control in EnumerateControlLineage(anchor))
        {
            if (control is TabStrip tabStrip)
                return tabStrip;
        }

        return null;
    }

    private static bool IsPointerFromButton(Avalonia.Controls.Control? source)
    {
        foreach (var control in EnumerateControlLineage(source))
        {
            if (control is Avalonia.Controls.Button)
                return true;
            if (control is TabStripItem)
                return false;
        }

        return false;
    }

    private void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        if (_tabContext == null || DataContext is not MainWindowViewModel vm)
            return;

        vm.CloseTab(_tabContext);
        _tabContext = null;
    }

    private async void OnTabDuplicateClick(object? sender, RoutedEventArgs e)
    {
        if (_tabContext == null || DataContext is not MainWindowViewModel vm)
            return;

        var tab = _tabContext;
        _tabContext = null;
        await vm.DuplicateTab(tab);
    }

    private async void OnTabPropertiesClick(object? sender, RoutedEventArgs e)
    {
        if (_tabContext == null || DataContext is not MainWindowViewModel vm)
            return;

        await vm.EditQuickSessionCommand.ExecuteAsync(_tabContext.Session);
    }

    private void OnTabAddQuickClick(object? sender, RoutedEventArgs e)
    {
        if (_tabContext == null || DataContext is not MainWindowViewModel vm)
            return;

        vm.SelectTabCommand.Execute(_tabContext);
        if (vm.AddCurrentSessionToQuickBarCommand.CanExecute(null))
            vm.AddCurrentSessionToQuickBarCommand.Execute(null);
    }

    private void StartRdpSmokeIfRequested()
    {
        if (Array.IndexOf(_startupArgs, "--rdp-smoke") < 0 ||
            DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            var host = GetStartupArg("--rdp-host") ?? "117.72.38.235";
            var port = int.TryParse(GetStartupArg("--rdp-port"), out var parsedPort) ? parsedPort : 3389;
            var username = GetStartupArg("--rdp-user") ?? "rdpuser";
            var password = GetStartupArg("--rdp-password") ?? string.Empty;
            var width = int.TryParse(GetStartupArg("--rdp-width"), out var parsedWidth) ? parsedWidth : 1280;
            var height = int.TryParse(GetStartupArg("--rdp-height"), out var parsedHeight) ? parsedHeight : 720;

            var session = new SessionInfo
            {
                Name = $"RDP Smoke {host}",
                Protocol = SessionProtocol.RDP,
                Host = host,
                Port = port,
                Username = username,
                AuthMethod = AuthMethod.Password,
                Password = PasswordEncryptionService.Encrypt(password),
                RdpWindowSize = "Custom",
                RdpDesktopWidth = width,
                RdpDesktopHeight = height
            };

            await vm.ConnectSession(session);
        });
    }

    private void ShowSessionManagerOnStartupIfNeeded()
    {
        if (Array.IndexOf(_startupArgs, "--rdp-smoke") >= 0 ||
            _startupLaunchOptions.HasCommand ||
            DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        Dispatcher.UIThread.Post(vm.ShowSessionManagerOnStartupIfEnabled, DispatcherPriority.Background);
    }

    private void HandleCommandLineLaunchIfRequested()
    {
        if (!_startupLaunchOptions.HasCommand ||
            Array.IndexOf(_startupArgs, "--rdp-smoke") >= 0 ||
            DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            await vm.ExecuteCommandLineLaunchAsync(_startupLaunchOptions);
        }, DispatcherPriority.Background);
    }

    private void StartCommandLineHandoffServer()
    {
        if (_commandLineHandoffServer != null)
            return;

        _commandLineHandoffServer = CommandLineHandoffService.StartServer(HandleCommandLineHandoffAsync);
    }

    private Task HandleCommandLineHandoffAsync(string[] args)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (WindowState == Avalonia.Controls.WindowState.Minimized)
                    WindowState = Avalonia.Controls.WindowState.Normal;

                Activate();

                if (DataContext is MainWindowViewModel vm)
                    await vm.ExecuteCommandLineLaunchAsync(CommandLineLaunchOptions.Parse(args));
            }
            catch
            {
                // Keep the command receiver alive even if a malformed handoff arrives.
            }
        }, DispatcherPriority.Background);

        return Task.CompletedTask;
    }

    private string? GetStartupArg(string name)
    {
        for (var index = 0; index < _startupArgs.Length - 1; index++)
        {
            if (string.Equals(_startupArgs[index], name, StringComparison.OrdinalIgnoreCase))
                return _startupArgs[index + 1];
        }

        return null;
    }

    private void OnMainWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty)
            return;

        // A path we do not control (window manager shortcut, native caption
        // button) may end the immersive mode; never leave the window without a
        // caption because the chrome was hidden for full screen.
        if (!_restoreFullScreenAfterMinimize &&
            WindowState != Avalonia.Controls.WindowState.FullScreen &&
            WindowState != Avalonia.Controls.WindowState.Minimized &&
            !_keepTaskbarFullScreen.IsActive)
        {
            _keepTaskbarFullScreen.RestoreDecorations();
            _keepTaskbarFullScreen.RestoreTitleBar();
        }

        if (!_restoreFullScreenAfterMinimize)
            return;

        if (WindowState == Avalonia.Controls.WindowState.Minimized)
            return;

        _restoreFullScreenAfterMinimize = false;
        if (DataContext is MainWindowViewModel { IsTerminalFullScreen: true } vm)
            ApplyTerminalFullScreenState(vm);
    }

    private void OnFullScreenBarMinimize(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel { IsTerminalFullScreen: true })
        {
            // Minimizing leaves the presentation state, so remember to re-apply
            // it when the window comes back from the taskbar.
            _restoreFullScreenAfterMinimize = true;
            _keepTaskbarFullScreen.Abandon();
        }

        WindowState = Avalonia.Controls.WindowState.Minimized;
    }

    private void OnFullScreenBarRestore(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ExitTerminalFullScreen();
    }

    private void OnFullScreenBarClose(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel { SelectedTab: { } tab } vm)
            vm.CloseTab(tab);
    }

    private void OnFullScreenBarPinnedChanged(object? sender, EventArgs e)
    {
        if (SessionTreeViewModel.SharedSettings is not { } settings)
            return;

        settings.FullScreenConnectionBarPinned = FullScreenConnectionBar.IsPinned;
        SessionTreeViewModel.PersistSharedSettings();
    }

    /// <summary>
    /// A tab-hosted RDP session drives a monitor layout just like a detached
    /// window: the viewport stands in for the host monitor and each extra screen
    /// opens its own window, so the connection bar needs the same entry point.
    /// The button stays hidden for everything that is not an RDP tab, and stays
    /// disabled when the native bridge predates the monitor-layout API so it can
    /// never open a popup whose layout the bridge cannot apply.
    /// </summary>
    private void UpdateConnectionBarMultiMonitorState(MainWindowViewModel vm)
    {
        var rdpView = FindRdpViewFor(vm.SelectedTab);
        FullScreenConnectionBar.ShowMultiMonitorButton = vm.SelectedTab?.IsRdpSession == true;
        FullScreenConnectionBar.IsMultiMonitorEnabled =
            rdpView?.DataContext is RdpViewModel { SupportsMultiMonitor: true, IsConnected: true };
        FullScreenConnectionBar.IsMultiMonitorActive = rdpView?.IsMultiMonitorActive == true;
        AttachConnectionBarMonitorSource(vm.SelectedTab);
    }

    /// <summary>
    /// Follows the selected tab's session so the bar's monitor state is not
    /// frozen at whatever it was when the tab was selected. The bridge reports
    /// its layout capability only once a connect attempt starts, and the view
    /// that renders the tab may not exist yet at selection time, so the flags
    /// have to be recomputed from the session's own notifications.
    /// </summary>
    private void AttachConnectionBarMonitorSource(TerminalTabViewModel? tab)
    {
        if (ReferenceEquals(_connectionBarMonitorTab, tab))
            return;

        if (_connectionBarMonitorTab?.Rdp is { } previous)
            previous.PropertyChanged -= OnConnectionBarSessionChanged;

        _connectionBarMonitorTab = tab;

        if (tab?.Rdp is { } rdp)
            rdp.PropertyChanged += OnConnectionBarSessionChanged;
    }

    private void OnConnectionBarSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(RdpViewModel.IsConnected) or
                                   nameof(RdpViewModel.SupportsMultiMonitor) or
                                   nameof(RdpViewModel.IsMultiMonitorActive)))
            return;

        if (DataContext is MainWindowViewModel vm)
            UpdateConnectionBarMultiMonitorState(vm);
    }

    /// <summary>
    /// Locates the <see cref="RdpView"/> that currently renders
    /// <paramref name="tab"/>. The XAML declares one instance per layout, so the
    /// visible one wins and an off-screen sibling is only a fallback.
    /// </summary>
    private RdpView? FindRdpViewFor(TerminalTabViewModel? tab)
    {
        if (tab?.Rdp is not { } rdp)
            return null;

        RdpView? fallback = null;
        foreach (var view in this.GetVisualDescendants().OfType<RdpView>())
        {
            if (!ReferenceEquals(view.DataContext, rdp))
                continue;
            if (view.IsEffectivelyVisible)
                return view;
            fallback ??= view;
        }

        return fallback;
    }

    private void OnFullScreenBarMultiMonitorRequested(AvaloniaControl anchor)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        FindRdpViewFor(vm.SelectedTab)?.ToggleMultiMonitorPopup(
            anchor,
            Avalonia.Controls.PlacementMode.Bottom,
            FullScreenConnectionBar.SetExternalPopupOpen);
    }

    /// <summary>
    /// Applies the full screen presentation requested by the view model. The
    /// immersive mode uses the full screen window state and covers the taskbar;
    /// the taskbar-visible mode maximizes the window so the working area is
    /// filled while the taskbar stays usable. Both modes hide the drawn title
    /// bar: <see cref="Avalonia.Controls.WindowState.FullScreen"/> removes only
    /// the native frame, while AtomUI renders its title bar inside the client
    /// area, which would otherwise leave the caption row on screen.
    /// </summary>
    private void ApplyTerminalFullScreenState(MainWindowViewModel vm)
    {
        if (!vm.IsTerminalFullScreen)
        {
            _keepTaskbarFullScreen.Leave();
            if (WindowState == Avalonia.Controls.WindowState.FullScreen)
            {
                WindowState = _windowStateBeforeFullScreen == Avalonia.Controls.WindowState.FullScreen
                    ? Avalonia.Controls.WindowState.Normal
                    : _windowStateBeforeFullScreen;
            }

            // Restore the chrome only after the window is back to its normal
            // state so it never flashes on exit.
            _keepTaskbarFullScreen.RestoreDecorations();
            _keepTaskbarFullScreen.RestoreTitleBar();

            WriteToolbarDiagnostics(
                $"Full screen left: state={WindowState} decorations={WindowDecorations} " +
                $"titleBar={IsTitleBarVisible} " +
                $"fullScreenTitleBarMode={FullScreenTitleBarMode}");

            return;
        }

        if (vm.IsTerminalFullScreenKeepTaskbar)
        {
            _keepTaskbarFullScreen.Enter();
        }
        else
        {
            _keepTaskbarFullScreen.Leave();
            // AtomUI 6.3.0's built-in full-screen bar is disabled via
            // FullScreenTitleBarMode="Hidden" in XAML; drop drawn decorations
            // and the title bar for an edge-to-edge session.
            _keepTaskbarFullScreen.HideDecorations();
            _keepTaskbarFullScreen.HideTitleBar();
            if (WindowState != Avalonia.Controls.WindowState.FullScreen)
            {
                _windowStateBeforeFullScreen = WindowState;
                WindowState = Avalonia.Controls.WindowState.FullScreen;
            }

            // Re-assert after the state change in case the platform restores decorations.
            _keepTaskbarFullScreen.HideDecorations();
        }

        WriteToolbarDiagnostics(
            $"Full screen applied: keepTaskbar={vm.IsTerminalFullScreenKeepTaskbar} state={WindowState} " +
            $"decorations={WindowDecorations} titleBar={IsTitleBarVisible} " +
            $"fullScreenTitleBarMode={FullScreenTitleBarMode}");
    }

}
