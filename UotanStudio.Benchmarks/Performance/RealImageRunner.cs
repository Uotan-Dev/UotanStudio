using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using UotanStudio.Benchmarks.Infrastructure;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;

namespace UotanStudio.Benchmarks.Performance;

/// <summary>
/// Measures a real EROFS image end to end: what the image declares, how long the parser needs to walk and
/// extract it, and how long the builder needs to pack the extracted tree back into an image — each phase run
/// once with one worker and once with the machine's default worker count, so the parallel speedup is measured
/// rather than assumed.
/// <para>对一个真实 EROFS 镜像做端到端测量：镜像声明了什么、解析器遍历与解出它需要多久、打包器把解出的目录树
/// 重新打包成镜像需要多久——每个阶段分别以 1 个工作者与机器默认工作者数各跑一次，使并行加速比是被测出来的而不是
/// 假设出来的。</para>
/// </summary>
internal static class RealImageRunner
{
    private static bool CompressDisabled;

    /// <summary>Dispatches one <c>--image-run</c> invocation.
    /// <para>派发一次 <c>--image-run</c> 调用。</para></summary>
    /// <param name="args">Raw command-line arguments. <para>原始命令行参数。</para></param>
    /// <returns>Zero when every requested phase reported. <para>所有请求的阶段都完成报告时为零。</para></returns>
    internal static int Run(string[] args)
    {
        var image = Value(args, "--image")
            ?? throw new InvalidOperationException("--image <path> is required.");
        if (!File.Exists(image))
        {
            throw new FileNotFoundException("Image not found.", image);
        }

        var work = Value(args, "--work") ?? Path.Combine(Path.GetTempPath(), "uotan-real-image");
        var mode = Value(args, "--image-run") ?? "facts";
        var level = int.Parse(Value(args, "--level") ?? "1", CultureInfo.InvariantCulture);
        var repeats = int.Parse(Value(args, "--repeats") ?? "1", CultureInfo.InvariantCulture);
        CompressDisabled = args.Contains("--no-compress", StringComparer.Ordinal);
        Directory.CreateDirectory(work);

        var facts = ReadFacts(image);
        PrintFacts(image, facts);
        if (mode == "facts")
        {
            return 0;
        }

        var workerCounts = Value(args, "--worker-counts") is { } list
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).Distinct().ToArray()
            : [1, FirmwareParallelism.DefaultWorkers];
        var workers = workerCounts;
        if (mode is "read-parallel")
        {
            ReadAllParallel(image, repeats, workers);
            return 0;
        }

        if (mode is "read" or "all")
        {
            ReadAll(image, repeats);
        }

        if (mode is "extract" or "all")
        {
            foreach (var worker in workers)
            {
                Extract(image, Path.Combine(work, FormattableString.Invariant($"extract-w{worker}")), worker, repeats);
            }
        }

        if (mode is "compare")
        {
            Compare(image, Value(args, "--rebuilt")
                ?? throw new InvalidOperationException("--rebuilt <path> is required for compare."));
            return 0;
        }

        if (mode is "pack" or "all")
        {
            var source = Path.Combine(work, FormattableString.Invariant($"extract-w{workers[^1]}"));
            if (!Directory.Exists(source))
            {
                throw new DirectoryNotFoundException(
                    $"Pack needs an extracted tree; run --image-run extract first (expected '{source}').");
            }

            foreach (var worker in workers)
            {
                var output = Path.Combine(work, FormattableString.Invariant($"rebuilt-w{worker}.img"));
                Pack(source, output, facts, level, worker, repeats);
                Compare(image, output);
            }
        }

        return 0;
    }

    private sealed record ImageFacts(long Length, int BlockSize, ushort Algorithms, ulong Inodes, ulong Blocks,
        uint Incompat, uint Compat, string Algorithm);

    private static ImageFacts ReadFacts(string image)
    {
        using var stream = File.OpenRead(image);
        var head = new byte[4096];
        stream.ReadExactly(head, 0, head.Length);
        var superblock = ErofsSuperblock.FromImage(head);
        var blockSize = 1 << superblock.BlockSizeBits;
        var algorithms = superblock.AvailableComprAlgs;
        var algorithm = DescribeAlgorithms(algorithms);
        return new ImageFacts(stream.Length, blockSize, algorithms, superblock.Inos, superblock.Blocks,
            superblock.FeatureIncompat, superblock.FeatureCompat, algorithm);
    }

    private static string DescribeAlgorithms(ushort algorithms)
    {
        var names = new List<string>();
        if ((algorithms & 1) != 0) names.Add("lz4");
        if ((algorithms & 2) != 0) names.Add("lzma");
        if ((algorithms & 4) != 0) names.Add("deflate");
        if ((algorithms & 8) != 0) names.Add("zstd");
        return names.Count == 0 ? "none" : string.Join("+", names);
    }

    private static ErofsCompressionAlgorithm PickAlgorithm(ushort algorithms)
        => (algorithms & 1) != 0 ? ErofsCompressionAlgorithm.Lz4
            : (algorithms & 8) != 0 ? ErofsCompressionAlgorithm.Zstd
            : (algorithms & 2) != 0 ? ErofsCompressionAlgorithm.Lzma
            : (algorithms & 4) != 0 ? ErofsCompressionAlgorithm.Deflate
            : ErofsCompressionAlgorithm.Lz4;

    private static void PrintFacts(string image, ImageFacts facts)
    {
        Console.WriteLine($"image           {image}");
        Console.WriteLine($"size            {facts.Length / (1024.0 * 1024.0):F1} MiB ({facts.Length} bytes)");
        Console.WriteLine($"block size      {facts.BlockSize} bytes");
        Console.WriteLine($"compression     {facts.Algorithm} (compr algs 0x{facts.Algorithms:X4})");
        Console.WriteLine($"inodes/blocks   {facts.Inodes} / {facts.Blocks}");
        Console.WriteLine($"features        incompat=0x{facts.Incompat:X8} compat=0x{facts.Compat:X8}");
        Console.WriteLine($"workers         default={FirmwareParallelism.DefaultWorkers} max={FirmwareParallelism.MaximumWorkers}");
    }

    private static void ReadAll(string image, int repeats)
    {
        for (var repeat = 1; repeat <= repeats; repeat++)
        {
            long bytes = 0;
            var files = 0;
            using var stream = File.OpenRead(image);
            using var parser = new ErofsParser(stream);
            var entries = parser.EnumerateFiles();
            var profile = RuntimeProfile.Start();
            var watch = Stopwatch.StartNew();
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            foreach (var entry in entries)
            {
                if (entry.IsSymlink || entry.IsSpecialFile)
                {
                    continue;
                }

                bytes += parser.ReadFileBytes(entry.Path).Length;
                files++;
            }

            watch.Stop();
            var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
            var report = profile.Stop();
            Console.WriteLine();
            Console.WriteLine($"read            single-thread repeat={repeat}  (decompress only, no file writes)");
            Console.WriteLine($"  entries       {files}");
            Console.WriteLine($"  bytes decoded {bytes / (1024.0 * 1024.0):F1} MiB");
            Console.WriteLine($"  wall          {watch.Elapsed.TotalMilliseconds,10:F1} ms  ({Throughput(bytes, watch.Elapsed):F1} MiB/s)");
            Console.WriteLine($"  cpu           {cpu.TotalMilliseconds,10:F1} ms  ({CpuShare(cpu, watch.Elapsed):F1}% of {Environment.ProcessorCount} cores)");
            Console.WriteLine($"  allocated     {report.AllocatedBytes / (1024.0 * 1024.0),10:F1} MiB");
            Console.WriteLine($"  stw pause     {report.Pause.TotalMilliseconds,10:F1} ms in {report.PauseCount} suspensions");
            Console.WriteLine($"  collections   gen0={report.Gen0} gen1={report.Gen1} gen2={report.Gen2}");
        }
    }

    private static void ReadAllParallel(string image, int repeats, int[] workerCounts)
    {
        foreach (var workers in workerCounts)
        {
            for (var repeat = 1; repeat <= repeats; repeat++)
            {
                long bytes = 0;
                var files = 0;
                int[]? entryCounts = null;
                var profile = RuntimeProfile.Start();
                var watch = Stopwatch.StartNew();
                var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
                var parsers = new ErofsParser[workers];
                var streams = new FileStream[workers];
                try
                {
                    var manifest = new List<(string Path, long Size)>();
                    using (var probe = new ErofsParser(File.OpenRead(image)))
                    {
                        foreach (var entry in probe.EnumerateFiles())
                        {
                            if (!entry.IsSymlink && !entry.IsSpecialFile)
                            {
                                manifest.Add((entry.Path, entry.Size));
                            }
                        }
                    }

                    entryCounts = new int[workers];
                    for (var index = 0; index < manifest.Count; index++)
                    {
                        entryCounts[index % workers]++;
                    }

                    for (var worker = 0; worker < workers; worker++)
                    {
                        streams[worker] = File.OpenRead(image);
                        parsers[worker] = new ErofsParser(streams[worker]);
                    }

                    Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
                    {
                        long localBytes = 0;
                        var localFiles = 0;
                        for (var index = worker; index < manifest.Count; index += workers)
                        {
                            localBytes += parsers[worker].ReadFileBytes(manifest[index].Path).Length;
                            localFiles++;
                        }

                        Interlocked.Add(ref bytes, localBytes);
                        Interlocked.Add(ref files, localFiles);
                    });
                }
                finally
                {
                    foreach (var parser in parsers)
                    {
                        parser?.Dispose();
                    }

                    foreach (var stream in streams)
                    {
                        stream?.Dispose();
                    }
                }

                watch.Stop();
                var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
                var report = profile.Stop();
                Console.WriteLine();
                Console.WriteLine($"read-parallel   workers={workers} repeat={repeat}  ({entryCounts?.Sum() ?? 0} files, one parser per worker)");
                Console.WriteLine($"  bytes decoded {bytes / (1024.0 * 1024.0):F1} MiB in {files} files");
                Console.WriteLine($"  wall          {watch.Elapsed.TotalMilliseconds,10:F1} ms  ({Throughput(bytes, watch.Elapsed):F1} MiB/s)");
                Console.WriteLine($"  cpu           {cpu.TotalMilliseconds,10:F1} ms  ({CpuShare(cpu, watch.Elapsed):F1}% of {Environment.ProcessorCount} cores)");
                Console.WriteLine($"  allocated     {report.AllocatedBytes / (1024.0 * 1024.0),10:F1} MiB");
                Console.WriteLine($"  stw pause     {report.Pause.TotalMilliseconds,10:F1} ms in {report.PauseCount} suspensions");
            }
        }
    }

    private static void Extract(string image, string target, int workers, int repeats)
    {
        for (var repeat = 1; repeat <= repeats; repeat++)
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.CreateDirectory(target);
            FirmwareParallelism.Workers = workers;
            long bytes = 0;
            var files = 0;
            using var stream = File.OpenRead(image);
            using var parser = new ErofsParser(stream);
            var profile = RuntimeProfile.Start();
            var watch = Stopwatch.StartNew();
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            parser.ExtractToDirectory(target, byteProgress: null, CancellationToken.None, overwrite: true,
                progress: new InlineProgress<(int processed, int total)>(p => (files, bytes) = (p.total, 0)));
            watch.Stop();
            var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
            var report = profile.Stop();
            var extracted = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            Console.WriteLine();
            Console.WriteLine($"extract         workers={workers} repeat={repeat}");
            Console.WriteLine($"  entries       {files}");
            Console.WriteLine($"  bytes written {extracted / (1024.0 * 1024.0):F1} MiB");
            Console.WriteLine($"  wall          {watch.Elapsed.TotalMilliseconds,10:F1} ms  ({Throughput(extracted, watch.Elapsed):F1} MiB/s)");
            Console.WriteLine($"  cpu           {cpu.TotalMilliseconds,10:F1} ms  ({CpuShare(cpu, watch.Elapsed):F1}% of {Environment.ProcessorCount} cores)");
            Console.WriteLine($"  allocated     {report.AllocatedBytes / (1024.0 * 1024.0),10:F1} MiB");
            Console.WriteLine($"  stw pause     {report.Pause.TotalMilliseconds,10:F1} ms in {report.PauseCount} suspensions");
            Console.WriteLine($"  collections   gen0={report.Gen0} gen1={report.Gen1} gen2={report.Gen2}");
        }

        FirmwareParallelism.Workers = FirmwareParallelism.DefaultWorkers;
    }

    private static void Pack(string source, string output, ImageFacts facts, int level, int workers, int repeats)
    {
        var options = new ErofsOptions
        {
            BlockSize = facts.BlockSize,
            Algorithm = PickAlgorithm(facts.Algorithms),
            CompressionLevel = level,
            Uuid = new byte[16],
            BuildTime = 1700000000,
            Compress = !CompressDisabled,
        };

        for (var repeat = 1; repeat <= repeats; repeat++)
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }

            var sourceBytes = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
            FirmwareParallelism.Workers = workers;
            var profile = RuntimeProfile.Start();
            var watch = Stopwatch.StartNew();
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            new ErofsBuilder().Build(source, output, options);
            watch.Stop();
            var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
            var report = profile.Stop();
            var produced = new FileInfo(output).Length;
            Console.WriteLine();
            Console.WriteLine($"pack            workers={workers} repeat={repeat}  algo={options.Algorithm} block={options.BlockSize} level={level}");
            Console.WriteLine($"  source bytes  {sourceBytes / (1024.0 * 1024.0):F1} MiB");
            Console.WriteLine($"  image bytes   {produced / (1024.0 * 1024.0):F1} MiB  ({(double)produced / Math.Max(1, sourceBytes) * 100:F1}%)");
            Console.WriteLine($"  wall          {watch.Elapsed.TotalMilliseconds,10:F1} ms  ({Throughput(sourceBytes, watch.Elapsed):F1} MiB/s)");
            Console.WriteLine($"  cpu           {cpu.TotalMilliseconds,10:F1} ms  ({CpuShare(cpu, watch.Elapsed):F1}% of {Environment.ProcessorCount} cores)");
            Console.WriteLine($"  allocated     {report.AllocatedBytes / (1024.0 * 1024.0),10:F1} MiB");
            Console.WriteLine($"  stw pause     {report.Pause.TotalMilliseconds,10:F1} ms in {report.PauseCount} suspensions");
            Console.WriteLine($"  collections   gen0={report.Gen0} gen1={report.Gen1} gen2={report.Gen2}");
        }

        FirmwareParallelism.Workers = FirmwareParallelism.DefaultWorkers;
    }

    private static void Compare(string sourceImage, string rebuiltImage)
    {
        var source = ReadManifest(sourceImage);
        var rebuilt = ReadManifest(rebuiltImage);
        var missingPaths = source.Keys.Except(rebuilt.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var extraPaths = rebuilt.Keys.Except(source.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var differing = source.Where(pair => rebuilt.TryGetValue(pair.Key, out var size) && size != pair.Value).Count();
        Console.WriteLine($"  compare       entries {source.Count} -> {rebuilt.Count}, missing={missingPaths.Length}, extra={extraPaths.Length}, size-differing={differing}");
        foreach (var path in missingPaths.Take(6))
        {
            Console.WriteLine($"    missing     {path}");
        }

        foreach (var path in extraPaths.Take(6))
        {
            Console.WriteLine($"    extra       {path}");
        }
        Console.WriteLine($"  content hash  {SampleDigest(rebuiltImage)}");
    }

    private static Dictionary<string, long> ReadManifest(string image)
    {
        using var stream = File.OpenRead(image);
        using var parser = new ErofsParser(stream);
        var manifest = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in parser.EnumerateFiles())
        {
            manifest.TryAdd(entry.Path, entry.Size);
        }

        return manifest;
    }

    private static string SampleDigest(string image)
    {
        using var stream = File.OpenRead(image);
        using var parser = new ErofsParser(stream);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sampled = 0;
        foreach (var entry in parser.EnumerateFiles().OrderBy(entry => entry.Path, StringComparer.Ordinal).Take(64))
        {
            if (entry.IsSymlink || entry.IsSpecialFile)
            {
                continue;
            }

            var bytes = parser.ReadFileBytes(entry.Path);
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(entry.Path));
            hash.AppendData(bytes);
            sampled++;
        }

        return $"SHA256({sampled} files) {Convert.ToHexString(hash.GetHashAndReset())[..32]}…";
    }

    private static double Throughput(long bytes, TimeSpan elapsed)
        => elapsed.TotalSeconds <= 0 ? 0 : bytes / (1024.0 * 1024.0) / elapsed.TotalSeconds;

    private static double CpuShare(TimeSpan cpu, TimeSpan wall)
        => wall.TotalMilliseconds <= 0 ? 0 : cpu.TotalMilliseconds / wall.TotalMilliseconds / Environment.ProcessorCount * 100;

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
