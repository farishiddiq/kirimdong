using System.Security.Cryptography;

namespace Kirimdong.Core;

/// <summary>
/// Copies exactly <c>length</c> bytes with two buffers in flight: the next block is read while the
/// current one is hashed and written, so disk and network work at the same time instead of taking turns.
/// </summary>
internal sealed class Pump
{
    public const int BlockSize = 2 * 1024 * 1024;

    private readonly byte[][] _buffers = { new byte[BlockSize], new byte[BlockSize] };

    public async Task CopyAsync(Stream source, Stream destination, long length, IncrementalHash hash,
        Action<int> progress, CancellationToken ct)
    {
        if (length == 0) return;
        var current = 0;
        var remaining = length;
        var pending = ReadAsync(source, _buffers[current], remaining, ct);
        while (true)
        {
            var read = await pending.ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            remaining -= read;
            var next = remaining > 0 ? ReadAsync(source, _buffers[1 - current], remaining, ct) : null;
            hash.AppendData(_buffers[current], 0, read);
            await destination.WriteAsync(_buffers[current].AsMemory(0, read), ct).ConfigureAwait(false);
            progress(read);
            if (next is null) return;
            current = 1 - current;
            pending = next;
        }
    }

    /// <summary>Fills as much of the buffer as one block allows (several socket reads may be needed).</summary>
    private static async Task<int> ReadAsync(Stream source, byte[] buffer, long remaining, CancellationToken ct)
    {
        var want = (int)Math.Min(buffer.Length, remaining);
        var filled = 0;
        while (filled < want)
        {
            var n = await source.ReadAsync(buffer.AsMemory(filled, want - filled), ct).ConfigureAwait(false);
            if (n == 0) break;
            filled += n;
        }
        return filled;
    }
}
