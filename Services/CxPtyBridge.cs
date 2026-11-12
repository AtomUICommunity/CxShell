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

    [DllImport(Library, EntryPoint = "cx_pty_read", SetLastError = true)]
    private static extern int ReadNative(int master, IntPtr buffer, int length);

    [DllImport(Library, EntryPoint = "cx_pty_write", SetLastError = true)]
    private static extern int WriteNative(int master, IntPtr buffer, int length);

    [DllImport(Library, EntryPoint = "cx_pty_close", SetLastError = true)]
    private static extern int CloseNative(int master);

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

    /// <summary>
    /// Reads into <paramref name="buffer"/>. Returns the byte count, 0 at end of
    /// file (the shell exited), or -1 on failure.
    /// </summary>
    public static int Read(int master, byte[] buffer, int offset, int count)
    {
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        try
        {
            return ReadNative(master, IntPtr.Add(handle.AddrOfPinnedObject(), offset), count);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Writes the whole range, retrying on partial writes. Returns 0 on success
    /// or -1 on failure.
    /// </summary>
    public static int Write(int master, byte[] buffer, int offset, int count)
    {
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        try
        {
            return WriteNative(master, IntPtr.Add(handle.AddrOfPinnedObject(), offset), count) < 0 ? -1 : 0;
        }
        finally
        {
            handle.Free();
        }
    }

    public static void Close(int master) => CloseNative(master);
}
