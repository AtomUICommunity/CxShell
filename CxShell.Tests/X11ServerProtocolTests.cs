using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using VelaShell.XServer;

namespace CxShell.Tests;

public sealed class X11ServerProtocolTests
{
    [Fact]
    public async Task TcpServer_AcceptsClientWithConfiguredCookie()
    {
        byte[] cookie = RandomNumberGenerator.GetBytes(16);
        await using X11Server server = await StartServerAsync(cookie);
        using TcpClient client = await ConnectAsync(server.DisplayNumber);
        NetworkStream stream = client.GetStream();

        await stream.WriteAsync(CreateSetupPacket(cookie));
        byte[] status = new byte[1];
        await stream.ReadExactlyAsync(status);

        Assert.Equal(1, status[0]);
    }

    [Fact]
    public async Task TcpServer_RejectsClientWithWrongCookie()
    {
        byte[] cookie = RandomNumberGenerator.GetBytes(16);
        byte[] wrongCookie = RandomNumberGenerator.GetBytes(16);
        await using X11Server server = await StartServerAsync(cookie);
        using TcpClient client = await ConnectAsync(server.DisplayNumber);
        NetworkStream stream = client.GetStream();

        await stream.WriteAsync(CreateSetupPacket(wrongCookie));
        byte[] status = new byte[1];
        await stream.ReadExactlyAsync(status);

        Assert.Equal(0, status[0]);
    }

    [Fact]
    public async Task SeparateServers_RejectEachOthersCookies()
    {
        byte[] firstCookie = RandomNumberGenerator.GetBytes(16);
        byte[] secondCookie = RandomNumberGenerator.GetBytes(16);
        await using X11Server firstServer = await StartServerAsync(firstCookie);
        await using X11Server secondServer = await StartServerAsync(secondCookie);
        using TcpClient client = await ConnectAsync(firstServer.DisplayNumber);

        await client.GetStream().WriteAsync(CreateSetupPacket(secondCookie));
        byte[] status = new byte[1];
        await client.GetStream().ReadExactlyAsync(status);

        Assert.Equal(0, status[0]);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesListenerAndCanBeCalledMoreThanOnce()
    {
        X11Server server = await StartServerAsync(RandomNumberGenerator.GetBytes(16));
        int port = server.Port;

        await server.DisposeAsync();
        await server.DisposeAsync();

        using TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();
    }

    [Fact]
    public async Task TcpServer_DisposeAfterAcceptingClientDoesNotThrow()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            X11Server server = await StartServerAsync(RandomNumberGenerator.GetBytes(16));
            using (await ConnectAsync(server.DisplayNumber))
            {
            }

            await server.DisposeAsync();
        }
    }

    private static async Task<X11Server> StartServerAsync(byte[] cookie)
    {
        X11Server server = new(new X11ServerOptions
        {
            DisplayNumber = FindAvailableDisplayNumber(),
            ListenAddress = IPAddress.Loopback,
            UnixSocketPath = string.Empty,
            AuthorizationCookie = cookie,
            ScreenWidth = 640,
            ScreenHeight = 480
        }, new TestHost());

        await server.StartAsync();
        return server;
    }

    private static int FindAvailableDisplayNumber()
    {
        for (int display = 100; display < 2000; display++)
        {
            using TcpListener listener = new(IPAddress.Loopback, 6000 + display);
            try
            {
                listener.Start();
                return display;
            }
            catch (SocketException)
            {
            }
        }

        throw new InvalidOperationException("No available X11 display port was found for the test.");
    }

    private static async Task<TcpClient> ConnectAsync(int? display)
    {
        TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, 6000 + display!.Value);
        return client;
    }

    private static byte[] CreateSetupPacket(byte[] cookie)
    {
        byte[] protocol = Encoding.ASCII.GetBytes("MIT-MAGIC-COOKIE-1");
        int paddedProtocolLength = (protocol.Length + 3) & ~3;
        int paddedCookieLength = (cookie.Length + 3) & ~3;
        byte[] packet = new byte[12 + paddedProtocolLength + paddedCookieLength];
        packet[0] = (byte)'B';
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 11);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), (ushort)protocol.Length);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8, 2), (ushort)cookie.Length);
        protocol.CopyTo(packet, 12);
        cookie.CopyTo(packet, 12 + paddedProtocolLength);
        return packet;
    }

    private sealed class TestHost : IX11ServerHost
    {
        public void TopLevelMapped(XTopLevelWindow window)
        {
        }

        public void TopLevelUnmapped(XTopLevelWindow window)
        {
        }

        public void TopLevelChanged(XTopLevelWindow window, XTopLevelChanges changes)
        {
        }

        public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage)
        {
        }

        public void CursorChanged(XTopLevelWindow? window, XCursor cursor)
        {
        }

        public void BellRequested(int volume)
        {
        }

        public void ClipboardChanged(string text)
        {
        }
    }
}
