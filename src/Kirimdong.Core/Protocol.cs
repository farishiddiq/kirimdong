using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kirimdong.Core;

/// <summary>
/// Wire format shared by sender and receiver.
///
/// TCP connection:  "KDNG" | version (1 byte) | kind (1 byte: 'P' ping, 'H' hello, 'O' offer)
///   ping  → receiver answers with a <see cref="Hello"/> frame and closes.
///   hello → caller sends its own <see cref="Hello"/> frame first, then gets the receiver's; both learn of each other
///           (how PCs find each other when UDP broadcasts do not get through).
///   offer → sender writes an <see cref="Offer"/> frame; receiver answers 1 byte (1 = accept, 0 = decline).
///           On accept, for every file in order: raw bytes (exactly Size) followed by its 32-byte SHA-256.
///           Receiver finishes with 1 byte (1 = everything saved and verified).
///
/// UDP discovery: one JSON <see cref="Announcement"/> per datagram.
/// Frames are a 4-byte little-endian length followed by UTF-8 JSON.
/// </summary>
public static class Protocol
{
    public const int Version = 1;
    public const int DefaultTcpPort = 47801;
    public const int DiscoveryPort = 47800;
    public const string AppTag = "kirimdong";
    public const int MaxFrameBytes = 4 * 1024 * 1024;
    public const int HashLength = 32;

    public static ReadOnlySpan<byte> Magic => "KDNG"u8;
    public const byte KindPing = (byte)'P';
    public const byte KindHello = (byte)'H';
    public const byte KindOffer = (byte)'O';

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteHeaderAsync(Stream stream, byte kind, CancellationToken ct)
    {
        var header = new byte[6];
        Magic.CopyTo(header);
        header[4] = Version;
        header[5] = kind;
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the connection header and returns its kind byte.</summary>
    public static async Task<byte> ReadHeaderAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[6];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("Not a kirimdong connection.");
        if (header[4] != Version)
            throw new InvalidDataException($"Unsupported protocol version {header[4]}.");
        return header[5];
    }

    public static async Task WriteFrameAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, body.Length);
        await stream.WriteAsync(length, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    public static async Task<T> ReadFrameAsync<T>(Stream stream, CancellationToken ct)
    {
        var lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length <= 0 || length > MaxFrameBytes)
            throw new InvalidDataException($"Invalid frame length {length}.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new InvalidDataException("Empty frame.");
    }

    public static async Task WriteByteAsync(Stream stream, byte value, CancellationToken ct)
    {
        await stream.WriteAsync(new[] { value }, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<byte> ReadByteAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer[0];
    }

    public static byte[] EncodeAnnouncement(Announcement a) => JsonSerializer.SerializeToUtf8Bytes(a, Json);

    /// <summary>Parses a discovery datagram; returns null for anything that is not a valid kirimdong announcement.</summary>
    public static Announcement? DecodeAnnouncement(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length == 0 || datagram.Length > 4096) return null;
        try
        {
            var a = JsonSerializer.Deserialize<Announcement>(datagram, Json);
            if (a is null || a.App != AppTag || a.V != Version) return null;
            if (string.IsNullOrWhiteSpace(a.Id) || a.Port is <= 0 or > 65535) return null;
            return a with { Name = CleanName(a.Name) };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Device names come from other machines: keep them printable and short.</summary>
    public static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "PC tanpa nama";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
            if (!char.IsControl(ch)) sb.Append(ch);
        var s = sb.ToString();
        return s.Length > 40 ? s[..40] : s;
    }
}

public sealed record Announcement(string App, int V, string Kind, string Id, string Name, int Port)
{
    public const string KindHello = "hi";
    public const string KindBye = "bye";
}

public sealed record Hello(string Id, string Name, int Port);

public sealed record OfferItem(string Path, long Size);

/// <param name="Edge">
/// Which edge of the sender's screen the parcel was dropped on ("left", "right", "top", "bottom"), when it came
/// through the screen-edge portal. The receiver shows it arriving from the opposite edge. Absent in 1.0.
/// </param>
public sealed record Offer(string SenderId, string SenderName, IReadOnlyList<OfferItem> Files, IReadOnlyList<string>? Folders,
    string? Edge = null)
{
    public long TotalBytes => Files.Sum(f => f.Size);
}
