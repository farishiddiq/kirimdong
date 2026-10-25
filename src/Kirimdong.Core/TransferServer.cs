using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Kirimdong.Core;

public sealed record IncomingOffer(Transfer Transfer, Offer Offer, IPAddress From);

/// <summary>Accepts parcels from other PCs and writes them into the save folder.</summary>
public sealed class TransferServer : IDisposable
{
    private const int SocketBuffer = 4 * 1024 * 1024;
    private const int MaxFiles = 200_000;
    public static readonly TimeSpan DecisionTimeout = TimeSpan.FromMinutes(2);
    private const string PartSuffix = ".kdpart";

    private readonly Func<Hello> _identity;
    private readonly Func<string> _saveFolder;
    private readonly Func<IncomingOffer, CancellationToken, Task<bool>> _decide;
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;

    public TransferServer(Func<Hello> identity, Func<string> saveFolder, Func<IncomingOffer, CancellationToken, Task<bool>> decide)
    {
        _identity = identity;
        _saveFolder = saveFolder;
        _decide = decide;
    }

    /// <summary>Raised as soon as an offer arrives (before the decision), so the UI can list it.</summary>
    public event Action<Transfer>? TransferStarted;

    /// <summary>Another PC introduced itself over TCP (it found us by scanning, or was added by IP).</summary>
    public event Action<Hello, IPAddress>? HelloReceived;

    public int Port { get; private set; }

    public void Start(int port)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(_listener, _stop.Token);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => HandleAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken serverToken)
    {
        using var _ = client;
        client.ReceiveBufferSize = SocketBuffer;
        var from = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        var stream = client.GetStream();
        byte kind;
        try
        {
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            kind = await Protocol.ReadHeaderAsync(stream, headerTimeout.Token).ConfigureAwait(false);
            if (kind == Protocol.KindPing)
            {
                await Protocol.WriteFrameAsync(stream, _identity(), headerTimeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(headerTimeout.Token).ConfigureAwait(false);
                return;
            }
            if (kind == Protocol.KindHello)
            {
                var caller = await Protocol.ReadFrameAsync<Hello>(stream, headerTimeout.Token).ConfigureAwait(false);
                await Protocol.WriteFrameAsync(stream, _identity(), headerTimeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(headerTimeout.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(caller.Id) && caller.Port is > 0 and < 65536)
                    HelloReceived?.Invoke(caller with { Name = Protocol.CleanName(caller.Name) }, from);
                return;
            }
            if (kind != Protocol.KindOffer) return;
        }
        catch (Exception)
        {
            return; // not one of ours, or gone before saying anything
        }

        Offer offer;
        try
        {
            using var frameTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            frameTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            offer = await Protocol.ReadFrameAsync<Offer>(stream, frameTimeout.Token).ConfigureAwait(false);
            Validate(offer);
        }
        catch (Exception)
        {
            return;
        }

        var plan = ReceivePlan.Create(offer);
        var transfer = new Transfer(TransferDirection.Receive, Protocol.CleanName(offer.SenderName),
            offer.TotalBytes, offer.Files.Count, Transfer.Describe(plan.TopLevelNames, offer.Files.Count))
        {
            PeerId = offer.SenderId,
            Edge = offer.Edge,
        };
        TransferStarted?.Invoke(transfer);
        await ReceiveAsync(client, stream, offer, plan, transfer, from, serverToken).ConfigureAwait(false);
    }

    private async Task ReceiveAsync(TcpClient client, NetworkStream stream, Offer offer, ReceivePlan plan,
        Transfer transfer, IPAddress from, CancellationToken serverToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, transfer.Token);
        var ct = linked.Token;
        using var reg = ct.Register(client.Dispose);
        string? partPath = null;
        try
        {
            bool accepted;
            using (var decision = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                decision.CancelAfter(DecisionTimeout);
                var decide = _decide(new IncomingOffer(transfer, offer, from), decision.Token);
                // The sender says nothing while we wait, so a readable socket here means it hung up.
                while (!decide.IsCompleted)
                {
                    await Task.WhenAny(decide, Task.Delay(500, decision.Token)).ConfigureAwait(false);
                    if (!decide.IsCompleted && IsClosedByPeer(client))
                    {
                        decision.Cancel();
                        throw new InvalidDataException("Pengirim membatalkan.");
                    }
                }
                try { accepted = await decide.ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { accepted = false; }
            }
            if (!accepted)
            {
                await Protocol.WriteByteAsync(stream, 0, ct).ConfigureAwait(false);
                transfer.SetState(TransferState.Declined);
                return;
            }

            var root = _saveFolder();
            Directory.CreateDirectory(root);
            transfer.SaveFolder = root;
            var map = plan.ResolveTopLevel(root);
            transfer.SavedPaths = map.Values.ToList();
            foreach (var folder in plan.Folders)
                Directory.CreateDirectory(plan.Target(root, map, folder));

            await Protocol.WriteByteAsync(stream, 1, ct).ConfigureAwait(false);
            transfer.SetState(TransferState.Running);

            var pump = new Pump();
            var expectedHash = new byte[Protocol.HashLength];
            foreach (var item in offer.Files)
            {
                transfer.CurrentFile = item.Path;
                var target = plan.Target(root, map, item.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                partPath = target + PartSuffix;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(partPath, new FileStreamOptions
                             {
                                 Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None,
                                 BufferSize = 0, Options = FileOptions.Asynchronous,
                                 PreallocationSize = item.Size, // one contiguous allocation instead of growing block by block
                             }))
                {
                    await pump.CopyAsync(stream, output, item.Size, hash, n => transfer.AddBytes(n), ct).ConfigureAwait(false);
                }
                await stream.ReadExactlyAsync(expectedHash, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expectedHash))
                    throw new InvalidDataException($"{Path.GetFileName(target)} rusak di jalan (hash tidak cocok).");
                File.Move(partPath, PathSafety.MakeUnique(target));
                partPath = null;
            }
            transfer.CurrentFile = null;
            await Protocol.WriteByteAsync(stream, 1, ct).ConfigureAwait(false);
            transfer.SetState(TransferState.Done);
        }
        catch (Exception ex)
        {
            if (partPath is not null)
                try { File.Delete(partPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            transfer.Fail(ex);
        }
    }

    private static bool IsClosedByPeer(TcpClient client)
    {
        try { return client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0; }
        catch (Exception) { return true; }
    }

    private static void Validate(Offer offer)
    {
        if (offer.Files is null) throw new InvalidDataException("Offer without files.");
        if (offer.Files.Count > MaxFiles) throw new InvalidDataException("Too many files.");
        if (offer.Files.Count == 0 && (offer.Folders?.Count ?? 0) == 0) throw new InvalidDataException("Empty offer.");
        foreach (var f in offer.Files)
        {
            if (f.Size < 0) throw new InvalidDataException("Negative size.");
            if (PathSafety.SanitizeRelative(f.Path) is null) throw new InvalidDataException("Unsafe path.");
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Stop();
    }
}

/// <summary>
/// Decides where an offer's items go. Each top-level name (a file, or the folder that holds the rest)
/// gets a free name in the save folder, so receiving "Foto" twice gives "Foto" and "Foto (1)".
/// </summary>
internal sealed class ReceivePlan
{
    private ReceivePlan(List<string> topLevelNames, List<string> folders)
    {
        TopLevelNames = topLevelNames;
        Folders = folders;
    }

    public IReadOnlyList<string> TopLevelNames { get; }
    public IReadOnlyList<string> Folders { get; }

    public static ReceivePlan Create(Offer offer)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = (offer.Folders ?? Array.Empty<string>())
            .Where(f => PathSafety.SanitizeRelative(f) is not null).ToList();
        foreach (var path in offer.Files.Select(f => f.Path).Concat(folders))
        {
            var first = PathSafety.SanitizeRelative(path)![0];
            if (seen.Add(first)) names.Add(first);
        }
        return new ReceivePlan(names, folders);
    }

    public Dictionary<string, string> ResolveTopLevel(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in TopLevelNames)
        {
            var candidate = PathSafety.MakeUnique(PathSafety.Resolve(root, name),
                p => File.Exists(p) || Directory.Exists(p) || File.Exists(p + ".kdpart") || map.Values.Contains(p, StringComparer.OrdinalIgnoreCase));
            map[name] = candidate;
        }
        return map;
    }

    public string Target(string root, IReadOnlyDictionary<string, string> map, string relative)
    {
        var segments = PathSafety.SanitizeRelative(relative) ?? throw new InvalidDataException("Unsafe path.");
        var top = map[segments[0]];
        var full = Path.GetFullPath(Path.Combine(new[] { top }.Concat(segments.Skip(1)).ToArray()));
        // Resolve() already confined the top-level entry; confirm the rest stays under it too.
        PathSafety.Resolve(root, Path.GetRelativePath(root, full));
        return full;
    }
}
