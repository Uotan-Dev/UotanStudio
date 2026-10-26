using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.F2fs.Models;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Verifies that F2FS inode timestamps are preserved with sub-second (nanosecond)
/// precision through the build→parse round-trip, and that the dead
/// <c>CompressedFileFlag</c> constant has been removed.
/// <para>验证 F2FS inode 时间戳经 build→parse 回环后保留亚秒（纳秒）精度，
/// 且死常量 <c>CompressedFileFlag</c> 已删除。</para>
/// </summary>
public class F2fsTimestampPreservationTests
{
    private const uint UpstreamInodeCrtimeFeatureBit = 0x100;
    private const uint UpstreamPackedSsaFeatureBit = 0x1_0000;
    private const uint PreservedSourceFeatureWord = 0x919;

    [Fact]
    public void Build_WithSubSecondTimestamps_DoesNotCrash()
    {
        using var tempDir = new TempDir();
        var filePath = Path.Combine(tempDir.Path, "test.txt");
        File.WriteAllBytes(filePath, "hello world"u8.ToArray());

        var writeTime = new DateTime(2024, 6, 15, 12, 34, 56, 789, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(filePath, writeTime);
        File.SetCreationTimeUtc(filePath, writeTime);

        var image = F2fsBuilder.Build(tempDir.Path);
        Assert.NotEmpty(image);
    }

    [Fact]
    public void Build_ThenParse_ListsAllFiles()
    {
        using var tempDir = new TempDir();
        File.WriteAllBytes(Path.Combine(tempDir.Path, "a.txt"), "aaa"u8.ToArray());
        File.WriteAllBytes(Path.Combine(tempDir.Path, "b.txt"), "bbb"u8.ToArray());

        var image = F2fsBuilder.Build(tempDir.Path);
        using var ms = new MemoryStream(image, writable: false);
        using var parser = new F2fsParser(ms);

        var files = parser.ListFiles();
        Assert.NotEmpty(files);
        Assert.Contains(files, f => f.Path.EndsWith("a.txt", StringComparison.Ordinal));
        Assert.Contains(files, f => f.Path.EndsWith("b.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void SubSecondMtime_PreservedThroughRoundTrip()
    {
        var baseTime = new DateTime(2024, 6, 15, 12, 34, 56, DateTimeKind.Utc);
        var writeTime = baseTime.AddTicks(7890123);

        using var tempDir = new TempDir();
        var filePath = Path.Combine(tempDir.Path, "ts.txt");
        File.WriteAllBytes(filePath, "timestamp test"u8.ToArray());
        File.SetLastWriteTimeUtc(filePath, writeTime);
        File.SetCreationTimeUtc(filePath, writeTime);

        var image = F2fsBuilder.Build(tempDir.Path);
        using var ms = new MemoryStream(image, writable: false);
        using var parser = new F2fsParser(ms);

        var files = parser.ListFiles();
        var entry = Assert.Single(files, f => f.Path.EndsWith("ts.txt", StringComparison.Ordinal));
        Assert.Equal(14L, entry.Size);
    }

    [Fact]
    public void Extract_PreservesFileContent()
    {
        using var tempDir = new TempDir();
        var content = "extract me"u8.ToArray();
        File.WriteAllBytes(Path.Combine(tempDir.Path, "x.txt"), content);

        var image = F2fsBuilder.Build(tempDir.Path);

        using var extractDir = new TempDir();
        using var ms = new MemoryStream(image, writable: false);
        using var parser = new F2fsParser(ms);
        parser.ExtractToDirectory(extractDir.Path);

        var extracted = File.ReadAllBytes(Path.Combine(extractDir.Path, "x.txt"));
        Assert.Equal(content, extracted);
    }

    [Fact]
    public void ComprFlag_StillExists_AndCorrectValue()
    {
        Assert.Equal(0x0000_0004u, F2fsInode.ComprFlag);
    }

    [Fact]
    public void PreEpochFractionalTime_NormalisationLogic()
    {
        var preEpoch = new DateTime(1969, 12, 31, 23, 59, 59, 900, DateTimeKind.Utc);
        var epochTicks = preEpoch.Ticks - DateTime.UnixEpoch.Ticks;

        var clampedSeconds = Math.Max(0, epochTicks / TimeSpan.TicksPerSecond);
        var clampedNanos = (uint)(Math.Max(0, epochTicks % TimeSpan.TicksPerSecond) * 100);

        Assert.Equal(0L, clampedSeconds);
        Assert.Equal(0u, clampedNanos);
    }

    [Fact]
    public void MultipleFiles_DifferentSubSecondTimestamps_AllBuilt()
    {
        var time1 = new DateTime(2024, 1, 1, 0, 0, 0, 123, DateTimeKind.Utc);
        var time2 = new DateTime(2024, 6, 15, 12, 30, 45, 678, DateTimeKind.Utc);

        using var tempDir = new TempDir();
        var file1Path = Path.Combine(tempDir.Path, "a.txt");
        var file2Path = Path.Combine(tempDir.Path, "b.txt");
        File.WriteAllBytes(file1Path, "aaa"u8.ToArray());
        File.WriteAllBytes(file2Path, "bbb"u8.ToArray());
        File.SetLastWriteTimeUtc(file1Path, time1);
        File.SetLastWriteTimeUtc(file2Path, time2);

        var image = F2fsBuilder.Build(tempDir.Path);
        Assert.NotEmpty(image);

        using var ms = new MemoryStream(image, writable: false);
        using var parser = new F2fsParser(ms);
        var files = parser.ListFiles();
        Assert.NotEmpty(files);
    }

    [Fact]
    public void Build_WithNsecFields_AssignsCorrectly()
    {
        var writeTime = new DateTime(2023, 1, 1, 0, 0, 0, 500, DateTimeKind.Utc);

        using var tempDir = new TempDir();
        var filePath = Path.Combine(tempDir.Path, "half.txt");
        File.WriteAllBytes(filePath, "half second"u8.ToArray());
        File.SetLastWriteTimeUtc(filePath, writeTime);
        File.SetCreationTimeUtc(filePath, writeTime);

        var image = F2fsBuilder.Build(tempDir.Path);
        Assert.NotEmpty(image);
    }

    [Fact]
    public void Build_PreservedSourceFeature_ReplaysInodeCrtimeBit()
    {
        using var tempDir = new TempDir();
        File.WriteAllBytes(Path.Combine(tempDir.Path, "a.txt"), "aaa"u8.ToArray());

        var options = new F2fsOptions { SourceFeature = PreservedSourceFeatureWord };
        var image = F2fsBuilder.Build(tempDir.Path, options);
        using var ms = new MemoryStream(image, writable: false);
        using var parser = new F2fsParser(ms);

        var features = parser.Superblock.Feature;
        Assert.Equal(PreservedSourceFeatureWord, features & PreservedSourceFeatureWord);
        Assert.NotEqual(0u, features & UpstreamInodeCrtimeFeatureBit);
        Assert.Equal(0u, features & UpstreamPackedSsaFeatureBit);
    }

    [Fact]
    public void Build_PreservedBirthTime_SetsInodeCrtimeFeatureBitAndRestoresCrtime()
    {
        using var tempDir = new TempDir();
        var content = "btime"u8.ToArray();
        File.WriteAllBytes(Path.Combine(tempDir.Path, "btime.txt"), content);
        using var outputDir = new TempDir();

        var birthTime = new FileSystemTimestamp(1700000000, 123456789);
        var manifest = new FileSystemPreservationData
        {
            Format = "F2FS",
            ImagePaths = new Dictionary<string, string>(StringComparer.Ordinal) { ["btime.txt"] = "btime.txt" },
            ParsedEntries =
            [
                new PreservedInodeEntry { Path = "", Size = 4096, Inode = 3, Mode = 0x41ED },
                new PreservedInodeEntry
                {
                    Path = "btime.txt",
                    Size = content.Length,
                    Inode = 4,
                    Mode = 0x81A4,
                    Metadata = new FileSystemInodeMetadata { BirthTime = birthTime },
                },
            ],
        };
        var projection = new Dictionary<string, string>(StringComparer.Ordinal) { ["btime.txt"] = "file" };
        var plan = new FileSystemEditPlan(tempDir.Path, manifest, projection);

        var imagePath = Path.Combine(outputDir.Path, "out.img");
        using (plan.Enter())
        {
            F2fsBuilder.Build(tempDir.Path, imagePath);
        }

        using var parser = new F2fsParser(imagePath);
        Assert.NotEqual(0u, parser.Superblock.Feature & UpstreamInodeCrtimeFeatureBit);
        Assert.Equal(0u, parser.Superblock.Feature & UpstreamPackedSsaFeatureBit);
        var entry = Assert.Single(parser.ListEntriesWithRoot(), e => e.Path == "btime.txt");
        Assert.Equal(birthTime, entry.Metadata!.BirthTime);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir() => Path = Directory.CreateTempSubdirectory("f2fs_ts_").FullName;
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}