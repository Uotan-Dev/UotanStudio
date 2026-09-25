using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Metadata;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;
using UotanStudio.Services.Firmware;
using Xunit;

namespace UotanStudio.Tests.Firmware;

[Collection("Workspace state")]
public sealed class FileSystemSnapshotReplayTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "uotan-snapshot-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public FileSystemSnapshotReplayTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    [InlineData("F2FS")]
    public async Task ReplaySnapshot_OriginalAndTreeDeleted_PreservesMetadataAndHardlinks(string format)
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "data"), "source-content", Token);
        await File.WriteAllTextAsync(Path.Combine(source, "alias"), "source-content", Token);
        await File.WriteAllTextAsync(Path.Combine(source, "link"), "data", Token);
        await File.WriteAllBytesAsync(Path.Combine(source, "device"), [], Token);
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        var image = Path.Combine(root, "system.img");
        var metadata = MakeMetadata(format);
        var entries = new List<PreservedInodeEntry>
        {
            Entry(".", 2, 0x41ED, metadata),
            Entry("data", 11, 0x89ED, metadata),
            Entry("alias", 11, 0x89ED, metadata),
            Entry("link", 12, 0xA1FF, metadata),
            Entry("empty", 13, 0x4000, metadata),
            Entry("device", 14, 0x21A0, metadata)
        };
        entries[3].SymlinkTarget = "data";
        entries[5].DeviceId = 0x103;
        var projection = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["data"] = "file:original",
            ["alias"] = "file:original",
            ["link"] = "file:link",
            ["empty"] = "directory",
            ["device"] = "file:empty"
        };
        var manifest = new FileSystemPreservationManifest { ParsedEntries = entries, Projection = projection };
        var plan = new FileSystemEditPlan(source, manifest, projection);
        using (plan.Enter())
        {
            Build(format, source, image);
        }
        if (format == "EROFS")
        {
            Assert.Equal(0, ErofsFormatHandler.ReadExtraDeviceCount(image));
        }
        var expected = ReadEntries(format, image);
        Assert.Equal(metadata.ModificationTime, expected["data"].Metadata!.ModificationTime);
        if (format == "EROFS")
        {
            Assert.Equal(2u, expected["data"].Metadata!.LinkCount);
            Assert.Equal(3u, expected[""].Metadata!.LinkCount);
        }
        var captured = await Capture(format, image);
        var configuration = RepackMetadata.Read(captured, RepackMetadataContext.LocalDirectory)!;
        var superblock = Assert.IsType<JsonObject>(configuration.Configuration["superblock"]);
        var structured = Assert.Single(Directory.GetFiles(captured, "metadata.json", SearchOption.AllDirectories));
        using (var profile = JsonDocument.Parse(await File.ReadAllTextAsync(structured, Token)))
        {
            var partitionInfo = profile.RootElement.GetProperty("partitionInfo");
            foreach (var (key, value) in superblock)
            {
                Assert.Equal(value?.ToString() ?? string.Empty, partitionInfo.GetProperty(key).GetString());
            }
        }
        var documentPath = Directory.GetFiles(captured, "manifest.json", SearchOption.AllDirectories).Single();
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(documentPath, Token)))
        {
            var objects = document.RootElement.GetProperty("ContentObjects");
            Assert.Equal(objects.GetProperty("data").GetProperty("Hash").GetString(),
                objects.GetProperty("alias").GetProperty("Hash").GetString());
            if (format == "EROFS")
            {
                var entriesInManifest = document.RootElement.GetProperty("ParsedEntries").EnumerateArray()
                    .ToDictionary(entry => entry.GetProperty("Path").GetString()!, StringComparer.Ordinal);
                Assert.Equal(2u, entriesInManifest["data"].GetProperty("Metadata").GetProperty("LinkCount").GetUInt32());
                Assert.Equal(3u, entriesInManifest["."].GetProperty("Metadata").GetProperty("LinkCount").GetUInt32());
            }
        }
        File.Delete(image);
        Directory.Delete(source, recursive: true);
        Directory.Delete(Path.Combine(root, "system"), recursive: true);
        var rebuilt = Path.Combine(root, "replayed.img");

        FileSystemPreservation.ReplaySnapshot(captured, rebuilt, Token);

        var actual = ReadEntries(format, rebuilt);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (path, before) in expected)
        {
            var after = actual[path];
            Assert.Equal(before.Mode, after.Mode);
            Assert.Equal(before.Uid, after.Uid);
            Assert.Equal(before.Gid, after.Gid);
            Assert.Equal(before.Metadata, after.Metadata);
            Assert.Equal(before.SymlinkTarget, after.SymlinkTarget);
            foreach (var (name, value) in before.Xattrs!)
            {
                Assert.Equal(value, after.Xattrs![name]);
            }
        }
        Assert.Equal(actual["data"].Inode, actual["alias"].Inode);
        Assert.DoesNotContain(Directory.GetFiles(captured, "*", SearchOption.AllDirectories),
            path => Path.GetExtension(path).Equals(".img", StringComparison.OrdinalIgnoreCase));
        if (OperatingSystem.IsWindows())
        {
            var tool = format switch { "EXT4" => "e2fsck", "EROFS" => "fsck.erofs", _ => "fsck.f2fs" };
            var args = format == "EXT4" ? new[] { tool, "-fn", WslPath(rebuilt) } : new[] { tool, WslPath(rebuilt) };
            var available = await Wsl("sh", "-c", "command -v " + tool);
            if (available.ExitCode == 0)
            {
                var check = await Wsl(args);
                Assert.True(check.ExitCode == 0, check.Output);
            }
        }
        if (Environment.GetEnvironmentVariable("UOTAN_GKI_REPLAY_IMAGES") is { Length: > 0 } artifacts)
        {
            Directory.CreateDirectory(artifacts);
            File.Copy(rebuilt, Path.Combine(artifacts, format.ToLowerInvariant() + ".img"), overwrite: true);
        }
    }

    [Theory]
    [InlineData("EXT4", false)]
    [InlineData("EROFS", true)]
    [InlineData("F2FS", false)]
    public async Task ReplaySnapshot_CorruptOrMissingObject_PreservesExistingOutput(string format, bool missing)
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "data"), "payload", Token);
        var image = Path.Combine(root, "system.img");
        Build(format, source, image);
        var captured = await Capture(format, image);
        var objects = Directory.GetDirectories(captured, "objects", SearchOption.AllDirectories).Single();
        var path = Assert.Single(Directory.GetFiles(objects));
        if (missing)
        {
            File.Delete(path);
        }
        else
        {
            await File.WriteAllTextAsync(path, "corrupt", Token);
        }
        var output = Path.Combine(root, "existing.img");
        await File.WriteAllTextAsync(output, "keep-existing-output", Token);

        if (missing)
        {
            Assert.Throws<FileNotFoundException>(() => FileSystemPreservation.ReplaySnapshot(captured, output, Token));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => FileSystemPreservation.ReplaySnapshot(captured, output, Token));
        }

        Assert.Equal("keep-existing-output", await File.ReadAllTextAsync(output, Token));
    }

    [Fact]
    public async Task CaptureObjectsAsync_DuplicateHashPathChanged_RejectsSnapshot()
    {
        var extracted = Path.Combine(root, "extracted");
        var metadata = Path.Combine(root, "metadata");
        Directory.CreateDirectory(extracted);
        Directory.CreateDirectory(Path.Combine(metadata, "objects"));
        var original = "original-content"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(original));
        await File.WriteAllBytesAsync(Path.Combine(extracted, "first"), original, Token);
        await File.WriteAllBytesAsync(Path.Combine(extracted, "second"), "modified-content"u8.ToArray(), Token);
        await File.WriteAllBytesAsync(Path.Combine(metadata, "objects", hash), original, Token);
        var manifest = new FileSystemPreservationManifest
        {
            Projection = new(StringComparer.Ordinal)
            {
                ["first"] = "file:" + hash,
                ["second"] = "file:" + hash
            }
        };

        await Assert.ThrowsAsync<IOException>(() =>
            FileSystemPreservation.CaptureObjectsAsync(extracted, metadata, manifest, Token));
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    [InlineData("F2FS")]
    public async Task ReplaySnapshot_ReferenceSidecarsRemoved_UsesOnlyProjectMetadata(string format)
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "data"), "captured-content", Token);
        var image = Path.Combine(root, "system.img");
        Build(format, source, image);
        var captured = await Capture(format, image);
        var sidecars = Directory.GetFiles(captured, "system_*", SearchOption.TopDirectoryOnly);
        Assert.Equal(3, sidecars.Length);
        foreach (var sidecar in sidecars)
        {
            await File.WriteAllTextAsync(sidecar, "not authoritative", Token);
        }
        var editedOutput = Path.Combine(root, "edited.img");
        using (RepackMetadata.UseContext(RepackMetadataContext.FromProjectRoot(Path.Combine(root, "metadata"))))
        {
            Assert.True(FileSystemPreservation.TryRestore(Path.Combine(root, "system"), editedOutput, format, Token));
        }
        Assert.Equal("captured-content", ReadFile(format, editedOutput, "data"));
        foreach (var sidecar in sidecars)
        {
            File.Delete(sidecar);
        }
        File.Delete(image);
        Directory.Delete(source, recursive: true);
        Directory.Delete(Path.Combine(root, "system"), recursive: true);
        var moved = Path.Combine(root, "relocated-project", ".uotan", "metadata", "system");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(captured, moved);
        var output = Path.Combine(root, "replayed.img");

        FileSystemPreservation.ReplaySnapshot(moved, output, Token);

        Assert.Equal("captured-content", ReadFile(format, output, "data"));
        Assert.False(Directory.Exists(Path.Combine(root, "system")));
        Assert.False(Directory.Exists(captured));
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    [InlineData("F2FS")]
    public async Task ReplaySnapshot_StructuredDocumentMissing_RejectsReferenceSidecars(string format)
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "data"), "captured-content", Token);
        var image = Path.Combine(root, "system.img");
        Build(format, source, image);
        var captured = await Capture(format, image);
        Assert.Equal(3, Directory.GetFiles(captured, "system_*", SearchOption.TopDirectoryOnly).Length);
        File.Delete(Assert.Single(Directory.GetFiles(captured, "metadata.json", SearchOption.AllDirectories)));
        File.Delete(image);
        Directory.Delete(source, recursive: true);
        Directory.Delete(Path.Combine(root, "system"), recursive: true);
        var output = Path.Combine(root, "replayed.img");
        await File.WriteAllTextAsync(output, "keep-existing-output", Token);

        Assert.Throws<InvalidDataException>(() => FileSystemPreservation.ReplaySnapshot(captured, output, Token));

        Assert.Equal("keep-existing-output", await File.ReadAllTextAsync(output, Token));
    }

    [Fact]
    public async Task ReplaySnapshot_CancelledOrCapacityExceeded_PreservesExistingOutput()
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "data"), "payload", Token);
        var image = Path.Combine(root, "system.img");
        Build("EXT4", source, image);
        var captured = await Capture("EXT4", image);
        var output = Path.Combine(root, "existing.img");
        await File.WriteAllTextAsync(output, "keep-existing-output", Token);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            FileSystemPreservation.ReplaySnapshot(captured, output, cancelled.Token));
        Assert.ThrowsAny<IOException>(() =>
            FileSystemPreservation.ReplaySnapshot(captured, output, Token, maximumOutputBytes: 4096));

        Assert.Equal("keep-existing-output", await File.ReadAllTextAsync(output, Token));
    }

    [Fact]
    public async Task ReplaySnapshot_F2fsMixedCompression_PreservesPerInodeAlgorithmsAndClusters()
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        var payload = Enumerable.Repeat((byte)0x61, 128 * 1024 + 234).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(source, "lz4"), payload, Token);
        await File.WriteAllBytesAsync(Path.Combine(source, "zstd"), payload, Token);
        var metadata = MakeMetadata("F2FS");
        var entries = new List<PreservedInodeEntry>
        {
            Entry(".", 2, 0x41ED, metadata),
            Entry("lz4", 11, 0x81A4, metadata with { F2fsCompression = new(1, 2, 1) }),
            Entry("zstd", 12, 0x81A4, metadata with { F2fsCompression = new(2, 5, 0) })
        };
        var projection = new Dictionary<string, string> { ["lz4"] = "file:before", ["zstd"] = "file:before" };
        var plan = new FileSystemEditPlan(source, new() { ParsedEntries = entries, Projection = projection }, projection);
        var image = Path.Combine(root, "system.img");
        using (plan.Enter())
        {
            Build("F2FS", source, image);
        }
        var captured = await Capture("F2FS", image);
        File.Delete(image);
        Directory.Delete(source, recursive: true);
        Directory.Delete(Path.Combine(root, "system"), recursive: true);
        var output = Path.Combine(root, "replayed.img");

        FileSystemPreservation.ReplaySnapshot(captured, output, Token);

        using var parser = new F2fsParser(output);
        var actual = parser.ListFiles().ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        Assert.Equal(new F2fsInodeCompression(1, 2, 1), actual["lz4"].Metadata!.F2fsCompression);
        Assert.Equal(new F2fsInodeCompression(2, 5, 0), actual["zstd"].Metadata!.F2fsCompression);
        foreach (var path in new[] { "lz4", "zstd" })
        {
            using var data = new MemoryStream();
            parser.ReadFile(path, data);
            Assert.Equal(payload, data.ToArray());
        }
        if (Environment.GetEnvironmentVariable("UOTAN_GKI_REPLAY_IMAGES") is { Length: > 0 } artifacts)
        {
            Directory.CreateDirectory(artifacts);
            File.Copy(output, Path.Combine(artifacts, "mixed-f2fs.img"), overwrite: true);
        }
    }

    [Fact]
    public void ErofsPrefixes_LengthPrefixedAlignedRecords_DecodeWithoutNullTerminators()
    {
        byte[] table = [4, 0, 1, (byte)'a', (byte)'b', (byte)'.', 0, 0, 3, 0, 6, (byte)'c', (byte)'.'];
        var prefixes = UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Models.ErofsXattr.ParsePrefixTable(table, 2);
        Assert.Equal([(1, "ab."), (6, "c.")], prefixes.Select(prefix => ((int)prefix.BaseIndex, prefix.Infix)));
        Assert.Throws<InvalidDataException>(() =>
            UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Models.ErofsXattr.ParsePrefixTable(table[..^1], 2));
    }

    [Fact]
    public async Task ReplaySnapshot_NativeErofsLongAndSharedXattrs_PreservesValues()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") != "1")
        {
            Assert.Skip("Requires the configured Alpine WSL erofs-utils tools.");
        }
        var available = await Wsl("sh", "-c", "command -v mkfs.erofs && command -v setfattr && command -v fsck.erofs");
        if (available.ExitCode != 0)
        {
            Assert.Skip("Alpine erofs-utils and attr are unavailable.");
        }
        var source = "/tmp/uotan-replay-" + Guid.NewGuid().ToString("N");
        var payload = Path.Combine(root, "payload");
        await File.WriteAllBytesAsync(payload, Enumerable.Repeat((byte)0x62, 128 * 1024).ToArray(), Token);
        var image = Path.Combine(root, "system.img");
        var create = await Wsl("mkdir", source);
        Assert.True(create.ExitCode == 0, create.Output);
        try
        {
            foreach (var name in new[] { "data", "other" })
            {
                var copy = await Wsl("cp", WslPath(payload), source + "/" + name);
                Assert.True(copy.ExitCode == 0, copy.Output);
                var attr = await Wsl("setfattr", "-n", "user.vendor.long_name", "-v", "native-value", source + "/" + name);
                Assert.True(attr.ExitCode == 0, attr.Output);
            }
            var make = await Wsl("mkfs.erofs", "-zlz4", "-x0", "--xattr-prefix=user.vendor.", WslPath(image), source);
            Assert.True(make.ExitCode == 0, make.Output);
            var captured = await Capture("EROFS", image);
            File.Delete(image);
            Directory.Delete(Path.Combine(root, "system"), recursive: true);
            var output = Path.Combine(root, "replayed.img");

            FileSystemPreservation.ReplaySnapshot(captured, output, Token);

            using var parser = new ErofsParser(output);
            foreach (var entry in parser.EnumerateFiles())
            {
                var attr = Assert.Single(entry.Xattrs!, attr => attr.Name == "user.vendor.long_name");
                Assert.Equal("native-value"u8.ToArray(), attr.Value);
            }
            var check = await Wsl("fsck.erofs", WslPath(output));
            Assert.True(check.ExitCode == 0, check.Output);
        }
        finally
        {
            await Wsl("rm", "-rf", "--", source);
        }
    }

    [Fact]
    public async Task ReplaySnapshot_NativeErofsMetabox_SurvivesSourceDeletionAndLoopMount()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") != "1")
        {
            Assert.Skip("Requires Alpine WSL erofs-utils and a metabox-capable kernel.");
        }
        var available = await Wsl("sh", "-c", "command -v mkfs.erofs && command -v fsck.erofs && command -v setfattr");
        if (available.ExitCode != 0)
        {
            Assert.Skip("Alpine WSL erofs-utils are unavailable.");
        }
        var source = "/tmp/uotan-erofs-metabox-source-" + Guid.NewGuid().ToString("N");
        var create = await Wsl("mkdir", source);
        Assert.True(create.ExitCode == 0, create.Output);
        var image = Path.Combine(root, "system.img");
        var compressedContent = string.Concat(
            new string('Q', 128 * 1024), new string('R', 128 * 1024), new string('S', 128 * 1024));
        try
        {
            var data = Path.Combine(root, "data");
            await File.WriteAllTextAsync(data, "native-metabox-content", Token);
            var compressed = Path.Combine(root, "compressed");
            await File.WriteAllTextAsync(compressed, compressedContent, Token);
            var copy = await Wsl("cp", WslPath(data), source + "/data");
            Assert.True(copy.ExitCode == 0, copy.Output);
            copy = await Wsl("cp", WslPath(data), source + "/extended");
            Assert.True(copy.ExitCode == 0, copy.Output);
            copy = await Wsl("cp", WslPath(compressed), source + "/compressed");
            Assert.True(copy.ExitCode == 0, copy.Output);
            var attr = await Wsl("setfattr", "-n", "user.native", "-v", "metabox-value", source + "/data");
            Assert.True(attr.ExitCode == 0, attr.Output);
            attr = await Wsl("setfattr", "-n", "user.native", "-v", "metabox-value", source + "/extended");
            Assert.True(attr.ExitCode == 0, attr.Output);
            var chown = await WslRoot("chown", "70000:70000", source + "/extended");
            Assert.True(chown.ExitCode == 0, chown.Output);
            var make = await Wsl("mkfs.erofs", "-m4096", "-x0", "-zlz4", WslPath(image), source);
            Assert.True(make.ExitCode == 0, make.Output);
        }
        finally
        {
            await Wsl("rm", "-rf", "--", source);
        }
        using (var parser = new ErofsParser(image))
        {
            Assert.True(parser.Superblock.HasMetabox);
            Assert.Equal("native-metabox-content", ReadFile("EROFS", image, "data"));
            var entries = parser.EnumerateFiles().ToDictionary(entry => entry.Path);
            Assert.True((entries["data"].Nid & (1UL << 63)) != 0);
            Assert.True((entries["extended"].Nid & (1UL << 63)) != 0);
            Assert.True((entries["compressed"].Nid & (1UL << 63)) != 0);
            Assert.Equal(compressedContent, ReadFile("EROFS", image, "compressed"));
            Assert.Equal("metabox-value"u8.ToArray(), Assert.Single(entries["data"].Xattrs!, x => x.Name == "user.native").Value);
            Assert.Equal("metabox-value"u8.ToArray(), Assert.Single(entries["extended"].Xattrs!, x => x.Name == "user.native").Value);
            Assert.Equal(70000u, entries["extended"].Uid);
        }
        var captured = await Capture("EROFS", image);
        var metadataPath = Directory.GetFiles(captured, "metadata.json", SearchOption.AllDirectories).Single();
        using (var metadataDocument = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath, Token)))
        {
            var inode = metadataDocument.RootElement.GetProperty("rebuild").GetProperty("erofs")
                .GetProperty("inodes").EnumerateArray()
                .Single(record => record.GetProperty("path").GetString()!.EndsWith("compressed", StringComparison.Ordinal));
            Assert.Equal(4, inode.GetProperty("sourceExtentRecordSize").GetInt32());
            Assert.True(inode.GetProperty("sourceExtentClusterSize").GetInt32() >= 128 * 1024);
            Assert.Equal(4096, inode.GetProperty("logicalClusterSize").GetInt32());
        }
        File.Delete(image);
        Directory.Delete(Path.Combine(root, "system"), recursive: true);
        var rebuilt = Path.Combine(root, "replayed.img");

        FileSystemPreservation.ReplaySnapshot(captured, rebuilt, Token);

        using (var parser = new ErofsParser(rebuilt))
        {
            Assert.True(parser.Superblock.HasMetabox);
            Assert.Equal("native-metabox-content", ReadFile("EROFS", rebuilt, "data"));
            var entries = parser.EnumerateFiles().ToDictionary(entry => entry.Path);
            Assert.True((entries["data"].Nid & (1UL << 63)) != 0);
            Assert.True((entries["extended"].Nid & (1UL << 63)) != 0);
            Assert.Equal(compressedContent, ReadFile("EROFS", rebuilt, "compressed"));
            Assert.Equal("metabox-value"u8.ToArray(), Assert.Single(entries["data"].Xattrs!, x => x.Name == "user.native").Value);
            Assert.Equal("metabox-value"u8.ToArray(), Assert.Single(entries["extended"].Xattrs!, x => x.Name == "user.native").Value);
            Assert.Equal(70000u, entries["extended"].Uid);
        }
        var check = await Wsl("fsck.erofs", WslPath(rebuilt));
        Assert.True(check.ExitCode == 0, check.Output);
        var mountpoint = "/tmp/uotan-erofs-metabox-" + Guid.NewGuid().ToString("N");
        var mkdir = await Wsl("mkdir", mountpoint);
        Assert.True(mkdir.ExitCode == 0, mkdir.Output);
        try
        {
            var mount = await WslRoot("mount", "-t", "erofs", "-o", "loop,ro", WslPath(rebuilt), mountpoint);
            Assert.True(mount.ExitCode == 0, mount.Output);
            try
            {
                var content = await Wsl("cat", mountpoint + "/data");
                Assert.Equal("native-metabox-content", content.Output);
                var compressed = await Wsl("sha256sum", mountpoint + "/compressed");
                Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(compressedContent))).ToLowerInvariant(),
                    compressed.Output.Split(' ')[0]);
                var extended = await Wsl("stat", "-c", "%u:%g", mountpoint + "/extended");
                Assert.Equal("70000:70000", extended.Output.Trim());
                var attribute = await Wsl("getfattr", "--absolute-names", "--only-values", "-n", "user.native", mountpoint + "/data");
                Assert.Equal("metabox-value", attribute.Output.Trim());
                attribute = await Wsl("getfattr", "--absolute-names", "--only-values", "-n", "user.native", mountpoint + "/extended");
                Assert.Equal("metabox-value", attribute.Output.Trim());
            }
            finally
            {
                var unmount = await WslRoot("umount", mountpoint);
                Assert.True(unmount.ExitCode == 0, unmount.Output);
            }
        }
        finally
        {
            await Wsl("rmdir", mountpoint);
        }
    }

    [Fact]
    public async Task ReplaySnapshot_NativeErofsCompressedMetabox_PreservesAttributesAfterSourceDeletion()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") != "1")
        {
            Assert.Skip("Requires Alpine WSL erofs-utils and a metabox-capable kernel.");
        }
        var available = await Wsl("sh", "-c", "command -v mkfs.erofs && command -v fsck.erofs && command -v setfattr");
        if (available.ExitCode != 0)
        {
            Assert.Skip("Alpine WSL erofs-utils and attr tools are unavailable.");
        }
        var input = Path.Combine(root, "shared-input");
        Directory.CreateDirectory(input);
        var names = Enumerable.Range(0, 64).Select(index => $"file{index:D2}").ToArray();
        foreach (var name in names)
        {
            await File.WriteAllTextAsync(Path.Combine(input, name), "shared-ea-content", Token);
        }
        var nativeSource = "/tmp/uotan-erofs-shared-mbox-" + Guid.NewGuid().ToString("N");
        var copy = await Wsl("cp", "-r", WslPath(input), nativeSource);
        Assert.True(copy.ExitCode == 0, copy.Output);
        var image = Path.Combine(root, "system.img");
        try
        {
            var set = await Wsl(new[] { "setfattr", "-n", "user.shared", "-v", "shared-value" }
                .Concat(names.Select(name => nativeSource + "/" + name)).ToArray());
            Assert.True(set.ExitCode == 0, set.Output);
            var make = await Wsl("mkfs.erofs", "-m4096", "-x2", "-zlz4", WslPath(image), nativeSource);
            Assert.True(make.ExitCode == 0, make.Output);
        }
        finally
        {
            await Wsl("rm", "-rf", "--", nativeSource);
        }
        using (var parser = new ErofsParser(image))
        {
            Assert.True(parser.Superblock.HasMetabox);
            Assert.Equal(0u, parser.Superblock.FeatureCompat & ErofsSuperblock.CompatSharedEaInMetabox);
            var bytes = await File.ReadAllBytesAsync(image, Token);
            var metaboxOffset = checked((int)parser.Superblock.GetInodeOffset(parser.Superblock.MetaBoxNid));
            var container = ErofsInode.FromBytes(bytes.AsSpan(metaboxOffset, ErofsInode.ExtendedSize));
            Assert.True(container.IsCompressed);
            foreach (var entry in parser.EnumerateFiles())
            {
                Assert.Equal("shared-value"u8.ToArray(),
                    Assert.Single(entry.Xattrs!, xattr => xattr.Name == "user.shared").Value);
            }
        }
        var captured = await Capture("EROFS", image);
        var profilePath = Assert.Single(Directory.GetFiles(captured, "metadata.json", SearchOption.AllDirectories));
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(profilePath, Token)))
        {
            var profile = document.RootElement.GetProperty("rebuild").GetProperty("erofs");
            Assert.True(profile.GetProperty("sourceMetaboxCompressed").GetBoolean());
            Assert.False(profile.GetProperty("sourceSharedEaInMetabox").GetBoolean());
        }
        File.Delete(image);
        Directory.Delete(input, recursive: true);
        Directory.Delete(Path.Combine(root, "system"), recursive: true);
        var rebuilt = Path.Combine(root, "replayed.img");

        FileSystemPreservation.ReplaySnapshot(captured, rebuilt, Token);

        using (var parser = new ErofsParser(rebuilt))
        {
            foreach (var entry in parser.EnumerateFiles())
            {
                Assert.Equal("shared-ea-content", ReadFile("EROFS", rebuilt, entry.Path));
                Assert.Equal("shared-value"u8.ToArray(),
                    Assert.Single(entry.Xattrs!, xattr => xattr.Name == "user.shared").Value);
            }
        }
        var check = await Wsl("fsck.erofs", WslPath(rebuilt));
        Assert.True(check.ExitCode == 0, check.Output);
        var mountpoint = "/tmp/uotan-erofs-shared-mbox-readback-" + Guid.NewGuid().ToString("N");
        var mkdir = await Wsl("mkdir", mountpoint);
        Assert.True(mkdir.ExitCode == 0, mkdir.Output);
        try
        {
            var mount = await WslRoot("mount", "-t", "erofs", "-o", "loop,ro", WslPath(rebuilt), mountpoint);
            Assert.True(mount.ExitCode == 0, mount.Output);
            try
            {
                var attr = await Wsl("getfattr", "--absolute-names", "--only-values", "-n",
                    "user.shared", mountpoint + "/file00");
                Assert.True(attr.ExitCode == 0, attr.Output);
                Assert.Equal("shared-value", attr.Output.Trim());
            }
            finally
            {
                var umount = await WslRoot("umount", mountpoint);
                Assert.True(umount.ExitCode == 0, umount.Output);
            }
        }
        finally
        {
            await Wsl("rmdir", mountpoint);
        }
    }

    [Fact]
    public async Task ReplaySnapshot_SharedEaInMetabox_KernelReadsSourceAndReplayedAttributes()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") != "1")
        {
            Assert.Skip("Requires Alpine WSL erofs-utils and a metabox-capable kernel.");
        }
        var available = await Wsl("sh", "-c", "command -v fsck.erofs && command -v getfattr");
        if (available.ExitCode != 0)
        {
            Assert.Skip("Alpine WSL erofs-utils and attr tools are unavailable.");
        }
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "data"), Enumerable.Repeat((byte)'Q', 4096).ToArray(), Token);
        var image = Path.Combine(root, "system.img");
        var entries = new List<PreservedInodeEntry>
        {
            new() { Path = ".", Inode = 2, Mode = 0x41ED },
            new()
            {
                Path = "data", Inode = 3, Mode = 0x81A4,
                Xattrs = new(StringComparer.Ordinal) { ["user.shared"] = "metabox-value"u8.ToArray() }
            }
        };
        var projection = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["data"] = "file:original"
        };
        using (new FileSystemEditPlan(source,
            new FileSystemPreservationManifest { ParsedEntries = entries, Projection = projection }, projection).Enter())
        {
            new ErofsBuilder().Build(source, image, new ErofsOptions
            {
                Metabox = true,
                Compress = false,
                TailPacking = false,
                Fragments = false,
                SuperblockChecksum = false
            });
        }
        var bytes = await File.ReadAllBytesAsync(image, Token);
        using (var parser = new ErofsParser(image))
        {
            var sb = parser.Superblock;
            var file = Assert.Single(parser.EnumerateFiles());
            Assert.True(sb.HasMetabox);
            Assert.True((file.Nid & (1UL << 63)) != 0);
            var containerOffset = checked((int)sb.GetInodeOffset(sb.MetaBoxNid));
            var container = ErofsInode.FromBytes(bytes.AsSpan(containerOffset, ErofsInode.ExtendedSize));
            Assert.Equal(ErofsInode.DataLayoutFlat, container.DataLayout);
            Assert.InRange(container.Size, 1UL, 4096UL);
            var oldMetaboxStart = checked((int)container.RawBlockAddress * 4096);
            var newMetaboxStart = bytes.Length;
            Array.Resize(ref bytes, checked(bytes.Length + 8192));
            bytes.AsSpan(oldMetaboxStart, checked((int)container.Size))
                .CopyTo(bytes.AsSpan(newMetaboxStart));
            var fileOffset = checked((int)((file.Nid & ~(1UL << 63)) * 32));
            var inode = ErofsInode.FromBytes(bytes.AsSpan(newMetaboxStart + fileOffset, ErofsInode.ExtendedSize));
            Assert.Equal(ErofsInode.DataLayoutFlat, inode.DataLayout);
            var ibodyOffset = newMetaboxStart + fileOffset + inode.OnDiskSize;
            var ibody = bytes.AsSpan(ibodyOffset, inode.XattrTotalSize);
            Assert.Empty(ErofsXattr.ParseSharedXattrIds(ibody, inode.XattrICount));
            Assert.True(ErofsXattr.TryReadEntry(ibody, ErofsXattr.HeaderSize, null, out var attr, out var entrySize));
            Assert.Equal("user.shared", attr.Name);
            ibody.Slice(ErofsXattr.HeaderSize, entrySize).CopyTo(bytes.AsSpan(newMetaboxStart + 4096));
            bytes[ibodyOffset + 4] = 1;
            bytes.AsSpan(ibodyOffset + ErofsXattr.HeaderSize, 4).Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(newMetaboxStart + fileOffset + 2), 2);
            container.U = checked((uint)(newMetaboxStart / 4096));
            container.Size = checked((ulong)(4096 + entrySize));
            container.WriteTo(bytes.AsSpan(containerOffset, container.OnDiskSize));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1024 + 8),
                sb.FeatureCompat | ErofsSuperblock.CompatSharedEaInMetabox);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1024 + 36), checked((uint)(bytes.Length / 4096)));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1024 + 44), 1);
        }
        await File.WriteAllBytesAsync(image, bytes, Token);
        using (var parser = new ErofsParser(image))
        {
            var entry = Assert.Single(parser.EnumerateFiles());
            Assert.Equal("metabox-value"u8.ToArray(),
                Assert.Single(entry.Xattrs!, attr => attr.Name == "user.shared").Value);
        }
        var originalCheck = await Wsl("fsck.erofs", "--xattrs", WslPath(image));
        Assert.True(originalCheck.ExitCode == 0, originalCheck.Output);
        await AssertSharedEaKernelReadback(image);
        var captured = await Capture("EROFS", image);
        var profilePath = Assert.Single(Directory.GetFiles(captured, "metadata.json", SearchOption.AllDirectories));
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(profilePath, Token)))
        {
            var profile = document.RootElement.GetProperty("rebuild").GetProperty("erofs");
            Assert.True(profile.GetProperty("sourceSharedEaInMetabox").GetBoolean());
            Assert.False(profile.GetProperty("sourceMetaboxCompressed").GetBoolean());
        }
        foreach (var reference in Directory.GetFiles(captured, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => path.EndsWith("_fs_config", StringComparison.Ordinal)
                         || path.EndsWith("_file_contexts", StringComparison.Ordinal)
                         || path.EndsWith("_info", StringComparison.Ordinal)))
        {
            File.Delete(reference);
        }
        File.Delete(image);
        Directory.Delete(source, recursive: true);
        Directory.Delete(Path.Combine(root, "system"), recursive: true);
        var replayed = Path.Combine(root, "replayed.img");
        FileSystemPreservation.ReplaySnapshot(captured, replayed, Token);
        using (var parser = new ErofsParser(replayed))
        {
            var entry = Assert.Single(parser.EnumerateFiles());
            Assert.Equal("metabox-value"u8.ToArray(),
                Assert.Single(entry.Xattrs!, attr => attr.Name == "user.shared").Value);
            Assert.Equal(Enumerable.Repeat((byte)'Q', 4096).ToArray(), parser.ReadFileBytes("data"));
        }
        var replayCheck = await Wsl("fsck.erofs", "--xattrs", WslPath(replayed));
        Assert.True(replayCheck.ExitCode == 0, replayCheck.Output);
        await AssertSharedEaKernelReadback(replayed);
    }

    private static async Task AssertSharedEaKernelReadback(string image)
    {
        var mountpoint = "/tmp/uotan-erofs-shared-ea-" + Guid.NewGuid().ToString("N");
        var mkdir = await Wsl("mkdir", mountpoint);
        Assert.True(mkdir.ExitCode == 0, mkdir.Output);
        try
        {
            var mount = await WslRoot("mount", "-t", "erofs", "-o", "loop,ro", WslPath(image), mountpoint);
            Assert.True(mount.ExitCode == 0, mount.Output);
            try
            {
                var attr = await Wsl("getfattr", "--absolute-names", "--only-values", "-n",
                    "user.shared", mountpoint + "/data");
                Assert.True(attr.ExitCode == 0, attr.Output);
                Assert.Equal("metabox-value", attr.Output.Trim());
                var content = await Wsl("sha256sum", mountpoint + "/data");
                Assert.True(content.ExitCode == 0, content.Output);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(
                    Enumerable.Repeat((byte)'Q', 4096).ToArray())).ToLowerInvariant(),
                    content.Output.Split(' ')[0]);
            }
            finally
            {
                var umount = await WslRoot("umount", mountpoint);
                Assert.True(umount.ExitCode == 0, umount.Output);
            }
        }
        finally
        {
            var remove = await Wsl("rmdir", mountpoint);
            Assert.True(remove.ExitCode == 0, remove.Output);
        }
    }

    [Fact]
    public async Task ReplaySnapshot_ErofsDeclaredExtraDevice_CapturesTableAndFlattensOutput()
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "data"), "primary-device-content", Token);
        var image = Path.Combine(root, "system.img");
        new ErofsBuilder().Build(source, image, new ErofsOptions
        {
            Compress = false,
            TailPacking = false,
            ExtraDeviceCount = 1,
            ExtraDeviceImages = [new ErofsDeviceSpec("unused.blob", 4096)]
        });
        using (var parser = new ErofsParser(image))
        {
            Assert.Equal(1, parser.Superblock.ExtraDevices);
            Assert.Equal("primary-device-content", ReadFile("EROFS", image, "data"));
        }
        var captured = await Capture("EROFS", image);
        var profilePath = Assert.Single(Directory.GetFiles(captured, "metadata.json", SearchOption.AllDirectories));
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(profilePath, Token)))
        {
            var device = Assert.Single(document.RootElement.GetProperty("rebuild").GetProperty("erofs")
                .GetProperty("sourceDevices").EnumerateArray());
            var tag = device.GetProperty("tag").GetBytesFromBase64();
            Assert.Equal("unused.blob", System.Text.Encoding.UTF8.GetString(tag).TrimEnd('\0'));
            Assert.Equal(1UL, device.GetProperty("blocks").GetUInt64());
            Assert.True(device.GetProperty("uniAddr").GetUInt64() > 0);
        }
        File.Delete(image);
        Directory.Delete(source, recursive: true);
        Directory.Delete(Path.Combine(root, "system"), recursive: true);
        var replayed = Path.Combine(root, "replayed.img");
        FileSystemPreservation.ReplaySnapshot(captured, replayed, Token);
        using (var parser = new ErofsParser(replayed))
        {
            Assert.Equal(0, parser.Superblock.ExtraDevices);
            Assert.Equal("primary-device-content", ReadFile("EROFS", replayed, "data"));
        }
        if (OperatingSystem.IsWindows())
        {
            var available = await Wsl("sh", "-c", "command -v fsck.erofs");
            if (available.ExitCode != 0)
                Assert.Skip("Alpine WSL erofs-utils are unavailable.");
            var check = await Wsl("fsck.erofs", WslPath(replayed));
            Assert.True(check.ExitCode == 0, check.Output);
            var mountpoint = "/tmp/uotan-erofs-flat-device-" + Guid.NewGuid().ToString("N");
            var mkdir = await Wsl("mkdir", mountpoint);
            Assert.True(mkdir.ExitCode == 0, mkdir.Output);
            try
            {
                var mount = await WslRoot("mount", "-t", "erofs", "-o", "loop,ro", WslPath(replayed), mountpoint);
                Assert.True(mount.ExitCode == 0, mount.Output);
                try
                {
                    var content = await Wsl("cat", mountpoint + "/data");
                    Assert.True(content.ExitCode == 0, content.Output);
                    Assert.Equal("primary-device-content", content.Output);
                }
                finally
                {
                    var unmount = await WslRoot("umount", mountpoint);
                    Assert.True(unmount.ExitCode == 0, unmount.Output);
                }
            }
            finally
            {
                var remove = await Wsl("rmdir", mountpoint);
                Assert.True(remove.ExitCode == 0, remove.Output);
            }
        }
    }

    [Fact]
    public async Task ReplaySnapshot_NativeErofsExternalBlob_SurvivesDeletionOfBothDeviceImages()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") != "1")
            Assert.Skip("Requires Alpine WSL erofs-utils.");
        var available = await Wsl("sh", "-c", "command -v mkfs.erofs && command -v fsck.erofs");
        if (available.ExitCode != 0)
            Assert.Skip("Alpine WSL erofs-utils are unavailable.");
        var payload = Enumerable.Range(0, 16 * 1024).Select(i => (byte)(i * 37)).ToArray();
        var input = Path.Combine(root, "input");
        await File.WriteAllBytesAsync(input, payload, Token);
        var nativeSource = "/tmp/uotan-erofs-blob-source-" + Guid.NewGuid().ToString("N");
        var create = await Wsl("mkdir", nativeSource);
        Assert.True(create.ExitCode == 0, create.Output);
        var image = Path.Combine(root, "system.img");
        var blob = Path.Combine(root, "blob.img");
        try
        {
            var copy = await Wsl("cp", WslPath(input), nativeSource + "/data");
            Assert.True(copy.ExitCode == 0, copy.Output);
            var make = await Wsl("mkfs.erofs", "--blobdev=" + WslPath(blob), "--chunksize=4096",
                "-x0", WslPath(image), nativeSource);
            Assert.True(make.ExitCode == 0, make.Output);
        }
        finally
        {
            await Wsl("rm", "-rf", "--", nativeSource);
        }
        Assert.True(new FileInfo(blob).Length > 0);
        Assert.Equal(1, ErofsFormatHandler.ReadExtraDeviceCount(image));
        using (var primary = File.OpenRead(image))
        using (var external = File.OpenRead(blob))
        using (var parser = new ErofsParser([primary, external]))
        {
            Assert.Equal(1, parser.Superblock.ExtraDevices);
            Assert.Equal(payload, parser.ReadFileBytes("data"));
        }
        var originalCheck = await Wsl("fsck.erofs", "--device=" + WslPath(blob), WslPath(image));
        Assert.True(originalCheck.ExitCode == 0, originalCheck.Output);
        var hostMetadata = Path.Combine(root, "host-metadata");
        var hostExtracted = Path.Combine(root, "host-extracted");
        using (RepackMetadata.UseContext(RepackMetadataContext.FromProjectRoot(hostMetadata)))
        {
            var hostResult = await FirmwareService.Instance.UnpackFileSystemImageAsync(image, hostExtracted,
                cancellationToken: Token, erofsExtraDevicePaths: [blob]);
            Assert.True(hostResult.Success, hostResult.Summary);
        }
        var hostSnapshotDirectory = Path.GetDirectoryName(
            Assert.Single(Directory.GetFiles(hostMetadata, RepackMetadata.FileName, SearchOption.AllDirectories)))!;
        Assert.Equal(3, Directory.GetFiles(hostSnapshotDirectory, "system_*", SearchOption.TopDirectoryOnly).Length);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(hostExtracted, "data"), Token));
        var withoutDevice = await new ErofsFormatHandler().UnpackAsync(image,
            Path.Combine(root, "missing-device"), cancellationToken: Token);
        Assert.False(withoutDevice.Success);
        var captured = await Capture("EROFS", image, new UnpackOptions { ErofsExtraDevicePaths = [blob] });
        var profilePath = Assert.Single(Directory.GetFiles(captured, "metadata.json", SearchOption.AllDirectories));
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(profilePath, Token)))
        {
            var device = Assert.Single(document.RootElement.GetProperty("rebuild").GetProperty("erofs")
                .GetProperty("sourceDevices").EnumerateArray());
            Assert.True(device.GetProperty("blocks").GetUInt64() > 0);
        }
        Assert.DoesNotContain(Directory.GetFiles(captured, "*", SearchOption.AllDirectories),
            path => Path.GetExtension(path).Equals(".img", StringComparison.OrdinalIgnoreCase));
        File.Delete(image);
        File.Delete(blob);
        File.Delete(input);
        Directory.Delete(hostExtracted, recursive: true);
        Directory.Delete(Path.Combine(root, "system"), recursive: true);
        var replayed = Path.Combine(root, "replayed.img");

        FileSystemPreservation.ReplaySnapshot(captured, replayed, Token);
        var hostReplay = Path.Combine(root, "host-replayed.img");
        FileSystemPreservation.ReplaySnapshot(hostSnapshotDirectory, hostReplay, Token);
        using (var parser = new ErofsParser(hostReplay))
        {
            Assert.Equal(payload, parser.ReadFileBytes("data"));
        }

        using (var parser = new ErofsParser(replayed))
        {
            Assert.Equal(0, parser.Superblock.ExtraDevices);
            Assert.Equal(payload, parser.ReadFileBytes("data"));
        }
        var check = await Wsl("fsck.erofs", WslPath(replayed));
        Assert.True(check.ExitCode == 0, check.Output);
        var mountpoint = "/tmp/uotan-erofs-blob-replayed-" + Guid.NewGuid().ToString("N");
        var mkdir = await Wsl("mkdir", mountpoint);
        Assert.True(mkdir.ExitCode == 0, mkdir.Output);
        try
        {
            var mount = await WslRoot("mount", "-t", "erofs", "-o", "loop,ro", WslPath(replayed), mountpoint);
            Assert.True(mount.ExitCode == 0, mount.Output);
            try
            {
                var readback = await Wsl("sha256sum", mountpoint + "/data");
                Assert.True(readback.ExitCode == 0, readback.Output);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                    readback.Output.Split(' ')[0]);
            }
            finally
            {
                var umount = await WslRoot("umount", mountpoint);
                Assert.True(umount.ExitCode == 0, umount.Output);
            }
        }
        finally
        {
            var remove = await Wsl("rmdir", mountpoint);
            Assert.True(remove.ExitCode == 0, remove.Output);
        }
        if (Environment.GetEnvironmentVariable("UOTAN_GKI_REPLAY_IMAGES") is { Length: > 0 } artifacts)
        {
            Directory.CreateDirectory(artifacts);
            File.Copy(replayed, Path.Combine(artifacts, "external-erofs.img"), overwrite: true);
        }
    }

    [Fact]
    public async Task ReplaySnapshot_NativeExt4ExternalAndEaInodeAttributes_SurviveSourceDeletion()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") != "1")
        {
            Assert.Skip("Requires the configured Alpine WSL e2fsprogs tools.");
        }
        var availability = await Wsl("sh", "-c", "command -v mkfs.ext4 && command -v debugfs && command -v e2fsck");
        if (availability.ExitCode != 0)
        {
            Assert.Skip("Alpine e2fsprogs is unavailable.");
        }
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "top"));
        await File.WriteAllTextAsync(Path.Combine(source, "data"), "native-source", Token);
        var largeText = new string('q', 6000);
        var largeValue = System.Text.Encoding.UTF8.GetBytes(largeText);
        var image = Path.Combine(root, "system.img");
        var make = await Wsl("mkfs.ext4", "-F", "-b", "4096", "-I", "256", "-O", "ea_inode",
            "-d", WslPath(source), WslPath(image), "16384");
        Assert.True(make.ExitCode == 0, make.Output);
        var set = await Wsl("debugfs", "-w", "-R", $"ea_set /data user.large {largeText}", WslPath(image));
        Assert.True(set.ExitCode == 0, set.Output);
        var smallValue = new string('x', 1200);
        set = await Wsl("debugfs", "-w", "-R", $"ea_set /data user.external {smallValue}", WslPath(image));
        Assert.True(set.ExitCode == 0, set.Output);
        set = await Wsl("debugfs", "-w", "-R", "set_inode_field /data flags 0x00080400", WslPath(image));
        Assert.True(set.ExitCode == 0, set.Output);
        set = await Wsl("debugfs", "-w", "-R", "set_inode_field /top flags 0x000A0000", WslPath(image));
        Assert.True(set.ExitCode == 0, set.Output);
        using (var parser = new Ext4Parser(image))
        {
            var file = Assert.Single(parser.ListFiles(), entry => entry.Path == "data");
            Assert.Equal(0x400u, file.Flags & 0x400u);
            Assert.Equal(largeValue, file.Xattrs!["user.large"]);
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(smallValue), file.Xattrs["user.external"]);
            Assert.Equal(0x20000u, parser.ListEntriesWithRoot().Single(entry => entry.Path == "top").Flags & 0x20000u);
        }
        var captured = await Capture("EXT4", image);
        File.Delete(image);
        Directory.Delete(source, recursive: true);
        Directory.Delete(Path.Combine(root, "system"), recursive: true);
        var rebuilt = Path.Combine(root, "replayed.img");

        FileSystemPreservation.ReplaySnapshot(captured, rebuilt, Token);

        var check = await Wsl("e2fsck", "-fn", WslPath(rebuilt));
        Assert.True(check.ExitCode == 0, check.Output);
        using var replayed = new Ext4Parser(rebuilt);
        var result = Assert.Single(replayed.ListFiles(), entry => entry.Path == "data");
        Assert.Equal(0x400u, result.Flags & 0x400u);
        Assert.Equal(0x20000u, replayed.ListEntriesWithRoot().Single(entry => entry.Path == "top").Flags & 0x20000u);
        Assert.Equal(largeValue, result.Xattrs!["user.large"]);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(smallValue), result.Xattrs["user.external"]);
    }

    [Fact]
    public async Task ReplaySnapshot_NativeExt4Casefold_PreservesEncodingAndDirectorySemantics()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") != "1")
            Assert.Skip("Requires Alpine WSL e2fsprogs.");
        var available = await Wsl("sh", "-c", "command -v mkfs.ext4 && command -v debugfs && command -v e2fsck");
        if (available.ExitCode != 0)
            Assert.Skip("Alpine WSL e2fsprogs are unavailable.");
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "Case"));
        await File.WriteAllTextAsync(Path.Combine(source, "Case", "Upper"), "casefold-content", Token);
        var image = Path.Combine(root, "system.img");
        var make = await Wsl("mkfs.ext4", "-F", "-b", "4096", "-O", "casefold", "-E", "encoding=utf8",
            "-d", WslPath(source), WslPath(image), "8192");
        Assert.True(make.ExitCode == 0, make.Output);
        var flag = await Wsl("debugfs", "-w", "-R", "set_inode_field /Case flags 0x40080000", WslPath(image));
        Assert.True(flag.ExitCode == 0, flag.Output);
        var sourceCheck = await Wsl("e2fsck", "-fn", WslPath(image));
        Assert.True(sourceCheck.ExitCode == 0, sourceCheck.Output);
        using (var parser = new Ext4Parser(image))
        {
            Assert.Equal(1, parser.Superblock.Encoding);
            Assert.Equal(0x40000000u, Assert.Single(parser.ListEntriesWithRoot(), e => e.Path == "Case").Flags & 0x40000000u);
        }

        var captured = await Capture("EXT4", image);
        File.Delete(image);
        Directory.Delete(source, recursive: true);
        Directory.Delete(Path.Combine(root, "system"), recursive: true);
        var replayed = Path.Combine(root, "replayed.img");

        FileSystemPreservation.ReplaySnapshot(captured, replayed, Token);

        using (var parser = new Ext4Parser(replayed))
        {
            Assert.Equal(1, parser.Superblock.Encoding);
            Assert.Equal(0x20000u, parser.Superblock.FeatureIncompat & 0x20000u);
            Assert.Equal(0x40000000u, Assert.Single(parser.ListEntriesWithRoot(), e => e.Path == "Case").Flags & 0x40000000u);
            using var content = new MemoryStream();
            parser.ReadFile("Case/Upper", content);
            Assert.Equal("casefold-content"u8.ToArray(), content.ToArray());
        }
        var check = await Wsl("e2fsck", "-fn", WslPath(replayed));
        Assert.True(check.ExitCode == 0, check.Output);
        if (Environment.GetEnvironmentVariable("UOTAN_GKI_REPLAY_IMAGES") is { Length: > 0 } artifacts)
        {
            Directory.CreateDirectory(artifacts);
            File.Copy(replayed, Path.Combine(artifacts, "casefold-ext4.img"), overwrite: true);
        }
    }

    [Fact]
    public async Task ReplaySnapshot_NativeF2fsMountedSource_PreservesInodeAttributesAndContent()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") != "1")
        {
            Assert.Skip("Requires Alpine WSL with loop mounting and F2FS security xattrs.");
        }
        var available = await Wsl("sh", "-c", "command -v mkfs.f2fs && command -v setfattr && command -v fsck.f2fs");
        if (available.ExitCode != 0)
        {
            Assert.Skip("Alpine WSL F2FS tools are unavailable.");
        }
        var security = await Wsl("sh", "-c", "zcat /proc/config.gz | grep -qx 'CONFIG_F2FS_FS_SECURITY=y'");
        if (security.ExitCode != 0)
        {
            Assert.Skip("The WSL kernel does not support F2FS security xattrs.");
        }
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "TestInfrastructure", "FilesystemGkiReplay", "create-native-f2fs.sh"));
        var image = Path.Combine(root, "system.img");
        var create = await WslRoot("sh", WslPath(fixture), WslPath(image));
        Assert.True(create.ExitCode == 0, create.Output);
        Dictionary<string, PreservedInodeEntry> before;
        using (var parser = new F2fsParser(image))
        {
            before = parser.ListEntriesWithRoot().Select(entry => new PreservedInodeEntry
            {
                Path = entry.Path,
                Inode = entry.Inode,
                Mode = entry.Mode,
                Uid = entry.Uid,
                Gid = entry.Gid,
                Flags = entry.Flags,
                Metadata = entry.Metadata,
                Xattrs = entry.Xattrs?.ToDictionary(StringComparer.Ordinal)
            }).ToDictionary(entry => FileSystemEditPlan.Normalize(entry.Path), StringComparer.Ordinal);
        }
        var captured = await Capture("F2FS", image);
        var manifestPath = Directory.GetFiles(captured, "manifest.json", SearchOption.AllDirectories).Single();
        var manifest = JsonSerializer.Deserialize<FileSystemPreservationManifest>(await File.ReadAllTextAsync(manifestPath, Token))!;
        Assert.Equal(0x400u, manifest.ParsedEntries.Single(entry => entry.Path == "data").Flags & 0x400u);
        var extracted = Path.Combine(root, "system");
        var plan = new FileSystemEditPlan(extracted, manifest,
            new Dictionary<string, string>(manifest.Projection, StringComparer.Ordinal));
        Assert.Equal(0x400u, plan.Entries["data"].Flags & 0x400u);
        File.Delete(image);
        Directory.Delete(Path.Combine(root, "system"), recursive: true);
        var rebuilt = Path.Combine(root, "replayed.img");

        FileSystemPreservation.ReplaySnapshot(captured, rebuilt, Token);

        var after = ReadEntries("F2FS", rebuilt);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.Equal(before["data"].Metadata, after["data"].Metadata);
        Assert.Equal(before["data"].Mode, after["data"].Mode);
        Assert.Equal(0x400u, before["data"].Flags & 0x400u);
        Assert.Equal(before["data"].Flags & 0x400u, after["data"].Flags & 0x400u);
        Assert.Equal(0x1000u, before["empty"].Flags & 0x1000u);
        Assert.Equal(before["empty"].Flags & 0x1000u, after["empty"].Flags & 0x1000u);
        Assert.Equal(before["data"].Uid, after["data"].Uid);
        Assert.Equal(before["data"].Gid, after["data"].Gid);
        Assert.Equal(before["data"].Xattrs!["user.binary"], after["data"].Xattrs!["user.binary"]);
        Assert.Equal(before["data"].Xattrs!["security.capability"], after["data"].Xattrs!["security.capability"]);
        Assert.Equal(after["data"].Inode, after["alias"].Inode);
        Assert.Equal("native-f2fs-content", ReadFile("F2FS", rebuilt, "data"));
        var check = await Wsl("fsck.f2fs", "--dry-run", "-f", WslPath(rebuilt));
        Assert.True(check.ExitCode == 0, check.Output);
        var mountpoint = "/tmp/uotan-f2fs-replay-" + Guid.NewGuid().ToString("N");
        var mkdir = await Wsl("mkdir", mountpoint);
        Assert.True(mkdir.ExitCode == 0, mkdir.Output);
        try
        {
            var mount = await WslRoot("mount", "-t", "f2fs", "-o", "loop,ro", WslPath(rebuilt), mountpoint);
            Assert.True(mount.ExitCode == 0, mount.Output);
            try
            {
                var stat = await Wsl("stat", "-c", "%u:%g:%a", mountpoint + "/data");
                Assert.Equal("70001:80002:4755", stat.Output.Trim());
                var content = await Wsl("cat", mountpoint + "/alias");
                Assert.Equal("native-f2fs-content", content.Output);
                var attribute = await WslRoot("getfattr", "-n", "security.capability", "-e", "hex", mountpoint + "/data");
                Assert.Contains("security.capability=0x0000000201000000", attribute.Output, StringComparison.Ordinal);
            }
            finally
            {
                var unmount = await WslRoot("umount", mountpoint);
                Assert.True(unmount.ExitCode == 0, unmount.Output);
            }
        }
        finally
        {
            await Wsl("rmdir", mountpoint);
        }
        if (Environment.GetEnvironmentVariable("UOTAN_GKI_REPLAY_IMAGES") is { Length: > 0 } artifacts)
        {
            Directory.CreateDirectory(artifacts);
            File.Copy(rebuilt, Path.Combine(artifacts, "native-f2fs.img"), overwrite: true);
        }
    }

    [Fact]
    public void Ext4Inode_RawExtendedFields_DecodesSignedEpochAndNanoseconds()
    {
        var raw = new byte[256];
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(128), 32);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(8), -1);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(140), (999_999_999u << 2) | 1);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(156), 7654321);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(100), 0x89ABCDEF);
        var inode = Ext4Inode.FromBytes(raw, 256);
        Assert.Equal(new FileSystemTimestamp(4_294_967_295, 999_999_999), inode.Metadata.AccessTime);
        Assert.Equal(7654321u, inode.Metadata.ProjectId);
        Assert.Equal(0x89ABCDEFu, inode.Metadata.Generation);
    }

    [Fact]
    public void F2fsInode_RawFields_DecodesIndependentAccessBirthAndGeneration()
    {
        var raw = new byte[4096];
        raw[3] = F2fsInode.ExtraAttrFlag;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(360), 24);
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(32), -10);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(56), 123456789);
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(372), 5_000_000_000);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(380), 987654321);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(68), 8765);
        var inode = F2fsInode.FromBytes(raw);
        Assert.Equal(new FileSystemTimestamp(-10, 123456789), inode.Metadata.AccessTime);
        Assert.Equal(new FileSystemTimestamp(5_000_000_000, 987654321), inode.Metadata.BirthTime);
        Assert.Equal(8765u, inode.Metadata.Generation);
    }

    [Theory]
    [InlineData("EXT4", 0x02000000u)]
    [InlineData("F2FS", 0x80000000u)]
    public void ValidateInodeFlags_UnsupportedSourceFlag_RejectsSilentLoss(string format, uint flag)
    {
        var entry = new PreservedInodeEntry { Path = "data", Flags = flag };
        var failure = Assert.Throws<NotSupportedException>(() => FileSystemPreservation.ValidateInodeFlags([entry], format));
        Assert.Contains("data", failure.Message, StringComparison.Ordinal);
    }

    private async Task<string> Capture(string format, string image, UnpackOptions? options = null)
    {
        var directory = Path.Combine(root, "metadata");
        using var context = RepackMetadata.UseContext(RepackMetadataContext.FromProjectRoot(directory));
        IFormatHandler handler = format switch
        {
            "EXT4" => new Ext4FormatHandler(),
            "EROFS" => new ErofsFormatHandler(),
            _ => new F2fsFormatHandler()
        };
        var result = await handler.UnpackAsync(image, Path.Combine(root, "system"), options, Token);
        Assert.True(result.Success, result.Summary);
        return Path.Combine(directory, "system");
    }

    private static PreservedInodeEntry Entry(string path, ulong inode, ushort mode, FileSystemInodeMetadata metadata) => new()
    {
        Path = path,
        Inode = inode,
        Mode = mode,
        Uid = 70001,
        Gid = 80002,
        Metadata = metadata,
        Xattrs = new(StringComparer.Ordinal)
        {
            ["security.selinux"] = "u:object_r:system_file:s0\0"u8.ToArray(),
            ["security.capability"] = [0, 0, 0, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            ["user.empty"] = [],
            ["user.binary"] = Enumerable.Range(0, 1024).Select(index => (byte)index).ToArray()
        }
    };

    private static FileSystemInodeMetadata MakeMetadata(string format)
    {
        var result = new FileSystemInodeMetadata { ModificationTime = new(2_500_000_000, 987654321) };
        return format == "EROFS" ? result : result with
        {
            AccessTime = new(-10, 123456789),
            ChangeTime = new(0, 1),
            BirthTime = new(2_000_000_000, 2),
            Generation = 0x89ABCDEF,
            ProjectId = 70000,
            Version = format == "EXT4" ? 0x1234567890UL : null
        };
    }

    private static void Build(string format, string source, string image)
    {
        switch (format)
        {
            case "EXT4": Ext4Builder.Build(source, image); break;
            case "EROFS": new ErofsBuilder().Build(source, image, new ErofsOptions { Compress = false }); break;
            default: F2fsBuilder.Build(source, image, new F2fsOptions { AndroidNativeProfile = false }); break;
        }
    }

    private static string ReadFile(string format, string image, string path)
    {
        using var output = new MemoryStream();
        switch (format)
        {
            case "EXT4":
                using (var parser = new Ext4Parser(image))
                {
                    parser.ReadFile(path, output);
                }
                break;
            case "EROFS":
                using (var parser = new ErofsParser(image))
                {
                    parser.ReadFile(path, output);
                }
                break;
            default:
                using (var parser = new F2fsParser(image))
                {
                    parser.ReadFile(path, output);
                }
                break;
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static Dictionary<string, PreservedInodeEntry> ReadEntries(string format, string image)
    {
        IEnumerable<PreservedInodeEntry> entries;
        if (format == "EXT4")
        {
            using var parser = new Ext4Parser(image);
            entries = parser.ListEntriesWithRoot().Select(e => new PreservedInodeEntry
            {
                Path = e.Path,
                Inode = e.Inode,
                Flags = e.Flags,
                Mode = e.Mode,
                Uid = e.Uid,
                Gid = e.Gid,
                Metadata = e.Metadata,
                SymlinkTarget = e.SymlinkTarget,
                Xattrs = e.Xattrs?.ToDictionary(StringComparer.Ordinal)
            }).ToList();
        }
        else if (format == "F2FS")
        {
            using var parser = new F2fsParser(image);
            entries = parser.ListEntriesWithRoot().Select(e => new PreservedInodeEntry
            {
                Path = e.Path,
                Inode = e.Inode,
                Flags = e.Flags,
                Mode = e.Mode,
                Uid = e.Uid,
                Gid = e.Gid,
                Metadata = e.Metadata,
                SymlinkTarget = e.SymlinkTarget,
                Xattrs = e.Xattrs?.ToDictionary(StringComparer.Ordinal)
            }).ToList();
        }
        else
        {
            using var parser = new ErofsParser(image);
            entries = parser.EnumerateEntries().Select(e => new PreservedInodeEntry
            {
                Path = e.Path,
                Inode = e.Nid,
                Mode = e.Mode,
                Uid = e.Uid,
                Gid = e.Gid,
                Metadata = new()
                {
                    ModificationTime = new(checked((long)e.MTime), e.MTimeNanoseconds),
                    LinkCount = checked((uint)e.LinkCount)
                },
                SymlinkTarget = e.SymlinkTarget,
                Xattrs = e.Xattrs?.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal)
            }).ToList();
        }
        return entries.ToDictionary(e => FileSystemEditPlan.Normalize(e.Path), StringComparer.Ordinal);
    }

    private static Task<(int ExitCode, string Output)> Wsl(params string[] arguments) => RunWsl(null, arguments);

    private static Task<(int ExitCode, string Output)> WslRoot(params string[] arguments) => RunWsl("root", arguments);

    private static async Task<(int ExitCode, string Output)> RunWsl(string? user, params string[] arguments)
    {
        var start = new ProcessStartInfo("wsl.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in user is null
            ? new[] { "-d", "Alpine", "--" }.Concat(arguments)
            : new[] { "-d", "Alpine", "-u", user, "--" }.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        return (process.ExitCode, await stdout + await stderr);
    }

    private static string WslPath(string path) => "/mnt/" + char.ToLowerInvariant(path[0]) + "/" + path[3..].Replace('\\', '/');

    public void Dispose() => Directory.Delete(root, recursive: true);
}