using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Kirimdong.Core;

public sealed class Peer
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required IPAddress Address { get; set; }
    public required int Port { get; set; }
    public DateTime LastSeen { get; set; }
    /// <summary>Found over TCP (added by IP, by a subnet scan, or because it introduced itself) rather than by
    /// broadcast; kept alive by pinging it.</summary>
    public bool KeepAliveByPing { get; set; }

    public IPEndPoint EndPoint => new(Address, Port);
}

/// <summary>
/// Finds other kirimdong PCs on the local network.
/// Primary: UDP broadcasts on <see cref="Protocol.DiscoveryPort"/>; every PC announces itself every two seconds
/// and forgets a peer it has not heard from for ten.
/// Fallback: while nobody has been found, every PC on this PC's subnets is asked over TCP (port 47801), which gets
/// through Wi-Fi access points that drop broadcasts, and through a firewall that blocks only one direction.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    private static readonly TimeSpan AnnounceEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan FirstSweepAfter = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SweepConnectTimeout = TimeSpan.FromMilliseconds(500);
    private const int SweepParallelism = 96;
    private const int MaxSweepHosts = 1022; // a /22 at most; bigger networks are not scanned

    private readonly Func<Hello> _identity;
    private readonly int _port;
    private readonly ConcurrentDictionary<string, Peer> _peers = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly DateTime _started = DateTime.UtcNow;
    private Socket? _socket;
    private int _sweeping;
    private DateTime _lastSweep = DateTime.MinValue;

    public DiscoveryService(Func<Hello> identity, int port = Protocol.DiscoveryPort)
    {
        _identity = identity;
        _port = port;
    }

    public event Action? PeersChanged;
    public event Action? SweepStateChanged;

    /// <summary>Set when this PC cannot listen for announcements (it can still announce and be found).</summary>
    public string? ListenError { get; private set; }

    public bool IsSweeping => Volatile.Read(ref _sweeping) == 1;

    public IReadOnlyList<Peer> Peers => _peers.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public void Start()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.EnableBroadcast = true;
        try
        {
            _socket.Bind(new IPEndPoint(IPAddress.Any, _port));
            _ = ReceiveLoopAsync(_socket, _stop.Token);
        }
        catch (SocketException ex)
        {
            ListenError = ex.Message;
        }
        _ = AnnounceLoopAsync(_stop.Token);
    }

    public void AnnounceNow() => Send(Announcement.KindHello, BroadcastTargets());

    /// <summary>A PC that answered a ping or a hello: remember it and keep it alive by pinging.</summary>
    public void AddManual(Hello hello, IPAddress address)
    {
        if (hello.Id == _identity().Id) return;
        Upsert(hello.Id, hello.Name, address, hello.Port, byPing: true);
        Send(Announcement.KindHello, new[] { new IPEndPoint(address, _port) });
    }

    /// <summary>A PC introduced itself to our receiver over TCP.</summary>
    public void Learn(Hello hello, IPAddress address) => AddManual(hello, address);

    public void Forget(string id)
    {
        if (_peers.TryRemove(id, out _)) PeersChanged?.Invoke();
    }

    /// <summary>Asks every address on this PC's subnets (up to a /22) whether kirimdong is there. Returns how many answered.</summary>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return 0;
        _lastSweep = DateTime.UtcNow;
        SweepStateChanged?.Invoke();
        var found = 0;
        try
        {
            var me = _identity();
            var own = new HashSet<IPAddress>(LocalAddresses());
            var targets = LocalSubnets().SelectMany(s => s.Hosts()).Where(a => !own.Contains(a)).Distinct().ToList();
            using var gate = new SemaphoreSlim(SweepParallelism);
            var tasks = targets.Select(async address =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var hello = await TransferClient.HelloAsync(address, Protocol.DefaultTcpPort, me, SweepConnectTimeout, ct).ConfigureAwait(false);
                    if (hello.Id == me.Id) return;
                    AddManual(hello, address);
                    Interlocked.Increment(ref found);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // Nobody there, or not kirimdong.
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _lastSweep = DateTime.UtcNow;
            Volatile.Write(ref _sweeping, 0);
            SweepStateChanged?.Invoke();
        }
        return found;
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try { result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; } // e.g. ICMP port unreachable from an earlier send
            var a = Protocol.DecodeAnnouncement(buffer.AsSpan(0, result.ReceivedBytes));
            if (a is null || result.RemoteEndPoint is not IPEndPoint from) continue;
            Handle(a, from.Address);
        }
    }

    internal void Handle(Announcement a, IPAddress from)
    {
        if (a.Id == _identity().Id) return;
        if (a.Kind == Announcement.KindBye)
        {
            Forget(a.Id);
            return;
        }
        if (a.Kind != Announcement.KindHello) return;
        var isNew = Upsert(a.Id, a.Name, from, a.Port, byPing: false);
        // Answer a newcomer directly so both lists fill without waiting for the next round.
        if (isNew) Send(Announcement.KindHello, new[] { new IPEndPoint(from, _port) });
    }

    private bool Upsert(string id, string name, IPAddress address, int port, bool byPing)
    {
        var isNew = false;
        var changed = false;
        _peers.AddOrUpdate(id,
            _ =>
            {
                isNew = true;
                return new Peer { Id = id, Name = name, Address = address, Port = port, LastSeen = DateTime.UtcNow, KeepAliveByPing = byPing };
            },
            (_, p) =>
            {
                changed = p.Name != name || p.Port != port;
                p.Name = name;
                p.Port = port;
                // Keep a LAN address over loopback when both reach us (two copies on one PC during testing).
                if (!IPAddress.IsLoopback(address) || IPAddress.IsLoopback(p.Address)) p.Address = address;
                p.LastSeen = DateTime.UtcNow;
                p.KeepAliveByPing |= byPing;
                return p;
            });
        if (isNew || changed) PeersChanged?.Invoke();
        return isNew;
    }

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        var lastPing = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            AnnounceNow();
            ExpireStale();
            var now = DateTime.UtcNow;
            if (now - lastPing >= PingEvery)
            {
                lastPing = now;
                foreach (var peer in _peers.Values.Where(p => p.KeepAliveByPing))
                    _ = PingAsync(peer, ct);
            }
            if (_peers.IsEmpty && now - _started >= FirstSweepAfter && now - _lastSweep >= SweepEvery)
                _ = SweepAsync(ct);
            try { await Task.Delay(AnnounceEvery, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PingAsync(Peer peer, CancellationToken ct)
    {
        try
        {
            var hello = await TransferClient.PingAsync(peer.Address, peer.Port, ct).ConfigureAwait(false);
            if (hello.Id == peer.Id) Upsert(peer.Id, hello.Name, peer.Address, hello.Port, byPing: true);
        }
        catch (Exception)
        {
            // Unreachable for now; ExpireStale removes it if this keeps failing.
        }
    }

    private void ExpireStale()
    {
        var cutoff = DateTime.UtcNow - ForgetAfter;
        var removed = false;
        foreach (var (id, peer) in _peers)
            if (peer.LastSeen < cutoff) removed |= _peers.TryRemove(id, out _);
        if (removed) PeersChanged?.Invoke();
    }

    private void Send(string kind, IEnumerable<IPEndPoint> targets)
    {
        var socket = _socket;
        if (socket is null) return;
        var me = _identity();
        var datagram = Protocol.EncodeAnnouncement(new Announcement(Protocol.AppTag, Protocol.Version, kind, me.Id, me.Name, me.Port));
        foreach (var target in targets)
        {
            try { socket.SendTo(datagram, target); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
        }
    }

    /// <summary>The limited broadcast, every adapter's directed broadcast, and loopback (two copies on one PC).</summary>
    internal IEnumerable<IPEndPoint> BroadcastTargets()
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        foreach (var subnet in LocalSubnets(includeLarge: true))
            if (subnet.Broadcast is { } b) targets.Add(b);
        return targets.Select(a => new IPEndPoint(a, _port));
    }

    internal static IPAddress? DirectedBroadcast(IPAddress address, IPAddress mask)
    {
        var ip = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (ip.Length != 4 || m.Length != 4 || m.All(b => b == 0)) return null;
        var b = new byte[4];
        for (var i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~m[i]);
        return new IPAddress(b);
    }

    /// <summary>One IPv4 network this PC sits on.</summary>
    internal readonly record struct Subnet(IPAddress Address, IPAddress Mask)
    {
        public IPAddress? Broadcast => DirectedBroadcast(Address, Mask);

        public int HostCount
        {
            get
            {
                var m = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(Mask.GetAddressBytes(), 0));
                var size = ~m;
                return size <= 1 ? 0 : (int)Math.Min(int.MaxValue, size - 1);
            }
        }

        public IEnumerable<IPAddress> Hosts()
        {
            var ip = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(Address.GetAddressBytes(), 0));
            var m = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(Mask.GetAddressBytes(), 0));
            var network = ip & m;
            var count = HostCount;
            if (count == 0 || count > MaxSweepHosts) yield break;
            for (uint i = 1; i <= count; i++)
                yield return new IPAddress(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)(network + i))));
        }
    }

    /// <summary>Subnets of active adapters that have a gateway (real networks, not virtual switches).</summary>
    internal static IReadOnlyList<Subnet> LocalSubnets(bool includeLarge = false)
    {
        var list = new List<Subnet>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                if (!includeLarge && props.GatewayAddresses.Count == 0) continue;
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    if (IPAddress.IsLoopback(ua.Address) || ua.Address.GetAddressBytes()[0] == 169) continue; // no link-local
                    var s = new Subnet(ua.Address, ua.IPv4Mask);
                    if (includeLarge || s.HostCount <= MaxSweepHosts) list.Add(s);
                }
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }

    /// <summary>This PC's IPv4 addresses on active adapters, for showing on its own label.</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.GetIPProperties().GatewayAddresses.Count == 0) continue; // skip virtual switches without a route
                list.AddRange(nic.GetIPProperties().UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address))
                    .Select(u => u.Address));
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }

    public void Dispose()
    {
        try { Send(Announcement.KindBye, BroadcastTargets()); } catch (Exception) { }
        _stop.Cancel();
        _socket?.Dispose();
    }
}
