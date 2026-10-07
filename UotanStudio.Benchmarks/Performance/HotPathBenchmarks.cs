using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;
using UotanStudio.Firmware.Application.Equivalence;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;

namespace UotanStudio.Benchmarks.Performance;

/// <summary>
/// Baselines for the EROFS compressed-extract hot path (per-pcluster metabox reads and LZ4 decode),
/// the Ext4 metadata walk (per-block reads and the 64-block cache overflow clear), the Delivery
/// per-file SHA-256 checksum loop, and the byte-equivalence scan. These back the Batch 5/6/7
/// allocation and wall-clock comparisons; a change that does not move these numbers is reverted.
/// </summary>
[MemoryDiagnoser]
public class HotPathBenchmarks
{
    private string _root = "";
    private byte[] _erofsImage = [];
    private string[] _erofsPaths = [];
    private byte[] _ext4Image = [];
    private string[] _stagedFiles = [];
    private byte[] _equivalenceImage = [];
    private string _equivalenceDigest = "";
    private long _equivalenceLength;

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotan_bench_hotpath_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        // EROFS: compressible multi-cluster payloads so extraction fires the metabox/pcluster path.
        var erofsSource = Path.Combine(_root, "erofs_src");
        Directory.CreateDirectory(erofsSource);
        var erofsNames = new List<string>();
        var rng = new Random(101);
        for (var f = 0; f < 64; f++)
        {
            var payload = CompressiblePayload(128 * 1024, rng);
            var name = $"dir{f % 8}/blob{f}.bin";
            var full = Path.Combine(erofsSource, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, payload);
            erofsNames.Add(name);
        }

        _erofsImage = new ErofsBuilder().Build(erofsSource, new ErofsOptions { Compress = true });
        _erofsPaths = [.. erofsNames];

        // Ext4: many entries to exceed MaxCachedMetadataBlocks (64) during the walk.
        var ext4Source = Path.Combine(_root, "ext4_src");
        for (var d = 0; d < 40; d++)
        {
            var dir = Path.Combine(ext4Source, $"d{d}");
            Directory.CreateDirectory(dir);
            for (var i = 0; i < 20; i++)
            {
                File.WriteAllText(Path.Combine(dir, $"leaf{i}.txt"), $"content-{d}-{i}");
            }
        }

        _ext4Image = Ext4Builder.Build(ext4Source, new Ext4Options { IncludeJournal = false });

        // Delivery checksum: a staged tree with mixed sizes.
        var stage = Path.Combine(_root, "stage");
        Directory.CreateDirectory(stage);
        long total = 0;
        var names = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            var size = (i % 4 == 0 ? 4 : 1) * 1024 * 1024;
            File.WriteAllBytes(Path.Combine(stage, $"art{i}.img"), new byte[size]);
            total += size;
            names.Add($"art{i}.img");
        }

        _stagedFiles = [.. names.Select(n => Path.Combine(stage, n))];

        // Byte-equivalence: one representative produced image, scanned whole.
        _equivalenceImage = CompressiblePayload(16 * 1024 * 1024, new Random(7));
        _equivalenceDigest = Convert.ToHexStringLower(SHA256.HashData(_equivalenceImage));
        _equivalenceLength = _equivalenceImage.Length;
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

    [Benchmark]
    public long ErofsCompressedExtract()
    {
        using var parser = new ErofsParser(_erofsImage);
        parser.EnumerateFiles();
        long read = 0;
        foreach (var path in _erofsPaths)
        {
            read += parser.ReadFileBytes(path).Length;
        }

        return read;
    }

    [Benchmark]
    public long Ext4MetadataWalk()
    {
        using var stream = new MemoryStream(_ext4Image, writable: false);
        using var parser = new Ext4Parser(stream);
        var files = parser.ListFiles();
        return files.Count;
    }

    [Benchmark]
    public async Task<string> DeliveryShaChecksumLoop()
    {
        var stagedFiles = _stagedFiles;
        var buffer = new byte[1024 * 1024];
        var hashes = new StringBuilder();
        long totalBytes = 0;
        foreach (var file in stagedFiles)
        {
            totalBytes = checked(totalBytes + new FileInfo(file).Length);
        }

        long processedBytes = 0;
        for (var i = 0; i < stagedFiles.Length; i++)
        {
            await using var input = new FileStream(stagedFiles[i], FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var fileLength = input.Length;
            var hashAlgorithm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) != 0)
            {
                hashAlgorithm.AppendData(buffer, 0, read);
                processedBytes = checked(processedBytes + read);
            }

            var hash = hashAlgorithm.GetHashAndReset();
            hashAlgorithm.Dispose();
            if (input.Length != fileLength)
            {
                throw new IOException("Output changed while verifying.");
            }

            hashes.Append(Convert.ToHexStringLower(hash)).Append("  ")
                .AppendLine(Path.GetFileName(stagedFiles[i]));
        }

        return hashes.Length == 0 ? "" : $"{processedBytes}-{totalBytes}";
    }

    [Benchmark]
    public FirmwareEquivalenceState ByteEquivalenceScan()
    {
        using var stream = new MemoryStream(_equivalenceImage, writable: false);
        return FilesystemEquivalenceReport.EvaluateByteEquivalence(stream, _equivalenceDigest, _equivalenceLength);
    }

    private static byte[] CompressiblePayload(int length, Random rng)
    {
        // Repeating 4 KiB blocks with small periodic jitter: LZ4 yields multi-block pclusters,
        // and the file spans enough clusters to hit the metabox reload path on extract.
        var data = new byte[length];
        var seed = new byte[4096];
        rng.NextBytes(seed);
        for (var offset = 0; offset < length; offset += 4096)
        {
            var take = Math.Min(4096, length - offset);
            for (var i = 0; i < take; i++)
            {
                data[offset + i] = (byte)(seed[i % seed.Length] ^ ((offset / 4096) * 31 + i) % 251);
            }
        }

        return data;
    }
}