namespace Kirimdong.Core;

public enum TransferDirection { Send, Receive }

public enum TransferState { Waiting, Running, Done, Declined, Failed, Canceled }

/// <summary>
/// One parcel moving between two PCs. Byte counters are updated from the network thread;
/// the UI polls them. <see cref="Changed"/> fires on every state change.
/// </summary>
public sealed class Transfer
{
    private long _transferred;
    private readonly CancellationTokenSource _cts = new();

    public Transfer(TransferDirection direction, string peerName, long totalBytes, int fileCount, string summary)
    {
        Direction = direction;
        PeerName = peerName;
        TotalBytes = totalBytes;
        FileCount = fileCount;
        Summary = summary;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public TransferDirection Direction { get; }
    public string PeerName { get; }
    public long TotalBytes { get; }
    public int FileCount { get; }
    /// <summary>"foto.jpg" or "Liburan (24 file)" — what the parcel is, in a few words.</summary>
    public string Summary { get; }
    public DateTime StartedAt { get; private set; } = DateTime.Now;
    public TransferState State { get; private set; } = TransferState.Waiting;
    public string? Error { get; private set; }
    public string? CurrentFile { get; internal set; }
    /// <summary>Where received items landed (top-level files and folders).</summary>
    public IReadOnlyList<string> SavedPaths { get; internal set; } = Array.Empty<string>();
    public string? SaveFolder { get; internal set; }
    /// <summary>Device id of the other PC, when known (always for received parcels).</summary>
    public string? PeerId { get; internal set; }
    /// <summary>For parcels dropped on the sender's screen-edge portal: which edge of the sender's screen.</summary>
    public string? Edge { get; internal set; }

    public long TransferredBytes => Interlocked.Read(ref _transferred);
    public CancellationToken Token => _cts.Token;
    public bool IsActive => State is TransferState.Waiting or TransferState.Running;

    public event Action<Transfer>? Changed;

    public void Cancel()
    {
        if (IsActive) _cts.Cancel();
    }

    internal void AddBytes(long count) => Interlocked.Add(ref _transferred, count);

    internal void SetState(TransferState state, string? error = null)
    {
        if (state == TransferState.Running && State != TransferState.Running) StartedAt = DateTime.Now;
        State = state;
        Error = error;
        Changed?.Invoke(this);
    }

    /// <summary>Records how a failed transfer ended: canceled by this side, or an error.</summary>
    internal void Fail(Exception ex)
    {
        if (!IsActive) return;
        if (_cts.IsCancellationRequested) SetState(TransferState.Canceled);
        else SetState(TransferState.Failed, Describe(ex));
    }

    private static string Describe(Exception ex) => ex switch
    {
        EndOfStreamException => "Dibatalkan PC lain atau koneksi terputus.",
        IOException { InnerException: System.Net.Sockets.SocketException } => "Dibatalkan PC lain atau koneksi terputus.",
        System.Net.Sockets.SocketException => "PC tujuan tidak bisa dihubungi.",
        OperationCanceledException => "Waktu habis.",
        InvalidDataException => ex.Message,
        UnauthorizedAccessException => "Tidak punya izin menulis ke folder simpan.",
        IOException => ex.Message,
        _ => ex.Message,
    };

    public static string Describe(IReadOnlyList<string> topLevelNames, int fileCount)
    {
        if (topLevelNames.Count == 0) return "Paket kosong";
        if (topLevelNames.Count == 1)
            return fileCount <= 1 ? topLevelNames[0] : $"{topLevelNames[0]} ({fileCount} file)";
        return $"{topLevelNames[0]} + {topLevelNames.Count - 1} lainnya ({fileCount} file)";
    }
}
