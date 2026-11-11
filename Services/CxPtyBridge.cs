using System.Runtime.InteropServices;

namespace CxShell.Services;

/// <summary>
/// Native shim around <c>forkpty</c>/<c>execve</c> for Linux and macOS.
/// The child process must never return to managed code after the fork, so the
/// whole fork/exec sequence lives in <c>native/CxPtyBridge/CxPtyBridge.c</c>;
/// this type only marshals the arguments before the call.
/// </summary>
internal static class CxPtyBridge
{
    private const string Library = "CxPtyBridge";

    [DllImport(Library, EntryPoint = "cx_pty_spawn", SetLastError = true)]
    private static extern int Spawn(
        IntPtr executable,
        IntPtr argv,
        IntPtr envp,
        IntPtr workingDirectory,
        ushort rows,
        ushort columns,
        out int master);

    [DllImport(Library, EntryPoint = "cx_pty_resize", SetLastError = true)]
    private static extern int Resize(int master, ushort rows, ushort columns);

    public static int Start(
        IntPtr executable,
        IntPtr argv,
        IntPtr envp,
        IntPtr workingDirectory,
        int rows,
        int columns,
        out int master)
    {
        try
        {
            return Spawn(
                executable,
                argv,
                envp,
                workingDirectory,
                (ushort)rows,
                (ushort)columns,
                out master);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            master = -1;
            throw new InvalidOperationException(
                "The native PTY bridge (libCxPtyBridge) is missing or outdated. " +
                "Run tools/build-pty-bridge.sh and relaunch CxShell.",
                ex);
        }
    }

    public static int SetWindowSize(int master, int rows, int columns)
        => Resize(master, (ushort)rows, (ushort)columns);
}
