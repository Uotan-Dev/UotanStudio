using System.Text;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.Archive.Writing;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.FileSystem.Native;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Locks the consumption policy for preserved extractions: <c>metadata.json</c> is the only attribute table a
/// pack reads, the AOSP-style flat sidecars stay extraction-time exports for external tools, and a native tool
/// receives a temporary sidecar materialised from the payload rather than a file left over from an earlier run.
/// <para>固定受保留解包的消费策略：<c>metadata.json</c> 是打包唯一读取的属性表，AOSP 风格扁平附属文件只是供外部
/// 工具使用的解包期导出物，本机工具收到的是由负载具象化出的临时附属文件，而不是上一次运行遗留的文件。</para>
/// </summary>
[Collection("Workspace state")]
public sealed class SidecarConsumptionPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-sidecar-policy-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public SidecarConsumptionPolicyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CpioRepack_RestoresUidGidMode_FromStructuredPayload()
    {
        var image = Path.Combine(_root, "ramdisk.cpio");
        using (var output = File.Create(image))
        {
            new CpioArchive().SaveAll(output,
            [
                new ArchiveEntry
                {
                    Name = "bin",
                    Data = [],
                    Metadata = new ArchiveEntryMetadata
                    {
                        FileType = CpioFileType.Directory,
                        Mode = 0x41E8,
                        UserId = 2000,
                        GroupId = 3000,
                        LinkCount = 2,
                        ModificationTimeUnixSeconds = 1_700_000_000,
                        UnixPermissions = 0x1E8,
                    },
                },
                new ArchiveEntry
                {
                    Name = "bin/tool",
                    Data = Encoding.UTF8.GetBytes("tool"),
                    Metadata = new ArchiveEntryMetadata
                    {
                        FileType = CpioFileType.Regular,
                        Mode = 0x81E8,
                        UserId = 2000,
                        GroupId = 3000,
                        LinkCount = 1,
                        ModificationTimeUnixSeconds = 1_700_000_001,
                        UnixPermissions = 0x1E8,
                    },
                },
            ], ArchiveFormat.NewAscii, new WriteOptions { IncludeTrailer = true });
        }

        var extracted = Path.Combine(_root, "ramdisk");
        var unpacked = await new CpioFormatHandler().UnpackAsync(image, extracted, cancellationToken: Token);
        Assert.True(unpacked.Success, unpacked.Summary);

        var payload = FileSystemMetadataOverrides.TryLoadDocument(RepackMetadata.Read(extracted)!);
        Assert.NotNull(payload);
        Assert.True(FileSystemMetadataOverrides.TryGetFsConfig(payload!.FsConfig, "bin/tool", out var preserved));
        Assert.Equal(2000u, preserved.Uid);
        Assert.Equal(3000u, preserved.Gid);
        Assert.Equal((ushort)0x1E8, preserved.Mode);
        Assert.False(File.Exists(Path.Combine(extracted, "ramdisk_fs_config")));

        DropRecordedEntry(extracted, "bin/tool");
        File.WriteAllLines(Path.Combine(extracted, "ramdisk_fs_config"), ["/bin/tool 1 1 0600 0"]);

        var repackedImage = Path.Combine(_root, "repacked.cpio");
        var packed = await new CpioFormatHandler().PackAsync(extracted, repackedImage, Token);
        Assert.True(packed.Success, packed.Summary);

        using var repackedStream = File.OpenRead(repackedImage);
        var repackedEntries = new CpioArchive().ReadAll(repackedStream, ArchiveFormat.NewAscii);
        var tool = repackedEntries.Single(entry => entry.Name == "bin/tool");
        Assert.Equal(2000u, tool.Metadata.UserId);
        Assert.Equal(3000u, tool.Metadata.GroupId);
        Assert.Equal(0x1E8, tool.Metadata.UnixPermissions);
        Assert.Equal("tool", Encoding.UTF8.GetString(tool.Data));
    }

    [Fact]
    public async Task NativeHandoff_MaterializesOctalSidecarFromPayload()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "data"), "native-handoff");
        var config = Path.Combine(_root, "in_fs_config");
        File.WriteAllLines(config, ["/ 9000 9001 0755", "/data 9000 9001 0640"]);
        var contexts = Path.Combine(_root, "in_file_contexts");
        File.WriteAllLines(contexts, ["/ u:object_r:rootfs:s0", "/data u:object_r:system_file:s0"]);
        var image = Path.Combine(_root, "system.img");
        Ext4Builder.Build(source, image, new Ext4Options { FsConfigPath = config, FileContextsPath = contexts });

        var extracted = Path.Combine(_root, "system");
        var unpacked = await new Ext4FormatHandler().UnpackAsync(image, extracted, cancellationToken: Token);
        Assert.True(unpacked.Success, unpacked.Summary);
        File.WriteAllText(Path.Combine(extracted, "system_fs_config"), "/ 0 0 0700 0\n");
        File.WriteAllText(Path.Combine(extracted, "system_file_contexts"), "/data u:object_r:stale_label:s0\n");

        var sidecars = NativeMetadataSidecars.TryCreate(extracted, Token);
        Assert.NotNull(sidecars);
        var set = sidecars!;
        var directory = Path.GetDirectoryName(set.FsConfigPath)!;
        using (set)
        {
            Assert.NotEqual(Path.GetFullPath(extracted), directory);
            Assert.True(Directory.Exists(directory));
            Assert.Equal(Path.Combine(directory, "fs_config"), set.FsConfigPath);
            Assert.Equal(Path.Combine(directory, "file_contexts"), set.FileContextsPath);

            var raw = File.ReadAllBytes(set.FsConfigPath);
            byte[] bom = [0xEF, 0xBB, 0xBF];
            Assert.False(raw.AsSpan(0, Math.Min(3, raw.Length)).SequenceEqual(bom));
            var rows = File.ReadAllLines(set.FsConfigPath);
            Assert.Contains("/ 9000 9001 0755 0", rows);
            Assert.Contains("/data 9000 9001 0640 0", rows);
            Assert.DoesNotContain(rows, row => row.Contains("0700", StringComparison.Ordinal));
            Assert.DoesNotContain(rows, row => row.Contains("493", StringComparison.Ordinal)
                || row.Contains("416", StringComparison.Ordinal));

            var labelRows = File.ReadAllLines(set.FileContextsPath);
            Assert.Contains("/data u:object_r:system_file:s0", labelRows);
            Assert.DoesNotContain(labelRows, row => row.Contains("stale_label", StringComparison.Ordinal));
        }

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void NativeHandoff_DirectoryBuildWithoutPayload_ProducesNoSidecar()
    {
        var staging = Path.Combine(_root, "fresh");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "data"), "fresh-build");

        Assert.Null(NativeMetadataSidecars.TryCreate(staging, Token));
    }

    [Fact]
    public async Task Unpack_FlatExportCannotExpressAPath_RecordsWarningInPackingNotes()
    {
        var source = Path.Combine(_root, "warn-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "sp ace.txt"), "warning-input");
        var image = Path.Combine(_root, "warn.img");
        Ext4Builder.Build(source, image);

        var extracted = Path.Combine(_root, "warn-unpacked");
        var unpacked = await new Ext4FormatHandler().UnpackAsync(image, extracted, cancellationToken: Token);
        Assert.True(unpacked.Success, unpacked.Summary);

        var metadata = RepackMetadata.Read(extracted)!;
        Assert.NotNull(FileSystemMetadataOverrides.TryLoadDocument(metadata));
        Assert.True(metadata.CanPackDirectory);
        Assert.Contains("Flat metadata export reported", metadata.PackingNotes, StringComparison.Ordinal);
        Assert.Contains("fs_config: /sp ace.txt", metadata.PackingNotes, StringComparison.Ordinal);
    }

    private static void DropRecordedEntry(string extracted, string projection)
    {
        var metadataPath = Path.Combine(extracted, RepackMetadata.FileName);
        var document = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
        var entries = document["configuration"]!["cpio"]!["entries"]!.AsArray();
        var recorded = entries.Single(item => (string?)item!["projection"] == projection);
        entries.Remove(recorded);
        File.WriteAllText(metadataPath, document.ToJsonString());
    }

    public void Dispose()
    {
        _scope.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}