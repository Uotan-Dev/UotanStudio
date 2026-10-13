using UotanStudio.Compress;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.BootSecurity.Workflow;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Recovery ramdisk merge tests. The service repacks a boot image whose ramdisk is the recovery CPIO archive with
/// every source-boot entry applied on top, so these tests pin the explicit source-over-recovery precedence, the
/// inherited compression, the published output path and sizes, and the guard rails that keep the source image and an
/// existing output intact when the merge cannot run.
/// <para>recovery ramdisk 合并测试。该服务重打包 boot 镜像，其 ramdisk 是在 recovery 的 CPIO 归档之上应用了全部源
/// boot 条目的结果，因此本测试钉住显式的"源覆盖 recovery"优先级、继承的压缩方式、发布出的输出路径与各项大小，
/// 以及在合并无法执行时保护源镜像与既有输出的约束。</para>
/// </summary>
public class RecoveryRamdiskMergeServiceTests
{
    private static readonly byte[] Kernel = BuildKernel();

    private static readonly byte[] SourceInit = "source-init"u8.ToArray();

    private static readonly byte[] RecoveryInit = "recovery-init"u8.ToArray();

    private static readonly byte[] RecoveryOnly = "recovery-only"u8.ToArray();

    private static readonly byte[] SourceOnly = "source-only"u8.ToArray();

    private static byte[] BuildKernel()
    {
        var data = new byte[2048];
        "\0Linux version 5.15.123-android13 (builder)\0"u8.CopyTo(data);
        return data;
    }

    private static byte[] Cpio(params (string Name, byte[] Data)[] entries)
    {
        var archive = new CpioArchive();
        using var stream = new MemoryStream();
        archive.SaveAll(stream,
        [
            .. entries.Select(entry => new ArchiveEntry
            {
                Name = entry.Name,
                Data = entry.Data,
                Metadata = new ArchiveEntryMetadata
                {
                    FileType = CpioFileType.Regular,
                    UnixPermissions = 0x81ED,
                    LinkCount = 1
                }
            })
        ], ArchiveFormat.NewAscii);
        return stream.ToArray();
    }

    private static string BootImage(TempDirectory temp, string name, byte[] ramdisk)
    {
        var packer = new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = BootImageVersion.V3,
                HeaderSize = 1580,
                OsVersion = 0x01030000,
                CommandLine = "androidboot.slot_suffix=_a"
            },
            Kernel = Kernel,
            Ramdisk = ramdisk
        };
        var path = Path.Combine(temp.Path, name);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        packer.PackToStream(stream);
        return path;
    }

    private static IReadOnlyDictionary<string, byte[]> RamdiskEntries(string imagePath)
    {
        using var parser = new BootImageParser();
        parser.ParseFile(imagePath);
        var ramdisk = parser.Header.RamdiskCompression == CompressionType.None
            ? parser.Ramdisk
            : BootCompressor.TryDecompress(parser.Ramdisk, parser.Header.RamdiskCompression)
                ?? throw new InvalidDataException("The merged ramdisk could not be decompressed.");
        var archive = new CpioArchive();
        using var stream = new MemoryStream(ramdisk, writable: false);
        return archive.ReadAll(stream).ToDictionary(entry => entry.Name, entry => entry.Data);
    }

    [Fact]
    public async Task MergeAsync_SourceAndRecoveryShareAnEntry_SourceContentWins()
    {
        using var temp = new TempDirectory();
        var source = BootImage(temp, "boot.img", Cpio(("init", SourceInit), ("source-only", SourceOnly)));
        var recovery = BootImage(temp, "recovery.img", Cpio(("init", RecoveryInit), ("recovery-only", RecoveryOnly)));
        var output = Path.Combine(temp.Path, "out", "merged.img");

        var result = await new RecoveryRamdiskMergeService().MergeAsync(
            source, recovery, output, TestContext.Current.CancellationToken);

        var entries = RamdiskEntries(output);
        Assert.Equal(SourceInit, entries["init"]);
        Assert.Equal(SourceOnly, entries["source-only"]);
        Assert.Equal(RecoveryOnly, entries["recovery-only"]);
        Assert.Equal(Path.GetFullPath(output), Path.GetFullPath(result.OutputPath));
        Assert.Equal(CompressionType.None, result.Compression);
        Assert.True(result.SourceBytes > 0);
        Assert.True(result.RecoveryBytes > 0);
        Assert.True(result.MergedBytes >= Math.Max(result.SourceBytes, result.RecoveryBytes));
        Assert.True(File.Exists(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MergeAsync_RecoveryOnlyEntries_SurviveTheMerge()
    {
        using var temp = new TempDirectory();
        var source = BootImage(temp, "boot.img", Cpio(("init", SourceInit)));
        var recovery = BootImage(temp, "recovery.img",
            Cpio(("init", RecoveryInit), ("sbin/recovery", RecoveryOnly), ("overlay.d/x.rc", RecoveryOnly)));
        var output = Path.Combine(temp.Path, "merged.img");

        await new RecoveryRamdiskMergeService().MergeAsync(source, recovery, output, TestContext.Current.CancellationToken);

        var entries = RamdiskEntries(output);
        Assert.Equal(SourceInit, entries["init"]);
        Assert.Equal(RecoveryOnly, entries["sbin/recovery"]);
        Assert.Equal(RecoveryOnly, entries["overlay.d/x.rc"]);
    }

    [Fact]
    public async Task MergeAsync_GzipCompressedRamdisk_InheritsSourceCompression()
    {
        var sourceCpio = Cpio(("init", SourceInit));
        var recoveryCpio = Cpio(("init", RecoveryInit), ("recovery-only", RecoveryOnly));
        using var temp = new TempDirectory();
        var source = BootImage(temp, "boot.img",
            CompressionService.Compress(sourceCpio, CompressionFormat.Gzip));
        var recovery = BootImage(temp, "recovery.img", recoveryCpio);
        var output = Path.Combine(temp.Path, "merged.img");

        var result = await new RecoveryRamdiskMergeService().MergeAsync(source, recovery, output, TestContext.Current.CancellationToken);

        Assert.Equal(CompressionType.Gzip, result.Compression);
        Assert.Equal(sourceCpio.Length, result.SourceBytes);
        Assert.Equal(recoveryCpio.Length, result.RecoveryBytes);
        using var parser = new BootImageParser();
        parser.ParseFile(output);
        Assert.Equal(CompressionType.Gzip, parser.Header.RamdiskCompression);
        Assert.Equal(SourceInit, RamdiskEntries(output)["init"]);
    }

    [Fact]
    public async Task MergeAsync_ExistingOutput_IsReplacedOnlyAfterSuccess()
    {
        using var temp = new TempDirectory();
        var source = BootImage(temp, "boot.img", Cpio(("init", SourceInit)));
        var recovery = BootImage(temp, "recovery.img", Cpio(("init", RecoveryInit)));
        var output = temp.WriteBytes("merged.img", "stale"u8.ToArray());

        await new RecoveryRamdiskMergeService().MergeAsync(source, recovery, output, TestContext.Current.CancellationToken);

        Assert.NotEqual("stale"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Equal(SourceInit, RamdiskEntries(output)["init"]);
    }

    [Fact]
    public async Task MergeAsync_OutputEqualsSourceBoot_ThrowsAndKeepsSourceBytes()
    {
        using var temp = new TempDirectory();
        var source = BootImage(temp, "boot.img", Cpio(("init", SourceInit)));
        var recovery = BootImage(temp, "recovery.img", Cpio(("init", RecoveryInit)));
        var before = File.ReadAllBytes(source);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new RecoveryRamdiskMergeService().MergeAsync(
            source, recovery, source, TestContext.Current.CancellationToken));

        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task MergeAsync_ImageWithoutRamdisk_ThrowsAndLeavesOutputAbsent()
    {
        using var temp = new TempDirectory();
        var source = BootImage(temp, "boot.img", Cpio(("init", SourceInit)));
        var ramdiskless = BootImage(temp, "no-ramdisk.img", []);
        var output = Path.Combine(temp.Path, "merged.img");

        await Assert.ThrowsAsync<InvalidDataException>(() => new RecoveryRamdiskMergeService().MergeAsync(
            source, ramdiskless, output, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task MergeAsync_UnparsableBootImage_ThrowsImageParseException()
    {
        using var temp = new TempDirectory();
        var source = BootImage(temp, "boot.img", Cpio(("init", SourceInit)));
        var garbage = temp.WriteBytes("garbage.img", new byte[4096]);

        await Assert.ThrowsAsync<ImageParseException>(() => new RecoveryRamdiskMergeService().MergeAsync(
            source, garbage, Path.Combine(temp.Path, "merged.img"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MergeAsync_CorruptGzipRamdisk_ThrowsInvalidData()
    {
        using var temp = new TempDirectory();
        var gzipHeader = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0, 3, 1, 2, 3, 4, 5, 6 };
        var source = BootImage(temp, "boot.img", gzipHeader);
        var recovery = BootImage(temp, "recovery.img", Cpio(("init", RecoveryInit)));

        await Assert.ThrowsAsync<InvalidDataException>(() => new RecoveryRamdiskMergeService().MergeAsync(
            source, recovery, Path.Combine(temp.Path, "merged.img"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("", "recovery.img", "merged.img")]
    [InlineData("boot.img", "", "merged.img")]
    [InlineData("boot.img", "recovery.img", "")]
    [InlineData("   ", "   ", "   ")]
    public async Task MergeAsync_BlankPath_ThrowsArgumentException(string source, string recovery, string output)
    {
        using var temp = new TempDirectory();
        BootImage(temp, "boot.img", Cpio(("init", SourceInit)));
        BootImage(temp, "recovery.img", Cpio(("init", RecoveryInit)));
        var sourcePath = string.IsNullOrWhiteSpace(source) ? source : Path.Combine(temp.Path, source);
        var recoveryPath = string.IsNullOrWhiteSpace(recovery) ? recovery : Path.Combine(temp.Path, recovery);
        var outputPath = string.IsNullOrWhiteSpace(output) ? output : Path.Combine(temp.Path, output);

        await Assert.ThrowsAsync<ArgumentException>(() => new RecoveryRamdiskMergeService().MergeAsync(
            sourcePath, recoveryPath, outputPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MergeAsync_CancelledToken_ThrowsBeforePublishingOutput()
    {
        using var temp = new TempDirectory();
        var source = BootImage(temp, "boot.img", Cpio(("init", SourceInit)));
        var recovery = BootImage(temp, "recovery.img", Cpio(("init", RecoveryInit)));
        var output = Path.Combine(temp.Path, "merged.img");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => new RecoveryRamdiskMergeService().MergeAsync(
            source, recovery, output, cancellation.Token));

        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public void RecoveryRamdiskMergeResult_RecordEquality_CoversEveryComponent()
    {
        var first = new RecoveryRamdiskMergeResult("out.img", 1, 2, 3, CompressionType.Gzip);
        var second = new RecoveryRamdiskMergeResult("out.img", 1, 2, 3, CompressionType.Gzip);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, first with { MergedBytes = 4 });
        Assert.NotEqual(first, first with { Compression = CompressionType.Lz4 });
    }
}