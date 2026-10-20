using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UotanStudio.Benchmarks.Infrastructure;
using UotanStudio.Benchmarks.Inputs;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;

namespace UotanStudio.Benchmarks.Performance;

/// <summary>
/// Runs one named end-to-end workload under <see cref="RuntimeProfile"/> and prints the pause, collection,
/// allocation and CPU figures, so a before/after pair for one change is reproducible from the command line.
/// <para>在 <see cref="RuntimeProfile"/> 下运行一个命名的端到端工作负载，并打印停顿、回收、分配与 CPU 数值，
/// 使某次改动的前后对照可以从命令行复现。</para>
/// <para>BenchmarkDotNet answers "how fast and how many bytes per operation"; this runner answers the two
/// questions its diagnoser cannot: how long the mutator actually stalled, and which types the garbage came
/// from. Both are needed before a change that trades allocation for reuse can be called an improvement.</para>
/// <para>BenchmarkDotNet 回答「每次操作多快、多少字节」；本运行器回答它诊断器无法回答的两个问题：应用线程实际
/// 停顿了多久，以及垃圾来自哪些类型。把「用复用换分配」称为改进之前，这两个问题都必须有答案。</para>
/// </summary>
internal static class ProfileRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Dispatches one <c>--profile &lt;workload&gt;</c> invocation.
    /// <para>派发一次 <c>--profile &lt;workload&gt;</c> 调用。</para></summary>
    /// <param name="args">Raw command-line arguments after the switch. <para>开关之后的原始命令行参数。</para></param>
    /// <returns>Zero when the workload ran and reported. <para>工作负载成功运行并报告时为零。</para></returns>
    /// <exception cref="InvalidOperationException">The named workload does not exist.
    /// <para>所命名的工作负载不存在。</para></exception>
    internal static int Run(string[] args)
    {
        var workload = Value(args, "--workload") ?? "erofs-zstd-pack";
        var repeats = int.Parse(Value(args, "--repeats") ?? "3", CultureInfo.InvariantCulture);
        var files = int.Parse(Value(args, "--files") ?? "384");
        var megabytes = int.Parse(Value(args, "--megabytes") ?? "4");

        var root = Path.Combine(Path.GetTempPath(), "uotan-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var report = Execute(workload, root, repeats, files, megabytes);
            Write(report, args);
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static ProfileReport Execute(string workload, string root, int repeats, int files, int megabytes)
        => workload.StartsWith("ext4", StringComparison.Ordinal)
            ? ExecuteExt4(workload, root, repeats, files, megabytes)
            : ExecuteErofs(workload, root, repeats, files, megabytes);

    private static ProfileReport ExecuteExt4(string workload, string root, int repeats, int files, int megabytes)
    {
        var source = BuildTree(Path.Combine(root, "tree"), files, megabytes);
        var options = new Ext4Options
        {
            Uuid = new byte[16],
            VolumeName = "UOTAN-BENCH",
            IncludeJournal = false,
        };

        var image = Path.Combine(root, "image.ext4");
        Ext4Builder.Build(source, image, options);
        Console.WriteLine($"image sha256    {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image)))}");

        var runCount = 0;
        Action run = () =>
        {
            runCount++;
            Ext4Builder.Build(source, Path.Combine(root, FormattableString.Invariant($"rebuild-{runCount}.ext4")), options);
        };

        return Measure(run, repeats);
    }

    private static ProfileReport ExecuteErofs(string workload, string root, int repeats, int files, int megabytes)
    {
        var source = BuildTree(Path.Combine(root, "tree"), files, megabytes);
        var options = workload switch
        {
            "erofs-zstd-pack" or "erofs-zstd-unpack" => new ErofsOptions
            {
                Algorithm = ErofsCompressionAlgorithm.Zstd,
                CompressionLevel = 1,
                Uuid = new byte[16],
                BuildTime = 1700000000,
            },
            "erofs-lz4-pack" or "erofs-lz4-unpack" => new ErofsOptions
            {
                Algorithm = ErofsCompressionAlgorithm.Lz4,
                CompressionLevel = 1,
                Uuid = new byte[16],
                BuildTime = 1700000000,
            },
            "erofs-lzma-pack" or "erofs-lzma-unpack" => new ErofsOptions
            {
                Algorithm = ErofsCompressionAlgorithm.Lzma,
                CompressionLevel = 1,
                Uuid = new byte[16],
                BuildTime = 1700000000,
            },
            _ => throw new InvalidOperationException($"Unknown workload '{workload}'.")
        };

        var image = Path.Combine(root, "image.erofs");
        new ErofsBuilder().Build(source, image, options);
        Console.WriteLine($"image sha256    {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image)))}");

        Action run = workload switch
        {
            "erofs-zstd-pack" or "erofs-lz4-pack" or "erofs-lzma-pack" => () =>
                new ErofsBuilder().Build(source, Path.Combine(root, "rebuild.erofs"), options),
            _ => () =>
                {
                    using var stream = File.OpenRead(image);
                    using var parser = new ErofsParser(stream);
                    foreach (var entry in parser.EnumerateFiles())
                    {
                        parser.ReadFileBytes(entry.Path);
                    }
                },
        };

        return Measure(run, repeats);
    }

    private static ProfileReport Measure(Action run, int repeats)
    {
        run();
        var profile = RuntimeProfile.Start();
        for (var repeat = 0; repeat < repeats; repeat++)
        {
            run();
        }

        return profile.Stop();
    }

    private static string BuildTree(string root, int files, int megabytes)
    {
        Directory.CreateDirectory(root);
        var corpus = BenchCorpus.Bytes(PayloadKind.Text, 1);
        for (var index = 0; index < files; index++)
        {
            var offset = (int)(((long)index * 4096) % (corpus.Length - 4096));
            var path = Path.Combine(root, FormattableString.Invariant($"payload-{index}.bin"));
            File.WriteAllBytes(path, corpus.Slice(offset, 4096).ToArray());
            File.SetLastWriteTimeUtc(path, NewYearUtc);
            File.SetCreationTimeUtc(path, NewYearUtc);
        }

        var large = Path.Combine(root, "large.bin");
        File.WriteAllBytes(large, BenchCorpus.Bytes(PayloadKind.Executable, megabytes).ToArray());
        File.SetLastWriteTimeUtc(large, NewYearUtc);
        File.SetCreationTimeUtc(large, NewYearUtc);
        return root;
    }

    private static readonly DateTime NewYearUtc = new(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static void Write(ProfileReport report, string[] args)
    {
        var output = Value(args, "--out");
        var text = new StringBuilder();
        var wall = report.Wall.TotalMilliseconds;
        var cpuShare = wall > 0
            ? report.Cpu.TotalMilliseconds / wall / Environment.ProcessorCount * 100
            : 0;
        text.AppendLine($"wall            {wall,10:F1} ms");
        text.AppendLine($"cpu time        {report.Cpu.TotalMilliseconds,10:F1} ms ({cpuShare:F1}% of {Environment.ProcessorCount} cores)");
        text.AppendLine($"events seen     {report.EventsSeen}");
        text.AppendLine($"stw pause       {report.Pause.TotalMilliseconds,10:F1} ms in {report.PauseCount} suspensions");
        text.AppendLine($"gc work         {report.GcWork.TotalMilliseconds,10:F1} ms");
        text.AppendLine($"collections     gen0={report.Gen0} gen1={report.Gen1} gen2={report.Gen2}");
        text.AppendLine($"                blocking={report.BlockingCollections} concurrent={report.ConcurrentCollections}");
        text.AppendLine($"allocated       {report.AllocatedBytes / (1024.0 * 1024.0),10:F1} MiB");
        text.AppendLine($"sampled         {report.SampledBytes / (1024.0 * 1024.0),10:F1} MiB across {report.TopAllocators.Length} types");
        text.AppendLine("top allocators");
        foreach (var (type, bytes) in report.TopAllocators.Take(20))
        {
            text.AppendLine($"  {bytes / (1024.0 * 1024.0),9:F2} MiB  {type}");
        }

        Console.WriteLine(text);
        if (output is null)
        {
            return;
        }

        var payload = new
        {
            wallMs = report.Wall.TotalMilliseconds,
            cpuMs = report.Cpu.TotalMilliseconds,
            eventsSeen = report.EventsSeen,
            pauseMs = report.Pause.TotalMilliseconds,
            pauseCount = report.PauseCount,
            gcWorkMs = report.GcWork.TotalMilliseconds,
            gen0 = report.Gen0,
            gen1 = report.Gen1,
            gen2 = report.Gen2,
            blocking = report.BlockingCollections,
            concurrent = report.ConcurrentCollections,
            allocatedBytes = report.AllocatedBytes,
            sampledBytes = report.SampledBytes,
            topAllocators = report.TopAllocators.Take(25).Select(p => new { type = p.Type, bytes = p.Bytes }),
        };
        File.WriteAllText(output, JsonSerializer.Serialize(payload, JsonOptions));
    }

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
}
