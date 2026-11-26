using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CxShell.Services;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class RdpView : UserControl
{
    private ushort _buttonFlags;
    private RdpViewModel? _boundVm;
    private bool _isAttached;
    private bool _controlDown;
    private bool _shiftDown;
    private bool _altDown;
    private bool _ctrlAltEndActive;
    private bool _suppressNextPasteKeyUp;
    private bool _applyingRemoteClipboardText;
    private bool _hasKeyboardFocus;
    private string? _lastSyncedClipboardText;
    private string? _lastSyncedClipboardImageHash;
    private string? _lastSyncedClipboardFilesKey;
    private uint _lastSyncedLocalClipboardSeq;
    private uint _lastAppliedRemoteClipboardSeq;
    private WindowsClipboardListener? _clipboardListener;
    private DispatcherTimer? _clipboardPollTimer;
    private WindowsRdpShortcutHook? _systemShortcutHook;
    private IntPtr _savedImeHwnd;
    private IntPtr _savedImeContext;
    private bool _imeDisabled;
    private Cursor? _customRemoteCursor;
    private Avalonia.Media.Imaging.WriteableBitmap? _customRemoteCursorBitmap;
    private TopLevel? _subscribedTopLevel;
    private static readonly IBrush PopupButtonNormalBrush = new SolidColorBrush(Color.Parse("#22FFFFFF"));
    private static readonly IBrush PopupButtonActiveBrush = new SolidColorBrush(Color.Parse("#4464D8FF"));
    private readonly System.Collections.Generic.HashSet<int> _selectedMonitorIndices = new();
    private readonly System.Collections.Generic.Dictionary<int, RdpMonitorWindow> _secondaryMonitorWindows = new();
    private readonly System.Collections.Generic.List<int> _virtualMonitorIds = new();
    private int _nextVirtualMonitorId = 100;
    private int? _preferredPrimaryMonitorIndex;
    private int _ultrawideMode; // 0=standard, 1=local ultrawide -> remote dual, 2=local dual PBP -> remote single ultrawide
    private Rect? _primarySliceRect;
    private ushort _lastRemotePointerX;
    private ushort _lastRemotePointerY;
    private double _pendingWheelY;
    private Action<bool>? _onMultiMonitorPopupOpenChanged;
    private Control? _lastMultiMonitorAnchor;
    private DateTime _multiMonitorPopupClosedAtUtc = DateTime.MinValue;

    public bool IsMultiMonitorActive => IsCustomMonitorLayoutActive;
    private bool IsCustomMonitorLayoutActive =>
        _selectedMonitorIndices.Count > 1 || _virtualMonitorIds.Count > 0 || _ultrawideMode != 0;

    public RdpView()
    {
        InitializeComponent();
        MultiMonitorPopup.Closed += (_, _) =>
        {
            _multiMonitorPopupClosedAtUtc = DateTime.UtcNow;
            var callback = _onMultiMonitorPopupOpenChanged;
            _onMultiMonitorPopupOpenChanged = null;
            callback?.Invoke(false);
        };
        AddHandler(KeyDownEvent, OnFramebufferKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnFramebufferKeyUp, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnRdpDragOver);
        AddHandler(DragDrop.DropEvent, OnRdpDrop);
        AttachedToVisualTree += (_, _) =>
        {
            _isAttached = true;
            Focus();
            StartClipboardListener();
            SubscribeTopLevelScaling();
            if (DataContext is RdpViewModel vm)
                ScheduleStart(vm);
            UpdateSystemShortcutHook();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _isAttached = false;
            _hasKeyboardFocus = false;
            CloseMultiMonitorPopup();
            CloseAllSecondaryMonitorWindows();
            _ultrawideMode = 0;
            _virtualMonitorIds.Clear();
            _selectedMonitorIndices.Clear();
            _preferredPrimaryMonitorIndex = null;
            _primarySliceRect = null;
            SyncMultiMonitorActiveFlag();
            StopClipboardListener();
            UnsubscribeTopLevelScaling();
            RestoreLocalIme();
            DisposeSystemShortcutHook();
            ReleaseTrackedModifiers();
            _pendingWheelY = 0;
            UnbindViewModel();
        };
        GotFocus += (_, _) =>
        {
            _hasKeyboardFocus = true;
            DisableLocalIme();
            UpdateSystemShortcutHook();
            SyncLocalLockKeysToRemote();
            _ = SyncLocalClipboardToRemoteAsync();
        };
        LostFocus += (_, _) =>
        {
            _hasKeyboardFocus = false;
            RestoreLocalIme();
            DisposeSystemShortcutHook();
            ReleaseTrackedModifiers();
        };
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnbindViewModel();
        if (DataContext is not RdpViewModel vm)
            return;

        _boundVm = vm;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.RemoteClipboardImageReceived += OnRemoteClipboardImageReceived;
        vm.RemoteClipboardFilesReceived += OnRemoteClipboardFilesReceived;
        vm.FramebufferInvalidated += OnFramebufferInvalidated;
        vm.PointerShapeChanged += OnPointerShapeChanged;
        ApplyScaleMode(vm);
        UpdateSystemShortcutHook();
        if (_isAttached)
        {
            ScheduleStart(vm);
            _ = SyncLocalClipboardToRemoteAsync();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_boundVm != null &&
            (args.PropertyName == nameof(RdpViewModel.IsFitToWindow) ||
             args.PropertyName == nameof(RdpViewModel.ScreenScalePercent) ||
             args.PropertyName == nameof(RdpViewModel.RemoteWidth) ||
             args.PropertyName == nameof(RdpViewModel.RemoteHeight)))
        {
            ApplyScaleMode(_boundVm);
        }

        if (_boundVm != null && args.PropertyName == nameof(RdpViewModel.IsConnected))
        {
            if (_boundVm.IsConnected)
            {
                SyncLocalLockKeysToRemote();
                SyncMultiMonitorActiveFlag();
                if (!IsCustomMonitorLayoutActive)
                    _boundVm.RequestViewportResize(RdpViewport.Bounds.Size, GetRenderScaling());
            }
            else
            {
                CloseMultiMonitorPopup();
                CloseAllSecondaryMonitorWindows();
                _ultrawideMode = 0;
                _virtualMonitorIds.Clear();
                _selectedMonitorIndices.Clear();
                _preferredPrimaryMonitorIndex = null;
                _primarySliceRect = null;
                PrimarySliceSurface.IsVisible = false;
                FramebufferImage.IsVisible = true;
                SyncMultiMonitorActiveFlag();
                ResetClipboardSyncState();
            }
        }
        if (_boundVm != null && args.PropertyName == nameof(RdpViewModel.IsClipboardChannelReady))
        {
            if (_boundVm.IsClipboardChannelReady)
                _ = SyncLocalClipboardToRemoteAsync();
            else
                ResetClipboardSyncState();
        }
        if (_boundVm != null && args.PropertyName == nameof(RdpViewModel.RemoteClipboardText))
            _ = ApplyRemoteClipboardTextAsync(_boundVm.RemoteClipboardText);
    }

    private void UnbindViewModel()
    {
        if (_boundVm == null)
            return;

        _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
        _boundVm.RemoteClipboardImageReceived -= OnRemoteClipboardImageReceived;
        _boundVm.RemoteClipboardFilesReceived -= OnRemoteClipboardFilesReceived;
        _boundVm.FramebufferInvalidated -= OnFramebufferInvalidated;
        _boundVm.PointerShapeChanged -= OnPointerShapeChanged;
        _boundVm = null;
        ResetClipboardSyncState();
        DisposeCustomRemoteCursor();
        DisposeSystemShortcutHook();
    }

    private void ResetClipboardSyncState()
    {
        _lastSyncedClipboardText = null;
        _lastSyncedClipboardImageHash = null;
        _lastSyncedClipboardFilesKey = null;
        _lastSyncedLocalClipboardSeq = 0;
        _lastAppliedRemoteClipboardSeq = 0;
    }

    private void OnFramebufferInvalidated()
    {
        if (PrimarySliceSurface.IsVisible && DataContext is RdpViewModel vm)
        {
            PrimarySliceSurface.Source = vm.Framebuffer;
            PrimarySliceSurface.InvalidateVisual();
        }
        else
        {
            FramebufferImage.InvalidateVisual();
        }
    }

    public void ToggleMultiMonitorPopup(
        Control anchor,
        PlacementMode placement = PlacementMode.Bottom,
        Action<bool>? onPopupOpenChanged = null)
    {
        // A tab in the main window drives the same layout as a detached window:
        // the tab viewport stands in for the host monitor and every extra screen
        // opens its own window. Only a missing top level makes the popup (and
        // the screen enumeration behind it) impossible.
        if (TopLevel.GetTopLevel(this) is not Window)
            return;
        if (DataContext is not RdpViewModel { IsConnected: true })
            return;

        if (MultiMonitorPopup.IsOpen)
        {
            var sameAnchor = ReferenceEquals(MultiMonitorPopup.PlacementTarget, anchor);
            var previousCallback = _onMultiMonitorPopupOpenChanged;
            _onMultiMonitorPopupOpenChanged = null;
            MultiMonitorPopup.IsOpen = false;
            previousCallback?.Invoke(false);
            if (sameAnchor)
                return;
        }

        if (ReferenceEquals(_lastMultiMonitorAnchor, anchor) &&
            (DateTime.UtcNow - _multiMonitorPopupClosedAtUtc).TotalMilliseconds < 250)
        {
            _lastMultiMonitorAnchor = null;
            return;
        }

        _lastMultiMonitorAnchor = anchor;
        _onMultiMonitorPopupOpenChanged = onPopupOpenChanged;
        MultiMonitorPopup.PlacementTarget = anchor;
        MultiMonitorPopup.Placement = placement;
        PopulateMonitorCheckboxes();
        UpdateUltrawideModeButtonStates();
        MultiMonitorPopup.IsOpen = true;
        _onMultiMonitorPopupOpenChanged?.Invoke(true);
    }

    public void CloseMultiMonitorPopup()
    {
        if (MultiMonitorPopup.IsOpen)
            MultiMonitorPopup.IsOpen = false;
    }

    /// <summary>
    /// Closes all secondary / virtual monitor windows and restores single-monitor
    /// mode before moving this RDP session back into a multi-tab window.
    /// </summary>
    public void ResetToSingleMonitor()
    {
        CloseMultiMonitorPopup();
        CloseAllSecondaryMonitorWindows();
        // The custom layout selection has to be cleared as well: leaving it set
        // means the next viewport change (or DPI move) would rebuild the layout
        // that was just torn down.
        _selectedMonitorIndices.Clear();
        _virtualMonitorIds.Clear();
        _ultrawideMode = 0;
        _primarySliceRect = null;
        PrimarySliceSurface.IsVisible = false;
        FramebufferImage.IsVisible = true;
        if (DataContext is RdpViewModel vm)
        {
            vm.IsMultiMonitorActive = false;
            var scaling = GetRenderScaling();
            var w = Math.Max(640, (int)Math.Round(RdpViewport.Bounds.Width * scaling)) & ~1;
            var h = Math.Max(480, (int)Math.Round(RdpViewport.Bounds.Height * scaling));
            var policy = Enum.TryParse<RdpDpiPolicy>(vm.Session.RdpDpiPolicy, true, out var parsed)
                ? parsed
                : RdpDpiPolicy.FollowLocal;
            var scale = RdpDpiScale.ResolveScalePercent(policy, scaling);
            vm.TryResizeMonitors([new Services.RdpMonitorRect(0, 0, w, h, scale, true)]);
        }
    }

    private void SyncMultiMonitorActiveFlag()
    {
        if (DataContext is RdpViewModel vm)
            vm.IsMultiMonitorActive = vm.IsConnected && IsCustomMonitorLayoutActive;
    }

    private void OnUltrawideModeStandardClick(object? sender, RoutedEventArgs e)
    {
        _ultrawideMode = 0;
        UpdateUltrawideModeButtonStates();
        ApplySelectedMonitorLayout();
    }

    private void OnUltrawideModeSplitDualClick(object? sender, RoutedEventArgs e)
    {
        _ultrawideMode = 1;
        UpdateUltrawideModeButtonStates();
        ApplySelectedMonitorLayout();
    }

    private void OnUltrawideModeMergeUltrawideClick(object? sender, RoutedEventArgs e)
    {
        _ultrawideMode = 2;
        if (TopLevel.GetTopLevel(this) is Window window && window.Screens.All.Count >= 2 && _selectedMonitorIndices.Count < 2)
        {
            _selectedMonitorIndices.Add(0);
            _selectedMonitorIndices.Add(1);
            PopulateMonitorCheckboxes();
        }
        UpdateUltrawideModeButtonStates();
        ApplySelectedMonitorLayout();
    }

    private void OnAddVirtualMonitorClick(object? sender, RoutedEventArgs e)
    {
        if (_virtualMonitorIds.Count >= 3)
            return;

        _ultrawideMode = 0;
        _virtualMonitorIds.Add(_nextVirtualMonitorId++);
        UpdateUltrawideModeButtonStates();
        ApplySelectedMonitorLayout();
        PopulateMonitorCheckboxes();
    }

    private void UpdateUltrawideModeButtonStates()
    {
        ModeStandardButton.FontWeight = _ultrawideMode == 0 ? FontWeight.Bold : FontWeight.Normal;
        ModeStandardButton.Background = _ultrawideMode == 0 ? PopupButtonActiveBrush : PopupButtonNormalBrush;
        ModeSplitDualButton.FontWeight = _ultrawideMode == 1 ? FontWeight.Bold : FontWeight.Normal;
        ModeSplitDualButton.Background = _ultrawideMode == 1 ? PopupButtonActiveBrush : PopupButtonNormalBrush;
        ModeMergeUltrawideButton.FontWeight = _ultrawideMode == 2 ? FontWeight.Bold : FontWeight.Normal;
        ModeMergeUltrawideButton.Background = _ultrawideMode == 2 ? PopupButtonActiveBrush : PopupButtonNormalBrush;

        var canSplitDual = false;
        var splitReason = "需在宽屏全屏/最大化状态下开启";
        var canMergeUltrawide = false;
        var mergeReason = "需两块高度与 DPI 缩放一致的屏幕在全屏/最大化模式下开启";

        if (TopLevel.GetTopLevel(this) is Window window)
        {
            var isFullOrMax = window.WindowState is WindowState.FullScreen or WindowState.Maximized;
            var currentScreen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
            if (isFullOrMax && currentScreen != null)
            {
                var aspect = currentScreen.Bounds.Height > 0
                    ? (double)currentScreen.Bounds.Width / currentScreen.Bounds.Height
                    : 0;
                canSplitDual = currentScreen.Bounds.Width >= 2560 || aspect >= 1.8;
            }

            var screens = window.Screens.All;
            if (isFullOrMax && screens.Count >= 2 && _virtualMonitorIds.Count == 0)
            {
                var s0 = screens[0];
                var s1 = screens[1];
                canMergeUltrawide = s0.Bounds.Height == s1.Bounds.Height &&
                                    Math.Abs(s0.Scaling - s1.Scaling) < 0.01;
            }
        }

        ModeSplitDualButton.IsEnabled = canSplitDual || _ultrawideMode == 1;
        ToolTip.SetTip(ModeSplitDualButton, canSplitDual ? "将当前带鱼屏拆分为远端左右双屏" : splitReason);

        ModeMergeUltrawideButton.IsEnabled = canMergeUltrawide || _ultrawideMode == 2;
        ToolTip.SetTip(ModeMergeUltrawideButton, canMergeUltrawide ? "将两块等高等缩放物理屏合并为远端单块带鱼屏" : mergeReason);
    }

    private void PopulateMonitorCheckboxes()
    {
        MonitorCheckboxList.Children.Clear();
        if (TopLevel.GetTopLevel(this) is not Window window)
            return;

        var screens = window.Screens.All;
        if (screens.Count == 0)
            return;

        var currentScreen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        var currentIndex = 0;
        for (var i = 0; i < screens.Count; i++)
        {
            if (currentScreen != null && screens[i].Bounds == currentScreen.Bounds)
            {
                currentIndex = i;
                break;
            }
        }

        if (_selectedMonitorIndices.Count == 0)
            _selectedMonitorIndices.Add(currentIndex);

        var activePrimaryIndex = _preferredPrimaryMonitorIndex.HasValue && _selectedMonitorIndices.Contains(_preferredPrimaryMonitorIndex.Value)
            ? _preferredPrimaryMonitorIndex.Value
            : currentIndex;

        for (var i = 0; i < screens.Count; i++)
        {
            var idx = i;
            var s = screens[i];
            var scalePct = (int)Math.Round(s.Scaling * 100);
            var label = string.Format(
                CxShell.Services.LocalizationService.Shared.Text("Rdp.ScreenLabelFormat"),
                i + 1,
                (int)s.Bounds.Width,
                (int)s.Bounds.Height,
                scalePct);
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 8
            };
            var cb = new CheckBox
            {
                Content = label,
                IsChecked = _selectedMonitorIndices.Contains(idx),
                Foreground = Brushes.WhiteSmoke,
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            cb.IsCheckedChanged += (_, _) =>
            {
                if (cb.IsChecked == true)
                {
                    _selectedMonitorIndices.Add(idx);
                }
                else
                {
                    if (_selectedMonitorIndices.Count <= 1)
                    {
                        cb.IsChecked = true;
                        return;
                    }
                    _selectedMonitorIndices.Remove(idx);
                    if (_preferredPrimaryMonitorIndex == idx)
                        _preferredPrimaryMonitorIndex = null;
                }

                ApplySelectedMonitorLayout();
                PopulateMonitorCheckboxes();
            };

            var isPrimary = idx == activePrimaryIndex;
            var primaryBtn = new Button
            {
                Content = isPrimary ? "★ 主屏" : "设为主屏",
                Foreground = Brushes.White,
                Background = isPrimary ? PopupButtonActiveBrush : PopupButtonNormalBrush,
                BorderBrush = new SolidColorBrush(Color.Parse("#40FFFFFF")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                FontSize = 11,
                Padding = new Thickness(6, 2),
                IsEnabled = !isPrimary,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            primaryBtn.Click += (_, _) =>
            {
                _selectedMonitorIndices.Add(idx);
                _preferredPrimaryMonitorIndex = idx;
                ApplySelectedMonitorLayout();
                PopulateMonitorCheckboxes();
            };

            Grid.SetColumn(cb, 0);
            Grid.SetColumn(primaryBtn, 1);
            row.Children.Add(cb);
            row.Children.Add(primaryBtn);
            MonitorCheckboxList.Children.Add(row);
        }
    }

    private static int ResolveMonitorScalePercent(RdpViewModel vm, double screenScaling)
    {
        var policy = Enum.TryParse<RdpDpiPolicy>(vm.Session.RdpDpiPolicy, true, out var parsed)
            ? parsed
            : RdpDpiPolicy.FollowLocal;
        var resolved = RdpDpiScale.ResolveScalePercent(policy, screenScaling);
        return resolved > 0 ? Math.Clamp(resolved, 100, 500) : 100;
    }

    private void ApplySelectedMonitorLayout()
    {
        if (DataContext is not RdpViewModel vm || !vm.IsConnected || TopLevel.GetTopLevel(this) is not Window window)
            return;

        var screens = window.Screens.All;
        if (screens.Count == 0 || _selectedMonitorIndices.Count == 0)
            return;

        var selected = new System.Collections.Generic.List<(int Index, Avalonia.Platform.Screen Screen)>();
        foreach (var idx in _selectedMonitorIndices)
        {
            if (idx >= 0 && idx < screens.Count)
                selected.Add((idx, screens[idx]));
        }

        if (selected.Count == 0)
            return;

        selected.Sort((a, b) => a.Index.CompareTo(b.Index));

        var currentScreen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        var hostEntry = selected[0];
        var remotePrimaryEntry = selected[0];
        foreach (var item in selected)
        {
            if (currentScreen != null && item.Screen.Bounds == currentScreen.Bounds)
                hostEntry = item;
            if (_preferredPrimaryMonitorIndex.HasValue && item.Index == _preferredPrimaryMonitorIndex.Value)
                remotePrimaryEntry = item;
        }
        if (!_preferredPrimaryMonitorIndex.HasValue)
            remotePrimaryEntry = hostEntry;

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        foreach (var item in selected)
        {
            if (item.Screen.Bounds.X < minX) minX = item.Screen.Bounds.X;
            if (item.Screen.Bounds.Y < minY) minY = item.Screen.Bounds.Y;
            if (item.Screen.Bounds.Right > maxX) maxX = item.Screen.Bounds.Right;
            if (item.Screen.Bounds.Bottom > maxY) maxY = item.Screen.Bounds.Bottom;
        }

        var hostScaleFactor = GetRenderScaling();
        var useViewportForHost = window.WindowState != WindowState.FullScreen &&
                                 RdpViewport.Bounds.Width >= 320 &&
                                 RdpViewport.Bounds.Height >= 240;
        var hostPixelW = useViewportForHost
            ? Math.Clamp(((int)Math.Round(RdpViewport.Bounds.Width * hostScaleFactor)) & ~1, 200, 8192)
            : Math.Clamp(hostEntry.Screen.Bounds.Width & ~1, 200, 8192);
        var hostPixelH = useViewportForHost
            ? Math.Clamp(((int)Math.Round(RdpViewport.Bounds.Height * hostScaleFactor)) & ~1, 200, 8192)
            : Math.Clamp(hostEntry.Screen.Bounds.Height & ~1, 200, 8192);

        // Mode 1: Single local ultrawide -> Remote virtual dual monitors (left/right 50/50 split)
        if (_ultrawideMode == 1)
        {
            CloseAllSecondaryMonitorWindows();
            _primarySliceRect = null;
            PrimarySliceSurface.IsVisible = false;
            FramebufferImage.IsVisible = true;

            var halfW = Math.Max(200, (hostPixelW / 2) & ~1);
            var h = Math.Max(200, hostPixelH & ~1);
            var scale = ResolveMonitorScalePercent(vm, hostEntry.Screen.Scaling);
            var splitLayouts = new System.Collections.Generic.List<RdpMonitorRect>
            {
                new(0, 0, halfW, h, scale, true),
                new(halfW, 0, halfW, h, scale, false)
            };
            if (!vm.TryResizeMonitors(splitLayouts))
            {
                ResetToSingleMonitor();
                return;
            }
            SyncMultiMonitorActiveFlag();
            return;
        }

        var layouts = new System.Collections.Generic.List<RdpMonitorRect>(selected.Count);
        var sliceByIndex = new System.Collections.Generic.Dictionary<int, Rect>();

        // Mode 2: Local dual PBP screens -> Remote single merged ultrawide monitor
        if (_ultrawideMode == 2 && selected.Count >= 2 && _virtualMonitorIds.Count == 0)
        {
            foreach (var item in selected)
            {
                var b = item.Screen.Bounds;
                var w = item.Index == hostEntry.Index ? hostPixelW : Math.Clamp(b.Width & ~1, 200, 8192);
                var h = item.Index == hostEntry.Index ? hostPixelH : Math.Clamp(b.Height & ~1, 200, 8192);
                sliceByIndex[item.Index] = new Rect(b.X - minX, b.Y - minY, w, h);
            }
            var totalW = Math.Clamp((maxX - minX) & ~1, 200, 8192);
            var totalH = Math.Clamp((maxY - minY) & ~1, 200, 8192);
            var scale = ResolveMonitorScalePercent(vm, remotePrimaryEntry.Screen.Scaling);
            layouts.Add(new RdpMonitorRect(0, 0, totalW, totalH, scale, true));
        }
        else
        {
            // Snap adjacent monitors tightly to hostEntry's effective pixel rect so
            // right-left, top-bottom, and bottom-top arrangements have zero gaps or
            // overlaps (required by MS-RDPEDISP) even when hostPixelW/H differs from
            // the physical screen bounds (e.g. taskbar or windowed mode).
            var hostBounds = hostEntry.Screen.Bounds;
            var snappedCoords = new System.Collections.Generic.Dictionary<int, (int X, int Y, int W, int H, int Scale, bool IsPrimary)>();
            foreach (var item in selected)
            {
                var b = item.Screen.Bounds;
                var w = item.Index == hostEntry.Index ? hostPixelW : Math.Clamp(b.Width & ~1, 200, 8192);
                var h = item.Index == hostEntry.Index ? hostPixelH : Math.Clamp(b.Height & ~1, 200, 8192);
                var x = b.X;
                var y = b.Y;

                if (item.Index != hostEntry.Index)
                {
                    if (b.X >= hostBounds.Right - 2)
                        x = hostBounds.X + hostPixelW;
                    else if (b.Right <= hostBounds.X + 2)
                        x = hostBounds.X - w;

                    if (b.Y >= hostBounds.Bottom - 2)
                        y = hostBounds.Y + hostPixelH;
                    else if (b.Bottom <= hostBounds.Y + 2)
                        y = hostBounds.Y - h;
                }

                var scale = ResolveMonitorScalePercent(vm, item.Screen.Scaling);
                snappedCoords[item.Index] = (x, y, w, h, scale, item.Index == remotePrimaryEntry.Index);
            }

            var primaryCoord = snappedCoords[remotePrimaryEntry.Index];
            var layoutByIndex = new System.Collections.Generic.Dictionary<int, RdpMonitorRect>();
            var minLayoutX = int.MaxValue;
            var minLayoutY = int.MaxValue;
            var maxLayoutRight = int.MinValue;

            foreach (var item in selected)
            {
                var c = snappedCoords[item.Index];
                var left = c.X - primaryCoord.X;
                var top = c.Y - primaryCoord.Y;
                var rect = new RdpMonitorRect(left, top, c.W, c.H, c.Scale, c.IsPrimary);
                layouts.Add(rect);
                layoutByIndex[item.Index] = rect;
                if (left < minLayoutX) minLayoutX = left;
                if (top < minLayoutY) minLayoutY = top;
                if (left + c.W > maxLayoutRight) maxLayoutRight = left + c.W;
            }

            // Append any user-created virtual monitors (windowed on host screen) to the right
            var hostLayout = layoutByIndex[hostEntry.Index];
            var nextRight = Math.Max(hostLayout.Left + hostLayout.Width, maxLayoutRight);
            var hostScale = Math.Clamp((int)Math.Round(hostEntry.Screen.Scaling * 100), 100, 500);
            foreach (var vId in _virtualMonitorIds)
            {
                var vw = Math.Clamp(hostBounds.Width & ~1, 200, 8192);
                var vh = Math.Clamp(hostBounds.Height & ~1, 200, 8192);
                var vRect = new RdpMonitorRect(nextRight, hostLayout.Top, vw, vh, hostScale, false);
                layouts.Add(vRect);
                layoutByIndex[vId] = vRect;
                if (vRect.Left < minLayoutX) minLayoutX = vRect.Left;
                if (vRect.Top < minLayoutY) minLayoutY = vRect.Top;
                nextRight += vw;
            }

            // Compute every monitor's framebuffer slice directly from the normalized
            // layout coordinates so negative left/top offsets (right-left, bottom-top)
            // map accurately to [0..totalWidth, 0..totalHeight] in the GDI framebuffer.
            foreach (var kv in layoutByIndex)
            {
                var l = kv.Value;
                sliceByIndex[kv.Key] = new Rect(l.Left - minLayoutX, l.Top - minLayoutY, l.Width, l.Height);
            }
        }

        if (!vm.TryResizeMonitors(layouts))
        {
            // The bridge or the server refused the layout. Reverting keeps the
            // session usable instead of leaving windows that show slices of a
            // framebuffer the server never produced.
            ResetToSingleMonitor();
            return;
        }

        if (selected.Count == 1 && _virtualMonitorIds.Count == 0)
        {
            CloseAllSecondaryMonitorWindows();
            _primarySliceRect = null;
            PrimarySliceSurface.IsVisible = false;
            FramebufferImage.IsVisible = true;
            SyncMultiMonitorActiveFlag();
            return;
        }

        _primarySliceRect = sliceByIndex[hostEntry.Index];
        PrimarySliceSurface.Source = vm.Framebuffer;
        PrimarySliceSurface.SourceRect = _primarySliceRect.Value;
        PrimarySliceSurface.IsVisible = true;
        FramebufferImage.IsVisible = false;

        var activeSecondary = new System.Collections.Generic.HashSet<int>();
        foreach (var item in selected)
        {
            if (item.Index == hostEntry.Index)
                continue;

            activeSecondary.Add(item.Index);
            var slice = sliceByIndex[item.Index];
            if (_secondaryMonitorWindows.TryGetValue(item.Index, out var existing))
            {
                existing.UpdateSlice(slice);
            }
            else
            {
                var monitorWin = new RdpMonitorWindow(this, vm, item.Screen, item.Index, slice, OnSecondaryMonitorCloseRequested);
                _secondaryMonitorWindows[item.Index] = monitorWin;
                monitorWin.Show();
            }
        }

        for (var vi = 0; vi < _virtualMonitorIds.Count; vi++)
        {
            var vId = _virtualMonitorIds[vi];
            activeSecondary.Add(vId);
            var slice = sliceByIndex[vId];
            if (_secondaryMonitorWindows.TryGetValue(vId, out var existing))
            {
                existing.UpdateSlice(slice);
            }
            else
            {
                var vTitle = string.Format(
                    CxShell.Services.LocalizationService.Shared.Text("Rdp.VirtualScreenLabelFormat"),
                    vi + 1,
                    (int)slice.Width,
                    (int)slice.Height);
                var monitorWin = new RdpMonitorWindow(
                    this,
                    vm,
                    hostEntry.Screen,
                    vId,
                    slice,
                    OnSecondaryMonitorCloseRequested,
                    startWindowed: true,
                    customTitle: vTitle);
                _secondaryMonitorWindows[vId] = monitorWin;
                monitorWin.Show();
            }
        }

        var toClose = new System.Collections.Generic.List<int>();
        foreach (var key in _secondaryMonitorWindows.Keys)
        {
            if (!activeSecondary.Contains(key))
                toClose.Add(key);
        }
        foreach (var key in toClose)
        {
            if (_secondaryMonitorWindows.Remove(key, out var win))
                win.CloseProgrammatically();
        }

        SyncMultiMonitorActiveFlag();
    }

    private void OnSecondaryMonitorCloseRequested(int monitorIndex)
    {
        _selectedMonitorIndices.Remove(monitorIndex);
        _virtualMonitorIds.Remove(monitorIndex);
        if (_secondaryMonitorWindows.Remove(monitorIndex, out var win))
            win.CloseProgrammatically();
        ApplySelectedMonitorLayout();
        UpdateUltrawideModeButtonStates();
        SyncMultiMonitorActiveFlag();
    }

    private void CloseAllSecondaryMonitorWindows()
    {
        foreach (var win in _secondaryMonitorWindows.Values)
            win.CloseProgrammatically();
        _secondaryMonitorWindows.Clear();
    }

    private void OnPointerShapeChanged(int kind, int width, int height, int hotX, int hotY, byte[]? pixels)
    {
        if (kind == 1)
        {
            DisposeCustomRemoteCursor();
            ApplyCursorToAllSurfaces(new Cursor(StandardCursorType.None));
            return;
        }

        if (kind == 2 && pixels != null && width > 0 && height > 0)
        {
            try
            {
                var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Unpremul);
                using (var fb = bitmap.Lock())
                {
                    var copyBytes = Math.Min(pixels.Length, fb.RowBytes * fb.Size.Height);
                    System.Runtime.InteropServices.Marshal.Copy(pixels, 0, fb.Address, copyBytes);
                }

                var clampedHotX = Math.Clamp(hotX, 0, width - 1);
                var clampedHotY = Math.Clamp(hotY, 0, height - 1);
                var cursor = new Cursor(bitmap, new PixelPoint(clampedHotX, clampedHotY));
                ApplyCursorToAllSurfaces(cursor);
                DisposeCustomRemoteCursor();
                _customRemoteCursor = cursor;
                _customRemoteCursorBitmap = bitmap;
                return;
            }
            catch
            {
                // Fall back to default arrow cursor if platform cursor creation fails.
            }
        }

        DisposeCustomRemoteCursor();
        ApplyCursorToAllSurfaces(new Cursor(StandardCursorType.Arrow));
    }

    private void ApplyCursorToAllSurfaces(Cursor cursor)
    {
        FramebufferImage.Cursor = cursor;
        PrimarySliceSurface.Cursor = cursor;
        foreach (var win in _secondaryMonitorWindows.Values)
            win.SetRemoteCursor(cursor);
    }

    private void DisposeCustomRemoteCursor()
    {
        _customRemoteCursor?.Dispose();
        _customRemoteCursor = null;
        _customRemoteCursorBitmap?.Dispose();
        _customRemoteCursorBitmap = null;
    }

    private void StartClipboardListener()
    {
        if (OperatingSystem.IsWindows())
        {
            if (_clipboardListener != null)
                return;

            _clipboardListener = new WindowsClipboardListener();
            _clipboardListener.ClipboardChanged += OnLocalClipboardChanged;
            return;
        }

        if (_clipboardPollTimer != null)
            return;

        _clipboardPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _clipboardPollTimer.Tick += OnClipboardPollTick;
        _clipboardPollTimer.Start();
    }

    private void StopClipboardListener()
    {
        if (_clipboardListener != null)
        {
            _clipboardListener.ClipboardChanged -= OnLocalClipboardChanged;
            _clipboardListener.Dispose();
            _clipboardListener = null;
        }

        if (_clipboardPollTimer != null)
        {
            _clipboardPollTimer.Stop();
            _clipboardPollTimer.Tick -= OnClipboardPollTick;
            _clipboardPollTimer = null;
        }
    }

    private void OnClipboardPollTick(object? sender, EventArgs e)
    {
        if (_isAttached && DataContext is RdpViewModel { IsConnected: true, IsClipboardChannelReady: true })
            _ = SyncLocalClipboardToRemoteAsync();
    }

    private void OnLocalClipboardChanged()
    {
        Dispatcher.UIThread.Post(() => _ = SyncLocalClipboardToRemoteAsync());
    }

    private void OnRemoteClipboardImageReceived(uint imageFormat, byte[] data)
    {
        _ = ApplyRemoteClipboardImageAsync(imageFormat, data);
    }

    private void OnRemoteClipboardFilesReceived(string[] paths)
    {
        _ = ApplyRemoteClipboardFilesAsync(paths);
    }

    private void UpdateSystemShortcutHook()
    {
        var shouldEnable = OperatingSystem.IsWindows() &&
                           _isAttached &&
                           _hasKeyboardFocus &&
                           DataContext is RdpViewModel { Session.RdpApplyKeyCombinations: true };
        if (!shouldEnable)
        {
            DisposeSystemShortcutHook();
            return;
        }

        _systemShortcutHook ??= WindowsRdpShortcutHook.TryCreate(OnSystemShortcutKey);
    }

    private void DisposeSystemShortcutHook()
    {
        _systemShortcutHook?.Dispose();
        _systemShortcutHook = null;
    }

    private void OnSystemShortcutKey(uint scancode, bool down)
    {
        if (DataContext is RdpViewModel vm)
            vm.SendKey(scancode, down);
    }

    private void ApplyScaleMode(RdpViewModel vm)
    {
        if (FramebufferImage == null)
            return;

        if (vm.IsFitToWindow || vm.RemoteWidth <= 0 || vm.RemoteHeight <= 0)
        {
            FramebufferImage.Width = double.NaN;
            FramebufferImage.Height = double.NaN;
            FramebufferImage.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            FramebufferImage.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            FramebufferImage.Stretch = Stretch.Uniform;
            return;
        }

        FramebufferImage.Width = vm.RemoteWidth * vm.FixedScaleFactor;
        FramebufferImage.Height = vm.RemoteHeight * vm.FixedScaleFactor;
        FramebufferImage.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        FramebufferImage.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        FramebufferImage.Stretch = Stretch.Fill;
    }

    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is RdpViewModel vm)
        {
            ConfigureInitialDisplay(vm, e.NewSize);
            if (_isAttached)
                vm.Start();
            if (IsCustomMonitorLayoutActive)
                ApplySelectedMonitorLayout();
            else
                vm.RequestViewportResize(e.NewSize, GetRenderScaling());
        }
    }

    private void ScheduleStart(RdpViewModel vm)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isAttached || !ReferenceEquals(DataContext, vm))
                return;

            ConfigureInitialDisplay(vm, RdpViewport.Bounds.Size);
            vm.Start();
        }, DispatcherPriority.Loaded);
    }

    private void ConfigureInitialDisplay(RdpViewModel vm, Size viewportSize)
    {
        vm.ConfigureInitialDisplay(viewportSize, GetCurrentMonitorPixelSize(), GetRenderScaling());
    }

    private void OnRdpDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is RdpViewModel vm &&
            vm.IsConnected &&
            vm.IsClipboardChannelReady &&
            e.DataTransfer.TryGetFiles() is { Length: > 0 })
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        e.DragEffects = DragDropEffects.None;
    }

    private void OnRdpDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not RdpViewModel vm || !vm.IsConnected || !vm.IsClipboardChannelReady)
            return;

        var files = e.DataTransfer.TryGetFiles();
        if (files == null || files.Length == 0)
            return;

        var paths = new System.Collections.Generic.List<string>(files.Length);
        foreach (var item in files)
        {
            var path = item.Path?.LocalPath;
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);
        }

        if (paths.Count == 0)
            return;

        e.Handled = true;
        var pathArray = paths.ToArray();
        _ = DropFilesToRemoteAsync(vm, pathArray);
    }

    private async Task DropFilesToRemoteAsync(RdpViewModel vm, string[] paths)
    {
        vm.SetClipboardFiles(paths);
        _lastSyncedClipboardFilesKey = string.Join("\n", paths);
        _lastSyncedClipboardText = null;
        _lastSyncedClipboardImageHash = null;
        await Task.Delay(150);
        SendClipboardPasteShortcut(vm);
    }

    private void SyncLocalLockKeysToRemote()
    {
        if (!OperatingSystem.IsWindows() || DataContext is not RdpViewModel vm || !vm.IsConnected || !vm.Session.RdpSyncLockKeys)
            return;

        uint flags = 0;
        if ((GetKeyState(0x91) & 1) != 0) // VK_SCROLL
            flags |= 0x01;
        if ((GetKeyState(0x90) & 1) != 0) // VK_NUMLOCK
            flags |= 0x02;
        if ((GetKeyState(0x14) & 1) != 0) // VK_CAPITAL
            flags |= 0x04;
        vm.SyncLockKeys(flags);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [System.Runtime.InteropServices.DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

    private void DisableLocalIme()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var hwnd = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            return;

        if (_imeDisabled)
        {
            if (_savedImeHwnd == hwnd)
                return;
            RestoreLocalIme();
        }

        _savedImeContext = ImmAssociateContext(hwnd, IntPtr.Zero);
        _savedImeHwnd = hwnd;
        _imeDisabled = true;
    }

    private void RestoreLocalIme()
    {
        if (!_imeDisabled || !OperatingSystem.IsWindows())
            return;

        if (_savedImeHwnd != IntPtr.Zero)
            ImmAssociateContext(_savedImeHwnd, _savedImeContext);

        _savedImeHwnd = IntPtr.Zero;
        _savedImeContext = IntPtr.Zero;
        _imeDisabled = false;
    }

    private void OnTopLevelScalingChanged(object? sender, EventArgs e)
    {
        if (DataContext is RdpViewModel vm)
        {
            if (IsCustomMonitorLayoutActive)
                ApplySelectedMonitorLayout();
            else
                vm.RequestViewportResize(RdpViewport.Bounds.Size, GetRenderScaling());
        }
    }

    private void SubscribeTopLevelScaling()
    {
        UnsubscribeTopLevelScaling();
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return;

        _subscribedTopLevel = topLevel;
        _subscribedTopLevel.ScalingChanged += OnTopLevelScalingChanged;
    }

    private void UnsubscribeTopLevelScaling()
    {
        if (_subscribedTopLevel == null)
            return;

        _subscribedTopLevel.ScalingChanged -= OnTopLevelScalingChanged;
        _subscribedTopLevel = null;
    }

    private double GetRenderScaling()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is { RenderScaling: > 0 } && double.IsFinite(topLevel.RenderScaling))
            return topLevel.RenderScaling;

        if (topLevel is Window window)
        {
            var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
            if (screen is { Scaling: > 0 } && double.IsFinite(screen.Scaling))
                return screen.Scaling;
        }

        return 1;
    }

    private PixelSize? GetCurrentMonitorPixelSize()
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
            return null;

        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        return screen?.Bounds.Size;
    }

    private void OnFramebufferPointerMoved(object? sender, PointerEventArgs e)
    {
        SendPointer(e, 0x0800);
    }

    private void OnFramebufferPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Focus();
        _ = SyncLocalClipboardToRemoteAsync();
        var pointerTarget = PrimarySliceSurface.IsVisible ? (Visual)PrimarySliceSurface : FramebufferImage;
        var point = e.GetCurrentPoint(pointerTarget);
        if (point.Properties.IsLeftButtonPressed)
            _buttonFlags |= 0x1000;
        if (point.Properties.IsMiddleButtonPressed)
            _buttonFlags |= 0x4000;
        if (point.Properties.IsRightButtonPressed)
            _buttonFlags |= 0x2000;

        SendPointer(e, (ushort)(0x8000 | _buttonFlags));
        e.Handled = true;
    }

    private void OnFramebufferPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var releaseFlag = e.InitialPressMouseButton switch
        {
            MouseButton.Left => (ushort)0x1000,
            MouseButton.Right => (ushort)0x2000,
            MouseButton.Middle => (ushort)0x4000,
            _ => (ushort)0
        };

        if (releaseFlag != 0)
        {
            SendPointer(e, releaseFlag);
            _buttonFlags &= (ushort)~releaseFlag;
        }

        e.Handled = true;
    }

    private void OnFramebufferPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var notches = RdpWheelInput.TakeNotches(ref _pendingWheelY, e.Delta.Y);

        if (notches != 0)
        {
            // Each RDP wheel event carries exactly one notch, so a fast tick of
            // three notches needs three events.
            var wheelFlag = RdpWheelInput.GetNotchFlags(notches);
            for (var i = 0; i < Math.Abs(notches); i++)
                SendPointer(e, wheelFlag);
        }

        e.Handled = true;
    }

    private void OnFramebufferKeyDown(object? sender, KeyEventArgs e)
    {
        if (TryStartLocalClipboardPaste(e))
            return;

        SendKey(e, true);
    }

    private void OnFramebufferKeyUp(object? sender, KeyEventArgs e)
    {
        if (_suppressNextPasteKeyUp && e.Key == Key.V)
        {
            _suppressNextPasteKeyUp = false;
            e.Handled = true;
            return;
        }

        SendKey(e, false);
    }

    internal void ForwardKeyDown(KeyEventArgs e) => OnFramebufferKeyDown(this, e);
    internal void ForwardKeyUp(KeyEventArgs e) => OnFramebufferKeyUp(this, e);
    internal void ForwardDragOver(DragEventArgs e) => OnRdpDragOver(this, e);
    internal void ForwardDrop(DragEventArgs e) => OnRdpDrop(this, e);
    internal void ForwardGotFocus()
    {
        SyncLocalLockKeysToRemote();
        _ = SyncLocalClipboardToRemoteAsync();
    }

    internal void ForwardLostFocus()
    {
        ReleaseTrackedModifiers();
    }

    internal void RequestClipboardSync() => _ = SyncLocalClipboardToRemoteAsync();

    internal bool TryMapGlobalScreenPoint(PixelPoint screenPoint, out ushort remoteX, out ushort remoteY)
    {
        remoteX = 0;
        remoteY = 0;

        if (PrimarySliceSurface.IsVisible && _primarySliceRect is { Width: > 0, Height: > 0 } slice)
        {
            var bounds = PrimarySliceSurface.Bounds;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                var topLeft = PrimarySliceSurface.PointToScreen(new Point(0, 0));
                var bottomRight = PrimarySliceSurface.PointToScreen(new Point(bounds.Width, bounds.Height));
                var sw = bottomRight.X - topLeft.X;
                var sh = bottomRight.Y - topLeft.Y;
                if (sw > 0 && sh > 0 &&
                    screenPoint.X >= topLeft.X && screenPoint.X <= bottomRight.X &&
                    screenPoint.Y >= topLeft.Y && screenPoint.Y <= bottomRight.Y)
                {
                    var relX = Math.Clamp((double)(screenPoint.X - topLeft.X) / sw, 0, 1);
                    var relY = Math.Clamp((double)(screenPoint.Y - topLeft.Y) / sh, 0, 1);
                    remoteX = (ushort)Math.Clamp((int)Math.Round(slice.X + relX * slice.Width), 0, ushort.MaxValue);
                    remoteY = (ushort)Math.Clamp((int)Math.Round(slice.Y + relY * slice.Height), 0, ushort.MaxValue);
                    return true;
                }
            }
        }

        foreach (var win in _secondaryMonitorWindows.Values)
        {
            if (win.TryMapScreenPoint(screenPoint, out remoteX, out remoteY))
                return true;
        }

        return false;
    }

    private void SendPointer(PointerEventArgs e, ushort flags)
    {
        if (DataContext is not RdpViewModel vm || FramebufferImage == null)
            return;

        if (PrimarySliceSurface.IsVisible && _primarySliceRect is { Width: > 0, Height: > 0 } slice)
        {
            var bounds = PrimarySliceSurface.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            var pos = e.GetPosition(PrimarySliceSurface);
            if (pos.X < 0 || pos.X > bounds.Width || pos.Y < 0 || pos.Y > bounds.Height)
            {
                var screenPt = PrimarySliceSurface.PointToScreen(pos);
                if (TryMapGlobalScreenPoint(screenPt, out var gx, out var gy))
                {
                    _lastRemotePointerX = gx;
                    _lastRemotePointerY = gy;
                    vm.SendPointer(flags, gx, gy);
                }
                else if (flags != 0x0800)
                {
                    // In gap between windows: pause move updates, but still deliver button release cleanly.
                    vm.SendPointer(flags, _lastRemotePointerX, _lastRemotePointerY);
                }
                return;
            }

            var relX = Math.Clamp(pos.X / bounds.Width, 0, 1);
            var relY = Math.Clamp(pos.Y / bounds.Height, 0, 1);
            var vx = slice.X + relX * slice.Width;
            var vy = slice.Y + relY * slice.Height;
            var rx = (ushort)Math.Clamp((int)Math.Round(vx), 0, ushort.MaxValue);
            var ry = (ushort)Math.Clamp((int)Math.Round(vy), 0, ushort.MaxValue);
            _lastRemotePointerX = rx;
            _lastRemotePointerY = ry;
            vm.SendPointer(flags, rx, ry);
            return;
        }

        var (x, y) = MapPointer(
            e.GetPosition(FramebufferImage),
            FramebufferImage.Bounds.Size,
            vm.RemoteWidth,
            vm.RemoteHeight,
            vm.IsFitToWindow);
        _lastRemotePointerX = (ushort)x;
        _lastRemotePointerY = (ushort)y;
        vm.SendPointer(flags, (ushort)x, (ushort)y);
    }

    private static (int X, int Y) MapPointer(Point point, Size imageSize, int remoteWidth, int remoteHeight, bool fitToWindow)
    {
        if (remoteWidth <= 0 || remoteHeight <= 0 || imageSize.Width <= 0 || imageSize.Height <= 0)
            return (0, 0);

        var scaleX = fitToWindow
            ? Math.Min(imageSize.Width / remoteWidth, imageSize.Height / remoteHeight)
            : Math.Max(0.01, imageSize.Width / remoteWidth);
        var scaleY = fitToWindow
            ? scaleX
            : Math.Max(0.01, imageSize.Height / remoteHeight);
        var displayedWidth = remoteWidth * scaleX;
        var displayedHeight = remoteHeight * scaleY;
        var offsetX = fitToWindow ? (imageSize.Width - displayedWidth) / 2 : 0;
        var offsetY = fitToWindow ? (imageSize.Height - displayedHeight) / 2 : 0;
        var x = (int)Math.Round((point.X - offsetX) / scaleX);
        var y = (int)Math.Round((point.Y - offsetY) / scaleY);
        return (Math.Clamp(x, 0, remoteWidth - 1), Math.Clamp(y, 0, remoteHeight - 1));
    }

    private void SendKey(KeyEventArgs e, bool down)
    {
        if (DataContext is not RdpViewModel vm)
            return;

        if (TrySendModifierKey(vm, e.Key, down))
        {
            e.Handled = true;
            return;
        }

        SyncModifierState(vm, e.KeyModifiers);

        if (TrySendCtrlAltEnd(vm, e, down))
        {
            e.Handled = true;
            return;
        }

        if (TryGetPrintableScancode(e, out var printableScancode, out _))
        {
            vm.SendKey(printableScancode, down);
            e.Handled = true;
            return;
        }

        var key = ToRdpScancode(e);
        if (key == 0)
            return;

        vm.SendKey(key, down);
        e.Handled = true;
    }

    private bool TrySendCtrlAltEnd(RdpViewModel vm, KeyEventArgs e, bool down)
    {
        if (e.Key != Key.End)
            return false;

        if (down)
        {
            var scancode = RdpKeyboardShortcutSequences.TranslateCtrlAltEnd(
                RdpKeyboardShortcutSequences.ExtendedEndScancode,
                e.KeyModifiers.HasFlag(KeyModifiers.Control),
                e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            if (scancode == RdpKeyboardShortcutSequences.ExtendedEndScancode)
                return false;

            if (!_ctrlAltEndActive)
                vm.SendKey(scancode, true);
            _ctrlAltEndActive = true;
            return true;
        }

        if (!_ctrlAltEndActive)
            return false;

        vm.SendKey(RdpKeyboardShortcutSequences.ExtendedDeleteScancode, false);
        _ctrlAltEndActive = false;
        return true;
    }

    private bool TryStartLocalClipboardPaste(KeyEventArgs e)
    {
        if (e.Key != Key.V || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return false;

        _suppressNextPasteKeyUp = true;
        e.Handled = true;
        _ = PasteClipboardViaRdpAsync();
        return true;
    }

    private async Task PasteClipboardViaRdpAsync()
    {
        if (DataContext is not RdpViewModel vm)
            return;

        var currentSeq = RdpClipboardImageHelper.GetWindowsClipboardSequenceNumber();
        if (vm.IsClipboardChannelReady && currentSeq != 0 && currentSeq == _lastAppliedRemoteClipboardSeq)
        {
            SendClipboardPasteShortcut(vm);
            return;
        }

        var seqChanged = currentSeq != 0 && currentSeq != _lastSyncedLocalClipboardSeq;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;

        if (vm.IsClipboardChannelReady)
        {
            var files = await RdpClipboardImageHelper.TryReadLocalFilesAsync(clipboard);
            if (files is { Length: > 0 })
            {
                var filesKey = string.Join("\n", files);
                var alreadySynced = !seqChanged && string.Equals(filesKey, _lastSyncedClipboardFilesKey, StringComparison.Ordinal);
                if (!alreadySynced)
                {
                    vm.SetClipboardFiles(files);
                    _lastSyncedClipboardFilesKey = filesKey;
                    _lastSyncedClipboardText = null;
                    _lastSyncedClipboardImageHash = null;
                    _lastSyncedLocalClipboardSeq = currentSeq;
                    await Task.Delay(150);
                }

                SendClipboardPasteShortcut(vm);
                return;
            }
        }

        var text = await TryReadLocalClipboardTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            if (!vm.IsClipboardChannelReady)
            {
                vm.SetClipboardText(text);
                _lastSyncedClipboardText = text;
                _lastSyncedClipboardImageHash = null;
                _lastSyncedClipboardFilesKey = null;
                _lastSyncedLocalClipboardSeq = currentSeq;
                await PasteLocalClipboardTextAsync(text);
                return;
            }

            var alreadySynced = !seqChanged && string.Equals(text, _lastSyncedClipboardText, StringComparison.Ordinal);
            if (!alreadySynced)
            {
                var (html, rtf) = await RdpClipboardImageHelper.TryReadLocalRichTextAsync(clipboard);
                vm.SetClipboardRichText(text, html, rtf);
                _lastSyncedClipboardText = text;
                _lastSyncedClipboardImageHash = null;
                _lastSyncedClipboardFilesKey = null;
                _lastSyncedLocalClipboardSeq = currentSeq;
                await Task.Delay(150);
            }

            SendClipboardPasteShortcut(vm);
            return;
        }

        if (!vm.IsClipboardChannelReady)
            return;

        var image = await RdpClipboardImageHelper.TryReadLocalImageAsync(clipboard);
        if (image == null)
            return;

        var imageAlreadySynced = !seqChanged && string.Equals(image.Hash, _lastSyncedClipboardImageHash, StringComparison.Ordinal);
        if (!imageAlreadySynced)
        {
            vm.SetClipboardImage(image.PngBytes, image.DibBytes);
            _lastSyncedClipboardImageHash = image.Hash;
            _lastSyncedClipboardText = null;
            _lastSyncedClipboardFilesKey = null;
            _lastSyncedLocalClipboardSeq = currentSeq;
            await Task.Delay(150);
        }

        SendClipboardPasteShortcut(vm);
    }

    private async Task PasteLocalClipboardTextAsync(string text)
    {
        if (DataContext is not RdpViewModel vm)
            return;

        if (string.IsNullOrEmpty(text))
            return;

        ReleaseTrackedModifiers(vm);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    continue;

                SendScancodeTap(vm, 0x1C);
                continue;
            }

            if (ch == '\n')
            {
                SendScancodeTap(vm, 0x1C);
                continue;
            }

            if (ch == '\t')
            {
                SendScancodeTap(vm, 0x0F);
                continue;
            }

            vm.SendUnicodeKey(ch, true);
            vm.SendUnicodeKey(ch, false);

            if (i % 128 == 127)
                await Task.Yield();
        }
    }

    private async Task<string?> TryReadLocalClipboardTextAsync()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            return clipboard == null ? null : await clipboard.TryGetTextAsync();
        }
        catch
        {
            return null;
        }
    }

    private async Task SyncLocalClipboardToRemoteAsync()
    {
        if (_applyingRemoteClipboardText || DataContext is not RdpViewModel vm || !vm.IsClipboardChannelReady)
            return;

        var currentSeq = RdpClipboardImageHelper.GetWindowsClipboardSequenceNumber();
        if (currentSeq != 0 && currentSeq == _lastAppliedRemoteClipboardSeq)
            return;

        var seqChanged = currentSeq != 0 && currentSeq != _lastSyncedLocalClipboardSeq;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;

        var files = await RdpClipboardImageHelper.TryReadLocalFilesAsync(clipboard);
        if (files is { Length: > 0 })
        {
            var filesKey = string.Join("\n", files);
            if (!seqChanged && string.Equals(filesKey, _lastSyncedClipboardFilesKey, StringComparison.Ordinal))
                return;

            vm.SetClipboardFiles(files);
            _lastSyncedClipboardFilesKey = filesKey;
            _lastSyncedClipboardText = null;
            _lastSyncedClipboardImageHash = null;
            _lastSyncedLocalClipboardSeq = currentSeq;
            return;
        }

        var text = await TryReadLocalClipboardTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            if (!seqChanged && string.Equals(text, _lastSyncedClipboardText, StringComparison.Ordinal))
                return;

            var (html, rtf) = await RdpClipboardImageHelper.TryReadLocalRichTextAsync(clipboard);
            vm.SetClipboardRichText(text, html, rtf);
            _lastSyncedClipboardText = text;
            _lastSyncedClipboardImageHash = null;
            _lastSyncedClipboardFilesKey = null;
            _lastSyncedLocalClipboardSeq = currentSeq;
            return;
        }

        var image = await RdpClipboardImageHelper.TryReadLocalImageAsync(clipboard);
        if (image == null || (!seqChanged && string.Equals(image.Hash, _lastSyncedClipboardImageHash, StringComparison.Ordinal)))
            return;

        vm.SetClipboardImage(image.PngBytes, image.DibBytes);
        _lastSyncedClipboardImageHash = image.Hash;
        _lastSyncedClipboardText = null;
        _lastSyncedClipboardFilesKey = null;
        _lastSyncedLocalClipboardSeq = currentSeq;
    }

    private async Task ApplyRemoteClipboardTextAsync(string text)
    {
        _applyingRemoteClipboardText = true;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
                _lastSyncedClipboardText = text;
                _lastSyncedClipboardImageHash = null;
                _lastSyncedClipboardFilesKey = null;
                _lastAppliedRemoteClipboardSeq = RdpClipboardImageHelper.GetWindowsClipboardSequenceNumber();
            }
        }
        catch
        {
            // Clipboard access may fail on some desktop backends.
        }
        finally
        {
            _applyingRemoteClipboardText = false;
        }
    }

    private async Task ApplyRemoteClipboardImageAsync(uint imageFormat, byte[] data)
    {
        _applyingRemoteClipboardText = true;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (await RdpClipboardImageHelper.TryWriteLocalImageAsync(clipboard, imageFormat, data))
            {
                _lastSyncedClipboardImageHash = RdpClipboardImageHelper.ComputeHash(
                    imageFormat == RdpBridgeClient.ClipboardImageFormatPng ? data : null,
                    imageFormat == RdpBridgeClient.ClipboardImageFormatDib ? data : null);
                _lastSyncedClipboardText = null;
                _lastSyncedClipboardFilesKey = null;
                _lastAppliedRemoteClipboardSeq = RdpClipboardImageHelper.GetWindowsClipboardSequenceNumber();
            }
        }
        catch
        {
            // Clipboard access may fail on some desktop backends.
        }
        finally
        {
            _applyingRemoteClipboardText = false;
        }
    }

    private async Task ApplyRemoteClipboardFilesAsync(string[] paths)
    {
        _applyingRemoteClipboardText = true;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (await RdpClipboardImageHelper.TryWriteLocalFilesAsync(clipboard, paths))
            {
                _lastSyncedClipboardFilesKey = string.Join("\n", paths);
                _lastSyncedClipboardText = null;
                _lastSyncedClipboardImageHash = null;
                _lastAppliedRemoteClipboardSeq = RdpClipboardImageHelper.GetWindowsClipboardSequenceNumber();
            }
        }
        catch
        {
            // Clipboard access may fail on some desktop backends.
        }
        finally
        {
            _applyingRemoteClipboardText = false;
        }
    }

    private void SendClipboardPasteShortcut(RdpViewModel vm)
    {
        ReleaseTrackedModifiers(vm);
        vm.SendKey(0x1D, true);
        SendScancodeTap(vm, 0x2F);
        vm.SendKey(0x1D, false);
    }

    private static void SendScancodeTap(RdpViewModel vm, uint scancode)
    {
        vm.SendKey(scancode, true);
        vm.SendKey(scancode, false);
    }

    private void ReleaseTrackedModifiers()
    {
        if (DataContext is RdpViewModel vm)
            ReleaseTrackedModifiers(vm);
        else
            ClearTrackedModifiers();
    }

    private void ReleaseTrackedModifiers(RdpViewModel vm)
    {
        if (_ctrlAltEndActive)
        {
            vm.SendKey(RdpKeyboardShortcutSequences.ExtendedDeleteScancode, false);
            _ctrlAltEndActive = false;
        }

        SetModifierState(vm, ref _controlDown, false, 0x1D);
        SetModifierState(vm, ref _shiftDown, false, 0x2A);
        SetModifierState(vm, ref _altDown, false, 0x38);
    }

    private void ClearTrackedModifiers()
    {
        _ctrlAltEndActive = false;
        _controlDown = false;
        _shiftDown = false;
        _altDown = false;
    }

    private void SyncModifierState(RdpViewModel vm, KeyModifiers modifiers)
    {
        SetModifierState(vm, ref _controlDown, modifiers.HasFlag(KeyModifiers.Control), 0x1D);
        SetModifierState(vm, ref _shiftDown, modifiers.HasFlag(KeyModifiers.Shift), 0x2A);
        SetModifierState(vm, ref _altDown, modifiers.HasFlag(KeyModifiers.Alt), 0x38);
    }

    private bool TrySendModifierKey(RdpViewModel vm, Key key, bool down)
    {
        switch (key)
        {
            case Key.LeftCtrl:
            case Key.RightCtrl:
                SetModifierState(vm, ref _controlDown, down, key == Key.RightCtrl ? 0x0100u | 0x1D : 0x1D);
                return true;
            case Key.LeftShift:
                SetModifierState(vm, ref _shiftDown, down, 0x2A);
                return true;
            case Key.RightShift:
                SetModifierState(vm, ref _shiftDown, down, 0x36);
                return true;
            case Key.LeftAlt:
            case Key.RightAlt:
                SetModifierState(vm, ref _altDown, down, key == Key.RightAlt ? 0x0100u | 0x38 : 0x38);
                return true;
            default:
                return false;
        }
    }

    private static void SetModifierState(RdpViewModel vm, ref bool current, bool desired, uint scancode)
    {
        if (current == desired)
            return;

        vm.SendKey(scancode, desired);
        current = desired;
    }

    private static bool TryGetPrintableScancode(KeyEventArgs e, out uint scancode, out bool needsShift)
    {
        scancode = 0;
        needsShift = false;

        if (e.Key >= Key.A && e.Key <= Key.Z)
        {
            needsShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            scancode = e.Key switch
            {
                Key.A => 0x1E,
                Key.B => 0x30,
                Key.C => 0x2E,
                Key.D => 0x20,
                Key.E => 0x12,
                Key.F => 0x21,
                Key.G => 0x22,
                Key.H => 0x23,
                Key.I => 0x17,
                Key.J => 0x24,
                Key.K => 0x25,
                Key.L => 0x26,
                Key.M => 0x32,
                Key.N => 0x31,
                Key.O => 0x18,
                Key.P => 0x19,
                Key.Q => 0x10,
                Key.R => 0x13,
                Key.S => 0x1F,
                Key.T => 0x14,
                Key.U => 0x16,
                Key.V => 0x2F,
                Key.W => 0x11,
                Key.X => 0x2D,
                Key.Y => 0x15,
                Key.Z => 0x2C,
                _ => 0
            };
            return scancode != 0;
        }

        if (e.Key >= Key.D0 && e.Key <= Key.D9)
        {
            needsShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var index = e.Key - Key.D0;
            scancode = index == 0 ? 0x0B : 0x01 + (uint)index;
            return true;
        }

        if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
        {
            scancode = 0x52 + (uint)(e.Key - Key.NumPad0);
            return true;
        }

        needsShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        scancode = e.Key switch
        {
            Key.Space => 0x39,
            Key.OemMinus => 0x0C,
            Key.OemPlus => 0x0D,
            Key.OemOpenBrackets => 0x1A,
            Key.OemCloseBrackets => 0x1B,
            Key.OemPipe => 0x2B,
            Key.OemSemicolon => 0x27,
            Key.OemQuotes => 0x28,
            Key.OemComma => 0x33,
            Key.OemPeriod => 0x34,
            Key.OemQuestion => 0x35,
            Key.OemTilde => 0x29,
            Key.Decimal => 0x53,
            Key.Add => 0x4E,
            Key.Subtract => 0x4A,
            Key.Multiply => 0x37,
            Key.Divide => 0x0100 | 0x35,
            _ => 0
        };

        return scancode != 0;
    }

    private static uint ToRdpScancode(KeyEventArgs e)
    {
        return e.Key switch
        {
            Key.Escape => 0x01,
            Key.Back => 0x0E,
            Key.Tab => 0x0F,
            Key.Enter => 0x1C,
            Key.F1 => 0x3B,
            Key.F2 => 0x3C,
            Key.F3 => 0x3D,
            Key.F4 => 0x3E,
            Key.F5 => 0x3F,
            Key.F6 => 0x40,
            Key.F7 => 0x41,
            Key.F8 => 0x42,
            Key.F9 => 0x43,
            Key.F10 => 0x44,
            Key.F11 => 0x57,
            Key.F12 => 0x58,
            Key.Home => 0x0100 | 0x47,
            Key.Up => 0x0100 | 0x48,
            Key.PageUp => 0x0100 | 0x49,
            Key.Left => 0x0100 | 0x4B,
            Key.Right => 0x0100 | 0x4D,
            Key.End => 0x0100 | 0x4F,
            Key.Down => 0x0100 | 0x50,
            Key.PageDown => 0x0100 | 0x51,
            Key.Insert => 0x0100 | 0x52,
            Key.Delete => 0x0100 | 0x53,
            Key.CapsLock => 0x3A,
            Key.NumLock => 0x45,
            Key.Scroll => 0x46,
            Key.PrintScreen => 0x0100 | 0x37,
            Key.LWin => 0x0100 | 0x5B,
            Key.RWin => 0x0100 | 0x5C,
            Key.Apps => 0x0100 | 0x5D,
            _ => 0
        };
    }
}
