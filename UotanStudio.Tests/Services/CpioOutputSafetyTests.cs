using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.Archive.Extraction;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Services.Firmware;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class CpioOutputSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cpio-output-").FullName;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CpioOperation_CancelDuringStaging_PreservesExistingOutput(bool pack)
    {
        using var scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var input = Directory.CreateDirectory(Path.Combine(_root, "input")).FullName;
        var payload = Path.Combine(input, "payload");
        using (var file = File.Create(payload))
        {
            file.SetLength(64 * 1024 * 1024);
        }
        var archive = Path.Combine(_root, "large.cpio");
        if (!pack)
        {
            var prepared = await FirmwareService.Instance.PackCpioAsync(input, archive, Token);
            Assert.True(prepared.Success, prepared.Summary);
        }
        var output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
        var sentinel = Path.Combine(output, "sentinel");
        await File.WriteAllTextAsync(sentinel, "previous", Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
        };
        watcher.Created += (_, args) =>
        {
            if (pack ? args.Name!.EndsWith(".tmp", StringComparison.Ordinal)
                : args.Name!.Contains("payload.tmp-", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        };
        watcher.EnableRaisingEvents = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pack
            ? FirmwareService.Instance.PackCpioAsync(input, sentinel, cancellation.Token)
            : FirmwareService.Instance.UnpackCpioAsync(archive, output, cancellationToken: cancellation.Token));

        watcher.EnableRaisingEvents = false;
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("previous", await File.ReadAllTextAsync(sentinel, Token));
        Assert.Single(Directory.GetFileSystemEntries(output));
        Assert.Empty(Directory.GetDirectories(_root, ".output.cpio-*"));
        Assert.Empty(Directory.GetFiles(output, "*.tmp"));
    }

    [Fact]
    public async Task PackCpioAsync_LockedPayload_PreservesExistingArchive()
    {
        using var scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var input = Directory.CreateDirectory(Path.Combine(_root, "input")).FullName;
        var source = Path.Combine(input, "payload");
        await File.WriteAllTextAsync(source, "payload", Token);
        var output = Path.Combine(_root, "output.cpio");
        await File.WriteAllTextAsync(output, "previous", Token);
        using var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await FirmwareService.Instance.PackCpioAsync(input, output, Token);

        Assert.False(result.Success);
        Assert.Equal("previous", await File.ReadAllTextAsync(output, Token));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CpioOperation_PreCancelled_PreservesExistingOutput(bool pack)
    {
        var output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
        var sentinel = Path.Combine(output, "sentinel");
        await File.WriteAllTextAsync(sentinel, "previous", Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pack
            ? FirmwareService.Instance.PackCpioAsync(output, sentinel, cancellation.Token)
            : FirmwareService.Instance.UnpackCpioAsync("missing.cpio", output, cancellationToken: cancellation.Token));

        Assert.Equal("previous", await File.ReadAllTextAsync(sentinel, Token));
        Assert.Single(Directory.GetFileSystemEntries(output));
    }

    [Fact]
    public async Task UnpackCpioAsync_TruncatedArchive_PreservesExistingDirectory()
    {
        using var scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var source = CreateArchive(Regular("first", [1, 2]), Regular("second", new byte[4096]));
        using (var stream = new FileStream(source, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(400);
        }
        var output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
        await File.WriteAllTextAsync(Path.Combine(output, "first"), "previous", Token);

        var result = await FirmwareService.Instance.UnpackCpioAsync(source, output, cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Equal("previous", await File.ReadAllTextAsync(Path.Combine(output, "first"), Token));
        Assert.Single(Directory.GetFileSystemEntries(output));
        Assert.Empty(Directory.GetDirectories(_root, ".output.cpio-*"));
    }

    [Fact]
    public async Task UnpackCpioAsync_MetadataPublicationFails_RestoresExistingDirectory()
    {
        var source = CreateArchive(Regular("new", [1, 2]));
        var output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
        await File.WriteAllTextAsync(Path.Combine(output, "sentinel"), "previous", Token);
        var metadataRoot = Path.Combine(_root, "blocked");
        await File.WriteAllTextAsync(metadataRoot, "not a directory", Token);
        using var scope = RepackMetadata.UseContext(RepackMetadataContext.FromProjectRoot(metadataRoot));

        var result = await FirmwareService.Instance.UnpackCpioAsync(source, output, cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Equal("previous", await File.ReadAllTextAsync(Path.Combine(output, "sentinel"), Token));
        Assert.Single(Directory.GetFileSystemEntries(output));
        Assert.Empty(Directory.GetDirectories(_root, ".output.cpio-*"));
    }

    [Fact]
    public async Task UnpackCpioAsync_OverwriteDisabled_PreservesNonemptyDirectory()
    {
        var source = CreateArchive(Regular("new", [1]));
        var output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
        await File.WriteAllTextAsync(Path.Combine(output, "sentinel"), "previous", Token);

        var result = await FirmwareService.Instance.UnpackCpioAsync(source, output, false, Token);

        Assert.False(result.Success);
        Assert.Single(Directory.GetFileSystemEntries(output));
        Assert.Equal("previous", await File.ReadAllTextAsync(Path.Combine(output, "sentinel"), Token));
    }

    [Fact]
    public async Task CpioServices_MetadataAndSymbolicLink_RestoreArchiveHeaders()
    {
        using var scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var source = CreateArchive(
            Regular("payload", [1, 2, 3]),
            new ArchiveEntry
            {
                Name = "link",
                LinkName = "payload",
                Data = [],
                Metadata = new ArchiveEntryMetadata
                {
                    FileType = CpioFileType.SymbolicLink,
                    Mode = 0xA1FF,
                    UnixPermissions = 0x1FF,
                    LinkCount = 1
                }
            });
        var output = Path.Combine(_root, "output");
        var unpacked = await FirmwareService.Instance.UnpackCpioAsync(source, output, cancellationToken: Token);
        Assert.True(unpacked.Success, unpacked.Summary);
        Assert.NotNull(RepackMetadata.Read(output));
        var repacked = Path.Combine(_root, "repacked.cpio");

        var packed = await FirmwareService.Instance.PackCpioAsync(output, repacked, Token);

        Assert.True(packed.Success, packed.Summary);
        using var stream = File.OpenRead(repacked);
        var entries = new CpioArchive().ReadAll(stream);
        var payload = Assert.Single(entries, entry => entry.Name == "payload");
        Assert.Equal(123U, payload.Metadata.UserId);
        Assert.Equal(456U, payload.Metadata.GroupId);
        Assert.Equal(0x1E8, payload.Metadata.UnixPermissions);
        var link = Assert.Single(entries, entry => entry.Name == "link");
        Assert.Equal(CpioFileType.SymbolicLink, link.Metadata.FileType);
        Assert.Equal("payload", System.Text.Encoding.UTF8.GetString(link.Data));
        Assert.DoesNotContain(entries, entry => entry.Name == RepackMetadata.FileName);
    }

    [Fact]
    public async Task PackCpioAsync_EmptySubdirectory_IncludesDirectoryEntry()
    {
        using var scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var input = Directory.CreateDirectory(Path.Combine(_root, "input")).FullName;
        Directory.CreateDirectory(Path.Combine(input, "empty"));
        var output = Path.Combine(_root, "output.cpio");

        var result = await FirmwareService.Instance.PackCpioAsync(input, output, Token);

        Assert.True(result.Success, result.Summary);
        using var stream = File.OpenRead(output);
        Assert.Contains(new CpioArchive().ReadAll(stream),
            entry => entry.Name.TrimEnd('/') == "empty" && entry.Metadata.FileType == CpioFileType.Directory);
    }

    [Fact]
    public async Task CpioServices_HardlinkMetadata_PreservesGroupIdentity()
    {
        using var scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var metadata = new ArchiveEntryMetadata
        {
            FileType = CpioFileType.Regular,
            Mode = 0x81A4,
            UnixPermissions = 0x1A4,
            Inode = 27,
            LinkCount = 2
        };
        var source = CreateArchive(
            new ArchiveEntry { Name = "first", Data = [1, 2, 3], Metadata = metadata },
            new ArchiveEntry { Name = "second", Data = [1, 2, 3], Metadata = metadata });
        var output = Path.Combine(_root, "output");
        var unpacked = await FirmwareService.Instance.UnpackCpioAsync(source, output, cancellationToken: Token);
        Assert.True(unpacked.Success, unpacked.Summary);
        var repacked = Path.Combine(_root, "repacked.cpio");

        var packed = await FirmwareService.Instance.PackCpioAsync(output, repacked, Token);

        Assert.True(packed.Success, packed.Summary);
        using var stream = File.OpenRead(repacked);
        var entries = new CpioArchive().ReadAll(stream);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal(27UL, entry.Metadata.Inode);
            Assert.Equal(2UL, entry.Metadata.LinkCount);
        });
    }

    [Fact]
    public async Task UnpackCpioAsync_ValidArchive_ReplacesOldTreeAndPublishesMetadata()
    {
        using var scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var source = CreateArchive(Regular("new", [42]));
        var output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
        await File.WriteAllTextAsync(Path.Combine(output, "obsolete"), "old", Token);

        var result = await FirmwareService.Instance.UnpackCpioAsync(source, output, cancellationToken: Token);

        Assert.True(result.Success, result.Summary);
        Assert.False(File.Exists(Path.Combine(output, "obsolete")));
        Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(Path.Combine(output, "new"), Token));
        Assert.NotNull(RepackMetadata.Read(output));
        Assert.Empty(Directory.GetDirectories(_root, ".output.cpio-*"));
    }

    [Fact]
    public async Task ExtractToDirectory_ExistingUnrelatedFile_PreservesMergeSemantics()
    {
        var output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
        await File.WriteAllTextAsync(Path.Combine(output, "sentinel"), "previous", Token);

        new CpioExtractor().ExtractToDirectory([Regular("new", [42])], output,
            new ExtractOptions { CancellationToken = Token });

        Assert.Equal("previous", await File.ReadAllTextAsync(Path.Combine(output, "sentinel"), Token));
        Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(Path.Combine(output, "new"), Token));
    }

    private string CreateArchive(params ArchiveEntry[] entries)
    {
        var path = Path.Combine(_root, "source.cpio");
        using var output = File.Create(path);
        new CpioArchive().SaveAll(output, entries, ArchiveFormat.NewAscii);
        return path;
    }

    private static ArchiveEntry Regular(string name, byte[] data) => new()
    {
        Name = name,
        Data = data,
        Metadata = new ArchiveEntryMetadata
        {
            FileType = CpioFileType.Regular,
            Mode = 0x81E8,
            UnixPermissions = 0x1E8,
            UserId = 123,
            GroupId = 456,
            LinkCount = 1
        }
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);
}