using System.Reflection;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using UotanStudio.Benchmarks.Infrastructure;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Projects;
using UotanStudio.Services;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Benchmarks.Performance;

/// <summary>Production-path text search and atomic JSON save cost across input sizes.
/// <para>在不同输入规模下的文本搜索与原子 JSON 保存开销。</para></summary>
[MemoryDiagnoser]
public class TextSearchBenchmarks
{
    [Params(64 * 1024, 1024 * 1024, 8 * 1024 * 1024)] public int Length;

    private string _text = "";
    private TextSearchMatch[] _matches = [];
    private string _path = "";
    private string _root = "";

    [GlobalSetup]
    public void Setup()
    {
        _text = new string('a', Length);
        _matches = [new TextSearchMatch(Length / 2, 1, "a", "b")];
        _root = Path.Combine(Path.GetTempPath(), "uotan_bench_text_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, $"state-{Length}.json");
        AtomicJsonFile.WriteIfChangedAsync(_path, _text, MeasurementJsonContext.Default.String, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Benchmark(Baseline = true)]
    public int Apply() => TextSearchService.Apply(_text, _matches).Length;

    [Benchmark]
    public async Task<bool> SaveUnchanged() =>
        !await AtomicJsonFile.WriteIfChangedAsync(_path, _text, MeasurementJsonContext.Default.String, CancellationToken.None);
}

/// <summary>AdbSync push cost over a simulated transport (no real device).
/// <para>在模拟传输（非真实设备）上的 AdbSync 推送开销。</para></summary>
[MemoryDiagnoser]
public class AdbPushBenchmarks
{
    private byte[] _data = [];
    private FakeAdbDeviceTransport _transport = null!;
    private AdbSession _adb = null!;
    private IAdbSyncClient _sync = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[8 * 1024 * 1024];
        new Random(42).NextBytes(_data);
        _transport = new FakeAdbDeviceTransport();
        _adb = new AdbSession(_transport, AdbAuthentication.CreateNew());
        _adb.Connect();
        _sync = _adb.CreateSyncClient();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _adb.Dispose();
        _transport.Dispose();
    }

    [Benchmark]
    public async Task<int> PushStream()
    {
        _transport.WrtePayloads.Clear();
        using var source = new MemoryStream(_data, writable: false);
        await _sync.PushStreamAsync(source, "/simulation/perf.bin", mtime: 1);
        return _transport.WrtePayloads.Count;
    }
}

/// <summary>Workspace log burst enqueue plus first flush cost on the headless UI dispatcher.
/// <para>在无头 UI 调度器上工作区日志突发入队与首次刷写的开销。</para></summary>
[MemoryDiagnoser]
public class LogBurstBenchmarks
{
    private NodeEditorUiFixture _fixture = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = new NodeEditorUiFixture();
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _fixture.DisposeAsync();

    [Benchmark]
    public async Task<int> Burst()
    {
        var count = 0;
        await _fixture.DispatchAsync(async () =>
        {
            var flush = typeof(WorkspaceLogViewModel).GetMethod("FlushPending", BindingFlags.Instance | BindingFlags.NonPublic)!;
            using var model = new WorkspaceLogViewModel();
            model.Clear();
            for (var i = 0; i < 100_000; i++)
            {
                LogService.Instance.Write(LogLevel.Info, "Firmware", "performance log entry");
            }
            flush.Invoke(model, null);
            count = model.Entries.Count;
            LogService.Instance.Clear();
            await Task.CompletedTask;
        }, CancellationToken.None);
        return count;
    }
}

[JsonSerializable(typeof(string))]
internal partial class MeasurementJsonContext : JsonSerializerContext;