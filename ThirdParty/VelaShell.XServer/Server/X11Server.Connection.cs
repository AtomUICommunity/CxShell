// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 8 节「Connection Setup」与附录 B「Connection Setup」
//   (客户端开场 12 字节 + 授权名 / 数据;成功回复的定长部分、FORMAT、SCREEN、DEPTH、VISUALTYPE 的布局;失败回复)
//   BIG-REQUESTS Extension(请求长度字段为 0 时后跟 4 字节的扩展长度;BigReqEnable,次操作码 0:回复 maximum-request-length)
//   第 10 节「Connection Close」(CloseDownMode = Destroy 时释放该连接的全部资源、选区、抓取)

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>核心协议允许的最大请求长度(以 4 字节计)。</summary>
    internal const ushort MaxRequestLength = 65535;

    /// <summary>BIG-REQUESTS 打开后的最大请求长度(以 4 字节计,16 MB)。</summary>
    internal const uint MaxBigRequestLength = 4 * 1024 * 1024;

    /// <summary>读端缓冲。一批典型的绘图请求(几十到几百条)一次读进来。</summary>
    private const int InputBufferSize = 64 * 1024;

    /// <summary>写出端拼包缓冲。</summary>
    private const int OutputBufferSize = 64 * 1024;

    private int _nextClientIndex = 1;

    /// <summary>
    /// 以 RetainPermanent / RetainTemporary 收尾的客户端:资源还留在资源表里(协议第 10 节),它的编号不分给新连接 ——
    /// 否则新客户端的资源 ID 与留下来的撞上。KillClient 销毁这些资源之后编号才放回去。
    /// </summary>
    private readonly Dictionary<int, XClient> _retainedClients = [];

    /// <summary>经 TCP / Unix 套接字接进来的连接(收工时等它们结束)。</summary>
    private readonly ConcurrentDictionary<Task, byte> _connections = new();

    private TcpListener? _listener;
    private Task? _acceptTask;

    // ------------------------------------------------------------------ TCP

    private void StartTcpListener()
    {
        TcpListener listener = new(_options.ListenAddress, 6000 + _options.DisplayNumber);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptTask = AcceptLoopAsync(listener, _lifetime.Token);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                // DisposeAsync may stop the listener between accepted clients.
                return;
            }
            tcp.NoDelay = true;
            bool local = tcp.Client.RemoteEndPoint is IPEndPoint { Address: var address } && IPAddress.IsLoopback(address);
            TrackConnection(ServeAndDisposeAsync(tcp, local, cancellationToken));
        }
    }

    private async Task ServeAndDisposeAsync(TcpClient tcp, bool local, CancellationToken cancellationToken)
    {
        using (tcp)
        {
            try
            {
                await ServeCoreAsync(tcp.GetStream(), new Peer(local, SameHost: false, Uid: null, LocalUser: false, Authenticated: false),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // 服务端正在收工。
            }
        }
    }

    /// <summary>登记一个接进来的连接任务,结束时自动摘掉。</summary>
    private void TrackConnection(Task connection)
    {
        _connections.TryAdd(connection, 0);
        _ = connection.ContinueWith(t => _connections.TryRemove(t, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>收工时:接进来的连接随 _lifetime 取消而收工,给它们一点时间关掉套接字。</summary>
    private async Task WaitForConnectionsAsync()
    {
        try
        {
            await Task.WhenAll(_connections.Keys).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 收工阶段的异常不关心。
        }
    }

    // ------------------------------------------------------------------ 一条连接

    /// <summary>连接的对端:服务端对它知道多少(授权检查与 MIT-SHM 用)。</summary>
    /// <param name="IsLocal">来自本机(环回 TCP、Unix 套接字、进程内的流)。没配置 cookie 时只接受本机连接。</param>
    /// <param name="SameHost">经 Unix 套接字连进来的:MIT-SHM 对它可见。</param>
    /// <param name="Uid">对端的 uid(Linux 上经 SO_PEERCRED);取不到为 null。</param>
    /// <param name="LocalUser">能确定对端就是运行服务端的这个用户(权限 0600 的套接字文件,或 uid 与本进程相同)。</param>
    /// <param name="Authenticated">调用方已经验过身份(<see cref="ServeAuthenticatedAsync" />),不再查授权。</param>
    internal readonly record struct Peer(bool IsLocal, bool SameHost, uint? Uid, bool LocalUser, bool Authenticated);

    /// <summary>
    /// 连接建立的时限:读连接建立报文(12 字节的头与授权名 / 数据)、回失败,都要在这之内做完。
    /// 对端连上来却迟迟不发完(卡住的,或者故意占着不放的),到点就断开 —— 否则每个这样的连接都一直占着一个套接字和一个任务,
    /// 而 <see cref="MaxClients" /> 只数已经建立的客户端,拦不住它们。握手只是一个往返,走 SSH 转发的慢链路也绰绰有余。
    /// </summary>
    internal TimeSpan SetupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private async Task ServeCoreAsync(Stream stream, Peer peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        CancellationToken ct = linked.Token;
        CancellationTokenSource? connection = null;
        // 连接建立阶段的读写用它:到了 SetupTimeout 还没发完就取消。等执行线程登记客户端那一步不计在内 ——
        // 那一步半途取消的话,执行线程照样登记了,却没人再用这个客户端。
        using CancellationTokenSource setup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        setup.CancelAfter(SetupTimeout);

        XClient? client = null;
        Task? writer = null;
        try
        {
            byte[] head = new byte[12];
            await stream.ReadExactlyAsync(head, setup.Token).ConfigureAwait(false);
            bool bigEndian = head[0] switch
            {
                (byte)'B' => true,
                (byte)'l' => false,
                _ => throw new InvalidDataException("连接建立报文的字节序标记非法。"),
            };
            ushort major = Read16(head.AsSpan(2), bigEndian);
            int nameLength = Read16(head.AsSpan(6), bigEndian);
            int dataLength = Read16(head.AsSpan(8), bigEndian);
            byte[] rest = new byte[XWire.Pad(nameLength) + XWire.Pad(dataLength)];
            await stream.ReadExactlyAsync(rest, setup.Token).ConfigureAwait(false);
            string authName = XWire.Latin1.GetString(rest, 0, nameLength);
            byte[] authData = rest.AsSpan(XWire.Pad(nameLength), dataLength).ToArray();

            if (major != 11)
            {
                await SendSetupFailureAsync(stream, bigEndian, "Protocol version mismatch", setup.Token).ConfigureAwait(false);
                return;
            }
            if (Authorize(authName, authData, peer) is { } reason)
            {
                Post(null, () =>
                {
                    if (ShouldLogFrequent())
                    {
                        Log($"connection refused: {reason}");
                    }
                });
                await SendSetupFailureAsync(stream, bigEndian, reason, setup.Token).ConfigureAwait(false);
                return;
            }

            setup.CancelAfter(Timeout.InfiniteTimeSpan);   // 报文收齐了:下面等执行线程登记,不计时
            client = await InvokeAsync(() => RegisterClient(bigEndian)).WaitAsync(ct).ConfigureAwait(false);
            if (client is null)
            {
                setup.CancelAfter(SetupTimeout);
                await SendSetupFailureAsync(stream, bigEndian, "Maximum number of clients reached", setup.Token).ConfigureAwait(false);
                return;
            }
            client.SameHost = peer.SameHost;
            client.PeerUid = peer.Uid;
            // 连接的读写还要跟着「服务端主动断开这个客户端」一起停。
            connection = CancellationTokenSource.CreateLinkedTokenSource(ct, client.Aborted);
            ct = connection.Token;
            writer = PumpOutputAsync(client, stream, ct);
            // 读端单独套一层缓冲:X 请求又小又密(常见 8–40 字节),不缓冲就是每条请求两次系统调用。
            // ⚠️ 只经它读、从不经它写 —— BufferedStream 读写共用一块缓冲,在不可寻址的流上混用会抛异常;
            // 写出端直接写底层流。
            BufferedStream input = new(stream, InputBufferSize);
            await ReadRequestsAsync(client, input, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException
                                       or OperationCanceledException or ObjectDisposedException)
        {
            // 对端走了、乱发、连接建立超时,或者服务端在收工。
            if (client is null && setup.IsCancellationRequested && !linked.IsCancellationRequested)
            {
                Post(null, () =>
                {
                    if (ShouldLogFrequent())
                    {
                        Log("connection setup timed out");
                    }
                });
            }
        }
        finally
        {
            if (client is not null)
            {
                XClient gone = client;
                Post(null, () => DisconnectClient(gone));
                gone.Output.Writer.TryComplete();
            }
            if (writer is not null)
            {
                // 对端已经不读了:别再等积压的输出写完(对端半关闭时那会永远等下去)。
                connection?.Cancel();
                try
                {
                    await writer.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // 写出端跟着收工。
                }
            }
            connection?.Dispose();
            client?.Dispose();
        }
    }

    /// <summary>
    /// 授权检查;通过返回 null,否则返回给客户端看的原因。依次:
    /// ① 调用方已经验过身份的流(<see cref="ServeAuthenticatedAsync" />)放行;
    /// ② 带了对的 MIT-MAGIC-COOKIE-1 放行;
    /// ③ 能确定对端就是运行服务端的这个用户(权限 0600 的套接字文件,或 SO_PEERCRED 的 uid 相同)放行;
    /// ④ 知道对端 uid 而它是别的用户:拒 —— Linux 抽象命名空间里的套接字没有文件权限可言,不看 uid 的话
    ///    本机任何用户都能连进来读窗口、记键盘、经 XTEST 注入输入;
    /// ⑤ 配置了 cookie 时其余一律拒(环回 TCP 也一样:本机别的进程、别的用户都连得到那个端口);
    ///    没配置时与 X.Org 的主机访问控制一致,只放行本机。
    /// </summary>
    internal string? Authorize(string name, byte[] data, Peer peer)
    {
        if (peer.Authenticated)
        {
            return null;
        }
        // ⚠️ 常数时间比较:逐字节短路会泄漏「前几个字节对了几个」。
        if (_options.AuthorizationCookie is { } cookie && name == "MIT-MAGIC-COOKIE-1" && CryptographicOperations.FixedTimeEquals(data, cookie))
        {
            return null;
        }
        if (peer.LocalUser)
        {
            return null;
        }
        if (peer.Uid is not null)
        {
            return "Authorization required: the connecting user does not own this display";
        }
        if (_options.AuthorizationCookie is not null)
        {
            return "Authorization required, but no authorization protocol specified";
        }
        return peer.IsLocal ? null : "No protocol specified: only local connections are accepted";
    }

    private static async Task SendSetupFailureAsync(Stream stream, bool bigEndian, string reason, CancellationToken ct)
    {
        byte[] text = XWire.Latin1.GetBytes(reason);
        XWriter w = new(bigEndian);
        w.U8(0).U8((byte)Math.Min(255, text.Length)).U16(11).U16(0).U16((ushort)(XWire.Pad(text.Length) / 4));
        w.Bytes(text).Pad4();
        await stream.WriteAsync(w.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 同时连着的客户端上限。资源 ID 的顶上三位恒为 0(协议第 8 节),每个客户端占低 21 位(<see cref="XClient.ResourceMask" />),
    /// 客户端编号就只剩 8 位;编号 0 是服务端自己的资源(根窗口、默认颜色表)。
    /// </summary>
    internal const int MaxClients = 255;

    /// <summary>分一个空闲的客户端编号并发出连接建立回复;编号用完了返回 null。</summary>
    private XClient? RegisterClient(bool bigEndian)
    {
        int index = _nextClientIndex;
        for (int tried = 0; tried < MaxClients; tried++, index = index >= MaxClients ? 1 : index + 1)
        {
            if (_clients.ContainsKey(index) || _retainedClients.ContainsKey(index))
            {
                continue;
            }
            _nextClientIndex = index >= MaxClients ? 1 : index + 1;
            XClient client = new(index, bigEndian);
            _clients[index] = client;
            client.Send(BuildSetupReply(client));
            if (ShouldLogFrequent())
            {
                Log($"{client} connected ({(bigEndian ? "MSB" : "LSB")} first)");
            }
            return client;
        }
        if (ShouldLogFrequent())
        {
            Log($"connection refused: {MaxClients} clients already connected");
        }
        return null;
    }

    /// <summary>连接建立成功回复:一块屏幕、深度 24 的 TrueColor 视觉(外加深度 32 与深度 1)。</summary>
    private byte[] BuildSetupReply(XClient client)
    {
        byte[] vendor = XWire.Latin1.GetBytes(_options.Vendor);
        XWriter w = client.Writer(256);
        w.U8(1).U8(0).U16(11).U16(0).U16(0);        // success、主版本 11、次版本 0、长度(回填)
        w.U32(12101000);                             // release-number
        w.U32(client.ResourceBase).U32(XClient.ResourceMask);
        w.U32(0);                                    // motion-buffer-size:不保存移动历史(GetMotionEvents 回空)
        w.U16((ushort)vendor.Length).U16(MaxRequestLength);
        w.U8(1);                                     // 屏幕数
        w.U8(7);                                     // FORMAT 数
        w.U8(0);                                     // image-byte-order:LSBFirst
        w.U8(0);                                     // bitmap-format-bit-order:LeastSignificant
        w.U8(32).U8(32);                             // bitmap scanline unit / pad
        w.U8(Input.Keymap.MinKeycode).U8(Input.Keymap.MaxKeycode);
        w.Zero(4);
        w.Bytes(vendor).Pad4();

        // FORMAT:depth、bits-per-pixel、scanline-pad、5 字节空
        foreach ((byte depth, byte bpp) in ((byte, byte)[])[(1, 1), (4, 8), (8, 8), (15, 16), (16, 16), (24, 32), (32, 32)])
        {
            w.U8(depth).U8(bpp).U8(32).Zero(5);
        }

        // SCREEN
        (int mmW, int mmH) = ScreenMillimeters();
        w.U32(Root.Id).U32(DefaultColormapId).U32(0xFFFFFF).U32(0x000000);
        w.U32(Root.AllEventMasks);
        w.U16((ushort)Root.Width).U16((ushort)Root.Height).U16((ushort)mmW).U16((ushort)mmH);
        w.U16(1).U16(1);                             // min / max installed maps
        w.U32(RootVisualId);
        w.U8(0);                                     // backing-stores:Never(我们另有顶层缓冲,不对客户端承诺)
        w.Bool(false);                               // save-unders
        w.U8(24);                                    // root-depth
        w.U8(7);                                     // DEPTH 数

        // DEPTH 24:一个 TrueColor 视觉
        w.U8(24).U8(0).U16(1).Zero(4);
        WriteVisual(w, RootVisualId);
        // DEPTH 1 / 4 / 8 / 15 / 16:没有视觉(只能做像素图 —— 协议只允许在列出的深度上建像素图)
        foreach (byte depth in (byte[])[1, 4, 8, 15, 16])
        {
            w.U8(depth).U8(0).U16(0).Zero(4);
        }
        // DEPTH 32:一个 TrueColor 视觉(ARGB,RENDER 用)
        w.U8(32).U8(0).U16(1).Zero(4);
        WriteVisual(w, ArgbVisualId);

        byte[] bytes = w.ToArray();
        ushort extra = (ushort)((bytes.Length - 8) / 4);
        if (client.BigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), extra);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), extra);
        }
        return bytes;

        static void WriteVisual(XWriter w, uint id) =>
            // visual-id、class(4 = TrueColor)、bits-per-rgb、colormap-entries、红绿蓝掩码、4 字节空
            w.U32(id).U8(4).U8(8).U16(256).U32(0xFF0000).U32(0x00FF00).U32(0x0000FF).Zero(4);
    }

    private async Task ReadRequestsAsync(XClient client, Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        byte[] extended = new byte[4];
        while (!ct.IsCancellationRequested && !client.Closed)
        {
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            uint units = Read16(header.AsSpan(2), client.BigEndian);
            int headerSize = 4;
            if (units == 0)
            {
                // BIG-REQUESTS:长度字段为 0,后面 4 字节才是真长度(含这 8 字节头)。
                // 没打开扩展就收到 0 长度 —— 按协议是 BadLength,这里直接断开,免得后面整条流错位。
                if (!client.BigRequestsEnabled)
                {
                    throw new InvalidDataException("收到长度为 0 的请求,但客户端没有打开 BIG-REQUESTS。");
                }
                await stream.ReadExactlyAsync(extended, ct).ConfigureAwait(false);
                units = client.BigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(extended)
                    : BinaryPrimitives.ReadUInt32LittleEndian(extended);
                headerSize = 8;
                if (units is < 2 or > MaxBigRequestLength)
                {
                    throw new InvalidDataException("BIG-REQUESTS 长度越界。");
                }
            }
            // 交给执行线程的请求统一是「4 字节头 + 正文」:扩展长度字段剥掉,正文紧接在头后。
            int size = 4 + (int)(units * 4) - headerSize;
            // 背压:已读进来、还没执行的请求条数或字节数到了上限,就先等执行线程消化,再分配、再读(同步完成的快路径不分配)。
            await client.PendingRequests.WaitAsync(ct).ConfigureAwait(false);
            await client.ReserveRequestBytesAsync(size, ct).ConfigureAwait(false);
            // 缓冲从池里租、执行完还回去(ExecuteRequest):整窗 PutImage 一帧就是几 MB,每条都新分配就是每条都进大对象堆。
            // 租来的数组可能比请求长,也不清零 —— 前 size 字节都会被读进来的字节盖掉,读不满就整条连接收工、这块数组随之丢弃。
            byte[] request = ArrayPool<byte>.Shared.Rent(size);
            header.CopyTo(request, 0);
            await stream.ReadExactlyAsync(request.AsMemory(4, size - 4), ct).ConfigureAwait(false);
            PostRequest(client, request, size);
        }
    }

    /// <summary>
    /// 写出端:把已经排队的回复 / 事件 / 错误拼进一块缓冲再一次写出 —— 事件动辄几十条一批,
    /// 每条单独写就是每条一次系统调用(TCP 上还可能每条一个包)。单条超过缓冲的(GetImage 的大回复)直接写。
    /// </summary>
    private static async Task PumpOutputAsync(XClient client, Stream stream, CancellationToken ct)
    {
        ChannelReader<byte[]> reader = client.Output.Reader;
        byte[] batch = ArrayPool<byte>.Shared.Rent(OutputBufferSize);
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                int used = 0;
                long written = 0;
                while (reader.TryRead(out byte[]? message))
                {
                    written += message.Length;
                    if (used + message.Length > batch.Length)
                    {
                        if (used > 0)
                        {
                            await stream.WriteAsync(batch.AsMemory(0, used), ct).ConfigureAwait(false);
                            used = 0;
                        }
                        if (message.Length > batch.Length)
                        {
                            await stream.WriteAsync(message, ct).ConfigureAwait(false);
                            continue;
                        }
                    }
                    message.CopyTo(batch, used);
                    used += message.Length;
                }
                if (used > 0)
                {
                    await stream.WriteAsync(batch.AsMemory(0, used), ct).ConfigureAwait(false);
                }
                await stream.FlushAsync(ct).ConfigureAwait(false);
                client.NoteWritten(written);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(batch);
        }
    }

    private static ushort Read16(ReadOnlySpan<byte> span, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span);

    /// <summary>
    /// 客户端断开(协议第 10 节「Connection Close」):事件选择、抓取、选区一律放掉;CloseDownMode 为 Destroy(默认)时销毁它的全部资源,
    /// 为 RetainPermanent / RetainTemporary 时资源留着,等 KillClient 来销毁。
    /// </summary>
    private void DisconnectClient(XClient client)
    {
        if (!_clients.Remove(client.Index))
        {
            return;
        }
        client.Closed = true;
        if (ShouldLogFrequent())
        {
            Log($"{client} disconnected");
        }
        try
        {
            if (client.CloseDownMode is 1 or 2)
            {
                ReleaseConnectionState(client);
                _retainedClients[client.Index] = client;
            }
            else
            {
                CleanupClient(client);
            }
        }
        catch (Exception ex)
        {
            Log($"cleanup of {client} failed: {ex}");
        }
        if (ReferenceEquals(_serverGrabber, client))
        {
            ReleaseServerGrab();
        }
    }

    /// <summary>以 RetainPermanent / RetainTemporary 收尾的客户端的资源:KillClient 指到它们时销毁,编号随之放回。</summary>
    private void DestroyRetainedClient(XClient client)
    {
        if (_retainedClients.Remove(client.Index))
        {
            CleanupClient(client);
        }
    }

    /// <summary>KillClient(AllTemporary):销毁所有以 RetainTemporary 收尾的客户端的资源。</summary>
    private void DestroyRetainedTemporaryClients()
    {
        foreach (XClient client in _retainedClients.Values.Where(c => c.CloseDownMode == 2).ToArray())
        {
            DestroyRetainedClient(client);
        }
    }

    /// <summary>这个客户端已经以 Retain 模式断开、资源还留着。</summary>
    private bool IsRetained(XClient client) => _retainedClients.TryGetValue(client.Index, out XClient? retained) && ReferenceEquals(retained, client);

    /// <summary>连接收尾时与资源无关的那一半:选区、抓取、别人窗口上的事件选择与被动抓取、各扩展的每连接状态。</summary>
    private void ReleaseConnectionState(XClient client)
    {
        ReleaseSelectionsAndGrabs(client);
        ReleaseEventSelections(client);
    }

    /// <summary>断开的客户端:释放它的资源、选区、抓取与事件选择,再让各扩展清掉自己的那份状态(<see cref="Extension.ClientClosed" />)。</summary>
    private void CleanupClient(XClient client)
    {
        ReleaseSelectionsAndGrabs(client);
        DestroyClientResources(client);
        ReleaseEventSelections(client);
    }

    private void ReleaseSelectionsAndGrabs(XClient client)
    {
        foreach ((uint atom, (XWindow Window, XClient? Client, uint Time) owner) in _selections.ToArray())
        {
            if (ReferenceEquals(owner.Client, client))
            {
                _selections.Remove(atom);
                NotifySelectionChange(atom, 2, 0, owner.Time);
            }
        }
        if (_fetch is { } fetch && !_selections.ContainsKey(fetch.Selection))
        {
            _fetch = null;   // 正在取的选区,属主走了
        }
        if (ReferenceEquals(PointerGrab?.Client, client))
        {
            PointerGrab = null;
        }
        if (ReferenceEquals(KeyboardGrab?.Client, client))
        {
            KeyboardGrab = null;
        }
    }

    private void DestroyClientResources(XClient client)
    {
        // 资源表只扫一遍:分出它的窗口与其余资源。先销毁「挂在别人窗口下」的那些(连同子窗口),再清其余资源。
        List<XWindow> windows = [];
        List<XResource> others = [];
        foreach (XResource resource in _resources.Values)
        {
            if (!ReferenceEquals(resource.Owner, client))
            {
                continue;
            }
            if (resource is XWindow window)
            {
                windows.Add(window);
            }
            else
            {
                others.Add(resource);
            }
        }
        foreach (XWindow window in windows)
        {
            if (_resources.ContainsKey(window.Id) && window.Parent is { } parent && !ReferenceEquals(parent.Owner, client))
            {
                Destroy(window);
            }
        }
        foreach (XWindow window in windows)
        {
            Destroy(window);   // 已随上级销毁的会在里面直接返回
        }
        DetachShmSegments(others);
        foreach (XResource resource in others)
        {
            _resources.Remove(resource.Id);
            if (resource is XPixmap pixmap)
            {
                CleanupDamage(pixmap);   // 客户端走了,它的像素图随之销毁:别的客户端建在上面的 Damage 一并销毁
            }
        }
    }

    private void ReleaseEventSelections(XClient client)
    {
        // 它在别人窗口上选的事件、登记的被动抓取一并摘掉。
        foreach (XResource resource in _resources.Values)
        {
            if (resource is XWindow window)
            {
                window.EventSelections.Remove(client);
                window.ButtonGrabs.RemoveAll(g => ReferenceEquals(g.Client, client));
                window.KeyGrabs.RemoveAll(g => ReferenceEquals(g.Client, client));
                window.ShapeSelections.Remove(client);
            }
        }
        foreach (Extension extension in _extensionList)
        {
            extension.ClientClosed?.Invoke(client);
        }
        UpdatePointerWindow();
        UpdateCursor();
    }

    // ------------------------------------------------------------------ BIG-REQUESTS

    private static void BigRequests(XClient c, XRequestReader r)
    {
        if (r.Data != 0)
        {
            throw new XProtocolError(XErrorCode.Request);
        }
        c.BigRequestsEnabled = true;
        c.Reply(0, w => w.U32(MaxBigRequestLength).Zero(20));
    }
}
