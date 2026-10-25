using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Kirimdong.Core;

namespace Kirimdong.Core.Tests;

/// <summary>Real sockets on loopback: a receiver and a sender in one process.</summary>
public sealed class TransferTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "kirimdong-tests", Guid.NewGuid().ToString("N"));
    private readonly string _outbox;
    private readonly string _inbox;
    private readonly Hello _sender = new("sender-id", "Laptop Pengirim", 0);

    public TransferTests()
    {
        _outbox = Directory.CreateDirectory(Path.Combine(_work, "outbox")).FullName;
        _inbox = Path.Combine(_work, "inbox");
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { }
    }

    private TransferServer StartServer(Func<IncomingOffer, CancellationToken, Task<bool>> decide, List<Transfer>? seen = null)
    {
        var server = new TransferServer(() => new Hello("receiver-id", "PC Penerima", 0), () => _inbox, decide);
        if (seen is not null) server.TransferStarted += t => { lock (seen) seen.Add(t); };
        server.Start(0);
        return server;
    }

    private static async Task<Transfer> WaitFinished(Func<Transfer?> get)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var t = get();
            if (t is not null && !t.IsActive) return t;
            await Task.Delay(20);
        }
        throw new TimeoutException();
    }

    private string WriteFile(string relative, int size, int seed)
    {
        var path = Path.Combine(_outbox, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public async Task SendsFilesAndFoldersIntact()
    {
        var received = new List<Transfer>();
        using var server = StartServer((_, _) => Task.FromResult(true), received);

        var single = WriteFile("laporan.pdf", 3_500_000, 1);
        WriteFile("Liburan/pantai/1.jpg", 1_200_000, 2);
        WriteFile("Liburan/pantai/2.jpg", 0, 3);
        WriteFile("Liburan/catatan.txt", 777, 4);
        Directory.CreateDirectory(Path.Combine(_outbox, "Liburan", "kosong"));

        var batch = FileCollector.Collect(new[] { single, Path.Combine(_outbox, "Liburan") });
        Assert.Equal(4, batch.Files.Count);

        var sending = TransferClient.CreateTransfer("PC Penerima", batch);
        await TransferClient.SendAsync(new IPEndPoint(IPAddress.Loopback, server.Port), _sender, batch, sending);

        Assert.Equal(TransferState.Done, sending.State);
        Assert.Equal(batch.TotalBytes, sending.TransferredBytes);
        var incoming = await WaitFinished(() => received.SingleOrDefault());
        Assert.Equal(TransferState.Done, incoming.State);
        Assert.Equal("Laptop Pengirim", incoming.PeerName);
        Assert.Equal(batch.TotalBytes, incoming.TransferredBytes);

        foreach (var f in batch.Files)
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(f.FullPath)), SHA256.HashData(File.ReadAllBytes(Path.Combine(_inbox, f.RelativePath))));
        Assert.True(Directory.Exists(Path.Combine(_inbox, "Liburan", "kosong")));
        Assert.Empty(Directory.GetFiles(_inbox, "*.kdpart", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SecondCopyGetsANewName()
    {
        var received = new List<Transfer>();
        using var server = StartServer((_, _) => Task.FromResult(true), received);
        var file = WriteFile("foto.jpg", 1000, 5);
        WriteFile("Album/a.jpg", 10, 6);
        var batch = FileCollector.Collect(new[] { file, Path.Combine(_outbox, "Album") });

        for (var i = 0; i < 2; i++)
        {
            var t = TransferClient.CreateTransfer("x", batch);
            await TransferClient.SendAsync(new IPEndPoint(IPAddress.Loopback, server.Port), _sender, batch, t);
            Assert.Equal(TransferState.Done, t.State);
        }

        Assert.True(File.Exists(Path.Combine(_inbox, "foto.jpg")));
        Assert.True(File.Exists(Path.Combine(_inbox, "foto (1).jpg")));
        Assert.True(File.Exists(Path.Combine(_inbox, "Album", "a.jpg")));
        Assert.True(File.Exists(Path.Combine(_inbox, "Album (1)", "a.jpg")));
    }

    [Fact]
    public async Task DeclinedParcelLeavesNothingBehind()
    {
        var received = new List<Transfer>();
        using var server = StartServer((_, _) => Task.FromResult(false), received);
        var batch = FileCollector.Collect(new[] { WriteFile("rahasia.txt", 100, 7) });
        var t = TransferClient.CreateTransfer("x", batch);
        await TransferClient.SendAsync(new IPEndPoint(IPAddress.Loopback, server.Port), _sender, batch, t);

        Assert.Equal(TransferState.Declined, t.State);
        Assert.Equal(TransferState.Declined, (await WaitFinished(() => received.SingleOrDefault())).State);
        Assert.False(Directory.Exists(_inbox) && Directory.EnumerateFileSystemEntries(_inbox).Any());
    }

    [Fact]
    public async Task SenderCancelWhileWaitingClosesTheRequest()
    {
        var received = new List<Transfer>();
        var asked = new TaskCompletionSource();
        CancellationToken decisionToken = default;
        using var server = StartServer(async (_, ct) =>
        {
            decisionToken = ct;
            asked.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        }, received);
        var batch = FileCollector.Collect(new[] { WriteFile("a.bin", 10, 8) });
        var t = TransferClient.CreateTransfer("x", batch);
        var send = TransferClient.SendAsync(new IPEndPoint(IPAddress.Loopback, server.Port), _sender, batch, t);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        t.Cancel();
        await send;

        Assert.Equal(TransferState.Canceled, t.State);
        var incoming = await WaitFinished(() => received.SingleOrDefault());
        Assert.Equal(TransferState.Failed, incoming.State);
        Assert.True(decisionToken.IsCancellationRequested);
    }

    [Fact]
    public async Task HostileOfferPathsLandInsideSaveFolder()
    {
        var received = new List<Transfer>();
        using var server = StartServer((_, _) => Task.FromResult(true), received);
        var payload = "isi"u8.ToArray();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var s = client.GetStream();
        await Protocol.WriteHeaderAsync(s, Protocol.KindOffer, default);
        await Protocol.WriteFrameAsync(s, new Offer("evil", "Jahat", new[] { new OfferItem("../../../keluar.txt", payload.Length) }, null), default);
        Assert.Equal(1, await Protocol.ReadByteAsync(s, default));
        await s.WriteAsync(payload);
        await s.WriteAsync(SHA256.HashData(payload));
        Assert.Equal(1, await Protocol.ReadByteAsync(s, default));

        Assert.True(File.Exists(Path.Combine(_inbox, "keluar.txt")));
        Assert.False(File.Exists(Path.Combine(_work, "keluar.txt")));
    }

    [Fact]
    public async Task CorruptedBytesAreRejected()
    {
        var received = new List<Transfer>();
        using var server = StartServer((_, _) => Task.FromResult(true), received);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var s = client.GetStream();
        await Protocol.WriteHeaderAsync(s, Protocol.KindOffer, default);
        await Protocol.WriteFrameAsync(s, new Offer("x", "x", new[] { new OfferItem("a.txt", 3) }, null), default);
        Assert.Equal(1, await Protocol.ReadByteAsync(s, default));
        await s.WriteAsync("abc"u8.ToArray());
        await s.WriteAsync(new byte[32]);

        var incoming = await WaitFinished(() => received.SingleOrDefault());
        Assert.Equal(TransferState.Failed, incoming.State);
        Assert.Contains("hash", incoming.Error);
        Assert.Empty(Directory.GetFiles(_inbox, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void FolderWalkKeepsHiddenFilesAndSkipsLinks()
    {
        WriteFile("Proyek/.env.example", 5, 10);
        var hidden = WriteFile("Proyek/tersembunyi.txt", 5, 11);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        WriteFile("Proyek/sub/a.txt", 5, 12);
        var link = Path.Combine(_outbox, "Proyek", "putaran");
        try { Directory.CreateSymbolicLink(link, Path.Combine(_outbox, "Proyek")); }
        catch (IOException) { link = ""; }
        catch (UnauthorizedAccessException) { link = ""; } // creating links needs Developer Mode; the rest still applies

        var batch = FileCollector.Collect(new[] { Path.Combine(_outbox, "Proyek") });

        Assert.Equal(new[] { "Proyek/.env.example", "Proyek/sub/a.txt", "Proyek/tersembunyi.txt" },
            batch.Files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("Proyek/putaran", batch.Folders);
    }

    [Fact]
    public async Task PingReturnsIdentity()
    {
        using var server = StartServer((_, _) => Task.FromResult(true));
        var hello = await TransferClient.PingAsync(IPAddress.Loopback, server.Port);
        Assert.Equal("receiver-id", hello.Id);
        Assert.Equal("PC Penerima", hello.Name);
    }

    [Fact]
    public async Task UnreachablePeerFailsWithMessage()
    {
        var batch = FileCollector.Collect(new[] { WriteFile("a.bin", 10, 9) });
        var t = TransferClient.CreateTransfer("x", batch);
        var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var free = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        await TransferClient.SendAsync(new IPEndPoint(IPAddress.Loopback, free), _sender, batch, t);
        Assert.Equal(TransferState.Failed, t.State);
        Assert.False(string.IsNullOrEmpty(t.Error));
    }
}

public class DiscoveryTests
{
    [Fact]
    public void AnnouncementsFillAndEmptyThePeerList()
    {
        using var d = new DiscoveryService(() => new Hello("me", "Saya", 47801), port: 0);
        var changes = 0;
        d.PeersChanged += () => changes++;

        d.Handle(new Announcement(Protocol.AppTag, 1, Announcement.KindHello, "me", "Saya", 47801), IPAddress.Loopback);
        Assert.Empty(d.Peers);

        d.Handle(new Announcement(Protocol.AppTag, 1, Announcement.KindHello, "other", "Laptop Ani", 47801), IPAddress.Parse("192.168.1.9"));
        var peer = Assert.Single(d.Peers);
        Assert.Equal("Laptop Ani", peer.Name);
        Assert.Equal(IPAddress.Parse("192.168.1.9"), peer.Address);

        d.Handle(new Announcement(Protocol.AppTag, 1, Announcement.KindHello, "other", "Laptop Ani", 47801), IPAddress.Loopback);
        Assert.Equal(IPAddress.Parse("192.168.1.9"), Assert.Single(d.Peers).Address);

        d.Handle(new Announcement(Protocol.AppTag, 1, Announcement.KindBye, "other", "Laptop Ani", 47801), IPAddress.Parse("192.168.1.9"));
        Assert.Empty(d.Peers);
        Assert.Equal(2, changes);
    }
}
