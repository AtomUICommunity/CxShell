using System;

namespace CxShell.Models;

/// <summary>
/// How RDP framebuffer updates are turned into visible frames.
///
/// Auto prefers the server's frame markers (like MSTSC) and falls back to time
/// based coalescing when the server does not send them. Coalesce and FrameMarker
/// force one strategy; Legacy restores the original "show every batch" path.
/// </summary>
public enum RdpFramePresentationMode
{
    Auto,
    Coalesce,
    FrameMarker,
    Legacy
}

public static class RdpFramePresentationModes
{
    public static RdpFramePresentationMode Resolve(string? value) =>
        Enum.TryParse<RdpFramePresentationMode>(value, true, out var mode) && Enum.IsDefined(mode)
            ? mode
            : RdpFramePresentationMode.Auto;

    public static string Normalize(string? value) => Resolve(value).ToString();

    public static bool UsesFrameMarkers(RdpFramePresentationMode mode) =>
        mode is RdpFramePresentationMode.Auto or RdpFramePresentationMode.FrameMarker;
}
