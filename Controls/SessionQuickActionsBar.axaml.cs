using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AtomUI.Desktop.Controls;
using CxShell.Services;
using CxShell.ViewModels;
using AtomContextMenu = AtomUI.Desktop.Controls.ContextMenu;
using AtomMenuItem = AtomUI.Desktop.Controls.MenuItem;
using AtomMenuSeparator = AtomUI.Desktop.Controls.MenuSeparator;

namespace CxShell.Controls;

/// <summary>Which session the bar is attached to; the two protocols expose different actions.</summary>
public enum SessionQuickActionTarget
{
    Rdp,
    Vnc
}

/// <summary>
/// Floating shortcut bar drawn over the remote frame. It replaces the buttons
/// that used to sit in the status bar under the session: the status bar is
/// hidden by default so the session keeps the full height, and these actions
/// have to stay reachable in windowed, full screen and detached windows alike.
/// Closing the bar hides it for every session and can be undone in settings.
/// </summary>
public partial class SessionQuickActionsBar : UserControl
{
    public static readonly StyledProperty<SessionQuickActionTarget> TargetProperty =
        AvaloniaProperty.Register<SessionQuickActionsBar, SessionQuickActionTarget>(nameof(Target));

    private RdpViewModel? _rdp;
    private VncViewModel? _vnc;
    private INotifyPropertyChanged? _session;
    private bool _preferencesAttached;
    private bool _sessionAttached;

    private Control? _host;
    private IDisposable? _hostBoundsSubscription;
    private IDisposable? _pillBoundsSubscription;
    private bool _pressed;
    private bool _dragging;
    private Point _pressPoint;
    private Point _dragOrigin;
    private double _left;
    private double _top;
    private double _availableWidth;
    private double _availableHeight;

    public SessionQuickActionsBar()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            AttachHost();
            AttachPreferences();
            ApplyPosition();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            DetachHost();
            DetachPreferences();
        };

        // The drag is picked up in the tunnel phase so a press that starts on a
        // button still reaches that button; only a real move (past the
        // threshold) turns into a drag and steals the capture away from it.
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Tunnel);
    }

    private const double DragThreshold = 4;
    private const double DefaultRightMargin = 16;
    private const double DefaultBottomMargin = 48;

    public SessionQuickActionTarget Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TargetProperty)
        {
            ApplyTarget();
            UpdateVisibility();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        DetachSession();
        _rdp = DataContext as RdpViewModel;
        _vnc = DataContext as VncViewModel;
        _session = _rdp as INotifyPropertyChanged ?? _vnc as INotifyPropertyChanged;
        AttachSession();
        ApplyTarget();
        UpdateVisibility();
    }

    private void ApplyTarget()
    {
        if (RdpActions == null || VncActions == null)
            return;

        RdpActions.IsVisible = Target == SessionQuickActionTarget.Rdp;
        VncActions.IsVisible = Target == SessionQuickActionTarget.Vnc;
    }

    private void AttachPreferences()
    {
        if (_preferencesAttached)
            return;

        _preferencesAttached = true;
        SessionOverlayPreferences.Changed += OnPreferencesChanged;
    }

    private void DetachPreferences()
    {
        if (!_preferencesAttached)
            return;

        _preferencesAttached = false;
        SessionOverlayPreferences.Changed -= OnPreferencesChanged;
    }

    private void AttachSession()
    {
        if (_sessionAttached || _session == null)
            return;

        _sessionAttached = true;
        _session.PropertyChanged += OnSessionPropertyChanged;
    }

    private void DetachSession()
    {
        if (!_sessionAttached || _session == null)
        {
            _sessionAttached = false;
            return;
        }

        _sessionAttached = false;
        _session.PropertyChanged -= OnSessionPropertyChanged;
    }

    private void OnPreferencesChanged(object? sender, EventArgs e)
    {
        UpdateVisibility();
        Dispatcher.UIThread.Post(ApplyPosition, DispatcherPriority.Loaded);
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Connection state arrives from a bridge thread; the bar is a UI object.
        if (e.PropertyName == nameof(RdpViewModel.IsConnected))
            Dispatcher.UIThread.Post(UpdateVisibility, DispatcherPriority.Background);
    }

    private void UpdateVisibility()
    {
        if (QuickActionPill == null)
            return;

        var connected = Target == SessionQuickActionTarget.Rdp
            ? _rdp?.IsConnected == true
            : _vnc?.IsConnected == true;

        QuickActionPill.IsVisible = connected && SessionOverlayPreferences.ShowQuickActions;
        RdpActions.IsEnabled = connected;
        VncActions.IsEnabled = connected;
    }

    /// <summary>
    /// Watches the container the bar floats in. The bar is positioned with a
    /// margin, so the free area has to be measured again whenever the session
    /// view is resized (window resize, side panels opening, full screen).
    /// </summary>
    private void AttachHost()
    {
        var host = Parent as Control;
        if (host == null || ReferenceEquals(host, _host))
            return;

        DetachHost();
        _host = host;
        _hostBoundsSubscription = host.GetObservable(BoundsProperty)
            .Subscribe(_ => ApplyPosition());

        if (QuickActionPill != null)
        {
            // The pill changes width when the protocol switches or the buttons
            // are shown again; the clamp has to follow that.
            _pillBoundsSubscription = QuickActionPill.GetObservable(BoundsProperty)
                .Subscribe(_ => Dispatcher.UIThread.Post(ApplyPosition, DispatcherPriority.Loaded));
        }
    }

    private void DetachHost()
    {
        _hostBoundsSubscription?.Dispose();
        _hostBoundsSubscription = null;
        _pillBoundsSubscription?.Dispose();
        _pillBoundsSubscription = null;
        _host = null;
    }

    /// <summary>
    /// Applies the remembered spot. Without one the bar keeps the original
    /// bottom right anchor, which lets the layout follow the window for free.
    /// </summary>
    private void ApplyPosition()
    {
        if (QuickActionPill == null || _dragging)
            return;

        var x = SessionOverlayPreferences.QuickActionsX;
        var y = SessionOverlayPreferences.QuickActionsY;
        if (x is null || y is null)
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
            Margin = new Thickness(0, 0, DefaultRightMargin, DefaultBottomMargin);
            return;
        }

        var available = MeasureFreeArea();
        if (available == null)
            return;

        Place(x.Value * available.Value.Width, y.Value * available.Value.Height, available.Value);
    }

    private Size? MeasureFreeArea()
    {
        if (_host == null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return null;

        return new Size(
            Math.Max(0, _host.Bounds.Width - Bounds.Width),
            Math.Max(0, _host.Bounds.Height - Bounds.Height));
    }

    private void Place(double left, double top, Size available)
    {
        _availableWidth = available.Width;
        _availableHeight = available.Height;
        _left = Math.Clamp(left, 0, available.Width);
        _top = Math.Clamp(top, 0, available.Height);

        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        Margin = new Thickness(_left, _top, 0, 0);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (QuickActionPill is not { IsVisible: true })
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _pressed = true;
        _dragging = false;
        _pressPoint = _host != null ? e.GetPosition(_host) : e.GetPosition(this);
        _dragOrigin = GetOrigin();
    }

    private Point GetOrigin()
    {
        if (_host == null)
            return default;

        if (HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Left &&
            VerticalAlignment == Avalonia.Layout.VerticalAlignment.Top)
            return new Point(Margin.Left, Margin.Top);

        return this.TranslatePoint(default, _host) ?? default;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed || _host == null)
            return;

        var current = e.GetPosition(_host);
        var dx = current.X - _pressPoint.X;
        var dy = current.Y - _pressPoint.Y;
        if (!_dragging)
        {
            if (Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold)
                return;

            _dragging = true;
            // A press that started on one of the buttons belongs to that
            // button until it turns into a move; taking the capture cancels
            // the pending click, which is what a drag should do.
            e.Pointer.Capture(this);
        }

        var available = MeasureFreeArea();
        if (available == null)
            return;

        Place(_dragOrigin.X + dx, _dragOrigin.Y + dy, available.Value);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging)
        {
            SessionOverlayPreferences.MoveQuickActions(
                _availableWidth > 1 ? _left / _availableWidth : null,
                _availableHeight > 1 ? _top / _availableHeight : null);
            e.Pointer.Capture(null);
        }

        _pressed = false;
        _dragging = false;
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _pressed = false;
        _dragging = false;
    }

    /// <summary>
    /// Double clicking the pill (but not one of its buttons) puts it back in
    /// the corner, so a bar dragged somewhere awkward is easy to recover.
    /// </summary>
    private void OnPillDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source &&
            source.FindAncestorOfType<Avalonia.Controls.Button>(includeSelf: true) != null)
        {
            return;
        }

        SessionOverlayPreferences.ResetQuickActionsPosition();
    }

    private async void OnVncClipboardClick(object? sender, RoutedEventArgs e)
    {
        if (_vnc is not { IsConnected: true })
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        var text = clipboard == null ? null : await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
            await _vnc.SendClipboardTextAndPasteAsync(text);
    }

    private async void OnVncCtrlAltDelClick(object? sender, RoutedEventArgs e)
    {
        if (_vnc is { IsConnected: true })
            await _vnc.SendCtrlAltDeleteAsync();
    }

    private async void OnRdpCtrlAltDelClick(object? sender, RoutedEventArgs e)
    {
        if (_rdp is { IsConnected: true })
            await _rdp.SendCtrlAltDeleteAsync();
    }

    private void OnKeyboardCombinationsClick(object? sender, RoutedEventArgs e)
    {
        if (_rdp is not { IsConnected: true } rdp || sender is not Control anchor)
            return;

        var menu = new AtomContextMenu
        {
            Placement = PlacementMode.Pointer,
            PlacementTarget = anchor
        };

        AddItem(menu, Text("Rdp.Keyboard.AltTab"), () => _ = rdp.SendAltTabAsync());
        AddItem(menu, Text("Rdp.Keyboard.Windows"), () => _ = rdp.SendWindowsKeyAsync());
        AddItem(menu, Text("Rdp.Keyboard.CtrlEsc"), () => _ = rdp.SendCtrlEscapeAsync());
        AddItem(menu, Text("Rdp.Keyboard.AltF4"), () => _ = rdp.SendAltF4Async());
        menu.Items.Add(new AtomMenuSeparator());
        AddItem(menu, Text("Rdp.Keyboard.TaskManager"), () => _ = rdp.SendTaskManagerAsync());
        AddItem(menu, Text("Rdp.Keyboard.PrintScreen"), () => _ = rdp.SendPrintScreenAsync());
        menu.Open(anchor);
    }

    private static void AddItem(AtomContextMenu menu, string header, Action action)
    {
        var item = new AtomMenuItem { Header = header };
        item.Click += (_, _) =>
        {
            menu.Close();
            action();
        };
        menu.Items.Add(item);
    }

    private static string Text(string key) => LocalizationService.Shared.Text(key);

    private void OnDismissClick(object? sender, RoutedEventArgs e) => SessionOverlayPreferences.Dismiss();
}
