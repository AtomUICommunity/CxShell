using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CxShell.Models;

namespace CxShell.Services;

/// <summary>
/// Keeps a loopback TCP listener that forwards every accepted connection to a
/// remote endpoint through a proxy. The RDP bridge can only dial a plain host
/// and port, so the proxy hop is established here and FreeRDP connects to
/// <c>127.0.0.1:LocalPort</c> instead of the real server.
/// </summary>
public sealed class LoopbackProxyRelay : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _clientsGate = new();
    private readonly List<TcpClient> _clients = [];
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly ProxySettings _proxy;
    private readonly string _ipVersion;
    private readonly Action<Exception>? _faulted;
    private bool _disposed;

    private LoopbackProxyRelay(
        string targetHost,
        int targetPort,
        ProxySettings proxy,
        string ipVersion,
        Action<Exception>? faulted)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _proxy = proxy;
        _ipVersion = ipVersion;
        _faulted = faulted;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int LocalPort { get; }

    /// <summary>
    /// Starts a relay towards <paramref name="targetHost"/>:<paramref name="targetPort"/>.
    /// The first accepted connection opens the proxied link; failures surface
    /// through <paramref name="faulted"/> so the caller can report them.
    /// </summary>
    public static LoopbackProxyRelay Start(
        string targetHost,
        int targetPort,
        ProxySettings proxy,
        string ipVersion = "Auto",
        Action<Exception>? faulted = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);
        ArgumentNullException.ThrowIfNull(proxy);
        return new LoopbackProxyRelay(targetHost, targetPort, proxy, ipVersion, faulted);
    }

    private async Task AcceptLoopAsync()
    {
        var cancellationToken = _cts.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            TrackClient(client);
            _ = Task.Run(() => HandleClientAsync(client, cancellationToken));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        TcpClient? upstream = null;
        try
        {
            client.NoDelay = true;
            upstream = await ProxyConnectionFactory
                .ConnectTcpAsync(_targetHost, _targetPort, _proxy, cancellationToken, _ipVersion)
                .ConfigureAwait(false);
            upstream.NoDelay = true;

            using var clientStream = client.GetStream();
            using var upstreamStream = upstream.GetStream();
            var toUpstream = PumpAsync(clientStream, upstreamStream, cancellationToken);
            var toClient = PumpAsync(upstreamStream, clientStream, cancellationToken);

            await Task.WhenAny(toUpstream, toClient).ConfigureAwait(false);
            // Half-close both directions so the pending pump finishes instead of
            // waiting for a timeout once the peer is gone.
            Shutdown(client);
            Shutdown(upstream);
            await Task.WhenAll(toUpstream, toClient).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _faulted?.Invoke(ex);
        }
        finally
        {
            upstream?.Dispose();
            UntrackClient(client);
            client.Dispose();
        }
    }

    private static async Task PumpAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                    return;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The peer closed the connection; the other pump will wind down too.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void Shutdown(TcpClient client)
    {
        try
        {
            client.Client.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // The socket may already be closed.
        }
    }

    private void TrackClient(TcpClient client)
    {
        lock (_clientsGate)
            _clients.Add(client);
    }

    private void UntrackClient(TcpClient client)
    {
        lock (_clientsGate)
            _clients.Remove(client);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // Ignore cancellation failures.
        }

        try
        {
            _listener.Stop();
        }
        catch
        {
            // Ignore listener shutdown failures.
        }

        TcpClient[] clients;
        lock (_clientsGate)
        {
            clients = _clients.ToArray();
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            try
            {
                client.Dispose();
            }
            catch
            {
                // Ignore client shutdown failures.
            }
        }

        _cts.Dispose();
    }
}
