using System.Text;
using System.Text.Json;
using CxShell.Models;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class LocalTerminalProfileTests
{
    [Fact]
    public void CreateSessionKeepsTheProfileRuntimeOnly()
    {
        var profile = new LocalTerminalProfile(
            "test-shell",
            "Test Shell",
            "shell",
            ["--interactive"],
            supportsPosixShellFeatures: true);

        var session = profile.CreateSession();
        var json = JsonSerializer.Serialize(session);

        Assert.Equal(SessionProtocol.Local, session.Protocol);
        Assert.Same(profile, session.LocalTerminalProfile);
        Assert.False(session.AutoReconnect);
        Assert.DoesNotContain("LocalTerminalProfile", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandLineQuotesExecutableAndArgumentsWhenNeeded()
    {
        var profile = new LocalTerminalProfile(
            "quoted",
            "Quoted",
            @"C:\Program Files\Shell\shell.exe",
            ["--title", "hello world"]);

        Assert.Equal(
            @"""C:\Program Files\Shell\shell.exe"" --title ""hello world""",
            profile.CommandLine);
    }

    [Fact]
    public void CatalogReturnsUniqueProfilesWithoutThrowing()
    {
        var profiles = LocalTerminalCatalog.Detect();

        Assert.Equal(
            profiles.Count,
            profiles.Select(profile => profile.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(profiles, profile =>
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.Id));
            Assert.False(string.IsNullOrWhiteSpace(profile.Name));
            Assert.False(string.IsNullOrWhiteSpace(profile.ExecutablePath));
        });
    }

    [Fact]
    public async Task WindowsConPtyCanStartCommandPromptAndReceiveOutput()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var profile = LocalTerminalCatalog.Detect()
            .FirstOrDefault(item => string.Equals(item.Id, "cmd", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(profile);

        using var service = new LocalTerminalConnectionService();
        var output = new StringBuilder();
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DataReceived += text =>
        {
            lock (output)
            {
                output.Append(text);
                if (output.ToString().Contains("CXSHELL_LOCAL_PTY_TEST", StringComparison.Ordinal))
                    received.TrySetResult(true);
            }
        };

        await service.ConnectAsync(profile.CreateSession(), null, 80, 24);
        await Task.Delay(250);
        service.SendData("echo CXSHELL_LOCAL_PTY_TEST\r");

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(received.Task, completed);
        Assert.True(service.IsConnected);
        service.ResizeTerminal(100, 30);
        service.Disconnect();
    }

    [Fact]
    public async Task UnixForkPtyCanStartShellAndReceiveOutput()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        if (!File.Exists("/bin/sh"))
            return;
        // GitHub Actions runners do not provide a reliable interactive PTY for
        // forkpty-based integration tests, so keep this check for local runs only.
        if (string.Equals(
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
            return;

        var profile = new LocalTerminalProfile(
            "test-sh",
            "sh",
            "/bin/sh",
            ["-i"],
            supportsPosixShellFeatures: true);

        using var service = new LocalTerminalConnectionService();
        var output = new StringBuilder();
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DataReceived += text =>
        {
            lock (output)
            {
                output.Append(text);
                if (output.ToString().Contains("CXSHELL_LOCAL_PTY_TEST", StringComparison.Ordinal))
                    received.TrySetResult(true);
            }
        };

        await service.ConnectAsync(profile.CreateSession(), null, 80, 24);
        await Task.Delay(250);
        service.SendData("echo CXSHELL_LOCAL_PTY_TEST\n");

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(received.Task, completed);
        Assert.True(service.IsConnected);
        service.ResizeTerminal(100, 30);
        service.Disconnect();
    }
}
