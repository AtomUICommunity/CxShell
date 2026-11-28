using System;
using System.IO;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class AppDataPathsTests
{
    [Fact]
    public void Resolve_EntersPortableModeWhenDataFolderExistsNextToExecutable()
    {
        using var root = new TemporaryDirectory();
        var exeDir = Path.Combine(root.Path, "portable-app");
        var dataDir = Path.Combine(exeDir, "data");
        Directory.CreateDirectory(dataDir);

        var resolved = AppDataPaths.Resolve(
            exeDir,
            Directory.Exists,
            () => Path.Combine(root.Path, "roaming"),
            () => Path.Combine(root.Path, "local"),
            exeDir);

        Assert.True(resolved.IsPortableMode);
        Assert.Equal(Path.GetFullPath(dataDir), resolved.StorageDirectory);
        Assert.Equal(Path.GetFullPath(dataDir), resolved.LocalCacheDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(dataDir), "Logs"), resolved.LogsDirectory);
    }

    [Fact]
    public void Resolve_UsesInstalledDirectoriesWhenDataFolderIsMissing()
    {
        using var root = new TemporaryDirectory();
        var exeDir = Path.Combine(root.Path, "installed-app");
        var roamingRoot = Path.Combine(root.Path, "roaming");
        var localRoot = Path.Combine(root.Path, "local");
        Directory.CreateDirectory(exeDir);

        var resolved = AppDataPaths.Resolve(
            exeDir,
            Directory.Exists,
            () => roamingRoot,
            () => localRoot,
            exeDir);

        Assert.False(resolved.IsPortableMode);
        Assert.Equal(Path.Combine(Path.GetFullPath(roamingRoot), "CxShell"), resolved.StorageDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(localRoot), "CxShell"), resolved.LocalCacheDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(localRoot), "CxShell", "Logs"), resolved.LogsDirectory);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"cxshell-portable-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for test artifacts.
            }
        }
    }
}
