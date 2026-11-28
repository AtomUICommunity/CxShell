using System;
using System.IO;
using System.Threading.Tasks;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class SingleInstanceChannelTests
{
    [Fact]
    public async Task SingleInstanceChannel_BlocksSecondInstanceInSameDataDirAndForwardsArguments()
    {
        using var dataDir = new TemporaryDirectory();
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var first = new SingleInstanceChannel(dataDir.Path, isPortableMode: true);
        Assert.True(first.IsFirstInstance);

        using var second = new SingleInstanceChannel(dataDir.Path, isPortableMode: true);
        Assert.False(second.IsFirstInstance);
        Assert.Equal(first.PipeName, second.PipeName);

        // Send before handler is attached to verify pending messages are buffered and flushed on AttachHandler.
        var sent = second.SendToFirstInstance(["-url", "ssh://ops@example.test:22"]);
        Assert.True(sent);

        first.AttachHandler(args =>
        {
            received.TrySetResult(args);
            return Task.CompletedTask;
        });

        var delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["-url", "ssh://ops@example.test:22"], delivered);
    }

    [Fact]
    public async Task SingleInstanceChannel_ForwardsEmptyActivationPing()
    {
        using var dataDir = new TemporaryDirectory();
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var first = new SingleInstanceChannel(dataDir.Path, isPortableMode: true);
        first.AttachHandler(args =>
        {
            received.TrySetResult(args);
            return Task.CompletedTask;
        });

        using var second = new SingleInstanceChannel(dataDir.Path, isPortableMode: true);
        Assert.False(second.IsFirstInstance);
        Assert.True(second.SendToFirstInstance([]));

        var delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(delivered);
    }

    [Fact]
    public void SingleInstanceChannel_AllowsInstalledAndPortableInstancesSimultaneously()
    {
        using var installedDir = new TemporaryDirectory();
        using var portableDir = new TemporaryDirectory();

        using var installedInstance = new SingleInstanceChannel(installedDir.Path, isPortableMode: false);
        using var portableInstance = new SingleInstanceChannel(portableDir.Path, isPortableMode: true);

        Assert.True(installedInstance.IsFirstInstance);
        Assert.True(portableInstance.IsFirstInstance);
        Assert.NotEqual(installedInstance.PipeName, portableInstance.PipeName);
        Assert.NotEqual(installedInstance.LockFilePath, portableInstance.LockFilePath);
    }

    [Fact]
    public void SingleInstanceChannel_ReleasesLockOnDispose()
    {
        using var dataDir = new TemporaryDirectory();

        var first = new SingleInstanceChannel(dataDir.Path, isPortableMode: true);
        Assert.True(first.IsFirstInstance);
        first.Dispose();

        using var restarted = new SingleInstanceChannel(dataDir.Path, isPortableMode: true);
        Assert.True(restarted.IsFirstInstance);
    }

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "-url", "ssh://ops@example.test:22" }, false)]
    [InlineData(new[] { "-newwin" }, true)]
    [InlineData(new[] { "--newwin", "-url", "ssh://ops@example.test:22" }, true)]
    [InlineData(new[] { "--rdp-smoke" }, true)]
    public void ShouldBypassSingleInstance_RespectsExplicitNewWindowAndSmokeArgs(string[] args, bool expected)
    {
        Assert.Equal(expected, CommandLineHandoffService.ShouldBypassSingleInstance(args));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"cxshell-single-instance-tests-{Guid.NewGuid():N}");
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
