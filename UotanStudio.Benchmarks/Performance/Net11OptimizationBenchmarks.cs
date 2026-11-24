using BenchmarkDotNet.Attributes;
using UotanStudio.Compress;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Hash.Adler32;
using UotanStudio.Services;
using UotanStudio.Services.Logging;

namespace UotanStudio.Benchmarks.Performance;

/// <summary>Measures bounded collection construction, checksum reduction, exact-size decompression and NDJSON export.
/// <para>度量有界集合构造、校验和归约、精确长度解压与 NDJSON 导出。</para></summary>
[MemoryDiagnoser]
public class Net11Adler32Benchmarks
{
    private byte[] _data = [];

    [Params(64, 65_536)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[Length];
        new Random(42).NextBytes(_data);
    }

    /// <summary>Computes Adler-32 over a representative whole buffer.
    /// <para>在代表性整块缓冲上计算 Adler-32。</para></summary>
    [Benchmark]
    public uint Compute() => Adler32.Compute(_data);
}

/// <summary>Measures metadata construction as partition count grows.
/// <para>随分区数量增长度量元数据构造。</para></summary>
[MemoryDiagnoser]
public class Net11MetadataBenchmarks
{
    [Params(1, 256)]
    public int PartitionCount { get; set; }

    /// <summary>Builds a complete single-device metadata graph.
    /// <para>构建完整的单设备元数据图。</para></summary>
    [Benchmark]
    public int Build()
    {
        var deviceSize = (ulong)PartitionCount * 4096 + 16UL * 1024 * 1024;
        var builder = MetadataBuilder.New(deviceSize, 1024 * 1024, 2);
        for (var index = 0; index < PartitionCount; index++)
        {
            builder.AddPartition($"partition_{index}", "default", 4096);
        }

        return builder.Build().Partitions.Count;
    }
}

/// <summary>Measures whole-buffer Zstandard decode with a declared content size.
/// <para>度量声明内容尺寸的整块 Zstandard 解码。</para></summary>
[MemoryDiagnoser]
public class Net11ZstdDecompressBenchmarks
{
    private byte[] _encoded = [];

    [Params(1024, 1024 * 1024)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var payload = new byte[Length];
        new Random(42).NextBytes(payload);
        _encoded = CompressionService.Compress(payload, CompressionFormat.Zstd);
    }

    /// <summary>Decodes one complete frame into its exact-size result buffer.
    /// <para>把一个完整帧解码到精确长度的结果缓冲。</para></summary>
    [Benchmark]
    public int Decompress() => CompressionService.Decompress(_encoded, CompressionFormat.Zstd).Length;
}

/// <summary>Measures UTF-8 NDJSON snapshot export without intermediate JSON strings.
/// <para>度量不生成中间 JSON 字符串的 UTF-8 NDJSON 快照导出。</para></summary>
[MemoryDiagnoser]
public class Net11LogExportBenchmarks
{
    private LogFileRecord[] _entries = [];

    [Params(1, 256)]
    public int EntryCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _entries = new LogFileRecord[EntryCount];
        for (var index = 0; index < EntryCount; index++)
        {
            _entries[index] = new LogFileRecord(
                $"entry {index}: firmware \"system\"\n状态正常",
                LogLevel.Info,
                [new LogTextColumn($"{index:D8}", 8, 8), new LogTextColumn("Info", 4, 4)]);
        }
    }

    /// <summary>Writes one snapshot to an in-memory destination.
    /// <para>把一个快照写入内存目标。</para></summary>
    [Benchmark]
    public async Task<long> WriteAsync()
    {
        using var stream = new MemoryStream();
        await LogFileService.WriteAsync(stream, _entries, CancellationToken.None);
        return stream.Length;
    }

    private sealed record LogFileRecord(string Text, LogLevel? Level, LogTextColumn[] Columns) : ILogLine
    {
        IReadOnlyList<LogTextColumn> ILogLine.Columns => Columns;
    }
}