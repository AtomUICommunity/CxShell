using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class RdpMonitorWindow : AtomUI.Desktop.Controls.Window
{
    private readonly RdpView _ownerView;
    private readonly RdpViewModel _vm;
    private readonly Screen _screen;
    private readonly int _monitorIndex;
    private readonly Action<int> _onCloseRequested;
    private Rect _virtualSliceRect;
    private bool _keepTaskbar;
    private bool _isWindowed;
    private bool _updatingGeometry;
    private bool _isClosingProgrammatically;
    private ushort _buttonFlags;
    private double _pendingWheelY;
    private ushort _lastRemoteX;
    private ushort _lastRemoteY;

    public RdpMonitorWindow()
    {
        InitializeComponent();
        _ownerView = null!;
        _vm = null!;
        _screen = null!;
        _onCloseRequested = _ => { };
    }

    public RdpMonitorWindow(
        RdpView ownerView,
        RdpViewModel vm,
        Screen screen,
        int monitorIndex,
        Rect virtualSliceRect,
        Action<int> onCloseRequested,
        bool startWindowed = false,
        string? customTitle = null)
    {
        InitializeComponent();
        _ownerView = ownerView;
        _vm = vm;
        _screen = screen;
        _monitorIndex = monitorIndex;
        _virtualSliceRect = virtualSliceRect;
        _onCloseRequested = onCloseRequested;
        _isWindowed = startWindowed;

        AddHandler(KeyDownEvent, OnMonitorKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnMonitorKeyUp, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, (_, e) => _ownerView.ForwardDragOver(e));
        AddHandler(DragDrop.DropEvent, (_, e) => _ownerView.ForwardDrop(e));
        GotFocus += (_, _) => _ownerView.ForwardGotFocus();
        LostFocus += (_, _) => _ownerView.ForwardLostFocus();
        PropertyChanged += OnWindowPropertyChanged;

        var scalePct = (int)Math.Round(screen.Scaling * 100);
        var titleText = customTitle ?? $"屏幕 {monitorIndex + 1} · {screen.Bounds.Width}×{screen.Bounds.Height} · {scalePct}%";
        Title = titleText;

        FloatingBar.Endpoint = titleText;
        FloatingBar.CloseToolTip = "关闭此屏";
        FloatingBar.MinimizeRequested += (_, _) => WindowState = WindowState.Minimized;
        FloatingBar.RestoreRequested += (_, _) => OnFloatingBarRestoreRequested();
        FloatingBar.TaskbarModeToggleRequested += OnToggleImmersiveModeClick;
        FloatingBar.CloseRequested += (_, _) => _onCloseRequested(_monitorIndex);
        FloatingBar.InteractionCompleted += () => SliceSurface.Focus();
        FloatingBar.DragRestoreRequested += e =>
        {
            if (!_isWindowed)
            {
                _isWindowed = true;
                ApplyWindowGeometry();
                BeginMoveDrag(e);
                e.Handled = true;
                return;
            }

            if (WindowState is WindowState.Maximized or WindowState.FullScreen)
            {
                WindowState = WindowState.Normal;
                BeginMoveDrag(e);
                e.Handled = true;
            }
        };

        ApplyWindowGeometry();
        UpdateSlice(_virtualSliceRect);

        _vm.FramebufferInvalidated += OnFramebufferInvalidated;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) =>
        {
            FloatingBar?.StopTimer();
            _vm.FramebufferInvalidated -= OnFramebufferInvalidated;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            if (!_isClosingProgrammatically)
            {
                _isClosingProgrammatically = true;
                _onCloseRequested(_monitorIndex);
            }
        };
    }

    public void CloseProgrammatically()
    {
        _isClosingProgrammatically = true;
        Close();
    }

    private void OnMonitorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleImmersiveFullScreen();
            e.Handled = true;
            return;
        }

        _ownerView.ForwardKeyDown(e);
    }

    private void OnMonitorKeyUp(object? sender, KeyEventArgs e)
    {
        _ownerView.ForwardKeyUp(e);
    }

    public int MonitorIndex => _monitorIndex;

    public void UpdateSlice(Rect virtualSliceRect)
    {
        _virtualSliceRect = virtualSliceRect;
        SliceSurface.Source = _vm.Framebuffer;
        SliceSurface.SourceRect = virtualSliceRect;
        SliceSurface.InvalidateVisual();
    }

    public void SetRemoteCursor(Cursor cursor)
    {
        SliceSurface.Cursor = cursor;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_updatingGeometry || e.Property != WindowStateProperty)
            return;

        var state = WindowState;

        // This window only has two presentations: a framed window and a
        // borderless window sized to the monitor. Entering
        // WindowState.FullScreen would activate AtomUI's full screen popover
        // layer, which paints a window wide title bar strip (logo, title,
        // leave-full-screen and close) along the top edge as soon as the
        // pointer touches it. The caption button goes down that path, so fold
        // the state back into the borderless presentation instead of letting
        // the vendor chrome take over.
        if (state == WindowState.FullScreen)
        {
            _isWindowed = false;
            _keepTaskbar = false;
            WindowState = WindowState.Normal;
            ApplyWindowGeometry();
            return;
        }

        if (!_isWindowed)
            return;

        var isImmersive = state == WindowState.Maximized;
        IsTitleBarVisible = !isImmersive;
        WindowDecorations = WindowDecorations.Full;
        FloatingBar.ShowMinimizeButton = true;
        FloatingBar.RestoreToolTip = "还原窗口";
        FloatingBar.TaskbarModeLabel = "保留任务栏";
        FloatingBar.SetImmersiveActive(isImmersive);
    }

    private void ApplyWindowGeometry()
    {
        _updatingGeometry = true;
        try
        {
            var targetScreen = _monitorIndex >= 100 && IsVisible
                ? (Screens.ScreenFromWindow(this) ?? _screen)
                : _screen;
            var scaling = targetScreen.Scaling > 0 ? targetScreen.Scaling : 1.0;

            if (_isWindowed)
            {
                WindowState = WindowState.Normal;
                IsTitleBarVisible = true;
                WindowDecorations = WindowDecorations.Full;
                CanResize = true;
                ShowInTaskbar = true;
                var area = targetScreen.WorkingArea;
                var w = Math.Max(640, area.Width * 0.78);
                var h = Math.Max(420, area.Height * 0.78);
                Position = new PixelPoint(
                    area.X + (int)((area.Width - w) / 2),
                    area.Y + (int)((area.Height - h) / 2));
                Width = w / scaling;
                Height = h / scaling;
                FloatingBar.ShowMinimizeButton = true;
                FloatingBar.RestoreToolTip = "还原窗口";
                FloatingBar.TaskbarModeLabel = "保留任务栏";
                FloatingBar.SetImmersiveActive(false);
                return;
            }

            WindowState = WindowState.Normal;
            IsTitleBarVisible = false;
            WindowDecorations = WindowDecorations.None;
            CanResize = false;
            ShowInTaskbar = false;
            var rect = _keepTaskbar ? targetScreen.WorkingArea : targetScreen.Bounds;
            Position = new PixelPoint(rect.X, rect.Y);
            Width = rect.Width / scaling;
            Height = rect.Height / scaling;
            FloatingBar.ShowMinimizeButton = false;
            // The window already fills the monitor: only the way back matters.
            FloatingBar.RestoreToolTip = "窗口化";
            FloatingBar.TaskbarModeLabel = _keepTaskbar ? "真全屏" : "保留任务栏";
            FloatingBar.SetImmersiveActive(true);
        }
        finally
        {
            _updatingGeometry = false;
        }
    }

    private void OnFramebufferInvalidated()
    {
        SliceSurface.Source = _vm.Framebuffer;
        SliceSurface.InvalidateVisual();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RdpViewModel.Framebuffer))
        {
            SliceSurface.Source = _vm.Framebuffer;
            SliceSurface.InvalidateVisual();
        }
    }

    private void OnFloatingBarRestoreRequested()
    {
        if (!_isWindowed)
        {
            _isWindowed = true;
            ApplyWindowGeometry();
        }
        else if (WindowState is WindowState.Maximized or WindowState.FullScreen)
        {
            WindowState = WindowState.Normal;
        }

        SliceSurface.Focus();
    }

    private void ToggleImmersiveFullScreen()
    {
        if (_isWindowed)
        {
            if (WindowState == WindowState.FullScreen)
            {
                WindowState = WindowState.Normal;
            }
            else
            {
                _isWindowed = false;
                _keepTaskbar = false;
                ApplyWindowGeometry();
            }
        }
        else
        {
            _isWindowed = true;
            ApplyWindowGeometry();
        }

        SliceSurface.Focus();
    }

    private void OnToggleImmersiveModeClick()
    {
        if (_isWindowed)
        {
            _isWindowed = false;
            _keepTaskbar = true;
        }
        else
        {
            _keepTaskbar = !_keepTaskbar;
        }

        ApplyWindowGeometry();
    }

    private void OnEnterKeepTaskbarClick(object? sender, RoutedEventArgs e)
    {
        _isWindowed = false;
        _keepTaskbar = true;
        ApplyWindowGeometry();
        SliceSurface.Focus();
    }

    public bool TryMapScreenPoint(PixelPoint screenPoint, out ushort remoteX, out ushort remoteY)
    {
        remoteX = 0;
        remoteY = 0;
        var bounds = SliceSurface.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 || _virtualSliceRect.Width <= 0 || _virtualSliceRect.Height <= 0)
            return false;

        var topLeft = SliceSurface.PointToScreen(new Point(0, 0));
        var bottomRight = SliceSurface.PointToScreen(new Point(bounds.Width, bounds.Height));
        var sw = bottomRight.X - topLeft.X;
        var sh = bottomRight.Y - topLeft.Y;
        if (sw <= 0 || sh <= 0 ||
            screenPoint.X < topLeft.X || screenPoint.X > bottomRight.X ||
            screenPoint.Y < topLeft.Y || screenPoint.Y > bottomRight.Y)
        {
            return false;
        }

        var relX = Math.Clamp((double)(screenPoint.X - topLeft.X) / sw, 0, 1);
        var relY = Math.Clamp((double)(screenPoint.Y - topLeft.Y) / sh, 0, 1);
        remoteX = (ushort)Math.Clamp((int)Math.Round(_virtualSliceRect.X + relX * _virtualSliceRect.Width), 0, ushort.MaxValue);
        remoteY = (ushort)Math.Clamp((int)Math.Round(_virtualSliceRect.Y + relY * _virtualSliceRect.Height), 0, ushort.MaxValue);
        return true;
    }

    private bool TryMapPointer(PointerEventArgs e, out ushort remoteX, out ushort remoteY, bool allowFallbackOnGap = false)
    {
        remoteX = 0;
        remoteY = 0;
        var bounds = SliceSurface.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 || _virtualSliceRect.Width <= 0 || _virtualSliceRect.Height <= 0)
            return false;

        var pos = e.GetPosition(SliceSurface);
        if (pos.X < 0 || pos.X > bounds.Width || pos.Y < 0 || pos.Y > bounds.Height)
        {
            var screenPt = SliceSurface.PointToScreen(pos);
            if (_ownerView.TryMapGlobalScreenPoint(screenPt, out remoteX, out remoteY))
            {
                _lastRemoteX = remoteX;
                _lastRemoteY = remoteY;
                return true;
            }

            if (allowFallbackOnGap)
            {
                remoteX = _lastRemoteX;
                remoteY = _lastRemoteY;
                return true;
            }

            return false;
        }

        var relX = Math.Clamp(pos.X / bounds.Width, 0, 1);
        var relY = Math.Clamp(pos.Y / bounds.Height, 0, 1);
        var vx = _virtualSliceRect.X + relX * _virtualSliceRect.Width;
        var vy = _virtualSliceRect.Y + relY * _virtualSliceRect.Height;
        remoteX = (ushort)Math.Clamp((int)Math.Round(vx), 0, ushort.MaxValue);
        remoteY = (ushort)Math.Clamp((int)Math.Round(vy), 0, ushort.MaxValue);
        _lastRemoteX = remoteX;
        _lastRemoteY = remoteY;
        return true;
    }

    private void OnSurfacePointerMoved(object? sender, PointerEventArgs e)
    {
        if (TryMapPointer(e, out var x, out var y))
            _vm.SendPointer((ushort)(0x0800 | _buttonFlags), x, y);
    }

    private void OnSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        SliceSurface.Focus();
        _ownerView.RequestClipboardSync();
        if (!TryMapPointer(e, out var x, out var y))
            return;

        var point = e.GetCurrentPoint(SliceSurface);
        ushort flag = 0x1000; // left button (also covers touch/pen primary tap)
        if (point.Properties.IsRightButtonPressed)
            flag = 0x2000;
        else if (point.Properties.IsMiddleButtonPressed)
            flag = 0x4000;

        _buttonFlags |= flag;
        _vm.SendPointer((ushort)(0x8000 | flag), x, y);
    }

    private void OnSurfacePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!TryMapPointer(e, out var x, out var y, allowFallbackOnGap: true))
            return;

        ushort flag = e.InitialPressMouseButton switch
        {
            MouseButton.Right => 0x2000,
            MouseButton.Middle => 0x4000,
            _ => 0x1000
        };

        _buttonFlags = (ushort)(_buttonFlags & ~flag);
        _vm.SendPointer(flag, x, y);
    }

    private void OnSurfacePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!TryMapPointer(e, out var x, out var y))
            return;

        var notches = CxShell.Services.RdpWheelInput.TakeNotches(ref _pendingWheelY, e.Delta.Y);
        if (notches != 0)
        {
            var wheelFlag = CxShell.Services.RdpWheelInput.GetNotchFlags(notches);
            for (var i = 0; i < Math.Abs(notches); i++)
                _vm.SendPointer(wheelFlag, x, y);
        }

        e.Handled = true;
    }
}
