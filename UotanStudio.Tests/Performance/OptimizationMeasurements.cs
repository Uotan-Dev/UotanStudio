using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Serialization;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Workspace;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Views;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Performance;

/// <summary>Opt-in production-path measurements; run alone in Release, never as timing assertions.</summary>
[Collection("Node editor UI")]
public sealed class OptimizationMeasurements(NodeEditorUiFixture fixture, ITestOutputHelper output)
{
    public static bool Enabled => Environment.GetEnvironmentVariable("UOTAN_PERF") == "1";

    [Fact(Skip = "Set UOTAN_PERF=1 and run alone in Release to measure production paths.", SkipUnless = nameof(Enabled))]
    public async Task ProductionPaths_FixedInputs_ReportMeasurements()
    {
        output.WriteLine($"Runtime={Environment.Version}; OS={Environment.OSVersion}; CPUs={Environment.ProcessorCount}; mode=Release JIT; iterations=7 after warmup");
        using var directory = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        foreach (var length in new[] { 64 * 1024, 1024 * 1024, 8 * 1024 * 1024 })
        {
            var text = new string('a', length);
            TextSearchMatch[] matches = [new(length / 2, 1, "a", "b")];
            await MeasureAsync($"replace-{length}", () =>
            {
                var actual = TextSearchService.Apply(text, matches);
                Assert.Equal(length, actual.Length);
                Assert.Equal('b', actual[length / 2]);
                return Task.CompletedTask;
            });
            var path = Path.Combine(directory.Path, $"state-{length}.json");
            await AtomicJsonFile.WriteIfChangedAsync(path, text, MeasurementJsonContext.Default.String, token);
            await MeasureAsync($"save-unchanged-{length}", async () =>
            {
                Assert.False(await AtomicJsonFile.WriteIfChangedAsync(path, text, MeasurementJsonContext.Default.String, token));
            });
        }

        var data = new byte[8 * 1024 * 1024];
        new Random(42).NextBytes(data);
        using var transport = new FakeAdbDeviceTransport();
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        adb.Connect();
        using var sync = adb.CreateSyncClient();
        await MeasureAsync("adb-push-8MiB-including-fake-transport", () =>
        {
            transport.WrtePayloads.Clear();
            using var source = new MemoryStream(data, writable: false);
            sync.PushStream(source, "/simulation/perf.bin", mtime: 1);
            Assert.Equal(data.Length, transport.WrtePayloads.Where(p => p.AsSpan().StartsWith("DATA"u8)).Sum(p => p.Length - 8));
            return Task.CompletedTask;
        });

        await fixture.Session.Dispatch(() =>
        {
            // Reflection is confined to this baseline harness so the unmodified private method can be measured.
            var flush = typeof(WorkspaceLogViewModel).GetMethod("FlushPending", BindingFlags.Instance | BindingFlags.NonPublic)!;
            using var model = new WorkspaceLogViewModel();
            model.Clear();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < 100_000; i++)
            {
                LogService.Instance.Write(LogLevel.Info, "Firmware", "performance log entry");
            }
            var enqueue = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            flush.Invoke(model, null);
            output.WriteLine($"log-burst-100000: enqueueMs={enqueue:F3}; firstFlushMs={clock.Elapsed.TotalMilliseconds:F3}; allocated={GC.GetAllocatedBytesForCurrentThread() - before}; displayed={model.Entries.Count}");
            LogService.Instance.Clear();
        }, token);
    }

    private async Task MeasureAsync(string name, Func<Task> action)
    {
        await action();
        var times = new List<double>();
        var allocations = new List<long>();
        for (var i = 0; i < 7; i++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var clock = Stopwatch.StartNew();
            await action();
            times.Add(clock.Elapsed.TotalMilliseconds);
            allocations.Add(GC.GetTotalAllocatedBytes(precise: true) - before);
        }
        times.Sort();
        allocations.Sort();
        output.WriteLine($"{name}: medianMs={times[3]:F3}; minMs={times[0]:F3}; maxMs={times[^1]:F3}; medianAllocated={allocations[3]}");
    }
}

[JsonSerializable(typeof(string))]
internal partial class MeasurementJsonContext : JsonSerializerContext;