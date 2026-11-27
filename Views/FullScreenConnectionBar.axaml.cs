using System;
using AtomUI.Data;
using AtomUI.Theme.Resources;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CxShell.Services;
using CxShell.ViewModels;
using AtomToolTip = AtomUI.Desktop.Controls.ToolTip;

namespace CxShell.Views;

/// <summary>
/// Floating connection bar for full screen, modelled on the mstsc bar: pin,
/// link quality, endpoint, then minimize / leave full screen / close session.
/// <para>
/// The bar is laid out at the top centre at all times but stays transparent and
/// hit-test invisible. It shows itself for three seconds on entering full
/// screen, and after that resting the pointer in the top strip for half a
/// second reveals it - every reveal is followed by the same three second
/// auto-hide. The pin keeps it on screen instead.
/// </para>
/// </summary>
public partial class FullScreenConnectionBar : UserControl
{
    /// <summary>How long the pointer has to rest in the top strip.</summary>
    private static readonly TimeSpan RevealDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the bar stays up after it was revealed.</summary>
    private static readonly TimeSpan AutoHideDelay = TimeSpan.FromSeconds(3);

    private const double HotZoneHeight = 8;
    private const double HotZoneHorizontalSlop = 40;
    private const double BarHitPadding = 10;
    private const double FallbackBarWidth = 420;

    public static readonly StyledProperty<bool> IsFullScreenActiveProperty =
        AvaloniaProperty.Register<FullScreenConnectionBar, bool>(nameof(IsFullScreenActive));

    public static readonly StyledProperty<bool> IsPinnedProperty =
        AvaloniaProperty.Register<FullScreenConnectionBar, bool>(nameof(IsPinned));

    public static readonly StyledProperty<string?> EndpointProperty =
        AvaloniaProperty.Register<FullScreenConnectionBar, string?>(nameof(Endpoint));

    public static readonly StyledProperty<int> QualityLevelProperty =
        AvaloniaProperty.Register<FullScreenConnectionBar, int>(nameof(QualityLevel));

    public static readonly StyledProperty<bool> IsQualityVisibleProperty =
        AvaloniaProperty.Register<FullScreenConnectionBar, bool>(nameof(IsQualityVisible), true);

    private readonly DispatcherTimer _revealTimer;
    private readonly DispatcherTimer _hideTimer;
    private TopLevel? _topLevel;
    private bool _isShown;
    private bool _pointerInsideBar;
    private bool _externalPopupOpen;

    public FullScreenConnectionBar()
    {
        InitializeComponent();

        _revealTimer = new DispatcherTimer { Interval = RevealDelay };
        _revealTimer.Tick += OnRevealTimerTick;
        _hideTimer = new DispatcherTimer { Interval = AutoHideDelay };
        _hideTimer.Tick += OnHideTimerTick;

        ApplyLocalizedText();
        UpdatePinVisual();
        UpdateSignalBars();
    }

    /// <summary>Raised when the window should be minimized.</summary>
    public event EventHandler? MinimizeRequested;

    /// <summary>Raised when the window should leave full screen.</summary>
    public event EventHandler? RestoreRequested;

    /// <summary>Raised when the current session should be closed.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when the user toggles the pin, so the host can persist it.</summary>
    public event EventHandler? PinnedChanged;

    public event Action<RdpWorkspaceTabItem>? TabSelected;
    public event Action<RdpWorkspaceTabItem>? TabCloseRequested;
    public event Action<Control>? MultiMonitorRequested;
    public event Action? TaskbarModeToggleRequested;
    public event Action? ToggleFullScreenRequested;
    public event Action? InteractionCompleted;
#pragma warning disable CS0067
    public event Action<PointerPressedEventArgs>? DragRestoreRequested;
#pragma warning restore CS0067

    public bool ShowTabSwitcher
    {
        get => TabSwitcherScroll.IsVisible;
        set
        {
            TabSwitcherScroll.IsVisible = value;
            EndpointText.IsVisible = !value;
        }
    }

    public System.Collections.IEnumerable? TabItemsSource
    {
        get => FloatingTabItems.ItemsSource;
        set => FloatingTabItems.ItemsSource = value;
    }

    public bool ShowMultiMonitorButton
    {
        get => MultiMonitorButton.IsVisible;
        set
        {
            MultiMonitorButton.IsVisible = value;
            UpdateExtraActionsDivider();
        }
    }

    public bool IsMultiMonitorEnabled
    {
        get => MultiMonitorButton.IsEnabled;
        set => MultiMonitorButton.IsEnabled = value;
    }

    private bool _isMultiMonitorActive;

    /// <summary>
    /// True while the session really drives a custom monitor layout. The label
    /// picks up the accent colour in that case, so the bar shows at a glance
    /// whether the remote desktop still spans a single screen.
    /// </summary>
    public bool IsMultiMonitorActive
    {
        get => _isMultiMonitorActive;
        set
        {
            if (_isMultiMonitorActive == value)
                return;

            _isMultiMonitorActive = value;
            UpdateMultiMonitorVisual();
        }
    }

    public bool ShowTaskbarModeButton
    {
        get => TaskbarModeButton.IsVisible;
        set
        {
            TaskbarModeButton.IsVisible = value;
            UpdateExtraActionsDivider();
        }
    }

    public string TaskbarModeLabel
    {
        get => TaskbarModeButton.Content?.ToString() ?? "保留任务栏";
        set => TaskbarModeButton.Content = value;
    }

    public bool ShowMinimizeButton
    {
        get => MinimizeButton.IsVisible;
        set => MinimizeButton.IsVisible = value;
    }

    public bool ShowFullScreenButton
    {
        get => FullScreenButton.IsVisible;
        set => FullScreenButton.IsVisible = value;
    }

    public string? RestoreToolTip
    {
        set => AtomToolTip.SetTip(RestoreButton, value);
    }

    public string? FullScreenToolTip
    {
        set => AtomToolTip.SetTip(FullScreenButton, value);
    }

    public string? CloseToolTip
    {
        set => AtomToolTip.SetTip(CloseButton, value);
    }

    public void SetExternalPopupOpen(bool isOpen)
    {
        _externalPopupOpen = isOpen;
        if (isOpen)
        {
            _hideTimer.Stop();
            if (IsFullScreenActive && !_isShown)
            {
                _isShown = true;
                IsHitTestVisible = true;
                Opacity = 1;
            }
        }
        else if (IsFullScreenActive && !IsPinned)
        {
            ScheduleHide();
        }
    }

    public void SetImmersiveActive(bool active) => IsFullScreenActive = active;

    public void StopTimer()
    {
        _revealTimer.Stop();
        _hideTimer.Stop();
    }

    private void UpdateExtraActionsDivider()
    {
        ExtraActionsDivider.IsVisible = MultiMonitorButton.IsVisible || TaskbarModeButton.IsVisible;
    }

    /// <summary>
    /// Accents the multi-monitor entry while a layout is in force. Clearing the
    /// local value instead of assigning a fallback colour keeps the button on
    /// its themed foreground once the session is back to a single screen.
    /// </summary>
    private void UpdateMultiMonitorVisual()
    {
        if (_isMultiMonitorActive)
        {
            MultiMonitorButton.Foreground = new SolidColorBrush(
                ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorPrimary, Color.Parse("#1677FF")));
            return;
        }

        MultiMonitorButton.ClearValue(TemplatedControl.ForegroundProperty);
    }

    private void OnTabItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: RdpWorkspaceTabItem item })
        {
            TabSelected?.Invoke(item);
            InteractionCompleted?.Invoke();
            e.Handled = true;
        }
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RdpWorkspaceTabItem item })
        {
            TabCloseRequested?.Invoke(item);
            InteractionCompleted?.Invoke();
        }
    }

    private void OnMultiMonitorClick(object? sender, RoutedEventArgs e)
    {
        MultiMonitorRequested?.Invoke(MultiMonitorButton);
    }

    private void OnTaskbarModeClick(object? sender, RoutedEventArgs e)
    {
        TaskbarModeToggleRequested?.Invoke();
        InteractionCompleted?.Invoke();
    }

    private void OnFullScreenClick(object? sender, RoutedEventArgs e)
    {
        ToggleFullScreenRequested?.Invoke();
        InteractionCompleted?.Invoke();
    }

    /// <summary>True while the host window is in either full screen mode.</summary>
    public bool IsFullScreenActive
    {
        get => GetValue(IsFullScreenActiveProperty);
        set => SetValue(IsFullScreenActiveProperty, value);
    }

    /// <summary>When true the bar stays visible instead of auto hiding.</summary>
    public bool IsPinned
    {
        get => GetValue(IsPinnedProperty);
        set => SetValue(IsPinnedProperty, value);
    }

    /// <summary>Endpoint text shown in the middle of the bar.</summary>
    public string? Endpoint
    {
        get => GetValue(EndpointProperty);
        set => SetValue(EndpointProperty, value);
    }

    /// <summary>Link quality, 0 (unknown) to 4 (excellent).</summary>
    public int QualityLevel
    {
        get => GetValue(QualityLevelProperty);
        set => SetValue(QualityLevelProperty, value);
    }

    /// <summary>Hides the quality bars for sessions that have no RDP transport.</summary>
    public bool IsQualityVisible
    {
        get => GetValue(IsQualityVisibleProperty);
        set => SetValue(IsQualityVisibleProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel != null)
        {
            _topLevel.AddHandler(PointerMovedEvent, OnTopLevelPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            _topLevel.AddHandler(PointerExitedEvent, OnTopLevelPointerExited, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        ApplyLocalizedText();
        UpdateSignalBars();

        if (IsFullScreenActive)
            Reveal();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;

        if (_topLevel != null)
        {
            _topLevel.RemoveHandler(PointerMovedEvent, OnTopLevelPointerMoved);
            _topLevel.RemoveHandler(PointerExitedEvent, OnTopLevelPointerExited);
        }

        _topLevel = null;
        _revealTimer.Stop();
        _hideTimer.Stop();
        HideBar();

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsFullScreenActiveProperty)
        {
            if (IsFullScreenActive)
            {
                // Entering full screen shows the bar once so the controls are
                // discoverable, then it fades out like any other reveal.
                Reveal();
                return;
            }

            _revealTimer.Stop();
            _hideTimer.Stop();
            HideBar();
            return;
        }

        if (change.Property == IsPinnedProperty)
        {
            UpdatePinVisual();
            if (IsPinned)
                Reveal();
            else if (IsFullScreenActive)
                ScheduleHide();

            ApplyLocalizedText();
            PinnedChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (change.Property == EndpointProperty)
        {
            EndpointText.Text = Endpoint ?? string.Empty;
            return;
        }

        if (change.Property == QualityLevelProperty)
        {
            UpdateSignalBars();
            return;
        }

        if (change.Property == IsQualityVisibleProperty)
        {
            SignalPanel.IsVisible = IsQualityVisible;
        }
    }

    private void OnRevealTimerTick(object? sender, EventArgs e)
    {
        _revealTimer.Stop();
        if (IsFullScreenActive)
            Reveal();
    }

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();

        // The timer only measures how long the bar stays up, so a pointer that
        // happens to rest on it when the time is up would otherwise lose the
        // click. Wait for it to leave instead of pulling the bar away.
        if (!IsPinned && _pointerInsideBar && _isShown)
        {
            _hideTimer.Start();
            return;
        }

        if (!IsPinned)
            HideBar();
    }

    private void OnTopLevelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!IsFullScreenActive || _topLevel == null)
            return;

        var point = e.GetPosition(_topLevel);

        if (_isShown)
        {
            _pointerInsideBar = IsInsideBar(point) || IsInHotZone(point);
            return;
        }

        _pointerInsideBar = false;

        if (IsInHotZone(point))
        {
            if (!_revealTimer.IsEnabled)
                _revealTimer.Start();
        }
        else
        {
            _revealTimer.Stop();
        }
    }

    private void OnTopLevelPointerExited(object? sender, PointerEventArgs e)
    {
        _revealTimer.Stop();
        _pointerInsideBar = false;
    }

    private void OnPinClick(object? sender, RoutedEventArgs e)
    {
        IsPinned = !IsPinned;
        e.Handled = true;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        MinimizeRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnRestoreClick(object? sender, RoutedEventArgs e)
    {
        RestoreRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void Reveal()
    {
        if (!IsFullScreenActive)
            return;

        if (!_isShown)
        {
            _isShown = true;
            IsHitTestVisible = true;
            Opacity = 1;
        }

        ScheduleHide();
    }

    private void HideBar()
    {
        _hideTimer.Stop();
        _pointerInsideBar = false;
        _isShown = false;
        IsHitTestVisible = false;
        Opacity = 0;
    }

    /// <summary>Starts the three second countdown that takes the bar away.</summary>
    private void ScheduleHide()
    {
        _hideTimer.Stop();
        if (_externalPopupOpen)
            return;

        if (IsPinned)
            return;

        _hideTimer.Start();
    }

    private bool IsInsideBar(Point point)
    {
        if (_topLevel == null || !_isShown)
            return false;

        var origin = this.TranslatePoint(new Point(0, 0), _topLevel);
        if (origin is not { } topLeft)
            return false;

        return new Rect(topLeft, Bounds.Size).Inflate(BarHitPadding).Contains(point);
    }

    private bool IsInHotZone(Point point)
    {
        if (_topLevel == null)
            return false;

        var width = _topLevel.Bounds.Width;
        if (width <= 0)
            return false;

        var barWidth = Bounds.Width > 1 ? Bounds.Width : FallbackBarWidth;
        var center = width / 2;
        var left = center - barWidth / 2 - HotZoneHorizontalSlop;
        var right = center + barWidth / 2 + HotZoneHorizontalSlop;

        return point.Y >= 0 && point.Y <= HotZoneHeight && point.X >= left && point.X <= right;
    }

    private void UpdatePinVisual()
    {
        // Pinned shows the filled pushpin, auto-hiding shows the outline one.
        PinButton.IsVisible = !IsPinned;
        PinnedButton.IsVisible = IsPinned;
    }

    private void UpdateSignalBars()
    {
        var level = Math.Clamp(QualityLevel, 0, 4);
        var bars = new[] { SignalBar1, SignalBar2, SignalBar3, SignalBar4 };
        for (var i = 0; i < bars.Length; i++)
            bars[i].Opacity = i < level ? 1 : 0.2;

        UpdateQualityTip(level);
    }

    private void UpdateQualityTip(int level)
    {
        var quality = level switch
        {
            >= 4 => Text("FullScreenBar.QualityGood"),
            3 => Text("FullScreenBar.QualityFair"),
            >= 1 => Text("FullScreenBar.QualityPoor"),
            _ => Text("FullScreenBar.QualityUnknown")
        };

        AtomToolTip.SetTip(SignalPanel, $"{Text("FullScreenBar.Quality")}: {quality}");
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ApplyLocalizedText();
        UpdateSignalBars();
    }

    private void ApplyLocalizedText()
    {
        var pinTip = Text(IsPinned ? "FullScreenBar.Unpin" : "FullScreenBar.Pin");
        AtomToolTip.SetTip(PinButton, pinTip);
        AtomToolTip.SetTip(PinnedButton, pinTip);
        AtomToolTip.SetTip(MultiMonitorButton, Text("Rdp.MultiMonitorTip"));
        MultiMonitorButton.Content = Text("Rdp.MultiMonitor");
        AtomToolTip.SetTip(MinimizeButton, Text("FullScreenBar.Minimize"));
        AtomToolTip.SetTip(RestoreButton, Text("FullScreenBar.Restore"));
        AtomToolTip.SetTip(FullScreenButton, Text("FullScreenBar.ToggleFullScreen"));
        AtomToolTip.SetTip(CloseButton, Text("FullScreenBar.Close"));
    }

    private static string Text(string key) => LocalizationService.Shared.Text(key);
}
