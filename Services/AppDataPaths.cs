using System;
using System.IO;

namespace CxShell.Services;

/// <summary>
/// Resolves CxShell's storage, cache, and log directories. When a <c>data</c>
/// directory exists next to the running executable (or AppImage on Linux),
/// CxShell enters portable mode and keeps all runtime data inside that folder.
/// </summary>
public static class AppDataPaths
{
    private const string CurrentAppDirectoryName = "CxShell";
    private const string PortableDirectoryName = "data";

    private static readonly Lazy<ResolvedPaths> Cached = new(ResolveCurrentProcessPaths);

    public static bool IsPortableMode => Cached.Value.IsPortableMode;

    public static string GetStorageDirectory() => Cached.Value.StorageDirectory;

    public static string GetLocalCacheDirectory() => Cached.Value.LocalCacheDirectory;

    public static string GetLogsDirectory() => Cached.Value.LogsDirectory;

    internal static ResolvedPaths Resolve(
        string? executableDirectory,
        Func<string, bool> directoryExists,
        Func<string?> resolveInstalledStorageRoot,
        Func<string?> resolveInstalledLocalRoot,
        string? fallbackBaseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            var portableDir = TryGetFullPath(Path.Combine(executableDirectory, PortableDirectoryName));
            if (!string.IsNullOrWhiteSpace(portableDir) && directoryExists(portableDir))
            {
                return new ResolvedPaths(
                    IsPortableMode: true,
                    StorageDirectory: portableDir,
                    LocalCacheDirectory: portableDir,
                    LogsDirectory: Path.Combine(portableDir, "Logs"));
            }
        }

        var installedRoot = TryGetFullPath(resolveInstalledStorageRoot());
        var storageDir = !string.IsNullOrWhiteSpace(installedRoot)
            ? Path.Combine(installedRoot, CurrentAppDirectoryName)
            : Path.Combine(fallbackBaseDirectory ?? AppContext.BaseDirectory, ".cxshell-data");

        var localRoot = TryGetFullPath(resolveInstalledLocalRoot());
        var localCacheDir = !string.IsNullOrWhiteSpace(localRoot)
            ? Path.Combine(localRoot, CurrentAppDirectoryName)
            : storageDir;

        return new ResolvedPaths(
            IsPortableMode: false,
            StorageDirectory: storageDir,
            LocalCacheDirectory: localCacheDir,
            LogsDirectory: Path.Combine(localCacheDir, "Logs"));
    }

    internal static string? GetExecutableDirectory()
    {
        if (OperatingSystem.IsLinux())
        {
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage))
            {
                var appImageDir = Path.GetDirectoryName(appImage);
                if (!string.IsNullOrWhiteSpace(appImageDir))
                    return appImageDir;
            }
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var processDir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(processDir))
                return processDir;
        }

        return AppContext.BaseDirectory;
    }

    private static ResolvedPaths ResolveCurrentProcessPaths()
    {
        return Resolve(
            GetExecutableDirectory(),
            Directory.Exists,
            ResolveInstalledStorageRoot,
            ResolveInstalledLocalRoot,
            AppContext.BaseDirectory);
    }

    private static string? ResolveInstalledStorageRoot()
    {
        var appData = TryGetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        if (!string.IsNullOrWhiteSpace(appData))
            return appData;

        if (OperatingSystem.IsWindows())
            return null;

        var xdgConfigHome = TryGetFullPath(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
            return xdgConfigHome;

        var userProfile = TryGetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return OperatingSystem.IsMacOS()
                ? Path.Combine(userProfile, "Library", "Application Support")
                : Path.Combine(userProfile, ".config");
        }

        return null;
    }

    private static string? ResolveInstalledLocalRoot()
    {
        var localAppData = TryGetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (!string.IsNullOrWhiteSpace(localAppData))
            return localAppData;

        if (OperatingSystem.IsWindows())
            return null;

        var xdgCacheHome = TryGetFullPath(Environment.GetEnvironmentVariable("XDG_CACHE_HOME"));
        if (!string.IsNullOrWhiteSpace(xdgCacheHome))
            return xdgCacheHome;

        return ResolveInstalledStorageRoot();
    }

    private static string? TryGetFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            if (!Path.IsPathFullyQualified(expanded))
                return null;

            return Path.GetFullPath(expanded);
        }
        catch
        {
            return null;
        }
    }

    internal sealed record ResolvedPaths(
        bool IsPortableMode,
        string StorageDirectory,
        string LocalCacheDirectory,
        string LogsDirectory);
}
