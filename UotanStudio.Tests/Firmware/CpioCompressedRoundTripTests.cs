using System.Text;
using UotanStudio.Compress;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.Core.Repacking;
using Xunit;

namespace UotanStudio.Tests.Firmware;

[Collection("Workspace state")]
public sealed partial class CpioCompressedRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cpio-compressed-{Guid.NewGuid():N}");

    [Fact]
    public async Task UnpackAsync_ContentCompressedArchiveWithoutCompressionSuffix_ExtractsEntries()
    {
        Directory.CreateDirectory(_root);
        var token = TestContext.Current.CancellationToken;
        using var archive = new MemoryStream();
        new CpioArchive().SaveAll(archive,
        [
            new ArchiveEntry
            {
                Name = "init",
                Data = Encoding.UTF8.GetBytes("ramdisk"),
                Metadata = new ArchiveEntryMetadata
                {
                    FileType = CpioFileType.Regular,
                    Mode = 0x81A4,
                    UserId = 0,
                    GroupId = 0,
                    LinkCount = 1,
                    UnixPermissions = 0x1A4,
                    ModificationTimeUnixSeconds = 1_700_000_000
                }
            }
        ], ArchiveFormat.NewAscii);
        var source = Path.Combine(_root, "ramdisk.cpio");
        await File.WriteAllBytesAsync(source, CompressionService.Compress(archive.ToArray(), CompressionFormat.Gzip), token);

        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var output = Path.Combine(_root, "extracted");
        var result = await new CpioFormatHandler().UnpackAsync(source, output, cancellationToken: token);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("ramdisk", await File.ReadAllTextAsync(Path.Combine(output, "init"), token));
        var metadata = RepackMetadata.Read(output, RepackMetadataContext.LocalDirectory);
        Assert.Equal("Gzip", metadata!.Configuration["cpio"]!["compression"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnpackAsync_DuplicateDirectoryEntries_AreAccepted()
    {
        Directory.CreateDirectory(_root);
        var token = TestContext.Current.CancellationToken;
        using var archive = new MemoryStream();
        var directory = new ArchiveEntry
        {
            Name = "dev",
            Data = [],
            Metadata = new ArchiveEntryMetadata
            {
                FileType = CpioFileType.Directory,
                Mode = 0x41ED,
                UnixPermissions = 0x1ED,
                LinkCount = 2
            }
        };
        new CpioArchive().SaveAll(archive, [directory, new ArchiveEntry
        {
            Name = "dev",
            Data = [],
            Metadata = new ArchiveEntryMetadata
            {
                FileType = CpioFileType.Directory,
                Mode = 0x41ED,
                UnixPermissions = 0x1ED,
                LinkCount = 2
            }
        }], ArchiveFormat.NewAscii);
        var source = Path.Combine(_root, "ramdisk.cpio");
        await File.WriteAllBytesAsync(source, archive.ToArray(), token);

        var output = Path.Combine(_root, "extracted");
        var result = await new CpioFormatHandler().UnpackAsync(source, output, cancellationToken: token);

        Assert.True(result.Success, result.Summary);
        Assert.True(Directory.Exists(Path.Combine(output, "dev")));
    }

    [Fact]
    public async Task UnpackAsync_DuplicateRegularEntries_AreRejected()
    {
        Directory.CreateDirectory(_root);
        var token = TestContext.Current.CancellationToken;
        using var archive = new MemoryStream();
        var file = new ArchiveEntry
        {
            Name = "init",
            Data = Encoding.UTF8.GetBytes("one"),
            Metadata = new ArchiveEntryMetadata
            {
                FileType = CpioFileType.Regular,
                Mode = 0x81A4,
                UnixPermissions = 0x1A4,
                LinkCount = 1
            }
        };
        new CpioArchive().SaveAll(archive, [file, new ArchiveEntry
        {
            Name = "init",
            Data = Encoding.UTF8.GetBytes("two"),
            Metadata = new ArchiveEntryMetadata
            {
                FileType = CpioFileType.Regular,
                Mode = 0x81A4,
                UnixPermissions = 0x1A4,
                LinkCount = 1
            }
        }], ArchiveFormat.NewAscii);
        var source = Path.Combine(_root, "ramdisk.cpio");
        await File.WriteAllBytesAsync(source, archive.ToArray(), token);

        var output = Path.Combine(_root, "extracted");
        var result = await new CpioFormatHandler().UnpackAsync(source, output, cancellationToken: token);

        Assert.False(result.Success);
        Assert.Contains("duplicate paths", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PackAsync_CancelledOrInvalidMetadata_PreservesExistingOutput()
    {
        Directory.CreateDirectory(_root);
        var extracted = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(extracted);
        await File.WriteAllTextAsync(Path.Combine(extracted, "init.rc"), "boot",
            TestContext.Current.CancellationToken);
        var target = Path.Combine(_root, "ramdisk.cpio.gz");
        var original = Encoding.UTF8.GetBytes("old output");
        await File.WriteAllBytesAsync(target, original, TestContext.Current.CancellationToken);
        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new CpioFormatHandler().PackAsync(extracted, target, cancelled.Token));
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));

        var metadata = new RepackMetadata
        {
            FormatName = "CPIO/TAR Archive",
            SourceFileName = "ramdisk.cpio.gz",
        };
        metadata.Configuration["cpio"] = new System.Text.Json.Nodes.JsonObject
        {
            ["format"] = "CrcAscii",
            ["compression"] = "Unsupported",
            ["entries"] = new System.Text.Json.Nodes.JsonArray(),
        };
        await metadata.SaveAsync(extracted, TestContext.Current.CancellationToken);
        var result = await new CpioFormatHandler().PackAsync(extracted, target,
            TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal(original, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}