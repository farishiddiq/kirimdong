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
    /// <summary>Added by IP address rather than found by broadcast; kept alive by pinging.</summary>
    public bool IsManual { get; set; }

    public IPEndPoint EndPoint => new(Address, Port);
}

/// <summary>
/// Finds other kirimdong PCs on the local network with UDP broadcasts on <see cref="Protocol.DiscoveryPort"/>.
/// Every PC announces itself every two seconds and forgets a peer it has not heard from for ten.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    private static readonly TimeSpan AnnounceEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PingManualEvery = TimeSpan.FromSeconds(4);

    private readonly Func<Hello> _identity;
    private readonly int _port;
    private readonly ConcurrentDictionary<string, Peer> _peers = new();
    private readonly CancellationTokenSource _stop = new();
    private Socket? _socket;

    public DiscoveryService(Func<Hello> identity, int port = Protocol.DiscoveryPort)
    {
        _identity = identity;
        _port = port;
    }

    public event Action? PeersChanged;

    /// <summary>Set when this PC cannot listen for announcements (it can still announce and be found).</summary>
    public string? ListenError { get; private set; }

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

    public void AddManual(Hello hello, IPAddress address)
    {
        if (hello.Id == _identity().Id) return;
        Upsert(hello.Id, hello.Name, address, hello.Port, manual: true);
        Send(Announcement.KindHello, new[] { new IPEndPoint(address, _port) });
    }

    public void Forget(string id)
    {
        if (_peers.TryRemove(id, out _)) PeersChanged?.Invoke();
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
        var isNew = Upsert(a.Id, a.Name, from, a.Port, manual: false);
        // Answer a newcomer directly so both lists fill without waiting for the next round.
        if (isNew) Send(Announcement.KindHello, new[] { new IPEndPoint(from, _port) });
    }

    private bool Upsert(string id, string name, IPAddress address, int port, bool manual)
    {
        var isNew = false;
        var changed = false;
        _peers.AddOrUpdate(id,
            _ =>
            {
                isNew = true;
                return new Peer { Id = id, Name = name, Address = address, Port = port, LastSeen = DateTime.UtcNow, IsManual = manual };
            },
            (_, p) =>
            {
                changed = p.Name != name || p.Port != port;
                p.Name = name;
                p.Port = port;
                // Keep a LAN address over loopback when both reach us (two copies on one PC during testing).
                if (!IPAddress.IsLoopback(address) || IPAddress.IsLoopback(p.Address)) p.Address = address;
                p.LastSeen = DateTime.UtcNow;
                p.IsManual |= manual;
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
            if (DateTime.UtcNow - lastPing >= PingManualEvery)
            {
                lastPing = DateTime.UtcNow;
                foreach (var peer in _peers.Values.Where(p => p.IsManual))
                    _ = PingManualAsync(peer, ct);
            }
            try { await Task.Delay(AnnounceEvery, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PingManualAsync(Peer peer, CancellationToken ct)
    {
        try
        {
            var hello = await TransferClient.PingAsync(peer.Address, peer.Port, ct).ConfigureAwait(false);
            if (hello.Id == peer.Id) Upsert(peer.Id, hello.Name, peer.Address, hello.Port, manual: true);
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
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    var b = DirectedBroadcast(ua.Address, ua.IPv4Mask);
                    if (b is not null) targets.Add(b);
                }
            }
        }
        catch (NetworkInformationException) { }
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
