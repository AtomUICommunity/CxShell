using System;
using System.Threading.Tasks;

namespace CxShell.Services;

public static class CommandLineHandoffService
{
    private static readonly object ChannelGate = new();
    private static SingleInstanceChannel? _activeChannel;

    /// <summary>
    /// Attempts to claim single-instance ownership for the current data
    /// directory. When another instance already owns the same data directory,
    /// forwards the launch arguments (or an activation ping when empty) to that
    /// instance and returns <see langword="false"/> so this process can exit.
    /// </summary>
    public static bool TryAcquireSingleInstanceOrForward(string[] args, out IDisposable? instanceLifetime)
    {
        instanceLifetime = null;
        if (ShouldBypassSingleInstance(args))
            return true;

        var channel = new SingleInstanceChannel();
        if (channel.IsFirstInstance)
        {
            lock (ChannelGate)
                _activeChannel = channel;

            instanceLifetime = new ChannelRegistration(channel);
            return true;
        }

        channel.SendToFirstInstance(args);
        channel.Dispose();
        return false;
    }

    public static void AttachHandler(Func<string[], Task> handler)
    {
        SingleInstanceChannel? channel;
        lock (ChannelGate)
            channel = _activeChannel;

        channel?.AttachHandler(handler);
    }

    internal static bool ShouldBypassSingleInstance(string[] args)
    {
        if (args.Length == 0)
            return false;

        if (ContainsArgument(args, "--rdp-smoke"))
            return true;

        var options = CommandLineLaunchOptions.Parse(args);
        return options.NewWindow;
    }

    private static bool ContainsArgument(string[] args, string expected)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal sealed record HandoffPayload(string[] Args);

    private sealed class ChannelRegistration(SingleInstanceChannel channel) : IDisposable
    {
        public void Dispose()
        {
            lock (ChannelGate)
            {
                if (ReferenceEquals(_activeChannel, channel))
                    _activeChannel = null;
            }

            channel.Dispose();
        }
    }
}
