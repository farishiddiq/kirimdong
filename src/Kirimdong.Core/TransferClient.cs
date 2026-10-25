using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Kirimdong.Core;

public static class TransferClient
{
    private const int SocketBuffer = 4 * 1024 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Asks a kirimdong at host:port who it is. Used to add a PC by IP address.</summary>
    public static async Task<Hello> PingAsync(IPAddress address, int port, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
        var stream = client.GetStream();
        await Protocol.WriteHeaderAsync(stream, Protocol.KindPing, timeout.Token).ConfigureAwait(false);
        var hello = await Protocol.ReadFrameAsync<Hello>(stream, timeout.Token).ConfigureAwait(false);
        return hello with { Name = Protocol.CleanName(hello.Name) };
    }

    /// <summary>Introduces this PC to a kirimdong at host:port and returns who answered.</summary>
    public static async Task<Hello> HelloAsync(IPAddress address, int port, Hello me, TimeSpan timeout, CancellationToken ct = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(address, port, limit.Token).ConfigureAwait(false);
        var stream = client.GetStream();
        await Protocol.WriteHeaderAsync(stream, Protocol.KindHello, limit.Token).ConfigureAwait(false);
        await Protocol.WriteFrameAsync(stream, me, limit.Token).ConfigureAwait(false);
        await stream.FlushAsync(limit.Token).ConfigureAwait(false);
        // Reading the answer may take longer than connecting on a busy Wi-Fi; allow a little more.
        limit.CancelAfter(ConnectTimeout);
        var hello = await Protocol.ReadFrameAsync<Hello>(stream, limit.Token).ConfigureAwait(false);
        return hello with { Name = Protocol.CleanName(hello.Name) };
    }

    public static Transfer CreateTransfer(string peerName, OutgoingBatch batch)
    {
        var topLevel = batch.Files.Select(f => f.RelativePath.Split('/')[0])
            .Concat(batch.Folders.Select(f => f.Split('/')[0]))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new Transfer(TransferDirection.Send, peerName, batch.TotalBytes, batch.Files.Count,
            Transfer.Describe(topLevel, batch.Files.Count));
    }

    /// <summary>Sends <paramref name="batch"/> and drives <paramref name="transfer"/> to its final state. Never throws.</summary>
    public static async Task SendAsync(IPEndPoint target, Hello me, OutgoingBatch batch, Transfer transfer, string? edge = null)
    {
        var ct = transfer.Token;
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork) { SendBufferSize = SocketBuffer };
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(target, connect.Token).ConfigureAwait(false);
            }
            using var reg = ct.Register(client.Dispose);
            var stream = client.GetStream();

            await Protocol.WriteHeaderAsync(stream, Protocol.KindOffer, ct).ConfigureAwait(false);
            var offer = new Offer(me.Id, me.Name,
                batch.Files.Select(f => new OfferItem(f.RelativePath, f.Size)).ToList(),
                batch.Folders.Count > 0 ? batch.Folders : null, edge);
            await Protocol.WriteFrameAsync(stream, offer, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            // The receiver may take a while to decide; it closes the connection itself after its own timeout.
            var decision = await Protocol.ReadByteAsync(stream, ct).ConfigureAwait(false);
            if (decision != 1)
            {
                transfer.SetState(TransferState.Declined);
                return;
            }
            transfer.SetState(TransferState.Running);

            var pump = new Pump();
            foreach (var file in batch.Files)
            {
                transfer.CurrentFile = file.RelativePath;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read,
                                 FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.SequentialScan | FileOptions.Asynchronous))
                {
                    try { await pump.CopyAsync(input, stream, file.Size, hash, n => transfer.AddBytes(n), ct).ConfigureAwait(false); }
                    catch (EndOfStreamException) { throw new InvalidDataException($"{Path.GetFileName(file.FullPath)} berubah saat dikirim."); }
                }
                await stream.WriteAsync(hash.GetHashAndReset(), ct).ConfigureAwait(false);
            }
            await stream.FlushAsync(ct).ConfigureAwait(false);
            transfer.CurrentFile = null;

            var result = await Protocol.ReadByteAsync(stream, ct).ConfigureAwait(false);
            if (result != 1) throw new InvalidDataException("PC tujuan gagal menyimpan paket.");
            transfer.SetState(TransferState.Done);
        }
        catch (Exception ex)
        {
            transfer.Fail(ex);
        }
    }
}
