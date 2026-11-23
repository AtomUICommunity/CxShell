using System;

namespace CxShell.ViewModels;

internal enum RdpReconnectPhase
{
    None,
    ConnectionLost,
    Reconnecting,
    Reconnected,
    Failed
}

/// <summary>
/// Maps the reconnect status lines emitted by the native bridge to the states
/// the RDP view shows. Keep the prefixes in sync with
/// <c>native/CxRdpBridge/cxrdp_bridge.cpp</c>.
/// </summary>
internal static class RdpReconnectStatus
{
    private const string ConnectionLostPrefix = "RDP connection lost";
    private const string ReconnectingPrefix = "RDP reconnecting";
    private const string ReconnectedPrefix = "RDP reconnected";
    private const string FailedPrefix = "RDP reconnect failed";

    public static RdpReconnectPhase Classify(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return RdpReconnectPhase.None;

        if (message.StartsWith(ReconnectingPrefix, StringComparison.OrdinalIgnoreCase))
            return RdpReconnectPhase.Reconnecting;

        if (message.StartsWith(FailedPrefix, StringComparison.OrdinalIgnoreCase))
            return RdpReconnectPhase.Failed;

        if (message.StartsWith(ReconnectedPrefix, StringComparison.OrdinalIgnoreCase))
            return RdpReconnectPhase.Reconnected;

        if (message.StartsWith(ConnectionLostPrefix, StringComparison.OrdinalIgnoreCase))
            return RdpReconnectPhase.ConnectionLost;

        return RdpReconnectPhase.None;
    }

    /// <summary>
    /// True for a failure that the bridge is still recovering from. The old UI
    /// treated every "failed" line as a terminal disconnect, so a single
    /// failed security profile or a resize that raced the display channel
    /// made the tab flicker through three "disconnected" states while the
    /// connection was actually being established.
    /// </summary>
    public static bool IsTransientFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        // One security profile attempt; the bridge tries the next one.
        if (message.StartsWith("FreeRDP connection failed", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("mode=", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Display control channel is not ready yet; the request stays queued.
        if (message.StartsWith("RDP display update failed", StringComparison.OrdinalIgnoreCase))
            return true;

        if (message.StartsWith("RDP authentication failed;", StringComparison.OrdinalIgnoreCase))
            return true;

        // Optional clipboard channel problems: the bridge logs the retry line
        // and continues (or reconnects once without clipboard).
        if (message.StartsWith("RDP clipboard ", StringComparison.OrdinalIgnoreCase) &&
            (message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("retrying without clipboard", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("closed the session early", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads the attempt counter out of "RDP reconnecting (attempt 7)" so the
    /// view can render the line in the user's language.
    /// </summary>
    public static int ParseAttempt(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) ||
            !message.StartsWith(ReconnectingPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var open = message.IndexOf('(');
        if (open < 0)
            return 0;

        var start = -1;
        for (var i = open + 1; i < message.Length; i++)
        {
            if (char.IsDigit(message[i]))
            {
                start = i;
                break;
            }

            if (message[i] == ')')
                return 0;
        }

        if (start < 0)
            return 0;

        var length = 0;
        while (start + length < message.Length && char.IsDigit(message[start + length]))
            length++;

        return int.TryParse(message.AsSpan(start, length), out var attempt) ? attempt : 0;
    }
}
