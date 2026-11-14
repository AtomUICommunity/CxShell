using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CxShell.Models;
using CxShell.Services;
using Xunit;

namespace CxShell.Tests;

public sealed class LoopbackProxyRelayTests
{
    [Fact]
    public async Task ForwardsTrafficThroughHttpConnectProxy()
    {
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var echoTask = EchoOnceAsync(target);

        using var proxy = new FakeHttpConnectProxy();
        var proxySettings = new ProxySettings
        {
            Protocol = ProxyProtocol.Http,
            Host = "127.0.0.1",
            Port = proxy.Port
        };
        using var relay = LoopbackProxyRelay.Start("127.0.0.1", targetPort, proxySettings);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relay.LocalPort, timeout.Token);

        var payload = Encoding.UTF8.GetBytes("hello rdp proxy");
        var stream = client.GetStream();
        await stream.WriteAsync(payload, timeout.Token);
        await stream.FlushAsync(timeout.Token);

        var received = await ReadExactlyAsync(stream, payload.Length, timeout.Token);

        Assert.Equal(payload, received);
        await echoTask.WaitAsync(timeout.Token);
        Assert.Equal(1, proxy.ConnectRequestCount);
    }

    [Fact]
    public async Task ReportsProxyFailuresAndClosesTheClient()
    {
        // No listener on this port, so the relay cannot reach the proxy.
        var proxyPort = GetUnusedLoopbackPort();
        var proxySettings = new ProxySettings
        {
            Protocol = ProxyProtocol.Socks5,
            Host = "127.0.0.1",
            Port = proxyPort
        };

        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var relay = LoopbackProxyRelay.Start(
            "127.0.0.1",
            3389,
            proxySettings,
            faulted: ex => failure.TrySetResult(ex));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relay.LocalPort, timeout.Token);

        var reported = await failure.Task.WaitAsync(timeout.Token);
        Assert.NotNull(reported);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (chunk <= 0)
                break;

            read += chunk;
        }

        return buffer[..read];
    }

    private static async Task EchoOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer);
        if (read > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read));
            await stream.FlushAsync();
        }
    }

    private static int GetUnusedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FakeHttpConnectProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private int _connectRequestCount;

        public FakeHttpConnectProxy()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int ConnectRequestCount => Volatile.Read(ref _connectRequestCount);

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var clientStream = client.GetStream();
                    var header = await ReadHeaderAsync(clientStream);
                    var requestLine = header.Split("\r\n")[0];
                    if (!requestLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsciiAsync(clientStream, "HTTP/1.1 405 Method Not Allowed\r\n\r\n");
                        return;
                    }

                    Interlocked.Increment(ref _connectRequestCount);
                    var authority = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
                    var separator = authority.LastIndexOf(':');
                    var host = authority[..separator];
                    var port = int.Parse(authority[(separator + 1)..]);

                    using var upstream = new TcpClient();
                    await upstream.ConnectAsync(host, port, _cts.Token);
                    await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n");

                    var upstreamStream = upstream.GetStream();
                    await Task.WhenAll(
                        PumpAsync(clientStream, upstreamStream),
                        PumpAsync(upstreamStream, clientStream));
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                }
                catch (IOException)
                {
                }
                catch (SocketException)
                {
                }
            }
        }

        private static async Task<string> ReadHeaderAsync(Stream stream)
        {
            var builder = new StringBuilder();
            var buffer = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0)
                    return builder.ToString();

                builder.Append((char)buffer[0]);
                if (builder.Length >= 4 && builder[^4] == '\r' && builder[^3] == '\n' && builder[^2] == '\r' && builder[^1] == '\n')
                    return builder.ToString();
            }
        }

        private static async Task PumpAsync(Stream source, Stream destination)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer);
                    if (read <= 0)
                        return;

                    await destination.WriteAsync(buffer.AsMemory(0, read));
                    await destination.FlushAsync();
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static Task WriteAsciiAsync(Stream stream, string text)
            => stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
