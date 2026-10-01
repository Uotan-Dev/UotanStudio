using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;

namespace UotanStudio.Benchmarks.Firmware;

/// <summary>Payload compression throughput across build concurrency and worker counts.</summary>
[MemoryDiagnoser]
public class PayloadBuildBenchmarks
{
    [Params(1, 2)] public int Builds;
    [Params(1, 2, 4, 8)] public int Workers;

    private string _root = "";
    private string _input = "";

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotan_bench_build_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _input = Path.Combine(_root, "input.img");
        var bytes = new byte[8 * 1024 * 1024];
        new Random(42).NextBytes(bytes);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] &= 15;
        }
        File.WriteAllBytes(_input, bytes);
    }

    [GlobalCleanup]
    public void Cleanup() => TryDeleteRoot();

    [Benchmark]
    public async Task<string> BuildConcurrent()
    {
        var paths = new string[Builds];
        await Task.WhenAll(Enumerable.Range(0, Builds).Select(async index =>
        {
            var path = Path.Combine(_root, $"payload-{index}");
            using var source = File.OpenRead(_input);
            using var builder = new PayloadBuilder().WithCompressionWorkers(Workers);
            builder.AddPartitionCompressed("system", source, CompressionType.BZip2);
            await builder.BuildAsync(path, CancellationToken.None);
            paths[index] = path;
        }));
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths[0])));
    }

    private void TryDeleteRoot()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Raw-payload ODM extraction throughput across parallelism settings; requires UOTAN_PERF_RAW.</summary>
[MemoryDiagnoser]
public class PayloadExtractBenchmarks
{
    [Params(1, 2, 4, 8)] public int Workers;

    private FileStream _stream = null!;
    private PartitionUpdate _odm = null!;
    private ulong _baseOffset;
    private uint _blockSize;
    private string _outDir = "";
    private string _outPath = "";
    private int _previousWorkers;

    [GlobalSetup]
    public void Setup()
    {
        var payloadPath = Environment.GetEnvironmentVariable("UOTAN_PERF_RAW");
        if (string.IsNullOrWhiteSpace(payloadPath))
        {
            throw new InvalidOperationException("Set UOTAN_PERF_RAW to a raw payload image path to run this benchmark.");
        }

        _previousWorkers = FirmwareParallelism.Workers;
        FirmwareParallelism.Workers = Workers;
        _stream = File.OpenRead(payloadPath);
        var (manifest, header) = PayloadParser.ReadManifest(_stream);
        _odm = manifest.Partitions.Single(p => p.PartitionName == "odm");
        _baseOffset = header.BaseOffset;
        _blockSize = manifest.BlockSize;
        _outDir = Path.Combine(Path.GetTempPath(), "uotan_bench_extract_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outDir);
        _outPath = Path.Combine(_outDir, "odm.img");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        FirmwareParallelism.Workers = _previousWorkers;
        _stream.Dispose();
        TryDelete(_outDir);
    }

    [Benchmark]
    public async Task<long> ExtractOdm()
    {
        _stream.Position = 0;
        await new PayloadExtractor().ExtractPartitionAsync(_odm, _outPath, _stream, _baseOffset, _blockSize, CancellationToken.None);
        return new FileInfo(_outPath).Length;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>ReplaceXz operation decoding across a sampled window; requires UOTAN_PERF_RAW.</summary>
[MemoryDiagnoser]
public class OperationProcessorBenchmarks
{
    private FileStream _stream = null!;
    private List<InstallOperation> _ops = [];
    private ulong _baseOffset;
    private uint _blockSize;
    private string _outDir = "";
    private string _outPath = "";

    [GlobalSetup]
    public void Setup()
    {
        var payloadPath = Environment.GetEnvironmentVariable("UOTAN_PERF_RAW");
        if (string.IsNullOrWhiteSpace(payloadPath))
        {
            throw new InvalidOperationException("Set UOTAN_PERF_RAW to a raw payload image path to run this benchmark.");
        }

        _stream = File.OpenRead(payloadPath);
        var (manifest, header) = PayloadParser.ReadManifest(_stream);
        _baseOffset = header.BaseOffset;
        _blockSize = manifest.BlockSize;
        var odm = manifest.Partitions.Single(p => p.PartitionName == "odm");
        _ops = odm.Operations
            .Where(o => o.Type == InstallOperation.Types.Type.ReplaceXz)
            .OrderBy(o => o.DataOffset)
            .TakeLast(400)
            .ToList();
        _outDir = Path.Combine(Path.GetTempPath(), "uotan_bench_ops_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outDir);
        _outPath = Path.Combine(_outDir, "out.img");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _stream.Dispose();
        try
        {
            Directory.Delete(_outDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Benchmark]
    public long ProcessSampledOperations()
    {
        var totalData = 0L;
        foreach (var op in _ops)
        {
            using var outFile = File.Create(_outPath);
            OperationProcessor.ProcessOperation(op, outFile, _stream, _baseOffset, _blockSize, new(), new(), CancellationToken.None);
            totalData += (long)op.DataLength;
        }
        return totalData;
    }
}

/// <summary>Whole-archive OTA zip extraction; requires UOTAN_PERF_ZIP.</summary>
[MemoryDiagnoser]
public class OtaZipExtractBenchmarks
{
    private string _zipPath = "";
    private string _outDir = "";

    [GlobalSetup]
    public void Setup()
    {
        _zipPath = Environment.GetEnvironmentVariable("UOTAN_PERF_ZIP")
            ?? throw new InvalidOperationException("Set UOTAN_PERF_ZIP to an OTA zip path to run this benchmark.");
        _outDir = Path.Combine(Path.GetTempPath(), "uotan_bench_zip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outDir);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_outDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Benchmark]
    public long ExtractAllPartitions()
    {
        var metadata = PayloadParser.GetPayloadMetadata(_zipPath);
        foreach (var name in metadata.PartitionNames)
        {
            var dir = Path.Combine(_outDir, name);
            Directory.CreateDirectory(dir);
            PayloadParser.Extract(_zipPath, [name], dir);
        }
        return Directory.EnumerateFiles(_outDir, "*.img", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
    }
}
