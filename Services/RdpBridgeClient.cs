using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CxShell.Models;
using Renci.SshNet;

namespace CxShell.Services;

public readonly record struct RdpMonitorRect(
    int Left,
    int Top,
    int Width,
    int Height,
    int DesktopScaleFactor,
    bool IsPrimary);

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMonitorRect
{
    public int Left;
    public int Top;
    public uint Width;
    public uint Height;
    public uint DesktopScaleFactor;
    public uint DeviceScaleFactor;
    public int IsPrimary;
}

public sealed class RdpFramebufferEventArgs : EventArgs
{
    private Action? _release;

    public RdpFramebufferEventArgs(
        int width,
        int height,
        int stride,
        byte[] pixels,
        int? pixelLength = null,
        Action? release = null)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
        PixelLength = pixelLength ?? pixels.Length;
        _release = release;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Pixels { get; }

    /// <summary>
    /// Valid byte count in <see cref="Pixels"/>. A pooled array may be larger
    /// than the frame, so callers must use this instead of Pixels.Length.
    /// </summary>
    public int PixelLength { get; }

    /// <summary>Returns the pooled buffer once the UI has copied the pixels.</summary>
    public void ReleasePixels()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

public sealed class RdpBridgeClient : IDisposable
{
    private const string LibraryName = "CxRdpBridge";
    private const uint DriveRedirectionCapability = 0x00000002;
    private const uint AudioPlaybackCapability = 0x00000004;
    private const uint MicrophoneCapability = 0x00000008;
    private const uint KeyboardHookCapability = 0x00000010;
    private const uint AutoReconnectCapability = 0x00000020;
    private const uint RuntimeResizeCapability = 0x00000040;
    private const uint AudioRedirectionApiVersion = 3;
    private const uint KeyboardOptionsApiVersion = 4;
    private const uint RuntimeResizeApiVersion = 6;
    private const uint FastConnectApiVersion = 7;
    private const uint FastConnectCapability = 0x00000080;
    private const uint FrameMarkerApiVersion = 8;
    private const uint FrameMarkerCapability = 0x00000100;
    private const uint PerformanceFlagsApiVersion = 9;
    private const uint PerformanceFlagsCapability = 0x00000200;
    private const uint DirtyRectApiVersion = 10;
    private const uint DirtyRectCapability = 0x00000400;
    private const uint GfxApiVersion = 11;
    private const uint GfxCapability = 0x00000800;
    private const uint SyncLockKeysApiVersion = 12;
    private const uint SyncLockKeysCapability = 0x00001000;
    private const uint PointerShapeApiVersion = 13;
    private const uint PointerShapeCapability = 0x00002000;
    public const uint ClipboardImageFormatPng = 1;
    public const uint ClipboardImageFormatDib = 2;
    private const uint ClipboardExtendedApiVersion = 14;
    private const uint ClipboardImageCapability = 0x00004000;
    private const uint ClipboardFilesCapability = 0x00008000;
    private const uint ClipboardRichTextCapability = 0x00010000;
    private const uint ClipboardLockCapability = 0x00020000;
    private const uint MultiMonitorApiVersion = 15;
    private const uint MultiMonitorCapability = 0x00040000;
    private static readonly object DebugLogLock = new();
    private static readonly object NativeLoadFailureLock = new();
    private static readonly bool DetailedDebugLogEnabled = IsEnvironmentFlagEnabled("CXSHELL_RDP_DEBUG_LOG");
    private static readonly bool FrameCoalescingEnabled = !IsEnvironmentFlagDisabled("CXSHELL_RDP_FRAME_COALESCE");
    private static readonly bool FrameCoverageEnabled = !IsEnvironmentFlagDisabled("CXSHELL_RDP_COVERAGE");
    // EGFX is on by default (verified against a real server): the graphics DVC
    // is registered by its addin name "rdpgfx", RdpgfxClientContext is bound to
    // the GDI backend and one EndPaint now equals one complete frame. The
    // native side still refuses to advertise the pipeline when the addin does
    // not resolve, and retries the profile without EGFX after a post-connect
    // abort, so a server that dislikes it cannot leave the tab dead.
    // CXSHELL_RDP_GFX=1 forces it on globally; the per-session setting decides
    // otherwise.
    private static readonly bool GraphicsPipelineForcedOn = IsEnvironmentFlagEnabled("CXSHELL_RDP_GFX");
    private static string? _lastNativeLoadFailure;
    private readonly FrameCallback _frameCallback;
    private readonly StatusCallback _statusCallback;
    private readonly DisconnectCallback _disconnectCallback;
    private readonly ClipboardTextCallback _clipboardTextCallback;
    private readonly ClipboardImageCallback _clipboardImageCallback;
    private readonly ClipboardFilesCallback _clipboardFilesCallback;
    private readonly PointerCallback _pointerCallback;
    private bool _supportsClipboardImage;
    private bool _supportsClipboardFiles;
    private bool _supportsClipboardRichText;
    private bool _supportsClipboardLock;
    private bool _supportsMultiMonitor;
    private readonly object _frameSync = new();
    private readonly RdpFrameComposer _frameComposer = new(enabled: FrameCoalescingEnabled, useCoverage: FrameCoverageEnabled);
    private RdpFramePresentationMode _frameMode = RdpFramePresentationMode.Auto;
    private bool _frameMarkerSupported;
    private bool _supportsSyncLockKeys;
    private bool _dirtyRectSupported;
    private bool _gfxFrameMode;
    private byte[] _frameBuffer = Array.Empty<byte>();
    private byte[] _previousFrameBuffer = Array.Empty<byte>();
    private int _previousFrameWidth;
    private int _previousFrameHeight;
    private int _previousFrameStride;
    private string _dirtyRectSource = "none";
    // Input arrives on the UI thread. Only a single atomic write is done there;
    // the presentation timer folds it into the composer under _frameSync, so
    // sending a mouse event never has to wait for a frame-copy lock.
    private long _lastInteractionTicks;
    private DateTime _lastFrameLogAt;
    private static readonly TimeSpan FrameLogInterval = TimeSpan.FromSeconds(2);
    private int _frameWidth;
    private int _frameHeight;
    private int _frameStride;
    private bool _frameDirty;
    private bool _loggedFirstFrame;
    private Timer? _presentTimer;
    private IntPtr _handle;
    private uint _nativeApiVersion;
    private uint _nativeCapabilities;
    private SshClient? _sshTunnelClient;
    private ForwardedPortLocal? _sshTunnelPort;
    private LoopbackProxyRelay? _proxyRelay;
    private string _profileCacheHost = string.Empty;
    private int _profileCachePort;

    static RdpBridgeClient()
    {
        NativeLibrary.SetDllImportResolver(typeof(RdpBridgeClient).Assembly, ResolveNativeLibrary);
    }

    public event EventHandler<RdpFramebufferEventArgs>? FramebufferUpdated;
    public event Action<string>? StatusChanged;
    public event Action? Disconnected;
    public event Action<string>? ClipboardTextReceived;
    public event Action<uint, byte[]>? ClipboardImageReceived;
    public event Action<string[]>? ClipboardFilesReceived;
    public event Action<int, int, int, int, int, byte[]?>? PointerShapeChanged;

    public static string DebugLogPath => Path.Combine(GetDebugLogDirectory(), "rdp-debug.log");

    public static string? LastNativeLoadFailure
    {
        get
        {
            lock (NativeLoadFailureLock)
                return _lastNativeLoadFailure;
        }
    }

    public RdpBridgeClient()
    {
        _frameCallback = OnFrame;
        _statusCallback = OnStatus;
        _disconnectCallback = OnDisconnected;
        _clipboardTextCallback = OnClipboardText;
        _clipboardImageCallback = OnClipboardImage;
        _clipboardFilesCallback = OnClipboardFiles;
        _pointerCallback = OnPointer;
    }

    public void Connect(
        SessionInfo session,
        string? password,
        int? desktopWidth = null,
        int? desktopHeight = null,
        int dpiScalePercent = 0)
    {
        Disconnect();

        _frameMode = RdpFramePresentationModes.Resolve(session.RdpFrameStrategy);
        DebugLog($"frame presentation mode={_frameMode}");

        var host = session.Host ?? string.Empty;
        var port = session.Port > 0 ? session.Port : 3389;
        var width = Math.Max(1, desktopWidth ?? session.RdpDesktopWidth);
        var height = Math.Max(1, desktopHeight ?? session.RdpDesktopHeight);
        var driveName = NormalizeDriveName(session.RdpDriveName);
        var drivePath = string.Empty;
        var audioPlaybackMode = RdpAudioOptions.ResolvePlaybackMode(session.RdpAudioMode);
        if (session.RdpRedirectDrives)
        {
            if (string.IsNullOrWhiteSpace(session.RdpDrivePath))
                throw new InvalidOperationException("Select a local folder to redirect to the RDP session.");

            drivePath = Path.GetFullPath(session.RdpDrivePath.Trim());
            if (!Directory.Exists(drivePath))
                throw new DirectoryNotFoundException($"The RDP redirected folder does not exist: {drivePath}");
        }

        // The security-profile cache is keyed by the real target, not by the
        // loopback endpoint that a tunnel or proxy relay substitutes below.
        _profileCacheHost = host;
        _profileCachePort = port;

        var targetDescription = $"{host}:{port}";
        var transportDescription = "direct";
        if (session.RdpUseSshTunnel)
        {
            port = StartSshTunnel(session);
            host = "127.0.0.1";
            targetDescription = $"{session.Host}:{(session.Port > 0 ? session.Port : 3389)} via SSH tunnel";
            transportDescription = "ssh-tunnel";
        }
        else
        {
            var proxy = ProxyConnectionFactory.ResolveEffectiveProxy(session.Proxy);
            if (proxy != null)
            {
                port = StartProxyRelay(session, proxy, host, port);
                host = "127.0.0.1";
                targetDescription = $"{session.Host}:{(session.Port > 0 ? session.Port : 3389)} via {proxy.TypeDisplay} proxy {proxy.Host}:{proxy.Port}";
                transportDescription = "proxy-relay";
            }
        }

        var colorDepth = ResolveColorDepth(session.RdpColorQuality);
        DebugLog($"connect start host={SanitizeLogValue(host)} port={port} target={SanitizeLogValue(targetDescription)} user={SanitizeLogValue(session.Username)} size={width}x{height} colorDepth={colorDepth} hasPassword={!string.IsNullOrEmpty(password)} transport={transportDescription}");
        _handle = NativeMethods.cxrdp_create();
        if (_handle == IntPtr.Zero)
        {
            DebugLog("connect failed: native create returned null");
            StopProxyRelay();
            StopSshTunnel();
            throw new InvalidOperationException("Failed to create RDP bridge session.");
        }

        NativeMethods.cxrdp_set_callbacks(_handle, _frameCallback, _statusCallback, _disconnectCallback, IntPtr.Zero);
        NativeMethods.cxrdp_set_clipboard_callback(_handle, _clipboardTextCallback);

        var nativeApiVersion = GetNativeApiVersion();
        var nativeCapabilities = GetNativeCapabilities();
        _nativeApiVersion = nativeApiVersion;
        _nativeCapabilities = nativeCapabilities;
        DebugLog($"native bridge apiVersion={nativeApiVersion} capabilities=0x{nativeCapabilities:X8} dpiScale={dpiScalePercent}");
        if ((nativeCapabilities & AutoReconnectCapability) == 0)
        {
            // Older bridges reconnect by tearing the session down; the UI still
            // handles it, it just cannot resume the same remote session.
            DebugLog("native bridge does not expose RDP auto reconnect (ARC).");
        }

        _frameMarkerSupported = nativeApiVersion >= FrameMarkerApiVersion &&
                                (nativeCapabilities & FrameMarkerCapability) != 0;
        _supportsSyncLockKeys = nativeApiVersion >= SyncLockKeysApiVersion &&
                                (nativeCapabilities & SyncLockKeysCapability) != 0;
        if (nativeApiVersion >= PointerShapeApiVersion &&
            (nativeCapabilities & PointerShapeCapability) != 0)
        {
            NativeMethods.cxrdp_set_pointer_callback(_handle, _pointerCallback);
        }

        _supportsClipboardImage = nativeApiVersion >= ClipboardExtendedApiVersion &&
                                  (nativeCapabilities & ClipboardImageCapability) != 0;
        _supportsClipboardFiles = nativeApiVersion >= ClipboardExtendedApiVersion &&
                                  (nativeCapabilities & ClipboardFilesCapability) != 0;
        _supportsClipboardRichText = nativeApiVersion >= ClipboardExtendedApiVersion &&
                                     (nativeCapabilities & ClipboardRichTextCapability) != 0;
        _supportsClipboardLock = nativeApiVersion >= ClipboardExtendedApiVersion &&
                                 (nativeCapabilities & ClipboardLockCapability) != 0;
        if (_supportsClipboardImage)
            NativeMethods.cxrdp_set_clipboard_image_callback(_handle, _clipboardImageCallback);
        if (_supportsClipboardFiles)
            NativeMethods.cxrdp_set_clipboard_files_callback(_handle, _clipboardFilesCallback);
        if (_supportsClipboardLock)
            NativeMethods.cxrdp_set_clipboard_lock_enabled(_handle, session.RdpEnableClipboardLock ? 1 : 0);
        _supportsMultiMonitor = nativeApiVersion >= MultiMonitorApiVersion &&
                                (nativeCapabilities & MultiMonitorCapability) != 0;
        if (nativeApiVersion >= GfxApiVersion && (nativeCapabilities & GfxCapability) != 0)
        {
            var gfxRequested = GraphicsPipelineForcedOn || session.RdpGraphicsPipelineEnabled;
            TryInvokeOptionalNative("cxrdp_set_graphics_pipeline", () =>
            {
                var gfxResult = NativeMethods.cxrdp_set_graphics_pipeline(_handle, gfxRequested ? 1 : 0);
                DebugLog($"graphics pipeline requested={gfxRequested} session={session.RdpGraphicsPipelineEnabled} env={GraphicsPipelineForcedOn} result={gfxResult}");
            });
        }
        else
        {
            DebugLog("native bridge lacks EGFX support; using legacy bitmap updates.");
        }
        _dirtyRectSupported = nativeApiVersion >= DirtyRectApiVersion &&
                              (nativeCapabilities & DirtyRectCapability) != 0 &&
                              FrameCoverageEnabled;
        DebugLog($"dirty rect frame boundary={_dirtyRectSupported}");
        // Surface frame markers are a protocol-level frame boundary, strictly
        // better than any host-side timing heuristic. Probe for them on every
        // strategy except Legacy; if the server does not send them the bridge
        // reports back and we stay on pure debounce. The RDP strategy only
        // controls what happens when markers really arrive.
        var useFrameMarkers = _frameMode != RdpFramePresentationMode.Legacy;
        if (_frameMarkerSupported)
        {
            if (!TryInvokeOptionalNative("cxrdp_set_frame_marker_enabled", () =>
            {
                var markerResult = NativeMethods.cxrdp_set_frame_marker_enabled(_handle, useFrameMarkers ? 1 : 0);
                DebugLog($"frame markers enabled={useFrameMarkers} result={markerResult}");
            }))
            {
                _frameMarkerSupported = false;
            }
        }
        else if (useFrameMarkers)
        {
            // Old bridge: keep the host-side time coalescing as the fallback.
            DebugLog("native bridge lacks frame markers; using time coalescing only.");
        }

        if (nativeApiVersion >= FastConnectApiVersion &&
            (nativeCapabilities & FastConnectCapability) != 0)
        {
            var explicitProfile = NormalizeExplicitSecurityProfile(session.RdpSecurityMode);
            var targetProfile = !string.IsNullOrEmpty(explicitProfile)
                ? explicitProfile
                : RdpSecurityProfileCache.Get(_profileCacheHost, _profileCachePort);
            if (!string.IsNullOrEmpty(targetProfile))
            {
                TryInvokeOptionalNative("cxrdp_set_security_profile", () =>
                {
                    var profileResult = NativeMethods.cxrdp_set_security_profile(_handle, targetProfile);
                    DebugLog($"security profile hint={targetProfile} explicit={!string.IsNullOrEmpty(explicitProfile)} result={profileResult}");
                });
            }

            // Environment override CXSHELL_RDP_CLIPBOARD_CHANNEL=0 still wins
            // inside the bridge, so it remains a global troubleshooting switch.
            TryInvokeOptionalNative("cxrdp_set_clipboard_channel", () =>
            {
                var clipboardResult = NativeMethods.cxrdp_set_clipboard_channel(
                    _handle,
                    session.RdpClipboardEnabled ? 1 : 0);
                DebugLog($"clipboard channel requested={session.RdpClipboardEnabled} result={clipboardResult}");
            });
        }
        else
        {
            DebugLog("native bridge does not expose fast connect options; using auto negotiation.");
        }

        if (nativeApiVersion >= PerformanceFlagsApiVersion &&
            (nativeCapabilities & PerformanceFlagsCapability) != 0)
        {
            TryInvokeOptionalNative("cxrdp_set_disable_wallpaper", () =>
            {
                var wallpaperResult = NativeMethods.cxrdp_set_disable_wallpaper(
                    _handle,
                    session.RdpDisableWallpaper ? 1 : 0);
                DebugLog($"disable wallpaper requested={session.RdpDisableWallpaper} result={wallpaperResult}");
            });
        }
        else
        {
            DebugLog("native bridge does not expose RDP performance flags; wallpaper setting ignored.");
        }

        if (session.RdpRedirectDrives)
        {
            if ((nativeCapabilities & DriveRedirectionCapability) == 0)
            {
                Disconnect();
                throw new InvalidOperationException(
                    "The installed CxRdpBridge does not support RDP drive redirection. Reinstall or update CxShell so the native bridge matches the application version.");
            }

            var driveResult = NativeMethods.cxrdp_set_drive_redirection(
                _handle,
                1,
                driveName,
                drivePath);
            if (driveResult != 0)
            {
                var error = GetLastError();
                Disconnect();
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? $"RDP drive redirection configuration failed: {driveResult}"
                        : error);
            }
        }

        if (RdpAudioOptions.RequiresNativeConfiguration(audioPlaybackMode, session.RdpMicrophoneEnabled))
        {
            if (nativeApiVersion < AudioRedirectionApiVersion)
            {
                Disconnect();
                throw new InvalidOperationException(
                    "The installed CxRdpBridge does not support RDP audio settings. Reinstall or update CxShell so the native bridge matches the application version.");
            }

            if (audioPlaybackMode == RdpAudioPlaybackMode.PlayLocal &&
                (nativeCapabilities & AudioPlaybackCapability) == 0)
            {
                Disconnect();
                throw new PlatformNotSupportedException(
                    "This CxRdpBridge build does not include a local RDP audio playback backend.");
            }

            if (session.RdpMicrophoneEnabled &&
                (nativeCapabilities & MicrophoneCapability) == 0)
            {
                Disconnect();
                throw new PlatformNotSupportedException(
                    "This CxRdpBridge build does not include an RDP microphone backend.");
            }

            var audioResult = NativeMethods.cxrdp_set_audio_redirection(
                _handle,
                (int)audioPlaybackMode,
                session.RdpMicrophoneEnabled ? 1 : 0);
            if (audioResult != 0)
            {
                var error = GetLastError();
                Disconnect();
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? $"RDP audio redirection configuration failed: {audioResult}"
                        : error);
            }
        }

        if (nativeApiVersion >= KeyboardOptionsApiVersion &&
            (nativeCapabilities & KeyboardHookCapability) != 0)
        {
            var keyboardResult = NativeMethods.cxrdp_set_keyboard_options(
                _handle,
                session.RdpApplyKeyCombinations ? 1 : 0);
            if (keyboardResult != 0)
            {
                var error = GetLastError();
                Disconnect();
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? $"RDP keyboard configuration failed: {keyboardResult}"
                        : error);
            }
        }

        if (dpiScalePercent > 0 &&
            nativeApiVersion >= RuntimeResizeApiVersion &&
            (nativeCapabilities & RuntimeResizeCapability) != 0)
        {
            var scaleResult = NativeMethods.cxrdp_set_display_scale(_handle, dpiScalePercent);
            if (scaleResult != 0)
                throw new InvalidOperationException($"RDP display scale configuration failed: {scaleResult}");
        }
        else if (dpiScalePercent > 0)
        {
            DebugLog("native bridge cannot advertise DPI; keeping the remote session's scaling.");
        }

        // Prepare the frame pipeline before the worker starts so the very first
        // paint batches are captured rather than cleared by a late reset.
        StartFramePresentation();

        var result = NativeMethods.cxrdp_connect(
            _handle,
            host,
            port,
            session.Username ?? string.Empty,
            password ?? string.Empty,
            width,
            height,
            colorDepth);

        if (result != 0)
        {
            var error = GetLastError();
            DebugLog($"connect failed result={result} error={SanitizeLogValue(error)}");
            Disconnect();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"RDP bridge connect failed: {result}" : error);
        }

        DebugLog($"connect worker started frameCoalescing={FrameCoalescingEnabled}");
    }

    private void StartFramePresentation()
    {
        lock (_frameSync)
        {
            _frameComposer.Reset();
            _frameDirty = false;
            _loggedFirstFrame = false;
            _gfxFrameMode = false;
            Interlocked.Exchange(ref _lastInteractionTicks, 0);
            _lastFrameLogAt = default;

            // Auto starts in time-coalescing mode and switches to frame
            // boundaries as soon as the first server marker arrives.
            switch (_frameMode)
            {
                case RdpFramePresentationMode.Coalesce:
                    _frameComposer.Configure(FrameCoalescingEnabled, false);
                    break;
                case RdpFramePresentationMode.FrameMarker:
                    // Start in coalescing mode too: switching straight to
                    // boundary mode would publish every half-painted batch if
                    // this server turns out not to send markers.
                    _frameComposer.Configure(FrameCoalescingEnabled, false);
                    break;
                case RdpFramePresentationMode.Legacy:
                    _frameComposer.Configure(false, false);
                    break;
                default:
                    _frameComposer.Configure(FrameCoalescingEnabled, false);
                    break;
            }
        }

        _presentTimer?.Dispose();
        // The timer runs off the UI thread. Frames arriving from the native
        // callback are copied into a reusable buffer; the timer decides when a
        // complete-enough snapshot is worth rendering.
        _presentTimer = new Timer(
            TryPresentFrame,
            null,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(16));
    }

    private void StopFramePresentation()
    {
        _presentTimer?.Dispose();
        _presentTimer = null;
        lock (_frameSync)
        {
            _frameDirty = false;
            _frameComposer.Reset();
        }
    }

    private void TryPresentFrame(object? state)
    {
        byte[] snapshot;
        int width;
        int height;
        int stride;
        int length;
        int merged;
        bool first;
        double coverage;
        bool hasDirtyRects;
        bool wave;
        long collectMs;
        string dirtySource;
        bool interactive;
        bool settling;
        bool logFrame;

        lock (_frameSync)
        {
            var now = DateTime.UtcNow;
            // Fold the lock-free input timestamp into the composer. The timer
            // runs every 16 ms, so this adds at most one tick of latency.
            var interactionTicks = Interlocked.Read(ref _lastInteractionTicks);
            if (interactionTicks != 0)
                _frameComposer.NoteInteraction(new DateTime(interactionTicks, DateTimeKind.Utc));

            if (!_frameDirty || !_frameComposer.ShouldPresent(now))
                return;

            length = checked(_frameStride * _frameHeight);
            if (length <= 0 || _frameBuffer.Length < length)
                return;

            // Snapshot the decision metrics before MarkPresented clears them.
            coverage = _frameComposer.Coverage;
            hasDirtyRects = _frameComposer.HasDirtyRect;
            wave = _frameComposer.InRepaintWave;
            collectMs = (long)_frameComposer.FirstFrameAge(now).TotalMilliseconds;
            dirtySource = _dirtyRectSource;
            snapshot = ArrayPool<byte>.Shared.Rent(length);
            Buffer.BlockCopy(_frameBuffer, 0, snapshot, 0, length);
            width = _frameWidth;
            height = _frameHeight;
            stride = _frameStride;
            _frameDirty = false;
            interactive = _frameComposer.IsInteractive(now);
            settling = _frameComposer.InSettlingWindow(now);
            merged = _frameComposer.MarkPresented(now);
            first = !_loggedFirstFrame;
            _loggedFirstFrame = true;
            // Interactive mode publishes up to ~60 times a second; writing a
            // log line per frame would turn file I/O into input latency.
            logFrame = first || now - _lastFrameLogAt >= FrameLogInterval;
            if (logFrame)
                _lastFrameLogAt = now;
        }

        var handlers = FramebufferUpdated;
        if (handlers is null)
        {
            ArrayPool<byte>.Shared.Return(snapshot);
            return;
        }

        // Throttled sampling: one line every couple of seconds is enough to
        // diagnose a "top to bottom" or a laggy-pointer report, and it keeps
        // the interactive presentation path free of synchronous file I/O.
        if (first)
            DebugLog($"frame composer first frame presented mergedBatches={merged} coverage={coverage:0.00} wave={wave} dirtyRects={hasDirtyRects} dirtySource={dirtySource} collectMs={collectMs} interactive={interactive} size={width}x{height}");
        else if (logFrame)
            DebugLog($"frame composer presented mergedBatches={merged} coverage={coverage:0.00} wave={wave} interactive={interactive} settling={settling}");

        var args = new RdpFramebufferEventArgs(
            width,
            height,
            stride,
            snapshot,
            length,
            () => ArrayPool<byte>.Shared.Return(snapshot));

        try
        {
            handlers(this, args);
        }
        catch
        {
            // A subscriber that throws must not leak the rented buffer.
            args.ReleasePixels();
            throw;
        }
    }

    private int StartSshTunnel(SessionInfo session)
    {
        var sshHost = string.IsNullOrWhiteSpace(session.RdpSshHost) ? session.Host : session.RdpSshHost.Trim();
        var sshPort = session.RdpSshPort is >= 1 and <= 65535 ? session.RdpSshPort : 22;
        var sshUser = session.RdpSshUsername?.Trim() ?? string.Empty;
        var remoteHost = string.IsNullOrWhiteSpace(session.Host) ? "127.0.0.1" : session.Host.Trim();
        var remotePort = session.Port is >= 1 and <= 65535 ? session.Port : 3389;
        var localPort = GetFreeLoopbackPort();

        if (string.IsNullOrWhiteSpace(sshHost))
            throw new InvalidOperationException("RDP SSH tunnel host is required.");
        if (string.IsNullOrWhiteSpace(sshUser))
            throw new InvalidOperationException("RDP SSH tunnel username is required.");

        var connectionInfo = new ConnectionInfo(sshHost, sshPort, sshUser, CreateRdpSshAuthMethods(session, sshUser));
        connectionInfo.Timeout = TimeSpan.FromSeconds(12);
        _sshTunnelClient = new SshClient(connectionInfo);
        // An idle SSH channel is dropped by NAT devices and by sshd keepalive
        // limits, which silently kills the tunneled RDP transport. Probe it so
        // the tunnel lives as long as the desktop session does.
        _sshTunnelClient.KeepAliveInterval = TimeSpan.FromSeconds(30);
        SshHostKeyTrustService.Shared.Attach(
            _sshTunnelClient,
            sshHost,
            sshPort,
            session.SshAcceptAndSaveHostKey);

        DebugLog($"ssh tunnel connecting ssh={SanitizeLogValue(sshUser)}@{SanitizeLogValue(sshHost)}:{sshPort} local=127.0.0.1:{localPort} remote={SanitizeLogValue(remoteHost)}:{remotePort}");
        StatusChanged?.Invoke($"Opening SSH tunnel to {sshHost}:{sshPort}...");
        try
        {
            _sshTunnelClient.Connect();
        }
        catch (Exception ex)
        {
            var message = $"RDP SSH tunnel login failed: {ex.Message}";
            DebugLog($"ssh tunnel failed {SanitizeLogValue(ex.GetType().Name)}: {SanitizeLogValue(ex.Message)}");
            StopSshTunnel();
            throw new InvalidOperationException(message, ex);
        }

        _sshTunnelPort = new ForwardedPortLocal("127.0.0.1", (uint)localPort, remoteHost, (uint)remotePort);
        _sshTunnelPort.Exception += (_, e) => StatusChanged?.Invoke($"RDP SSH tunnel failed: {e.Exception.Message}");
        _sshTunnelClient.AddForwardedPort(_sshTunnelPort);
        _sshTunnelPort.Start();
        DebugLog($"ssh tunnel started local=127.0.0.1:{localPort} remote={SanitizeLogValue(remoteHost)}:{remotePort}");
        return localPort;
    }

    private static AuthenticationMethod[] CreateRdpSshAuthMethods(SessionInfo session, string username)
    {
        if (session.RdpSshUsePrivateKey)
        {
            if (string.IsNullOrWhiteSpace(session.RdpSshPrivateKeyPath))
                throw new InvalidOperationException("RDP SSH private key path is required.");
            return [new PrivateKeyAuthenticationMethod(username, new PrivateKeyFile(ExpandPath(session.RdpSshPrivateKeyPath)))];
        }

        var password = PasswordEncryptionService.Decrypt(session.RdpSshPassword);
        return [new PasswordAuthenticationMethod(username, password)];
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ExpandPath(string path)
    {
        if (path.StartsWith("~"))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }

        return Path.GetFullPath(path);
    }

    public void SendPointer(ushort flags, ushort x, ushort y)
    {
        if (_handle == IntPtr.Zero)
            return;

        NoteUserInteraction();
        NativeMethods.cxrdp_send_pointer(_handle, flags, x, y);
    }

    public void SendKey(uint key, bool down)
    {
        if (_handle == IntPtr.Zero)
            return;

        NoteUserInteraction();
        NativeMethods.cxrdp_send_key(_handle, key, down ? 1 : 0);
    }

    public void SendUnicodeKey(char key, bool down)
    {
        if (_handle == IntPtr.Zero)
            return;

        NoteUserInteraction();
        NativeMethods.cxrdp_send_unicode_key(_handle, key, down ? 1 : 0);
    }

    public void SyncLockKeys(uint flags)
    {
        if (_handle != IntPtr.Zero && _supportsSyncLockKeys)
            NativeMethods.cxrdp_sync_lock_keys(_handle, flags);
    }

    /// <summary>Whether the loaded native bridge can drive a multi-monitor layout.</summary>
    public bool SupportsMultiMonitor => _supportsMultiMonitor;

    public bool TryResizeMonitors(IReadOnlyList<RdpMonitorRect> monitors)
    {
        if (_handle == IntPtr.Zero || monitors == null || monitors.Count == 0)
            return false;

        if (!_supportsMultiMonitor)
        {
            // Older bridges only know how to resize a single desktop. Collapsing
            // the layout to its primary monitor keeps "back to one screen" and
            // window resizes working instead of silently doing nothing.
            if (monitors.Count != 1)
                return false;

            var single = monitors[0];
            var singleScale = single.DesktopScaleFactor > 0 ? single.DesktopScaleFactor : 100;
            return UpdateDisplay(
                Math.Max(200, single.Width),
                Math.Max(200, single.Height),
                Math.Clamp(singleScale, 50, 400));
        }

        var array = new NativeMonitorRect[Math.Min(monitors.Count, 16)];
        for (var i = 0; i < array.Length; i++)
        {
            var m = monitors[i];
            var clampedScale = Math.Clamp(m.DesktopScaleFactor, 100, 500);
            var deviceScale = clampedScale >= 180 ? 180u : clampedScale >= 140 ? 140u : 100u;
            array[i] = new NativeMonitorRect
            {
                Left = m.Left,
                Top = m.Top,
                Width = (uint)Math.Max(200, m.Width),
                Height = (uint)Math.Max(200, m.Height),
                DesktopScaleFactor = (uint)clampedScale,
                DeviceScaleFactor = deviceScale,
                IsPrimary = m.IsPrimary ? 1 : 0
            };
        }

        var rc = NativeMethods.cxrdp_resize_monitors(_handle, array, array.Length);
        DebugLogDetailed($"multi-monitor resize count={array.Length} rc={rc}");
        return rc == 0;
    }

    /// <summary>
    /// Marks the session as interactive so the next presentation tick publishes
    /// the pending framebuffer immediately instead of waiting out a repaint
    /// wave. Kept lock-free: pointer events must never block on frame copying.
    /// </summary>
    private void NoteUserInteraction() =>
        Interlocked.Exchange(ref _lastInteractionTicks, DateTime.UtcNow.Ticks);

    public void SetClipboardText(string text)
    {
        DebugLogDetailed($"clipboard set local length={text?.Length ?? 0}");
        if (_handle != IntPtr.Zero)
            NativeMethods.cxrdp_set_clipboard_text(_handle, text ?? string.Empty);
    }

    public void SetClipboardRichText(string plainText, byte[]? htmlBytes, byte[]? rtfBytes)
    {
        if (_handle == IntPtr.Zero)
            return;

        if (_supportsClipboardRichText)
        {
            NativeMethods.cxrdp_set_clipboard_rich_text(
                _handle,
                plainText ?? string.Empty,
                htmlBytes,
                (uint)(htmlBytes?.Length ?? 0),
                rtfBytes,
                (uint)(rtfBytes?.Length ?? 0));
        }
        else
        {
            NativeMethods.cxrdp_set_clipboard_text(_handle, plainText ?? string.Empty);
        }
    }

    public void SetClipboardImage(uint imageFormat, byte[] data)
    {
        if (_handle == IntPtr.Zero || !_supportsClipboardImage || data.Length == 0)
            return;

        NativeMethods.cxrdp_set_clipboard_image(_handle, imageFormat, data, (uint)data.Length);
    }

    public void SetClipboardFiles(IReadOnlyList<string> filePaths)
    {
        if (_handle == IntPtr.Zero || !_supportsClipboardFiles || filePaths.Count == 0)
            return;

        var joined = string.Join('\n', filePaths.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (string.IsNullOrWhiteSpace(joined))
            return;

        NativeMethods.cxrdp_set_clipboard_files(_handle, joined);
    }

    /// <summary>
    /// Requests a resolution/DPI change on a live session without reconnecting.
    /// Returns false when the native bridge cannot do it (then callers should
    /// fall back to a full reconnect).
    /// </summary>
    public bool UpdateDisplay(int width, int height, int dpiScalePercent)
    {
        if (_handle == IntPtr.Zero ||
            _nativeApiVersion < RuntimeResizeApiVersion ||
            (_nativeCapabilities & RuntimeResizeCapability) == 0)
        {
            return false;
        }

        var result = NativeMethods.cxrdp_update_display(_handle, width, height, dpiScalePercent);
        DebugLog($"display update requested size={width}x{height} dpi={dpiScalePercent} result={result}");
        return result == 0;
    }

    public void Disconnect()
    {
        StopFramePresentation();

        if (_handle == IntPtr.Zero)
        {
            StopProxyRelay();
            StopSshTunnel();
            return;
        }

        DebugLog("disconnect requested");
        NativeMethods.cxrdp_disconnect(_handle);
        NativeMethods.cxrdp_destroy(_handle);
        _handle = IntPtr.Zero;
        StopProxyRelay();
        StopSshTunnel();
        DebugLog("disconnect completed");
    }

    /// <summary>
    /// Starts a loopback relay that forwards to <paramref name="targetHost"/>:
    /// <paramref name="targetPort"/> through the configured HTTP/SOCKS proxy and
    /// returns the local port the native bridge should connect to. FreeRDP only
    /// accepts a plain host and port, so the proxy hop is established in managed
    /// code and the bridge dials <c>127.0.0.1</c>.
    /// </summary>
    private int StartProxyRelay(SessionInfo session, ProxySettings proxy, string targetHost, int targetPort)
    {
        if (proxy.Protocol is not (ProxyProtocol.Http or ProxyProtocol.Socks4 or ProxyProtocol.Socks4A or ProxyProtocol.Socks5))
        {
            throw new InvalidOperationException(
                $"{proxy.TypeDisplay} proxies are not supported for RDP. Use an HTTP or SOCKS proxy, or enable the RDP SSH tunnel.");
        }

        StatusChanged?.Invoke($"Opening {proxy.TypeDisplay} proxy {proxy.Host}:{proxy.Port}...");
        var ipVersion = string.IsNullOrWhiteSpace(session.AdvancedIpVersion) ? "Auto" : session.AdvancedIpVersion.Trim();
        LoopbackProxyRelay relay;
        try
        {
            relay = LoopbackProxyRelay.Start(
                targetHost,
                targetPort,
                proxy,
                ipVersion,
                ex =>
                {
                    DebugLog($"proxy relay failed: {SanitizeLogValue(ex.GetType().Name)}: {SanitizeLogValue(ex.Message)}");
                    StatusChanged?.Invoke($"RDP proxy connection failed: {ex.Message}");
                });
        }
        catch (Exception ex)
        {
            DebugLog($"proxy relay start failed: {SanitizeLogValue(ex.Message)}");
            throw new InvalidOperationException($"Could not start the RDP proxy relay: {ex.Message}", ex);
        }

        _proxyRelay = relay;
        DebugLog($"proxy relay started local=127.0.0.1:{relay.LocalPort} proxy={SanitizeLogValue(proxy.TypeDisplay)} {SanitizeLogValue(proxy.Host)}:{proxy.Port} target={SanitizeLogValue(targetHost)}:{targetPort}");
        return relay.LocalPort;
    }

    private void StopProxyRelay()
    {
        var relay = _proxyRelay;
        if (relay == null)
            return;

        _proxyRelay = null;
        try
        {
            relay.Dispose();
            DebugLog("proxy relay stopped");
        }
        catch (Exception ex)
        {
            DebugLog($"proxy relay stop failed: {SanitizeLogValue(ex.Message)}");
        }
    }

    private void StopSshTunnel()
    {
        try
        {
            if (_sshTunnelPort?.IsStarted == true)
                _sshTunnelPort.Stop();
        }
        catch
        {
            // Ignore tunnel shutdown failures.
        }

        try
        {
            if (_sshTunnelClient != null && _sshTunnelPort != null)
                _sshTunnelClient.RemoveForwardedPort(_sshTunnelPort);
        }
        catch
        {
            // Ignore tunnel shutdown failures.
        }

        try
        {
            if (_sshTunnelClient?.IsConnected == true)
                _sshTunnelClient.Disconnect();
            _sshTunnelClient?.Dispose();
        }
        catch
        {
            // Ignore tunnel shutdown failures.
        }

        _sshTunnelPort = null;
        _sshTunnelClient = null;
    }

    public void Dispose()
    {
        Disconnect();
    }

    private string GetLastError()
    {
        if (_handle == IntPtr.Zero)
            return string.Empty;

        var pointer = NativeMethods.cxrdp_get_last_error(_handle);
        return pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
    }

    public static string GetExpectedNativeLibraryName()
    {
        if (OperatingSystem.IsWindows())
            return "CxRdpBridge.dll";

        if (OperatingSystem.IsMacOS())
            return "libCxRdpBridge.dylib";

        return "libCxRdpBridge.so";
    }

    public static string GetNativeLibraryLoadErrorMessage(Exception exception)
    {
        var loadFailure = LastNativeLoadFailure;
        if (!string.IsNullOrWhiteSpace(loadFailure))
            return loadFailure;

        return $"{GetExpectedNativeLibraryName()} could not be loaded: {exception.Message}";
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
            return IntPtr.Zero;

        var failures = new List<string>();
        var missing = new List<string>();
        foreach (var candidate in GetNativeLibraryCandidates())
        {
            if (!File.Exists(candidate))
            {
                missing.Add(candidate);
                continue;
            }

            try
            {
                var handle = NativeLibrary.Load(candidate);
                SetLastNativeLoadFailure(null);
                return handle;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                failures.Add($"{candidate}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            SetLastNativeLoadFailure(
                $"{GetExpectedNativeLibraryName()} was found, but Windows could not load it. A dependent native DLL may be missing or the architecture may be wrong. {string.Join(" | ", failures)}");
        }
        else
        {
            SetLastNativeLoadFailure(
                $"{GetExpectedNativeLibraryName()} was not found. Checked: {string.Join("; ", missing)}");
        }

        return IntPtr.Zero;
    }

    private static void SetLastNativeLoadFailure(string? message)
    {
        lock (NativeLoadFailureLock)
            _lastNativeLoadFailure = message;
    }

    private static string[] GetNativeLibraryCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var fileName = GetExpectedNativeLibraryName();
        var rid = GetCurrentRid();
        var candidates = new List<string>
        {
            // Prefer the RID-specific bridge. A stale root-level DLL can be left
            // behind by an earlier local build and may expose an older API.
            Path.Combine(baseDirectory, "runtimes", rid, "native", fileName),
            Path.Combine(baseDirectory, fileName)
        };

        var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(processDirectory) &&
            !string.Equals(processDirectory, baseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(processDirectory, "runtimes", rid, "native", fileName));
            candidates.Add(Path.Combine(processDirectory, fileName));
        }

        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeSearchDirectories)
        {
            foreach (var directory in nativeSearchDirectories.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(Path.Combine(directory, "runtimes", rid, "native", fileName));
                candidates.Add(Path.Combine(directory, fileName));
            }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetCurrentRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        if (OperatingSystem.IsWindows())
            return $"win-{arch}";

        if (OperatingSystem.IsMacOS())
            return $"osx-{arch}";

        return $"linux-{arch}";
    }

    private void OnFrame(IntPtr userData, int width, int height, int stride, IntPtr data)
    {
        if (width <= 0 || height <= 0 || stride <= 0 || data == IntPtr.Zero)
            return;

        // The GDI dirty rectangle is only valid for this callback, so read it
        // from the bridge before copying the pixels into the reusable buffer.
        RdpFrameDirtyRect? dirty = null;
        var dirtySource = "none";
        if (_dirtyRectSupported && _handle != IntPtr.Zero)
        {
            try
            {
                if (NativeMethods.cxrdp_get_frame_dirty_rect(
                        _handle, out var dx, out var dy, out var dw, out var dh) == 0)
                {
                    dirty = new RdpFrameDirtyRect(dx, dy, dw, dh);
                    dirtySource = "gdi";
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        var length = checked(stride * height);
        lock (_frameSync)
        {
            var now = DateTime.UtcNow;
            var classifyWave = _frameComposer.NeedsDirtyRect(now);

            // The native pointer is only valid for the duration of this
            // callback, so copy into a reusable managed buffer immediately.
            // No allocation per update: the buffer is rebuilt only on resize.
            var sizeChanged = width != _previousFrameWidth ||
                              height != _previousFrameHeight ||
                              stride != _previousFrameStride;
            if (_frameBuffer.Length < length)
                _frameBuffer = new byte[length];
            if (_previousFrameBuffer.Length < length || sizeChanged)
            {
                _previousFrameBuffer = new byte[length];
                _previousFrameWidth = width;
                _previousFrameHeight = height;
                _previousFrameStride = stride;
            }

            Marshal.Copy(data, _frameBuffer, 0, length);

            // Most Windows servers leave FreeRDP's GDI invalid region empty on
            // the legacy bitmap-update path. Diffing against the previous batch
            // gives a server-independent dirty region: the first batch only
            // covers the strip that was actually painted, so the composer can
            // hold the frame until the whole desktop has arrived.
            // Wave classification is only needed for the first frame and the
            // settling window. In interactive/settled mode every snapshot is
            // published as-is, so skip the sampled full-surface diff (thousands
            // of cache-missing reads per mouse-move batch).
            if (dirty is null && classifyWave && FrameCoverageEnabled && FrameCoalescingEnabled)
            {
                var diff = ComputeDirtyRectFromDiff(width, height, stride);
                if (diff.HasValue)
                {
                    dirty = diff;
                    dirtySource = "diff";
                }
            }

            // Keep the diff baseline fresh even when classification is idle,
            // so switching back to wave mode never sees a stale full-screen
            // difference. This is a plain sequential copy and stays cheap.
            Buffer.BlockCopy(_frameBuffer, 0, _previousFrameBuffer, 0, length);
            _dirtyRectSource = dirtySource;
            _frameWidth = width;
            _frameHeight = height;
            _frameStride = stride;
            _frameDirty = true;
            _frameComposer.NoteFrame(now, dirty, width, height);
        }
    }

    /// <summary>
    /// Samples a 16x9 grid and returns the bounding box of cells that changed
    /// since the previous native batch. Cheap enough to run on every update:
    /// only a few hundred pixels are compared, not the whole framebuffer.
    /// </summary>
    private RdpFrameDirtyRect? ComputeDirtyRectFromDiff(int width, int height, int stride)
    {
        const int columns = 16;
        const int rows = 9;
        const int samples = 8;
        var minColumn = columns;
        var maxColumn = -1;
        var minRow = rows;
        var maxRow = -1;

        for (var row = 0; row < rows; row++)
        {
            var y0 = row * height / rows;
            var y1 = Math.Max(y0 + 1, (row + 1) * height / rows);
            for (var column = 0; column < columns; column++)
            {
                var x0 = column * width / columns;
                var x1 = Math.Max(x0 + 1, (column + 1) * width / columns);
                var changed = false;
                for (var sy = 0; sy < samples && !changed; sy++)
                {
                    var y = y0 + (y1 - y0) * sy / samples;
                    var rowOffset = y * stride;
                    for (var sx = 0; sx < samples; sx++)
                    {
                        var x = x0 + (x1 - x0) * sx / samples;
                        var offset = rowOffset + x * 4;
                        if (offset + 3 >= _frameBuffer.Length)
                            break;
                        if (_frameBuffer[offset] != _previousFrameBuffer[offset] ||
                            _frameBuffer[offset + 1] != _previousFrameBuffer[offset + 1] ||
                            _frameBuffer[offset + 2] != _previousFrameBuffer[offset + 2] ||
                            _frameBuffer[offset + 3] != _previousFrameBuffer[offset + 3])
                        {
                            changed = true;
                            break;
                        }
                    }
                }

                if (!changed)
                    continue;
                minColumn = Math.Min(minColumn, column);
                maxColumn = Math.Max(maxColumn, column);
                minRow = Math.Min(minRow, row);
                maxRow = Math.Max(maxRow, row);
            }
        }

        if (maxColumn < 0)
            return null;

        var left = minColumn * width / columns;
        var top = minRow * height / rows;
        var right = (maxColumn + 1) * width / columns;
        var bottom = (maxRow + 1) * height / rows;
        return new RdpFrameDirtyRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private void OnStatus(IntPtr userData, IntPtr message)
    {
        var text = message == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(message) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return;

        // Internal one-shot signals from the frame-marker path. They are not
        // user-facing status lines, so they never reach the UI overlay.
        if (text.StartsWith("RDP GFX frame mode active", StringComparison.OrdinalIgnoreCase))
        {
            lock (_frameSync)
            {
                _gfxFrameMode = true;
                // EGFX groups surface commands between START_FRAME/END_FRAME;
                // FreeRDP's GDI backend calls EndPaint exactly once at END_FRAME,
                // after copying the whole frame into the primary buffer. No
                // coalescing or hold is needed - this is MSTSC's frame model.
                _frameComposer.Configure(false, true);
            }
            DebugLog("GFX frame mode active; publishing one complete frame per EndPaint");
            return;
        }
        if (text.StartsWith("RDP frame markers active", StringComparison.OrdinalIgnoreCase))
        {
            lock (_frameSync)
            {
                if (_frameMode != RdpFramePresentationMode.Legacy)
                    _frameComposer.SetBoundaryMode(true);
            }
            DebugLog("frame markers active; switched to frame-boundary presentation");
            return;
        }
        if (text.StartsWith("RDP frame markers unavailable", StringComparison.OrdinalIgnoreCase))
        {
            if (_gfxFrameMode)
            {
                DebugLog("ignoring frame-marker fallback while GFX frames are active");
                return;
            }
            lock (_frameSync)
            {
                if (_frameMode != RdpFramePresentationMode.Legacy)
                    _frameComposer.Configure(FrameCoalescingEnabled, false);
            }
            DebugLog("frame markers unavailable; staying on coalescing");
            return;
        }

        DebugLogStatus(text);
        if (RdpSecurityProfileCache.TryParseProfile(text, out var profile))
            RdpSecurityProfileCache.Store(_profileCacheHost, _profileCachePort, profile);
        StatusChanged?.Invoke(text);
    }

    private static bool TryInvokeOptionalNative(string exportName, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            DebugLog($"native bridge export missing: {exportName}; falling back.");
            return false;
        }
    }

    internal static string NormalizeExplicitSecurityProfile(string? configuredMode)
    {
        if (string.IsNullOrWhiteSpace(configuredMode))
            return string.Empty;

        return configuredMode.Trim().ToLowerInvariant() switch
        {
            "negotiate" => "negotiate",
            "nla" => "nla",
            "tls" => "tls",
            "rdp" => "rdp",
            _ => string.Empty
        };
    }

    private void OnDisconnected(IntPtr userData)
    {
        DebugLog("disconnected callback");
        Disconnected?.Invoke();
    }

    private void OnPointer(IntPtr userData, int kind, int width, int height, int hotX, int hotY, IntPtr bgraPixels)
    {
        byte[]? copy = null;
        if (kind == 2 && bgraPixels != IntPtr.Zero && width > 0 && height > 0)
        {
            var byteCount = checked(width * height * 4);
            copy = new byte[byteCount];
            Marshal.Copy(bgraPixels, copy, 0, byteCount);
        }

        PointerShapeChanged?.Invoke(kind, width, height, hotX, hotY, copy);
    }

    private void OnClipboardText(IntPtr userData, IntPtr text)
    {
        var value = text == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(text) ?? string.Empty;
        DebugLogDetailed($"clipboard received remote length={value.Length}");
        ClipboardTextReceived?.Invoke(value);
    }

    private void OnClipboardImage(IntPtr userData, uint imageFormat, IntPtr data, uint length)
    {
        if (data == IntPtr.Zero || length == 0)
            return;

        var copy = new byte[length];
        Marshal.Copy(data, copy, 0, (int)length);
        ClipboardImageReceived?.Invoke(imageFormat, copy);
    }

    private void OnClipboardFiles(IntPtr userData, IntPtr filePaths)
    {
        if (filePaths == IntPtr.Zero)
            return;

        var raw = Marshal.PtrToStringUTF8(filePaths);
        if (string.IsNullOrWhiteSpace(raw))
            return;

        var paths = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (paths.Length > 0)
            ClipboardFilesReceived?.Invoke(paths);
    }

    private static string GetDebugLogDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = AppContext.BaseDirectory;

        return Path.Combine(root, "CxShell", "Logs");
    }

    private static void DebugLog(string message)
    {
        try
        {
            Directory.CreateDirectory(GetDebugLogDirectory());
            var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
            lock (DebugLogLock)
                File.AppendAllText(DebugLogPath, line, Encoding.UTF8);
        }
        catch
        {
            // Debug logging must not break the RDP session.
        }
    }

    private static void DebugLogDetailed(string message)
    {
        if (DetailedDebugLogEnabled)
            DebugLog(message);
    }

    private static void DebugLogStatus(string status)
    {
        if (DetailedDebugLogEnabled || ShouldLogStatus(status))
            DebugLog($"status {SanitizeLogValue(status)}");
    }

    private static bool ShouldLogStatus(string status)
    {
        if (!status.StartsWith("RDP clipboard ", StringComparison.OrdinalIgnoreCase))
            return true;

        return status.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("disabled", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("waiting for server MonitorReady", StringComparison.OrdinalIgnoreCase) ||
               status.StartsWith("RDP clipboard channel ready", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEnvironmentFlagEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(value) &&
               !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEnvironmentFlagDisabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(value) &&
               (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "no", StringComparison.OrdinalIgnoreCase));
    }

    private static string SanitizeLogValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<empty>";

        return value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static int ResolveColorDepth(string? value)
    {
        return int.TryParse(value, out var parsed) && parsed is 15 or 16 or 24 or 32
            ? parsed
            : 32;
    }

    private static string NormalizeDriveName(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "CxShell" : value.Trim();
        return name.Length <= 32 ? name : name[..32];
    }

    private static uint GetNativeApiVersion()
    {
        try
        {
            return NativeMethods.cxrdp_get_api_version();
        }
        catch (EntryPointNotFoundException)
        {
            return 1;
        }
    }

    private static uint GetNativeCapabilities()
    {
        try
        {
            return NativeMethods.cxrdp_get_capabilities();
        }
        catch (EntryPointNotFoundException)
        {
            return 0x00000001;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FrameCallback(IntPtr userData, int width, int height, int stride, IntPtr bgraPixels);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StatusCallback(IntPtr userData, IntPtr message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DisconnectCallback(IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ClipboardTextCallback(IntPtr userData, IntPtr text);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ClipboardImageCallback(IntPtr userData, uint imageFormat, IntPtr data, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ClipboardFilesCallback(IntPtr userData, IntPtr filePaths);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PointerCallback(
        IntPtr userData,
        int kind,
        int width,
        int height,
        int hotX,
        int hotY,
        IntPtr bgraPixels);

    private static class NativeMethods
    {
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint cxrdp_get_api_version();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint cxrdp_get_capabilities();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr cxrdp_create();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_destroy(IntPtr handle);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_set_callbacks(
            IntPtr handle,
            FrameCallback frameCallback,
            StatusCallback statusCallback,
            DisconnectCallback disconnectCallback,
            IntPtr userData);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_set_clipboard_callback(
            IntPtr handle,
            ClipboardTextCallback clipboardTextCallback);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int cxrdp_connect(
            IntPtr handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string host,
            int port,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string username,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
            int width,
            int height,
            int colorDepth);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_disconnect(IntPtr handle);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cxrdp_resize_monitors(
            IntPtr handle,
            [In] NativeMonitorRect[] monitors,
            int count);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_send_pointer(IntPtr handle, ushort flags, ushort x, ushort y);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_send_key(IntPtr handle, uint key, int down);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_send_unicode_key(IntPtr handle, ushort code, int down);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_set_pointer_callback(IntPtr handle, PointerCallback? pointerCallback);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_set_clipboard_image_callback(IntPtr handle, ClipboardImageCallback? callback);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_set_clipboard_files_callback(IntPtr handle, ClipboardFilesCallback? callback);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_set_clipboard_rich_text(
            IntPtr handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string plainText,
            byte[]? htmlBytes,
            uint htmlLength,
            byte[]? rtfBytes,
            uint rtfLength);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_set_clipboard_image(
            IntPtr handle,
            uint imageFormat,
            byte[] data,
            uint length);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_set_clipboard_files(
            IntPtr handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filePathsNewlineSeparated);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cxrdp_set_clipboard_lock_enabled(IntPtr handle, int enabled);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_sync_lock_keys(IntPtr handle, uint flags);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_set_clipboard_text(
            IntPtr handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string text);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cxrdp_set_drive_redirection(
            IntPtr handle,
            int enabled,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string driveName,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string localPath);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cxrdp_set_audio_redirection(
            IntPtr handle,
            int playbackMode,
            int microphoneEnabled);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cxrdp_set_keyboard_options(
            IntPtr handle,
            int applyKeyCombinationsRemotely);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cxrdp_set_display_scale(IntPtr handle, int scalePercent);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cxrdp_update_display(
            IntPtr handle,
            int width,
            int height,
            int scalePercent);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cxrdp_set_security_profile(
            IntPtr handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string profileName);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cxrdp_set_clipboard_channel(IntPtr handle, int enabled);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cxrdp_set_frame_marker_enabled(IntPtr handle, int enabled);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cxrdp_set_disable_wallpaper(IntPtr handle, int enabled);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cxrdp_get_frame_dirty_rect(
            IntPtr handle,
            out int x,
            out int y,
            out int width,
            out int height);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cxrdp_set_graphics_pipeline(IntPtr handle, int enabled);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr cxrdp_get_last_error(IntPtr handle);
    }
}
