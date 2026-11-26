using System;

namespace CxShell.Services;

public enum RdpDpiPolicy
{
    /// <summary>Match the local display scaling in the remote session.</summary>
    FollowLocal,

    /// <summary>Do not advertise a scale factor; keep whatever the remote session uses.</summary>
    KeepRemote,

    /// <summary>Force the remote session to 100% scaling.</summary>
    Fixed100
}

public static class RdpDpiScale
{
    /// <summary>
    /// A scale factor of 0 tells the bridge not to send DPI remoting data at all.
    /// </summary>
    public const int NotAdvertised = 0;

    /// <summary>
    /// Windows RDP exposes scaling in discrete 25% steps (100/125/150/175/200).
    /// Quantizing avoids sending values the remote session may reject.
    /// </summary>
    public static int PercentFromRenderScaling(double renderScaling)
    {
        if (!double.IsFinite(renderScaling) || renderScaling <= 0)
            return 100;

        var percent = Math.Round(renderScaling * 4.0, MidpointRounding.AwayFromZero) * 25;
        return (int)Math.Clamp(percent, 100, 200);
    }

    public static int ResolveScalePercent(RdpDpiPolicy policy, double renderScaling)
    {
        return policy switch
        {
            RdpDpiPolicy.FollowLocal => PercentFromRenderScaling(renderScaling),
            RdpDpiPolicy.Fixed100 => 100,
            _ => NotAdvertised
        };
    }

    /// <summary>
    /// MS-RDPBCGR / MS-RDPEDISP only accept 100, 140, or 180 for DeviceScaleFactor;
    /// any other value causes Windows to ignore both DesktopScaleFactor and DeviceScaleFactor.
    /// </summary>
    public static uint DeviceScaleFactorFromDesktopScale(int desktopScalePercent)
    {
        if (desktopScalePercent >= 180)
            return 180u;
        if (desktopScalePercent >= 140)
            return 140u;
        return 100u;
    }
}
