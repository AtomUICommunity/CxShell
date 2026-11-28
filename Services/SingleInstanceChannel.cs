using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CxShell.Services;

/// <summary>
/// Coordinates single-instance locking and IPC forwarding scoped to a specific
/// data directory, following SourceGit's lock-file + named-pipe pattern so an
/// installed instance and one or more portable instances can run side by side.
/// </summary>
public sealed class SingleInstanceChannel : IDisposable
{
    private const int ConnectTimeoutMilliseconds = 1000;
    private const int MaximumPayloadBytes = 64 * 1024;

    private readonly string _lockFilePath;
    private readonly string _pipeName;
    private readonly FileStream? _singletonLock;
    private readonly Thread? _serverThread;
    private readonly object _handlerGate = new();
    private readonly Queue<string[]> _pendingMessages = new();
    private Func<string[], Task>? _handler;
    private volatile bool _stopping;
    private bool _disposed;

    public bool IsFirstInstance { get; }

    public string PipeName => _pipeName;

    public string LockFilePath => _lockFilePath;

    public SingleInstanceChannel(string? storageDirectory = null, bool? isPortableMode = null)
    {
        var effectiveStorageDir = string.IsNullOrWhiteSpace(storageDirectory)
            ? AppDataPaths.GetStorageDirectory()
            : Path.GetFullPath(storageDirectory);
        var effectivePortable = isPortableMode ?? AppDataPaths.IsPortableMode;

        _pipeName = BuildPipeName(effectiveStorageDir);
        var lockFile = ResolveLockFile(effectiveStorageDir, effectivePortable);
        _lockFilePath = lockFile.Path;

        var lockDir = Path.GetDirectoryName(_lockFilePath);
        if (!string.IsNullOrWhiteSpace(lockDir))
            Directory.CreateDirectory(lockDir);

        _singletonLock = TryAcquireLock(lockFile);
        IsFirstInstance = _singletonLock != null;

        if (IsFirstInstance)
        {
            _serverThread = new Thread(RunServerLoop)
            {
                Name = "CxShell.SingleInstanceServer",
                IsBackground = true
            };
            _serverThread.Start();
        }
    }

    public void AttachHandler(Func<string[], Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        List<string[]>? queued = null;
        lock (_handlerGate)
        {
            _handler = handler;
            if (_pendingMessages.Count > 0)
            {
                queued = new List<string[]>(_pendingMessages.Count);
                while (_pendingMessages.Count > 0)
                    queued.Add(_pendingMessages.Dequeue());
            }
        }

        if (queued == null)
            return;

        foreach (var args in queued)
            _ = DispatchToHandlerAsync(handler, args);
    }

    public bool SendToFirstInstance(string[]? args)
    {
        var payload = EncodePayload(args ?? []);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.InOut,
                    PipeOptions.CurrentUserOnly);
                client.Connect(ConnectTimeoutMilliseconds);

                using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = true
                };
                writer.WriteLine(payload);

                var acknowledgement = client.ReadByte();
                return acknowledgement == 1;
            }
            catch
            {
                if (attempt < 2)
                    Thread.Sleep(100);
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stopping = true;

        if (IsFirstInstance)
        {
            WakeServerLoop();
            try
            {
                _serverThread?.Join(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Ignore shutdown races.
            }
        }

        _singletonLock?.Dispose();

        if (IsFirstInstance && File.Exists(_lockFilePath))
        {
            try
            {
                File.Delete(_lockFilePath);
            }
            catch
            {
                // Ignore best-effort lock file cleanup failures on shutdown.
            }
        }
    }

    internal static string BuildPipeName(string storageDirectory)
    {
        var scope = BuildInstanceScope(storageDirectory);
        return $"CxShell.Instance.v2.{scope}";
    }

    internal static string BuildInstanceScope(string storageDirectory)
    {
        var normalizedDir = NormalizeDirectoryForScope(storageDirectory);
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}|{normalizedDir}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string NormalizeDirectoryForScope(string storageDirectory)
    {
        var fullPath = Path.GetFullPath(storageDirectory)
            .Replace('\\', '/')
            .TrimEnd('/');
        return OperatingSystem.IsWindows()
            ? fullPath.ToLowerInvariant()
            : fullPath;
    }

    private static LockFileInfo ResolveLockFile(string storageDirectory, bool isPortableMode)
    {
        if (!OperatingSystem.IsLinux() || isPortableMode)
            return new LockFileInfo(Path.Combine(storageDirectory, "process.lock"), false);

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDir) || !Directory.Exists(runtimeDir))
            return new LockFileInfo(Path.Combine(storageDirectory, "process.lock"), false);

        var scope = BuildInstanceScope(storageDirectory);
        return new LockFileInfo(Path.Combine(runtimeDir, $"cxshell.{scope}.lock"), true);
    }

    private static FileStream? TryAcquireLock(LockFileInfo lockFile)
    {
        if (OperatingSystem.IsLinux() && lockFile.NeedChangePermissions)
        {
            try
            {
                var created = new FileStream(
                    lockFile.Path,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None);
                File.SetUnixFileMode(
                    lockFile.Path,
                    File.GetUnixFileMode(lockFile.Path) | UnixFileMode.StickyBit);
                return created;
            }
            catch
            {
                try
                {
                    return new FileStream(
                        lockFile.Path,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }
                catch
                {
                    return null;
                }
            }
        }

        try
        {
            return new FileStream(
                lockFile.Path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch
        {
            return null;
        }
    }

    private void RunServerLoop()
    {
        while (!_stopping)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.CurrentUserOnly);
                server.WaitForConnection();

                if (_stopping)
                    break;

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var line = reader.ReadLine();
                if (_stopping)
                    break;

                var accepted = TryDecodePayload(line, out var args);
                server.WriteByte(accepted ? (byte)1 : (byte)0);
                server.Flush();

                if (accepted)
                    EnqueueOrDispatch(args);
            }
            catch
            {
                if (_stopping)
                    break;

                Thread.Sleep(200);
            }
        }
    }

    private void WakeServerLoop()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.CurrentUserOnly);
            client.Connect(150);
        }
        catch
        {
            // Server loop may not have entered WaitForConnection yet or has already exited.
        }
    }

    private void EnqueueOrDispatch(string[] args)
    {
        Func<string[], Task>? handler;
        lock (_handlerGate)
        {
            handler = _handler;
            if (handler == null)
            {
                _pendingMessages.Enqueue(args);
                return;
            }
        }

        _ = DispatchToHandlerAsync(handler, args);
    }

    private static async Task DispatchToHandlerAsync(Func<string[], Task> handler, string[] args)
    {
        try
        {
            await handler(args);
        }
        catch
        {
            // Keep the IPC server alive even if a dispatched message fails.
        }
    }

    private static string EncodePayload(string[] args)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new CommandLineHandoffService.HandoffPayload(args),
            AppJsonOptions.Default);
        return Convert.ToBase64String(json);
    }

    private static bool TryDecodePayload(string? payload, out string[] args)
    {
        args = [];
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > MaximumPayloadBytes)
            return false;

        try
        {
            var json = Convert.FromBase64String(payload.Trim());
            var decoded = JsonSerializer.Deserialize<CommandLineHandoffService.HandoffPayload>(
                json,
                AppJsonOptions.Default);
            if (decoded == null)
                return false;

            args = decoded.Args ?? [];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record LockFileInfo(string Path, bool NeedChangePermissions);
}
