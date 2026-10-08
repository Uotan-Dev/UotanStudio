using BenchmarkDotNet.Attributes;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;

namespace UotanStudio.Benchmarks.Firmware;

/// <summary>
/// Measures extracting an EROFS image from a file-backed source across worker counts, which shows whether image
/// reads still serialise the parallel extraction or overlap through positional I/O.
/// <para>度量以文件数据源在不同工作者数量下解包 EROFS 镜像，用以判断镜像读取是否仍把并行解包串行化，
/// 还是已通过定位 I/O 重叠。</para>
/// </summary>
/// <remarks>
/// Run with <c>dotnet run -c Release --project UotanStudio.Benchmarks -- --filter "*ErofsExtractParallelRead*" --job short</c>.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 2)]
public class ErofsExtractParallelReadBenchmarks : IDisposable
{
    private string _root = "";
    private string _imagePath = "";
    private int _previousWorkers;

    [Params(8, 96)]
    public int Files { get; set; }

    [Params(1, 2, 4, 8)]
    public int Workers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotan-bench-erofs-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(_root, "tree");
        Directory.CreateDirectory(source);
        var random = new Random(0x5EED);
        for (var index = 0; index < Files; index++)
        {
            var payload = new byte[64 * 1024];
            random.NextBytes(payload);
            for (var i = 0; i < payload.Length / 2; i++)
            {
                payload[i] = payload[payload.Length - 1 - i];
            }

            File.WriteAllBytes(Path.Combine(source, $"blob-{index}.bin"), payload);
        }

        var image = new ErofsBuilder().Build(source, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
        });
        _imagePath = Path.Combine(_root, "image.erofs");
        File.WriteAllBytes(_imagePath, image);
        _previousWorkers = FirmwareParallelism.Workers;
        FirmwareParallelism.Workers = Workers;
    }

    [Benchmark]
    public int Extract()
    {
        var target = Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N"));
        using var stream = File.OpenRead(_imagePath);
        using var parser = new ErofsParser(stream);
        parser.ExtractToDirectory(target);
        Directory.Delete(target, true);
        return Files;
    }

    public void Dispose()
    {
        FirmwareParallelism.Workers = _previousWorkers;
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
