using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CxShell.Models;
using CxShell.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CxShell.ViewModels;

public partial class RdpViewModel : ObservableObject, IDisposable
{
    private readonly RdpBridgeClient _client = new();
    private bool _started;
    private bool _hadSession;
    private bool _suppressLostOverlay;
    private RdpReconnectPhase _reconnectPhase = RdpReconnectPhase.None;
    private int _reconnectAttempt;
    private double _renderScaling = 1;
    private CancellationTokenSource? _resizeDebounceCts;
    private CancellationTokenSource? _resizeReconnectCts;
    private CancellationTokenSource? _connectWatchdogCts;
    private int? _runtimeDesktopWidth;
    private int? _runtimeDesktopHeight;
    private string? _lastMonitorLayoutSignature;
    private DateTime _lastMonitorLayoutAppliedUtc;
    /// <summary>
    /// Window in which a repeated identical monitor layout is dropped. Coming
    /// back to a single screen publishes a layout, the resulting desktop resize
    /// changes the viewport, and the viewport change asks for the same layout
    /// again - measured gaps between those repeats are 320-360 ms, so the window
    /// has to be wider than the 300 ms that first looked sufficient.
    /// </summary>
    private const int MonitorLayoutDebounceMilliseconds = 500;
    private int? _runtimeDisplayScalePercent;
    private WriteableBitmap? _bitmapA;
    private WriteableBitmap? _bitmapB;
    private bool _useBitmapA;
    private int _bitmapWidth;
    private int _bitmapHeight;
    /// <summary>
    /// Framebuffer bitmaps replaced by a resolution change, kept alive until the
    /// following frame so every consumer has rebound to the new pair before the
    /// old one is disposed.
    /// </summary>
    private readonly List<WriteableBitmap> _retiredFrames = new();
    private static readonly int ConnectTimeoutSeconds = ResolveConnectTimeoutSeconds();

    private static int ResolveConnectTimeoutSeconds()
    {
        // The native bridge bounds each TCP handshake; this watchdog is a
        // backstop for a host that ignores the abort entirely. Set to 0 to
        // disable it.
        var raw = Environment.GetEnvironmentVariable("CXSHELL_RDP_CONNECT_TIMEOUT_SECONDS");
        return int.TryParse(raw, out var seconds) && seconds >= 0 ? seconds : 45;
    }

    [ObservableProperty] private WriteableBitmap? _framebuffer;
    [ObservableProperty] private string _statusText = "RDP disconnected";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SupportsMultiMonitor))]
    private bool _isConnected;
    [ObservableProperty] private int _remoteWidth;
    [ObservableProperty] private int _remoteHeight;
    [ObservableProperty] private bool _isFitToWindow = true;
    [ObservableProperty] private bool _isMultiMonitorActive;
    [ObservableProperty] private int _screenScalePercent = 100;
    [ObservableProperty] private bool _isClipboardChannelReady;
    [ObservableProperty] private string _remoteClipboardText = string.Empty;

    /// <summary>
    /// Rough link quality for the full screen connection bar: 0 unknown,
    /// 1 poor, 2 acceptable, 3 good, 4 excellent. RDP does not expose a signal
    /// strength, so a bare TCP handshake to the session endpoint is timed.
    /// </summary>
    [ObservableProperty] private int _linkQualityLevel;

    private DispatcherTimer? _linkQualityTimer;
    private bool _linkQualityProbeRunning;

    /// <summary>
    /// Covers the viewport until the first complete desktop frame arrives.
    /// Legacy RDP sends a multi-second top-to-bottom repaint wave; showing the
    /// half-painted buffer is what makes the screen appear to fill in slowly.
    /// </summary>
    [ObservableProperty] private bool _isDesktopLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectionOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(IsCancelRetryVisible))]
    private bool _isReconnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectionOverlayVisible))]
    private bool _isConnectionLost;

    [ObservableProperty] private string _reconnectStatusText = string.Empty;

    public bool IsConnectionOverlayVisible => IsReconnecting || IsConnectionLost;
    public bool IsCancelRetryVisible => IsReconnecting;
    public string ReconnectLabel => Text("Rdp.Reconnect.Button");
    public string CancelRetryLabel => Text("Rdp.Reconnect.StopRetrying");
    public string DesktopLoadingText => Text("Rdp.LoadingDesktop");

    private static string Text(string key) => LocalizationService.Shared.Text(key);

    public SessionInfo Session { get; }
    public string? Password { get; }
    public event Action<int, int, int, int, int, byte[]?>? PointerShapeChanged;
    public event Action? FramebufferInvalidated;
    public event Action<uint, byte[]>? RemoteClipboardImageReceived;
    public event Action<string[]>? RemoteClipboardFilesReceived;
    public double FixedScaleFactor => Math.Clamp(ScreenScalePercent, 10, 500) / 100.0;
    public string ScaleModeText => IsFitToWindow
        ? "Fit to window"
        : ScreenScalePercent == 100 ? "Original size" : $"{ScreenScalePercent}%";

    public RdpViewModel(SessionInfo session, string? password)
    {
        Session = session;
        Password = password;
        ScreenScalePercent = ResolveScreenScalePercent(session);
        IsFitToWindow = ResolveInitialFitToWindow(session, ScreenScalePercent);
        _client.FramebufferUpdated += OnFramebufferUpdated;
        _client.StatusChanged += message => Dispatcher.UIThread.Post(() => HandleStatus(message));
        _client.ClipboardTextReceived += OnClipboardTextReceived;
        _client.ClipboardImageReceived += OnClipboardImageReceived;
        _client.ClipboardFilesReceived += OnClipboardFilesReceived;
        _client.PointerShapeChanged += OnPointerShapeChanged;
        LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
        _client.Disconnected += () => Dispatcher.UIThread.Post(() =>
        {
            // A disconnect we asked for (tab close, manual reconnect, resize)
            // must not raise the "connection lost" overlay. Bail out before
            // touching state, because a manual reconnect may already have
            // started a new session on this view model.
            if (_suppressLostOverlay)
            {
                _suppressLostOverlay = false;
                return;
            }

            _started = false;
            IsConnected = false;
            IsClipboardChannelReady = false;
            IsReconnecting = false;
            IsDesktopLoading = true;
            _hadSession = true;
            StatusText = "RDP disconnected";

            if (string.IsNullOrWhiteSpace(ReconnectStatusText))
            {
                _reconnectPhase = RdpReconnectPhase.Failed;
                _reconnectAttempt = 0;
                ReconnectStatusText = Text("Rdp.Reconnect.Failed");
            }
            IsConnectionLost = true;
        });
    }

    public void Start()
    {
        if (_started || IsConnected)
            return;

        _started = true;
        IsDesktopLoading = true;
        StatusText = "Starting RDP bridge...";
        StartConnectWatchdog();

        var initialWidth = _runtimeDesktopWidth;
        var initialHeight = _runtimeDesktopHeight;
        var initialScale = RdpDpiScale.ResolveScalePercent(ResolveDpiPolicy(), _renderScaling);
        _runtimeDisplayScalePercent = initialScale;

        _ = Task.Run(() =>
        {
            try
            {
                _client.Connect(
                    Session,
                    Password,
                    initialWidth,
                    initialHeight,
                    initialScale);
                // Do not reset IsConnected here: Connect() only starts the
                // native worker. A posted "false" used to race the "RDP
                // connected." status and clear the state right after the
                // first frame arrived.
            }
            catch (DllNotFoundException ex)
            {
                Dispatcher.UIThread.Post(() => ReportFailure(
                    $"RDP native library load failed: {RdpBridgeClient.GetNativeLibraryLoadErrorMessage(ex)}"));
            }
            catch (EntryPointNotFoundException ex)
            {
                Dispatcher.UIThread.Post(() => ReportFailure($"CxRdpBridge API mismatch: {ex.Message}"));
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => ReportFailure($"RDP failed: {ex.Message}"));
            }
        });
    }

    private void StartConnectWatchdog()
    {
        CancelConnectWatchdog();
        if (ConnectTimeoutSeconds <= 0)
            return;

        var cts = new CancellationTokenSource();
        _connectWatchdogCts = cts;
        _ = WatchConnectAsync(cts.Token);
    }

    private async Task WatchConnectAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(ConnectTimeoutSeconds), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested || IsConnected || !_started)
                return;

            ReportFailure(Text("Rdp.Connect.Timeout"));
            // Disconnect() joins the native connection thread, so keep that
            // (potentially blocking) call off the UI thread.
            _ = Task.Run(() => _client.Disconnect());
        });
    }

    private void CancelConnectWatchdog()
    {
        var cts = _connectWatchdogCts;
        _connectWatchdogCts = null;
        if (cts == null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down by another path.
        }
        cts.Dispose();
    }

    /// <summary>
    /// Surfaces a failed (re)connect. Once the view has shown a live session, a
    /// failure also raises the overlay so the Reconnect button stays reachable.
    /// </summary>
    private void ReportFailure(string message)
    {
        CancelConnectWatchdog();
        _started = false;
        StatusText = message;

        if (!_hadSession)
            return;

        IsConnected = false;
        IsClipboardChannelReady = false;
        IsReconnecting = false;
        IsConnectionLost = true;
        ReconnectStatusText = message;
    }

    private void HandleStatus(string message)
    {
        switch (RdpReconnectStatus.Classify(message))
        {
            case RdpReconnectPhase.ConnectionLost:
                _reconnectPhase = RdpReconnectPhase.ConnectionLost;
                _reconnectAttempt = 0;
                IsReconnecting = true;
                IsConnectionLost = false;
                RefreshReconnectText();
                StatusText = ReconnectStatusText;
                return;

            case RdpReconnectPhase.Reconnecting:
                _reconnectPhase = RdpReconnectPhase.Reconnecting;
                _reconnectAttempt = RdpReconnectStatus.ParseAttempt(message);
                IsReconnecting = true;
                IsConnectionLost = false;
                RefreshReconnectText();
                StatusText = ReconnectStatusText;
                return;

            case RdpReconnectPhase.Reconnected:
                CancelConnectWatchdog();
                _reconnectPhase = RdpReconnectPhase.None;
                _reconnectAttempt = 0;
                IsReconnecting = false;
                IsConnectionLost = false;
                ReconnectStatusText = string.Empty;
                IsConnected = true;
                // A fresh session knows nothing about the layout we applied
                // before the reconnect, so the next request must not be deduped.
                _lastMonitorLayoutSignature = null;
                _started = true;
                _hadSession = true;
                _suppressLostOverlay = false;
                StatusText = "RDP ready";
                return;

            case RdpReconnectPhase.Failed:
                CancelConnectWatchdog();
                _reconnectPhase = RdpReconnectPhase.Failed;
                _reconnectAttempt = 0;
                IsReconnecting = false;
                IsConnectionLost = true;
                IsConnected = false;
                _started = false;
                _hadSession = true;
                IsClipboardChannelReady = false;
                RefreshReconnectText();
                StatusText = "RDP disconnected";
                return;

            default:
                break;
        }

        // A single failed security profile, a queued resize or a clipboard
        // retry is not a disconnect. Keep showing "connecting" instead of
        // tearing the view down while the bridge is still working.
        if (RdpReconnectStatus.IsTransientFailure(message))
        {
            StatusText = "Connecting RDP...";
            return;
        }

        // The bridge emits exactly one terminal line after all fallbacks are
        // exhausted; show the real error instead of leaving "Connecting...".
        if (message.StartsWith("RDP connection failed.", StringComparison.OrdinalIgnoreCase))
        {
            ReportFailure(message);
            return;
        }

        StatusText = GetDisplayStatusText(message);

        if (message.StartsWith("RDP clipboard channel ready", StringComparison.OrdinalIgnoreCase))
        {
            IsClipboardChannelReady = true;
            return;
        }

        if (string.Equals(message, "RDP connected.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message, "RDP connected", StringComparison.OrdinalIgnoreCase))
        {
            CancelConnectWatchdog();
            IsConnected = true;
            _lastMonitorLayoutSignature = null;
            _started = true;
            _hadSession = true;
            _suppressLostOverlay = false;
            IsReconnecting = false;
            IsConnectionLost = false;
            ReconnectStatusText = string.Empty;
            return;
        }

        if (message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("closed", StringComparison.OrdinalIgnoreCase))
        {
            IsConnected = false;
            _started = false;
            IsClipboardChannelReady = false;
        }
    }

    private void RefreshReconnectText()
    {
        if (_reconnectPhase == RdpReconnectPhase.ConnectionLost)
            ReconnectStatusText = Text("Rdp.Reconnect.Lost");
        else if (_reconnectPhase == RdpReconnectPhase.Reconnecting)
            ReconnectStatusText = string.Format(Text("Rdp.Reconnect.Attempt"), _reconnectAttempt);
        else if (_reconnectPhase == RdpReconnectPhase.Failed)
            ReconnectStatusText = Text("Rdp.Reconnect.Failed");
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        RefreshReconnectText();
        OnPropertyChanged(nameof(ReconnectLabel));
        OnPropertyChanged(nameof(CancelRetryLabel));
        OnPropertyChanged(nameof(DesktopLoadingText));
    });

    private static string GetDisplayStatusText(string message)
    {
        if (string.Equals(message, "RDP clipboard waiting for server MonitorReady.", StringComparison.OrdinalIgnoreCase))
            return "Remote clipboard service is not responding. Restart rdpclip.exe on the remote Windows session.";

        if (message.StartsWith("RDP clipboard channel ready", StringComparison.OrdinalIgnoreCase))
            return "RDP clipboard ready";

        return message;
    }

    [RelayCommand]
    public void Reconnect()
    {
        CancelPendingResize();
        CancelConnectWatchdog();
        _reconnectPhase = RdpReconnectPhase.None;
        _reconnectAttempt = 0;
        IsReconnecting = false;
        IsConnectionLost = false;
        ReconnectStatusText = string.Empty;
        Disconnect();
        _started = false;
        Start();
    }

    /// <summary>
    /// Stops the automatic reconnect loop before it gives up on its own so the
    /// tab falls back to the manual Reconnect button.
    /// </summary>
    [RelayCommand]
    public void CancelReconnect()
    {
        // Disconnect suppresses the native teardown notification because this
        // shutdown is deliberate; set the lost state here instead.
        CancelConnectWatchdog();
        Disconnect();
        _reconnectPhase = RdpReconnectPhase.Failed;
        _reconnectAttempt = 0;
        IsReconnecting = false;
        IsConnectionLost = true;
        RefreshReconnectText();
        StatusText = "RDP disconnected";
    }

    public void Disconnect()
    {
        CancelPendingResize();
        CancelConnectWatchdog();
        // The native bridge only reports a disconnect while it believes the
        // transport is live, so remember whether this teardown can produce one.
        _suppressLostOverlay = _started || IsConnected;

        _client.Disconnect();
        _lastMonitorLayoutSignature = null;
        _started = false;
        IsConnected = false;
        IsClipboardChannelReady = false;
        StatusText = "RDP disconnected";
    }

    internal int? InitialDesktopWidth => _runtimeDesktopWidth;
    internal int? InitialDesktopHeight => _runtimeDesktopHeight;
    internal double InitialRenderScaling => _renderScaling;

    public void ConfigureInitialDisplay(Size viewportSize, PixelSize? monitorPixelSize, double renderScaling)
    {
        _renderScaling = renderScaling > 0 ? renderScaling : 1;

        if (_started || IsConnected)
            return;

        var size = RdpDisplaySizeResolver.ResolveInitial(
            Session.RdpWindowSize,
            viewportSize,
            monitorPixelSize,
            renderScaling);
        if (size == null)
            return;

        _runtimeDesktopWidth = size.Value.Width;
        _runtimeDesktopHeight = size.Value.Height;
    }

    private void CancelPendingResize()
    {
        var debounceCts = _resizeDebounceCts;
        _resizeDebounceCts = null;
        if (debounceCts != null)
        {
            try { debounceCts.Cancel(); } catch (ObjectDisposedException) { }
            debounceCts.Dispose();
        }

        var reconnectCts = _resizeReconnectCts;
        _resizeReconnectCts = null;
        if (reconnectCts != null)
        {
            try { reconnectCts.Cancel(); } catch (ObjectDisposedException) { }
            reconnectCts.Dispose();
        }
    }

    public void RequestViewportResize(Size viewportSize, double renderScaling = 1)
    {
        if (renderScaling > 0 && double.IsFinite(renderScaling))
            _renderScaling = renderScaling;

        if (!IsConnected)
            return;

        if (!UsesReconnectResizeMode(Session))
        {
            if (ResolveDpiPolicy() == RdpDpiPolicy.FollowLocal)
                UpdateDpiScaling(_renderScaling);
            return;
        }

        if (viewportSize.Width < 320 || viewportSize.Height < 240)
            return;

        var size = RdpDisplaySizeResolver.ResolveViewport(viewportSize, _renderScaling);
        if (size == null)
            return;

        var width = size.Value.Width;
        var height = size.Value.Height;
        var scale = RdpDpiScale.ResolveScalePercent(ResolveDpiPolicy(), _renderScaling);
        var currentWidth = _runtimeDesktopWidth ?? (RemoteWidth > 0 ? RemoteWidth : Math.Max(1, Session.RdpDesktopWidth));
        var currentHeight = _runtimeDesktopHeight ?? (RemoteHeight > 0 ? RemoteHeight : Math.Max(1, Session.RdpDesktopHeight));
        var currentScale = _runtimeDisplayScalePercent ?? scale;
        if (Math.Abs(width - currentWidth) < 2 &&
            Math.Abs(height - currentHeight) < 2 &&
            scale == currentScale)
        {
            return;
        }

        // Debounce rapid window drags / maximize / fullscreen transitions so we
        // only send the settled layout once; Windows MS-RDPEDISP drops or
        // throttles back-to-back SendMonitorLayout PDUs.
        var previousDebounce = _resizeDebounceCts;
        var debounceCts = new CancellationTokenSource();
        _resizeDebounceCts = debounceCts;
        if (previousDebounce != null)
        {
            try { previousDebounce.Cancel(); } catch (ObjectDisposedException) { }
            previousDebounce.Dispose();
        }

        _ = ApplyViewportResizeDebouncedAsync(width, height, scale, debounceCts.Token);
    }

    private async Task ApplyViewportResizeDebouncedAsync(int width, int height, int scale, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested || !IsConnected)
                    return;

                var currentWidth = _runtimeDesktopWidth ?? (RemoteWidth > 0 ? RemoteWidth : Math.Max(1, Session.RdpDesktopWidth));
                var currentHeight = _runtimeDesktopHeight ?? (RemoteHeight > 0 ? RemoteHeight : Math.Max(1, Session.RdpDesktopHeight));
                var currentScale = _runtimeDisplayScalePercent ?? scale;
                if (Math.Abs(width - currentWidth) < 2 &&
                    Math.Abs(height - currentHeight) < 2 &&
                    scale == currentScale)
                {
                    return;
                }

                if (_client.UpdateDisplay(width, height, scale))
                {
                    _runtimeDesktopWidth = width;
                    _runtimeDesktopHeight = height;
                    _runtimeDisplayScalePercent = scale;
                    StatusText = $"RDP display updated to {width}x{height} @ {scale}%.";
                    return;
                }

                // The bridge cannot resize in place (older native build or the display
                // control channel was not negotiated) - fall back to a full reconnect.
                _resizeReconnectCts?.Cancel();
                var cts = new CancellationTokenSource();
                _resizeReconnectCts = cts;
                _ = ReconnectAfterResizeDelayAsync(width, height, cts.Token);
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Handles a render-scaling change that did not change the viewport size,
    /// such as moving the window to a monitor with a different DPI.
    /// </summary>
    public void UpdateDpiScaling(double renderScaling)
    {
        if (renderScaling > 0 && double.IsFinite(renderScaling))
            _renderScaling = renderScaling;

        if (!IsConnected || ResolveDpiPolicy() != RdpDpiPolicy.FollowLocal)
            return;

        var scale = RdpDpiScale.ResolveScalePercent(RdpDpiPolicy.FollowLocal, _renderScaling);
        if (scale == _runtimeDisplayScalePercent)
            return;

        var width = _runtimeDesktopWidth ?? (RemoteWidth > 0 ? RemoteWidth : Math.Max(1, Session.RdpDesktopWidth));
        var height = _runtimeDesktopHeight ?? (RemoteHeight > 0 ? RemoteHeight : Math.Max(1, Session.RdpDesktopHeight));

        if (_client.UpdateDisplay(width, height, scale))
        {
            _runtimeDesktopWidth = width;
            _runtimeDesktopHeight = height;
            _runtimeDisplayScalePercent = scale;
            StatusText = $"RDP display updated to {width}x{height} @ {scale}%.";
            return;
        }

        _runtimeDesktopWidth = width;
        _runtimeDesktopHeight = height;
        _runtimeDisplayScalePercent = scale;
        Reconnect();
    }

    private RdpDpiPolicy ResolveDpiPolicy()
    {
        return Enum.TryParse<RdpDpiPolicy>(Session.RdpDpiPolicy, true, out var policy)
            ? policy
            : RdpDpiPolicy.FollowLocal;
    }

    private async Task ReconnectAfterResizeDelayAsync(int width, int height, CancellationToken cancellationToken)
    {
        try
        {
            // The display-control DVC is negotiated ~1s after the connection.
            // Wait for it, then try an in-place resize once more before
            // falling back to the (expensive) full reconnect.
            await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested || !IsConnected)
                    return;

                var scale = RdpDpiScale.ResolveScalePercent(ResolveDpiPolicy(), _renderScaling);
                if (_client.UpdateDisplay(width, height, scale))
                {
                    _runtimeDesktopWidth = width;
                    _runtimeDesktopHeight = height;
                    _runtimeDisplayScalePercent = scale;
                    StatusText = $"RDP display updated to {width}x{height} @ {scale}%.";
                    return;
                }

                _runtimeDesktopWidth = width;
                _runtimeDesktopHeight = height;
                StatusText = $"RDP resizing to {width}x{height}...";
                Reconnect();
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void SendPointer(ushort flags, ushort x, ushort y)
    {
        if (IsConnected)
            _client.SendPointer(flags, x, y);
    }

    public void SendKey(uint key, bool down)
    {
        if (IsConnected)
            _client.SendKey(key, down);
    }

    public void SendUnicodeKey(char key, bool down)
    {
        if (IsConnected)
            _client.SendUnicodeKey(key, down);
    }

    public Task SendCtrlAltDeleteAsync()
    {
        return SendKeyCombinationAsync(RdpKeyboardShortcutSequences.CtrlAltDelete);
    }

    public Task SendAltTabAsync()
    {
        return SendKeyCombinationAsync(0x38, 0x0F);
    }

    public Task SendWindowsKeyAsync()
    {
        return SendKeyCombinationAsync(0x0100 | 0x5B);
    }

    public Task SendCtrlEscapeAsync()
    {
        return SendKeyCombinationAsync(0x1D, 0x01);
    }

    public Task SendAltF4Async()
    {
        return SendKeyCombinationAsync(0x38, 0x3E);
    }

    public Task SendTaskManagerAsync()
    {
        return SendKeyCombinationAsync(0x1D, 0x2A, 0x01);
    }

    public Task SendPrintScreenAsync()
    {
        return SendKeyCombinationAsync(RdpKeyboardShortcutSequences.SaveRemoteScreenshot);
    }

    private async Task SendKeyCombinationAsync(params uint[] scancodes)
    {
        await SendKeyCombinationAsync((IReadOnlyList<uint>)scancodes);
    }

    private async Task SendKeyCombinationAsync(IReadOnlyList<uint> scancodes)
    {
        if (!IsConnected || scancodes.Count == 0)
            return;

        try
        {
            foreach (var scancode in scancodes)
            {
                _client.SendKey(scancode, true);
                await Task.Delay(20);
            }

            await Task.Delay(40);
        }
        finally
        {
            for (var i = scancodes.Count - 1; i >= 0; i--)
            {
                _client.SendKey(scancodes[i], false);
                await Task.Delay(10);
            }
        }
    }

    public void SetClipboardText(string text)
    {
        if (IsConnected)
            _client.SetClipboardText(text);
    }

    public void SetClipboardRichText(string plainText, byte[]? htmlBytes, byte[]? rtfBytes)
    {
        if (IsConnected)
            _client.SetClipboardRichText(plainText, htmlBytes, rtfBytes);
    }

    public void SetClipboardImage(uint imageFormat, byte[] data)
    {
        if (IsConnected)
            _client.SetClipboardImage(imageFormat, data);
    }

    public void SetClipboardImage(byte[]? pngBytes, byte[]? dibBytes)
    {
        if (!IsConnected)
            return;

        if (dibBytes is { Length: > 0 })
            _client.SetClipboardImage(RdpBridgeClient.ClipboardImageFormatDib, dibBytes);
        else if (pngBytes is { Length: > 0 })
            _client.SetClipboardImage(RdpBridgeClient.ClipboardImageFormatPng, pngBytes);
    }

    public void SetClipboardFiles(IReadOnlyList<string> filePaths)
    {
        if (IsConnected)
            _client.SetClipboardFiles(filePaths);
    }

    public void SyncLockKeys(uint flags)
    {
        if (IsConnected)
            _client.SyncLockKeys(flags);
    }

    /// <summary>
    /// Whether the loaded bridge can drive a monitor layout. This is decided
    /// when a connect attempt starts rather than when the view model is built,
    /// so it is reported again whenever <see cref="IsConnected"/> changes:
    /// callers that gate a button on it would otherwise be stuck with the
    /// value they read before the session existed.
    /// </summary>
    public bool SupportsMultiMonitor => _client.SupportsMultiMonitor;

    public bool TryResizeMonitors(IReadOnlyList<RdpMonitorRect> monitors)
    {
        if (!IsConnected)
            return false;

        var signature = BuildMonitorLayoutSignature(monitors);
        if (signature == _lastMonitorLayoutSignature &&
            (DateTime.UtcNow - _lastMonitorLayoutAppliedUtc).TotalMilliseconds < MonitorLayoutDebounceMilliseconds)
        {
            // The layout that is already in force is being asked for again by a
            // resize that our own request caused. Reporting success keeps the
            // caller from rolling back to single screen.
            return true;
        }

        if (!_client.TryResizeMonitors(monitors))
        {
            _lastMonitorLayoutSignature = null;
            return false;
        }

        if (monitors.Count == 1)
        {
            _runtimeDesktopWidth = Math.Max(200, monitors[0].Width) & ~1;
            _runtimeDesktopHeight = Math.Max(200, monitors[0].Height);
            if (monitors[0].DesktopScaleFactor > 0)
                _runtimeDisplayScalePercent = monitors[0].DesktopScaleFactor;
        }

        _lastMonitorLayoutSignature = signature;
        _lastMonitorLayoutAppliedUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Describes a layout by the fields that actually reach the server, so two
    /// requests that would produce the same monitor set compare equal.
    /// </summary>
    private static string BuildMonitorLayoutSignature(IReadOnlyList<RdpMonitorRect> monitors)
    {
        if (monitors.Count == 0)
            return string.Empty;

        var builder = new System.Text.StringBuilder(monitors.Count * 24);
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            builder.Append(m.Left).Append(',')
                   .Append(m.Top).Append(',')
                   .Append(Math.Max(200, m.Width)).Append(',')
                   .Append(Math.Max(200, m.Height)).Append(',')
                   .Append(Math.Clamp(m.DesktopScaleFactor, 100, 500)).Append(',')
                   .Append(m.IsPrimary ? '1' : '0').Append(';');
        }

        return builder.ToString();
    }

    [RelayCommand]
    private void ToggleScaleMode()
    {
        IsFitToWindow = !IsFitToWindow;
    }

    partial void OnIsFitToWindowChanged(bool value)
    {
        OnPropertyChanged(nameof(ScaleModeText));
        OnPropertyChanged(nameof(FixedScaleFactor));
    }

    partial void OnScreenScalePercentChanged(int value)
    {
        OnPropertyChanged(nameof(ScaleModeText));
        OnPropertyChanged(nameof(FixedScaleFactor));
    }

    private static bool ResolveInitialFitToWindow(SessionInfo session, int screenScalePercent)
    {
        if (!string.Equals(session.RdpScreenScale, "Auto", StringComparison.OrdinalIgnoreCase) &&
            screenScalePercent > 0)
        {
            return false;
        }

        return !string.Equals(session.RdpResizeMode, "NotUsed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool UsesReconnectResizeMode(SessionInfo session)
    {
        return string.Equals(session.RdpResizeMode, "SmartReconnect", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(session.RdpResizeMode, "LegacyReconnect", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveScreenScalePercent(SessionInfo session)
    {
        return int.TryParse(session.RdpScreenScale, out var parsed) && parsed is >= 10 and <= 500
            ? parsed
            : 100;
    }

    private void OnFramebufferUpdated(object? sender, RdpFramebufferEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                DisposeRetiredFrames();
                var target = GetTargetBitmap(e.Width, e.Height);
                using (var locked = target.Lock())
                {
                    if (e.DirtyRect is { } dirty &&
                        e.Stride == locked.RowBytes &&
                        dirty.X >= 0 && dirty.Y >= 0 &&
                        dirty.X + dirty.Width <= locked.Size.Width &&
                        dirty.Y + dirty.Height <= locked.Size.Height)
                    {
                        var rowBytes = dirty.Width * 4;
                        var xBytes = dirty.X * 4;
                        for (var row = 0; row < dirty.Height; row++)
                        {
                            var offset = (dirty.Y + row) * e.Stride + xBytes;
                            if (offset + rowBytes <= e.PixelLength)
                                Marshal.Copy(e.Pixels, offset, IntPtr.Add(locked.Address, offset), rowBytes);
                        }
                    }
                    else
                    {
                        var copyBytes = Math.Min(e.PixelLength, locked.RowBytes * locked.Size.Height);
                        Marshal.Copy(e.Pixels, 0, locked.Address, copyBytes);
                    }
                }

                // Alternating two persistent bitmaps keeps allocations at zero
                // per frame while still giving the Image a new source instance,
                // which is what forces Avalonia to upload the changed pixels.
                Framebuffer = target;
                FramebufferInvalidated?.Invoke();
                RemoteWidth = e.Width;
                RemoteHeight = e.Height;
                IsDesktopLoading = false;
            }
            finally
            {
                e.ReleasePixels();
            }
        });
    }

    private WriteableBitmap GetTargetBitmap(int width, int height)
    {
        if (_bitmapA is null || _bitmapB is null || _bitmapWidth != width || _bitmapHeight != height)
        {
            var previousA = _bitmapA;
            var previousB = _bitmapB;
            _bitmapA = CreateFrameBitmap(width, height);
            _bitmapB = CreateFrameBitmap(width, height);
            _bitmapWidth = width;
            _bitmapHeight = height;
            _useBitmapA = false;

            if (previousA is not null)
                _retiredFrames.Add(previousA);
            if (previousB is not null)
                _retiredFrames.Add(previousB);
        }

        var target = _useBitmapA ? _bitmapA : _bitmapB;
        _useBitmapA = !_useBitmapA;
        return target!;
    }

    private static WriteableBitmap CreateFrameBitmap(int width, int height) =>
        new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

    /// <summary>
    /// Disposes the frame bitmaps retired by a resolution change.
    /// <para>
    /// A dispatcher post is not enough: it runs on the next tick, which can still
    /// precede the moment the frame image and the per-monitor slice surfaces
    /// rebind to the new pair. A render pass that reads one of them in that gap
    /// throws <see cref="ObjectDisposedException"/> on the render thread, which
    /// takes the whole process down rather than surfacing as a failed frame.
    /// Holding them for one more frame guarantees the rebind; the slice surface
    /// additionally drops a stale source instead of trusting it.
    /// </para>
    /// </summary>
    private void DisposeRetiredFrames()
    {
        if (_retiredFrames.Count == 0)
            return;

        foreach (var frame in _retiredFrames)
            frame.Dispose();
        _retiredFrames.Clear();
    }

    private void OnClipboardTextReceived(string text)
    {
        Dispatcher.UIThread.Post(() => RemoteClipboardText = text);
    }

    private void OnClipboardImageReceived(uint imageFormat, byte[] data)
    {
        Dispatcher.UIThread.Post(() => RemoteClipboardImageReceived?.Invoke(imageFormat, data));
    }

    private void OnClipboardFilesReceived(string[] paths)
    {
        Dispatcher.UIThread.Post(() => RemoteClipboardFilesReceived?.Invoke(paths));
    }

    private void OnPointerShapeChanged(int kind, int width, int height, int hotX, int hotY, byte[]? pixels)
    {
        Dispatcher.UIThread.Post(() => PointerShapeChanged?.Invoke(kind, width, height, hotX, hotY, pixels));
    }

    partial void OnIsConnectedChanged(bool value)
    {
        if (value)
        {
            StartLinkQualityProbe();
            return;
        }

        StopLinkQualityProbe();
        LinkQualityLevel = 0;
    }

    private void StartLinkQualityProbe()
    {
        if (_linkQualityTimer == null)
        {
            _linkQualityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _linkQualityTimer.Tick += (_, _) => _ = ProbeLinkQualityAsync();
        }

        _linkQualityTimer.Start();
        _ = ProbeLinkQualityAsync();
    }

    private void StopLinkQualityProbe()
    {
        _linkQualityTimer?.Stop();
    }

    /// <summary>
    /// Times a bare TCP connect to the RDP endpoint. RDP exposes no signal
    /// strength of its own, and a full reconnect would disturb the session, so
    /// the handshake round trip is used as the quality proxy for the bar.
    /// </summary>
    private async Task ProbeLinkQualityAsync()
    {
        if (_linkQualityProbeRunning || !IsConnected || string.IsNullOrWhiteSpace(Session.Host))
            return;

        _linkQualityProbeRunning = true;
        try
        {
            var port = Session.Port is > 0 and <= 65535 ? Session.Port : 3389;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var client = new TcpClient();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await client.ConnectAsync(Session.Host, port, timeout.Token).ConfigureAwait(true);
            stopwatch.Stop();

            var latency = stopwatch.Elapsed.TotalMilliseconds;
            LinkQualityLevel = latency switch
            {
                <= 50 => 4,
                <= 100 => 3,
                <= 200 => 2,
                _ => 1
            };
        }
        catch
        {
            LinkQualityLevel = 1;
        }
        finally
        {
            _linkQualityProbeRunning = false;
        }
    }

    public void Dispose()
    {
        StopLinkQualityProbe();
        _resizeReconnectCts?.Cancel();
        _resizeReconnectCts?.Dispose();
        CancelConnectWatchdog();
        _client.FramebufferUpdated -= OnFramebufferUpdated;
        _client.ClipboardTextReceived -= OnClipboardTextReceived;
        _client.ClipboardImageReceived -= OnClipboardImageReceived;
        _client.ClipboardFilesReceived -= OnClipboardFilesReceived;
        _client.PointerShapeChanged -= OnPointerShapeChanged;
        LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
        _client.Dispose();
        DisposeRetiredFrames();
        _bitmapA?.Dispose();
        _bitmapB?.Dispose();
        _bitmapA = null;
        _bitmapB = null;
    }
}
