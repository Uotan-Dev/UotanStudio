using System.Diagnostics;
using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Performance;

[Collection("Node editor UI")]
public sealed class FirmwareMeasurements(ITestOutputHelper output)
{
    [Fact(Skip = "Set UOTAN_PERF=1 and run alone in Release.", SkipType = typeof(OptimizationMeasurements), SkipUnless = nameof(OptimizationMeasurements.Enabled))]
    public async Task Payload_ConcurrentBuilds_ReportResourceScaling()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var input = Path.Combine(temp.Path, "input.img");
        var bytes = new byte[8 * 1024 * 1024];
        new Random(42).NextBytes(bytes);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] &= 15;
        }
        await File.WriteAllBytesAsync(input, bytes, token);
        output.WriteLine($"Input=8MiB, seed42, low-nibble; SHA256={Convert.ToHexString(SHA256.HashData(bytes))}");
        string? expected = null;
        foreach (var builds in new[] { 1, 2 })
        {
            foreach (var workers in new[] { 1, 2, 4, 8 })
            {
                var path = Path.Combine(temp.Path, $"payload-{builds}-{workers}");
                long peakManaged = 0;
                using var monitor = CancellationTokenSource.CreateLinkedTokenSource(token);
                var sampling = Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
                            await Task.Delay(10, monitor.Token);
                        }
                    }
                    catch (OperationCanceledException) when (monitor.IsCancellationRequested) { }
                }, token);
                var before = GC.GetTotalAllocatedBytes(true);
                var clock = Stopwatch.StartNew();
                try
                {
                    await Task.WhenAll(Enumerable.Range(0, builds).Select(async index =>
                    {
                        using var source = File.OpenRead(input);
                        using var builder = new PayloadBuilder().WithCompressionWorkers(workers);
                        builder.AddPartitionCompressed("system", source, CompressionType.BZip2);
                        await builder.BuildAsync(path + index, token);
                    }));
                }
                finally
                {
                    monitor.Cancel();
                    await sampling;
                }
                output.WriteLine($"builds={builds}; workers={workers}; ms={clock.Elapsed.TotalMilliseconds:F1}; allocated={GC.GetTotalAllocatedBytes(true) - before}; sampledManagedPeak={peakManaged}");
                for (var i = 0; i < builds; i++)
                {
                    using var result = File.OpenRead(path + i);
                    var hash = Convert.ToHexString(SHA256.HashData(result));
                    expected ??= hash;
                    Assert.Equal(expected, hash);
                }
            }
        }
    }
}