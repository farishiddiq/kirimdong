using System.Diagnostics;
using System.Net;
using Kirimdong.Core;
using Xunit.Abstractions;

namespace Kirimdong.Core.Tests;

/// <summary>
/// Loopback throughput, sender and receiver in one process. Not part of the normal run:
///   dotnet test -c Release --filter Category=Benchmark --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class Benchmark(ITestOutputHelper output) : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "kirimdong-bench", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { }
    }

    [Fact]
    public async Task LoopbackThroughput()
    {
        const long size = 768L * 1024 * 1024;
        var outbox = Directory.CreateDirectory(Path.Combine(_work, "out")).FullName;
        var source = Path.Combine(outbox, "besar.bin");
        await using (var f = File.Create(source))
        {
            var chunk = new byte[4 * 1024 * 1024];
            new Random(1).NextBytes(chunk);
            for (long written = 0; written < size; written += chunk.Length) await f.WriteAsync(chunk);
        }

        using var server = new TransferServer(() => new Hello("r", "r", 0), () => Path.Combine(_work, "in"), (_, _) => Task.FromResult(true));
        server.Start(0);
        var batch = FileCollector.Collect(new[] { source });
        var t = TransferClient.CreateTransfer("r", batch);
        var sw = Stopwatch.StartNew();
        await TransferClient.SendAsync(new IPEndPoint(IPAddress.Loopback, server.Port), new Hello("s", "s", 0), batch, t);
        sw.Stop();

        Assert.Equal(TransferState.Done, t.State);
        output.WriteLine($"{size / 1024.0 / 1024 / sw.Elapsed.TotalSeconds:0} MB/s ({sw.Elapsed.TotalSeconds:0.00} s for {size / 1024 / 1024} MB)");
    }
}
