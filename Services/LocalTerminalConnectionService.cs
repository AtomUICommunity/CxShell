using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using CxShell.Models;

namespace CxShell.Services;

/// <summary>
/// Hosts a local interactive shell through a real pseudo-terminal. A PTY is
/// required for full-screen programs such as vim/nano and for correct Ctrl+C,
/// sudo, terminal modes, and window-size negotiation.
/// </summary>
public sealed class LocalTerminalConnectionService : ITerminalConnectionService
{
    private readonly object _writeGate = new();
    private readonly object _stateGate = new();
    private PtySession? _pty;
    private CancellationTokenSource? _readCancellation;
    private Task? _readTask;
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private int _connected;
    private int _closedRaised;
    private bool _disposed;

    public bool IsConnected => Volatile.Read(ref _connected) != 0;

    public bool SupportsPosixShellFeatures { get; private set; } = true;

    public event Action<string>? DataReceived;
    public event Func<byte[], bool>? BinaryDataReceived;
    public event Action<string>? ConnectionClosed;
    public event Action<string>? ErrorOccurred;

    public Task ConnectAsync(
        SessionInfo session,
        string? password,
        int columns = 80,
        int rows = 24,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (session.Protocol != SessionProtocol.Local)
            throw new InvalidOperationException("The local terminal service requires a local session.");

        var profile = session.LocalTerminalProfile
            ?? throw new InvalidOperationException("The local terminal profile is missing.");

        lock (_stateGate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(LocalTerminalConnectionService));

            if (_pty != null)
                throw new InvalidOperationException("The local terminal is already connected.");

            SupportsPosixShellFeatures = profile.SupportsPosixShellFeatures;
            _decoder = TerminalSessionOptions.GetEncoding(session).GetDecoder();
            _closedRaised = 0;
            _pty = PtySession.Start(profile, columns, rows);
            _readCancellation = new CancellationTokenSource();
            Volatile.Write(ref _connected, 1);
            _readTask = ReadOutputAsync(_pty, _readCancellation.Token);
        }

        return Task.CompletedTask;
    }

    public void SendData(string data)
    {
        if (string.IsNullOrEmpty(data) || !IsConnected)
            return;

        SendBytes(Encoding.UTF8.GetBytes(data));
    }

    public void SendBytes(byte[] data)
    {
        if (data.Length == 0 || !IsConnected)
            return;

        PtySession? pty;
        lock (_stateGate)
            pty = _pty;

        if (pty == null)
            return;

        try
        {
            lock (_writeGate)
            {
                if (!IsConnected)
                    return;

                pty.Write(data);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (IsConnected)
                ErrorOccurred?.Invoke($"Local terminal input failed: {ex.Message}");
        }
    }

    public void SendKeepAlive()
    {
        // A local PTY has no keep-alive protocol. Writing a byte here would be
        // visible to the shell, so this operation intentionally does nothing.
    }

    public void ResizeTerminal(int columns, int rows)
    {
        PtySession? pty;
        lock (_stateGate)
            pty = _pty;

        try
        {
            pty?.Resize(columns, rows);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            if (IsConnected)
                ErrorOccurred?.Invoke($"Local terminal resize failed: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        PtySession? pty;
        CancellationTokenSource? readCancellation;
        lock (_stateGate)
        {
            pty = _pty;
            _pty = null;
            readCancellation = _readCancellation;
            _readCancellation = null;
            Volatile.Write(ref _connected, 0);
        }

        readCancellation?.Cancel();
        try
        {
            pty?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Local terminal cleanup failed: {ex.Message}");
        }

        readCancellation?.Dispose();
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        Disconnect();
    }

    private async Task ReadOutputAsync(PtySession pty, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await pty.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                    break;

                var chunk = buffer[..bytesRead].ToArray();
                if (BinaryDataReceived?.Invoke(chunk) == true)
                    continue;

                var charCount = _decoder.GetCharCount(chunk, 0, chunk.Length);
                if (charCount == 0)
                    continue;

                var chars = new char[charCount];
                var charsRead = _decoder.GetChars(chunk, 0, chunk.Length, chars, 0);
                if (charsRead > 0)
                    DataReceived?.Invoke(new string(chars, 0, charsRead));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (!cancellationToken.IsCancellationRequested)
                failure = ex;
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                failure = ex;
        }
        finally
        {
            if (failure != null)
                ErrorOccurred?.Invoke($"Local terminal output failed: {failure.Message}");

            lock (_stateGate)
            {
                if (ReferenceEquals(_pty, pty))
                {
                    _pty = null;
                    Volatile.Write(ref _connected, 0);
                }
            }

            try
            {
                pty.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Local terminal output cleanup failed: {ex.Message}");
            }

            RaiseConnectionClosed(failure?.Message ?? "Local terminal closed.");
        }
    }

    private void RaiseConnectionClosed(string reason)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
            ConnectionClosed?.Invoke(reason);
    }

    private abstract class PtySession : IDisposable
    {
        public abstract Stream Input { get; }
        public abstract Stream Output { get; }

        public abstract Task<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken);

        public abstract void Write(byte[] data);

        public abstract void Resize(int columns, int rows);

        public abstract void Dispose();

        public static PtySession Start(LocalTerminalProfile profile, int columns, int rows)
        {
            if (OperatingSystem.IsWindows())
                return WindowsConPtySession.Start(profile, columns, rows);

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return UnixForkPtySession.Start(profile, columns, rows);

            throw new PlatformNotSupportedException("Local terminals are not supported on this operating system.");
        }
    }

    private sealed class WindowsConPtySession : PtySession
    {
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const int StartfUseStdHandles = 0x00000100;
        private const int ProcThreadAttributePseudoConsole = 0x00020016;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;

        private readonly IntPtr _console;
        private readonly FileStream _input;
        private readonly FileStream _output;
        private readonly Process _process;
        private readonly IntPtr _job;
        private readonly object _closeGate = new();
        private bool _consoleClosed;
        private bool _disposed;

        private WindowsConPtySession(
            IntPtr console,
            FileStream input,
            FileStream output,
            Process process,
            IntPtr job)
        {
            _console = console;
            _input = input;
            _output = output;
            _process = process;
            _job = job;
        }

        public override Stream Input => _input;
        public override Stream Output => _output;

        public override Task<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            // CreatePipe returns synchronous anonymous-pipe handles. Use the native
            // blocking read on the thread pool instead of FileStream.ReadAsync, which
            // requires an overlapped/asynchronous Windows handle.
            return Task.Run(() =>
            {
                if (WindowsNative.ReadFile(
                        _output.SafeFileHandle,
                        buffer,
                        (uint)buffer.Length,
                        out var bytesRead,
                        IntPtr.Zero))
                {
                    return (int)bytesRead;
                }

                var error = Marshal.GetLastWin32Error();
                return error is 109 or 232 or 995
                    ? 0
                    : throw new IOException($"ReadFile(output) failed: {error}");
            }, cancellationToken);
        }

        public override void Write(byte[] data)
        {
            if (!WindowsNative.WriteFile(
                    _input.SafeFileHandle,
                    data,
                    (uint)data.Length,
                    out var bytesWritten,
                    IntPtr.Zero) ||
                bytesWritten != (uint)data.Length)
            {
                ThrowLastError("WriteFile(input)");
            }
        }

        public static new WindowsConPtySession Start(LocalTerminalProfile profile, int columns, int rows)
        {
            columns = Math.Clamp(columns, 2, 500);
            rows = Math.Clamp(rows, 2, 500);
            var workingDirectory = string.IsNullOrWhiteSpace(profile.WorkingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : profile.WorkingDirectory;

            IntPtr inputRead = IntPtr.Zero;
            IntPtr inputWrite = IntPtr.Zero;
            IntPtr outputRead = IntPtr.Zero;
            IntPtr outputWrite = IntPtr.Zero;
            IntPtr console = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            IntPtr processHandle = IntPtr.Zero;
            IntPtr threadHandle = IntPtr.Zero;
            Process? process = null;
            IntPtr job = IntPtr.Zero;

            try
            {
                var pipeAttributes = new WindowsNative.SecurityAttributes
                {
                    Length = Marshal.SizeOf<WindowsNative.SecurityAttributes>(),
                    InheritHandle = 1
                };

                if (!WindowsNative.CreatePipe(out inputRead, out inputWrite, ref pipeAttributes, 0))
                    ThrowLastError("CreatePipe(input)");

                if (!WindowsNative.CreatePipe(out outputRead, out outputWrite, ref pipeAttributes, 0))
                    ThrowLastError("CreatePipe(output)");

                var result = WindowsNative.CreatePseudoConsole(
                    new Coord((short)columns, (short)rows),
                    inputRead,
                    outputWrite,
                    0,
                    out console);
                if (result != 0)
                    throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{result:X8}");

                var startupInfo = new StartupInfoEx();
                startupInfo.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
                startupInfo.StartupInfo.flags = StartfUseStdHandles;
                IntPtr attributeListSize = IntPtr.Zero;
                WindowsNative.InitializeProcThreadAttributeList(
                    IntPtr.Zero,
                    1,
                    0,
                    ref attributeListSize);
                attributeList = Marshal.AllocHGlobal(attributeListSize);

                if (!WindowsNative.InitializeProcThreadAttributeList(
                        attributeList,
                        1,
                        0,
                        ref attributeListSize) ||
                    !WindowsNative.UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        ProcThreadAttributePseudoConsole,
                        console,
                        IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    ThrowLastError("InitializeProcThreadAttributeList");
                }

                startupInfo.lpAttributeList = attributeList;
                var commandLine = new StringBuilder(profile.CommandLine);
                if (!WindowsNative.CreateProcess(
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        ExtendedStartupInfoPresent,
                        IntPtr.Zero,
                        workingDirectory,
                        ref startupInfo,
                        out var processInfo))
                {
                    ThrowLastError($"CreateProcess ({profile.CommandLine})");
                }

                // ConPTY needs the server-side pipe handles to remain valid through
                // CreateProcess. The pseudo console retains its channel after that
                // point, so only the parent-side handles are kept for I/O.
                WindowsNative.CloseHandle(inputRead);
                inputRead = IntPtr.Zero;
                WindowsNative.CloseHandle(outputWrite);
                outputWrite = IntPtr.Zero;

                processHandle = processInfo.hProcess;
                threadHandle = processInfo.hThread;
                job = CreateKillOnCloseJob(processHandle);
                process = Process.GetProcessById((int)processInfo.dwProcessId);
                WindowsNative.CloseHandle(threadHandle);
                threadHandle = IntPtr.Zero;
                WindowsNative.CloseHandle(processHandle);
                processHandle = IntPtr.Zero;

                var input = new FileStream(
                    new SafeFileHandle(inputWrite, ownsHandle: true),
                    FileAccess.Write,
                    4096,
                    isAsync: false);
                inputWrite = IntPtr.Zero;
                var output = new FileStream(
                    new SafeFileHandle(outputRead, ownsHandle: true),
                    FileAccess.Read,
                    16 * 1024,
                    isAsync: false);
                outputRead = IntPtr.Zero;
                var session = new WindowsConPtySession(console, input, output, process, job);
                console = IntPtr.Zero;
                process = null;
                job = IntPtr.Zero;

                session._process.EnableRaisingEvents = true;
                session._process.Exited += (_, _) => _ = Task.Run(async () =>
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    session.CloseConsole();
                });
                return session;
            }
            catch
            {
                process?.Dispose();
                if (job != IntPtr.Zero)
                    WindowsNative.CloseHandle(job);
                if (threadHandle != IntPtr.Zero)
                    WindowsNative.CloseHandle(threadHandle);
                if (processHandle != IntPtr.Zero)
                    WindowsNative.CloseHandle(processHandle);
                if (console != IntPtr.Zero)
                    WindowsNative.ClosePseudoConsole(console);
                CloseHandleIfPresent(inputRead);
                CloseHandleIfPresent(inputWrite);
                CloseHandleIfPresent(outputRead);
                CloseHandleIfPresent(outputWrite);
                throw;
            }
            finally
            {
                if (attributeList != IntPtr.Zero)
                {
                    WindowsNative.DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
            }
        }

        public override void Resize(int columns, int rows)
        {
            lock (_closeGate)
            {
                if (_disposed || _consoleClosed)
                    return;

                var result = WindowsNative.ResizePseudoConsole(
                    _console,
                    new Coord((short)Math.Clamp(columns, 2, 500), (short)Math.Clamp(rows, 2, 500)));
                if (result != 0)
                    throw new InvalidOperationException($"ResizePseudoConsole failed: 0x{result:X8}");
            }
        }

        public override void Dispose()
        {
            lock (_closeGate)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }

            try
            {
                if (_job != IntPtr.Zero)
                    WindowsNative.TerminateJobObject(_job, 0);
                else if (!_process.HasExited)
                    _process.Kill();
            }
            catch
            {
                // The shell may have already exited.
            }

            CloseConsole();
            try { _output.Dispose(); } catch { }
            if (_job != IntPtr.Zero)
                WindowsNative.CloseHandle(_job);
            _process.Dispose();
        }

        private void CloseConsole()
        {
            lock (_closeGate)
            {
                if (_consoleClosed)
                    return;

                _consoleClosed = true;

                if (_console != IntPtr.Zero)
                    WindowsNative.ClosePseudoConsole(_console);
            }

            try { _input.Dispose(); } catch { }
        }

        private static IntPtr CreateKillOnCloseJob(IntPtr processHandle)
        {
            var job = WindowsNative.CreateJobObject(IntPtr.Zero, IntPtr.Zero);
            if (job == IntPtr.Zero)
                return IntPtr.Zero;

            var info = new JobObjectExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var infoPointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, infoPointer, fDeleteOld: false);
                if (!WindowsNative.SetInformationJobObject(
                        job,
                        JobObjectExtendedLimitInformationClass,
                        infoPointer,
                        (uint)size) ||
                    !WindowsNative.AssignProcessToJobObject(job, processHandle))
                {
                    WindowsNative.CloseHandle(job);
                    return IntPtr.Zero;
                }

                return job;
            }
            finally
            {
                Marshal.FreeHGlobal(infoPointer);
            }
        }

        private static void ThrowLastError(string operation)
        {
            throw new InvalidOperationException($"{operation} failed: {Marshal.GetLastWin32Error()}");
        }

        private static void CloseHandleIfPresent(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
                WindowsNative.CloseHandle(handle);
        }

        private static class WindowsNative
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct SecurityAttributes
            {
                public int Length;
                public IntPtr SecurityDescriptor;
                public int InheritHandle;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CreatePipe(
                out IntPtr readPipe,
                out IntPtr writePipe,
                ref SecurityAttributes pipeAttributes,
                int size);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr CreateJobObject(IntPtr jobAttributes, IntPtr name);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetInformationJobObject(
                IntPtr job,
                int informationClass,
                IntPtr information,
                uint length);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool TerminateJobObject(IntPtr job, uint exitCode);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool WriteFile(
                SafeFileHandle file,
                byte[] buffer,
                uint numberOfBytesToWrite,
                out uint numberOfBytesWritten,
                IntPtr overlapped);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ReadFile(
                SafeFileHandle file,
                byte[] buffer,
                uint numberOfBytesToRead,
                out uint numberOfBytesRead,
                IntPtr overlapped);

            [DllImport("kernel32.dll")]
            public static extern int CreatePseudoConsole(
                Coord size,
                IntPtr input,
                IntPtr output,
                uint flags,
                out IntPtr console);

            [DllImport("kernel32.dll")]
            public static extern int ResizePseudoConsole(IntPtr console, Coord size);

            [DllImport("kernel32.dll")]
            public static extern void ClosePseudoConsole(IntPtr console);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool InitializeProcThreadAttributeList(
                IntPtr attributeList,
                int attributeCount,
                int flags,
                ref IntPtr size);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool UpdateProcThreadAttribute(
                IntPtr attributeList,
                uint flags,
                int attribute,
                IntPtr value,
                IntPtr size,
                IntPtr previousValue,
                IntPtr returnSize);

            [DllImport("kernel32.dll")]
            public static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

            [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CreateProcess(
                string? applicationName,
                StringBuilder commandLine,
                IntPtr processAttributes,
                IntPtr threadAttributes,
                [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
                uint creationFlags,
                IntPtr environment,
                string? currentDirectory,
                ref StartupInfoEx startupInfo,
                out ProcessInformation processInformation);
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct Coord
        {
            public Coord(short x, short y)
            {
                X = x;
                Y = y;
            }

            public readonly short X;
            public readonly short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfo
        {
            public int cb;
            public IntPtr reserved;
            public IntPtr desktop;
            public IntPtr title;
            public int x;
            public int y;
            public int xSize;
            public int ySize;
            public int xCountChars;
            public int yCountChars;
            public int fillAttribute;
            public int flags;
            public short showWindow;
            public short reserved2;
            public IntPtr reserved2Pointer;
            public IntPtr standardInput;
            public IntPtr standardOutput;
            public IntPtr standardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long perProcessUserTimeLimit;
            public long perJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr minimumWorkingSetSize;
            public UIntPtr maximumWorkingSetSize;
            public uint activeProcessLimit;
            public UIntPtr affinity;
            public uint priorityClass;
            public uint schedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong readOperationCount;
            public ulong writeOperationCount;
            public ulong otherOperationCount;
            public ulong readTransferCount;
            public ulong writeTransferCount;
            public ulong otherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr processMemoryLimit;
            public UIntPtr jobMemoryLimit;
            public UIntPtr peakProcessMemoryUsed;
            public UIntPtr peakJobMemoryUsed;
        }
    }

    private sealed class UnixForkPtySession : PtySession
    {
        private const int SigTerm = 15;
        private const int SigKill = 9;

        private readonly FileStream _stream;
        private readonly int _processId;
        private readonly object _closeGate = new();
        private bool _closed;

        private UnixForkPtySession(FileStream stream, int processId)
        {
            _stream = stream;
            _processId = processId;
        }

        public override Stream Input => _stream;
        public override Stream Output => _stream;

        public override Task<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            // A PTY master file descriptor does not support asynchronous I/O.
            // Use a blocking read on the thread pool instead of FileStream.ReadAsync,
            // which requires an async-capable handle on macOS.
            return Task.Run(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return 0;

                try
                {
                    return _stream.Read(buffer, 0, buffer.Length);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    return 0;
                }
            }, cancellationToken);
        }

        public override void Write(byte[] data)
        {
            _stream.Write(data, 0, data.Length);
            _stream.Flush();
        }

        public static new UnixForkPtySession Start(LocalTerminalProfile profile, int columns, int rows)
        {
            columns = Math.Clamp(columns, 2, 500);
            rows = Math.Clamp(rows, 2, 500);
            var argv = new List<IntPtr>(profile.Arguments.Count + 1);
            var environment = BuildEnvironment();
            var envp = new List<IntPtr>(environment.Count + 1);
            var allocatedStrings = new List<IntPtr>(argv.Count + envp.Count + 2);
            IntPtr argvPointer = IntPtr.Zero;
            IntPtr envpPointer = IntPtr.Zero;
            IntPtr workingDirectory = IntPtr.Zero;

            try
            {
                argv.Add(AllocateUtf8(profile.ExecutablePath, allocatedStrings));
                foreach (var argument in profile.Arguments)
                    argv.Add(AllocateUtf8(argument, allocatedStrings));
                argvPointer = WritePointerArray(argv);

                foreach (var entry in environment)
                    envp.Add(AllocateUtf8(entry, allocatedStrings));
                envpPointer = WritePointerArray(envp);

                if (!string.IsNullOrWhiteSpace(profile.WorkingDirectory))
                    workingDirectory = AllocateUtf8(profile.WorkingDirectory, allocatedStrings);

                // The child is started and exec'd entirely inside the native
                // bridge: returning to managed code after fork() crashes the
                // child process on both Linux and macOS.
                var processId = CxPtyBridge.Start(
                    argv[0],
                    argvPointer,
                    envpPointer,
                    workingDirectory,
                    rows,
                    columns,
                    out var master);
                if (processId < 0)
                    throw new InvalidOperationException($"forkpty failed: errno {Marshal.GetLastPInvokeError()}");

                var stream = new FileStream(
                    new SafeFileHandle((IntPtr)master, ownsHandle: true),
                    FileAccess.ReadWrite,
                    16 * 1024,
                    isAsync: false);
                return new UnixForkPtySession(stream, processId);
            }
            finally
            {
                if (argvPointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(argvPointer);
                if (envpPointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(envpPointer);
                foreach (var pointer in allocatedStrings)
                    Marshal.FreeHGlobal(pointer);
            }
        }

        private static List<string> BuildEnvironment()
        {
            var environment = new List<string>();
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is not string name)
                    continue;

                if (string.Equals(name, "TERM", StringComparison.Ordinal) ||
                    string.Equals(name, "COLORTERM", StringComparison.Ordinal))
                {
                    continue;
                }

                environment.Add($"{name}={entry.Value}");
            }

            environment.Add("TERM=xterm-256color");
            environment.Add("COLORTERM=truecolor");
            return environment;
        }

        public override void Resize(int columns, int rows)
        {
            lock (_closeGate)
            {
                if (_closed)
                    return;

                try
                {
                    var fileDescriptor = _stream.SafeFileHandle.DangerousGetHandle().ToInt32();
                    var result = CxPtyBridge.SetWindowSize(
                        fileDescriptor,
                        Math.Clamp(rows, 2, 500),
                        Math.Clamp(columns, 2, 500));
                    if (result != 0)
                        Debug.WriteLine($"ioctl(TIOCSWINSZ) failed: errno {Marshal.GetLastPInvokeError()}");
                }
                catch (Exception ex)
                {
                    // Window-size negotiation is best effort: a failure only
                    // leaves full-screen programs with a stale size, whereas a
                    // throw here would abort the whole local terminal session.
                    Debug.WriteLine($"ioctl(TIOCSWINSZ) unavailable: {ex.Message}");
                }
            }
        }

        public override void Dispose()
        {
            lock (_closeGate)
            {
                if (_closed)
                    return;

                _closed = true;
            }

            if (_processId > 0)
            {
                try { UnixNative.Kill(-_processId, SigTerm); } catch { }
            }

            try { _stream.Dispose(); } catch { }
            ReapProcess();
        }

        private void ReapProcess()
        {
            if (_processId <= 0)
                return;

            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    var result = UnixNative.WaitPid(_processId, out _, UnixNative.NoHang);
                    if (result != 0)
                        return;
                }
                catch
                {
                    return;
                }

                Thread.Sleep(25);
            }

            try { UnixNative.Kill(-_processId, SigKill); } catch { }
            try { UnixNative.WaitPid(_processId, out _, 0); } catch { }
        }

        private static IntPtr AllocateUtf8(string value, ICollection<IntPtr> allocatedStrings)
        {
            var pointer = Marshal.StringToCoTaskMemUTF8(value);
            allocatedStrings.Add(pointer);
            return pointer;
        }

        private static IntPtr WritePointerArray(IReadOnlyList<IntPtr> values)
        {
            var pointer = Marshal.AllocHGlobal((values.Count + 1) * IntPtr.Size);
            for (var index = 0; index < values.Count; index++)
                Marshal.WriteIntPtr(pointer, index * IntPtr.Size, values[index]);
            Marshal.WriteIntPtr(pointer, values.Count * IntPtr.Size, IntPtr.Zero);
            return pointer;
        }

        private static class UnixNative
        {
            public const int NoHang = 1;

            // On Unix the runtime resolves the exported symbol by its exact
            // name, so every entry point is declared explicitly; leaving it out
            // makes the method name (Kill, WaitPid, ...) the lookup key.
            [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
            public static extern int Kill(int processId, int signal);

            [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
            public static extern int WaitPid(int processId, out int status, int options);
        }
    }
}
