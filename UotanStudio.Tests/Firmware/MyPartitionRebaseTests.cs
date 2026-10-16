using System.Text.Json;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Firmware.Patching;
using UotanStudio.Firmware.VendorFirmware.Workflow;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class MyPartitionRebaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-mypartition-" + Guid.NewGuid().ToString("N"));

    public MyPartitionRebaseTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void FileContextRebase_SyntheticRules_PrefixesDropsDefaultsAndAppendsDefault()
    {
        var text = "/ u:object_r:rootfs:s0\n" +
            "/app1/bin u:object_r:system_file:s0\n" +
            "/app1/x -- u:object_r:system_file:s0\n" +
            "/system/my_product u:object_r:stale:s0\n";

        var result = FileContextPatch.Rebase(text, "/system", "my_product", CancellationToken.None);

        Assert.Equal(
            "/system/my_product/app1/bin u:object_r:system_file:s0\n" +
            "/system/my_product/app1/x -- u:object_r:system_file:s0\n" +
            "/system/my_product u:object_r:system_file:s0\n",
            result.Text);
        Assert.Equal(2, result.PrefixedRules);
        Assert.Equal(2, result.DroppedRules);
        Assert.Empty(result.MetacharacterWarnings);
        Assert.True(result.Changed);
    }

    [Fact]
    public void FileContextRebase_AppliedTwice_SecondApplicationIsIdentity()
    {
        var text = "/ u:object_r:rootfs:s0\n/app1/bin u:object_r:system_file:s0\n";

        var once = FileContextPatch.Rebase(text, "/system", "my_product", CancellationToken.None);
        var twice = FileContextPatch.Rebase(once.Text, "/system", "my_product", CancellationToken.None);

        Assert.Equal(once.Text, twice.Text);
        Assert.False(twice.Changed);
        Assert.Equal(0, twice.PrefixedRules);
    }

    [Fact]
    public void FileContextRebase_RegexExpressions_WarnsAndKeepsLinesUnprefixed()
    {
        var text = "/system(/.*)? u:object_r:system_file:s0\n/plain u:object_r:system_file:s0\n";

        var result = FileContextPatch.Rebase(text, "/system", "my_product", CancellationToken.None);

        Assert.Single(result.MetacharacterWarnings);
        Assert.Contains("/system(/.*)?", result.MetacharacterWarnings[0], StringComparison.Ordinal);
        Assert.Contains("/system(/.*)? u:object_r:system_file:s0\n", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/system/my_product/system(", result.Text, StringComparison.Ordinal);
        Assert.Equal(1, result.PrefixedRules);
    }

    [Fact]
    public void FileContextRebase_MountPrefixWithMetacharacters_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => FileContextPatch.Rebase("/x u:y:s0", "/sys(tem", "my_product",
            CancellationToken.None));
    }

    [Fact]
    public void FsConfigRebase_SyntheticEntries_PrefixesDropsDefaultsAndIsIdempotent()
    {
        var text = "/ 0 0 0755 0\n" +
            "bin/sh 0 2000 0755 0\n" +
            "etc/passwd 0 0 0644 0\n" +
            "system/my_product 0 0 0755\n" +
            "# trailing comment\n";

        var once = FsConfigPatch.Rebase(text, "system/", "my_product", CancellationToken.None);

        Assert.Equal(
            "system/my_product/bin/sh 0 2000 0755 0\n" +
            "system/my_product/etc/passwd 0 0 0644 0\n" +
            "# trailing comment\n" +
            "system/my_product 0 0 0755\n",
            once.Text);
        Assert.Equal(2, once.PrefixedEntries);
        Assert.Equal(2, once.DroppedEntries);
        var twice = FsConfigPatch.Rebase(once.Text, "system/", "my_product", CancellationToken.None);
        Assert.Equal(once.Text, twice.Text);
        Assert.False(twice.Changed);
    }

    [Fact]
    public void ImportLine_AppliedTwice_AppendsDirectiveOnce()
    {
        var text = "ro.product.name=pixel\n";

        var once = AndroidPropertyPatch.ImportLine(text, "/my_product/build.prop");
        var twice = AndroidPropertyPatch.ImportLine(once.Text, "/my_product/build.prop");

        Assert.Equal("ro.product.name=pixel\nimport /my_product/build.prop\n", once.Text);
        Assert.True(once.Changed);
        Assert.Equal(once.Text, twice.Text);
        Assert.False(twice.Changed);
        Assert.Equal(1, twice.Matches);
    }

    [Fact]
    public void ImportLine_RelativeOrMultilinePath_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => AndroidPropertyPatch.ImportLine("x=1\n", "my_product/build.prop"));
        Assert.Throws<InvalidDataException>(() => AndroidPropertyPatch.ImportLine("x=1\n", "/a/../b.prop"));
    }

    [Fact]
    public async Task Merge_MyPartitionTree_RebasesSidecarsAndPublishesImport()
    {
        var myPartition = Directory.CreateDirectory(Path.Combine(_root, "my_product")).FullName;
        Directory.CreateDirectory(Path.Combine(myPartition, "bin"));
        await File.WriteAllTextAsync(Path.Combine(myPartition, "bin", "tool"), "binary",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(myPartition, "build.prop"), "ro.a=1\n",
            TestContext.Current.CancellationToken);
        var target = Directory.CreateDirectory(Path.Combine(_root, "system-tree")).FullName;
        Directory.CreateDirectory(Path.Combine(target, "system"));
        await File.WriteAllTextAsync(Path.Combine(target, "system", "build.prop"), "ro.system=1\n",
            TestContext.Current.CancellationToken);
        var config = Directory.CreateDirectory(Path.Combine(_root, "config")).FullName;
        await File.WriteAllTextAsync(Path.Combine(config, "file_contexts"),
            "/ u:object_r:rootfs:s0\n/app1/bin u:object_r:system_file:s0\n/weird(/.*)? u:object_r:system_file:s0\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(config, "fs_config"),
            "/ 0 0 0755 0\nbin/tool 0 2000 0755 0\n", TestContext.Current.CancellationToken);
        var progress = new RecordingProgress();

        var result = await new MyPartitionMergeService().MergeAsync(myPartition, target, config,
            progress: progress, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.FileContextsRebased);
        Assert.True(result.FsConfigRebased);

        Assert.Equal(Path.Combine(target, "system", "my_product"), result.MovedRoot);
        Assert.Equal("binary", await File.ReadAllTextAsync(Path.Combine(target, "system", "my_product", "bin", "tool"),
            TestContext.Current.CancellationToken));
        Assert.Contains("/system/my_product/app1/bin u:object_r:system_file:s0",
            await File.ReadAllTextAsync(Path.Combine(config, "file_contexts"), TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        Assert.Contains("system/my_product/bin/tool 0 2000 0755 0",
            await File.ReadAllTextAsync(Path.Combine(config, "fs_config"), TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        Assert.Equal("ro.system=1\nimport /system/my_product/build.prop\n",
            await File.ReadAllTextAsync(Path.Combine(target, "system", "build.prop"),
                TestContext.Current.CancellationToken));
        Assert.Equal("ro.a=1\n",
            await File.ReadAllTextAsync(Path.Combine(target, "system", "my_product", "build.prop"),
                TestContext.Current.CancellationToken));
        Assert.Single(result.MetacharacterWarnings);
        Assert.True(result.BuildPropImported);
        Assert.Equal(100d, progress.Reports[^1].Percent);
    }

    [Fact]
    public async Task Merge_SlotSuffixedPartition_PlacesTreeUnderStrippedNameAndReadsSuffixedSidecars()
    {
        var myPartition = Directory.CreateDirectory(Path.Combine(_root, "my_product_a")).FullName;
        await File.WriteAllTextAsync(Path.Combine(myPartition, "build.prop"), "ro.a=1\n",
            TestContext.Current.CancellationToken);
        var target = Directory.CreateDirectory(Path.Combine(_root, "system_a")).FullName;
        Directory.CreateDirectory(Path.Combine(target, "system"));
        await File.WriteAllTextAsync(Path.Combine(target, "system", "build.prop"), "ro.system=1\n",
            TestContext.Current.CancellationToken);
        var metadata = Directory.CreateDirectory(Path.Combine(_root, "metadata")).FullName;
        await File.WriteAllTextAsync(Path.Combine(metadata, "my_product_a_file_contexts"),
            "/ u:object_r:rootfs:s0\n/app1/bin u:object_r:system_file:s0\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(metadata, "my_product_a_fs_config"),
            "/ 0 0 0755 0\nbin/tool 0 2000 0755 0\n", TestContext.Current.CancellationToken);

        var result = await new MyPartitionMergeService().MergeAsync(myPartition, target, null,
            new MyPartitionMergeOptions { MyMetadataDirectory = metadata },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(target, "system", "my_product"), result.MovedRoot);
        Assert.True(result.FileContextsRebased);
        Assert.True(result.FsConfigRebased);
        Assert.Contains("/system/my_product/app1/bin u:object_r:system_file:s0",
            await File.ReadAllTextAsync(Path.Combine(metadata, "my_product_a_file_contexts"),
                TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("system/my_product/bin/tool 0 2000 0755 0",
            await File.ReadAllTextAsync(Path.Combine(metadata, "my_product_a_fs_config"),
                TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal("ro.system=1\nimport /system/my_product/build.prop\n",
            await File.ReadAllTextAsync(Path.Combine(target, "system", "build.prop"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Merge_ExistingDestinationWithoutOverwrite_FailsBeforeMoving()
    {
        var myPartition = Directory.CreateDirectory(Path.Combine(_root, "conflict", "my_hebrew")).FullName;
        await File.WriteAllTextAsync(Path.Combine(myPartition, "build.prop"), "ro.b=2\n",
            TestContext.Current.CancellationToken);
        var target = Path.Combine(_root, "conflict", "system");
        Directory.CreateDirectory(Path.Combine(target, "system", "my_hebrew"));
        await File.WriteAllTextAsync(Path.Combine(target, "system", "my_hebrew", "build.prop"), "stale\n",
            TestContext.Current.CancellationToken);
        var config = Directory.CreateDirectory(Path.Combine(_root, "conflict", "config")).FullName;
        await File.WriteAllTextAsync(Path.Combine(config, "file_contexts"), "/x u:y:s0\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(config, "fs_config"), "x 0 0 0755\n",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() => new MyPartitionMergeService().MergeAsync(
            myPartition, target, config,
            new MyPartitionMergeOptions { Overwrite = false }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(File.Exists(Path.Combine(myPartition, "build.prop")));
    }

    [Fact]
    public async Task Merge_NullConfigDirectory_RebasesOwnedSidecarsAtMovedRoot()
    {
        var myPartition = Directory.CreateDirectory(Path.Combine(_root, "my_region")).FullName;
        await File.WriteAllTextAsync(Path.Combine(myPartition, "build.prop"), "ro.r=1\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(myPartition, "file_contexts"),
            "/ u:object_r:rootfs:s0\n/app1/bin u:object_r:system_file:s0\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(myPartition, "fs_config"),
            "/ 0 0 0755 0\nbin/tool 0 2000 0755 0\n", TestContext.Current.CancellationToken);
        var target = Directory.CreateDirectory(Path.Combine(_root, "system-tree")).FullName;
        Directory.CreateDirectory(Path.Combine(target, "system"));
        await File.WriteAllTextAsync(Path.Combine(target, "system", "build.prop"), "ro.system=1\n",
            TestContext.Current.CancellationToken);

        var result = await new MyPartitionMergeService().MergeAsync(myPartition, target, null,
            cancellationToken: TestContext.Current.CancellationToken);

        var movedRoot = Path.Combine(target, "system", "my_region");
        Assert.Equal(movedRoot, result.MovedRoot);
        Assert.Contains("/system/my_region/app1/bin u:object_r:system_file:s0",
            await File.ReadAllTextAsync(Path.Combine(movedRoot, "file_contexts"),
                TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("system/my_region/bin/tool 0 2000 0755 0",
            await File.ReadAllTextAsync(Path.Combine(movedRoot, "fs_config"),
                TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.True(result.FileContextsRebased);
        Assert.True(result.FsConfigRebased);
        Assert.True(result.BuildPropImported);
        Assert.Equal("ro.system=1\nimport /system/my_region/build.prop\n",
            await File.ReadAllTextAsync(Path.Combine(target, "system", "build.prop"),
                TestContext.Current.CancellationToken));
        Assert.Equal("ro.r=1\n",
            await File.ReadAllTextAsync(Path.Combine(movedRoot, "build.prop"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Merge_MissingSidecarsWithSkipOption_MovesTreeWithoutRebase()
    {
        var myPartition = Directory.CreateDirectory(Path.Combine(_root, "my_carrier")).FullName;
        await File.WriteAllTextAsync(Path.Combine(myPartition, "build.prop"), "ro.c=1\n",
            TestContext.Current.CancellationToken);
        var target = Directory.CreateDirectory(Path.Combine(_root, "carrier-system")).FullName;

        var result = await new MyPartitionMergeService().MergeAsync(myPartition, target, null,
            new MyPartitionMergeOptions { SkipMissingSidecars = true },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(target, "system", "my_carrier"), result.MovedRoot);
        Assert.True(File.Exists(Path.Combine(target, "system", "my_carrier", "build.prop")));
        Assert.False(result.FileContextsRebased);
        Assert.False(result.FsConfigRebased);
        Assert.Empty(result.MetacharacterWarnings);
    }

    [Fact]
    public async Task Merge_MissingSidecarsWithoutSkipOption_ThrowsAfterMoveWithoutRebase()
    {
        var myPartition = Directory.CreateDirectory(Path.Combine(_root, "my_stock")).FullName;
        await File.WriteAllTextAsync(Path.Combine(myPartition, "build.prop"), "ro.s=1\n",
            TestContext.Current.CancellationToken);
        var target = Directory.CreateDirectory(Path.Combine(_root, "stock-system")).FullName;

        await Assert.ThrowsAsync<InvalidDataException>(() => new MyPartitionMergeService().MergeAsync(
            myPartition, target, null, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("ro.s=1\n", await File.ReadAllTextAsync(Path.Combine(target, "system", "my_stock", "build.prop"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Merge_LostFoundDirectory_StaysOutOfMovedTree()
    {
        var myPartition = Directory.CreateDirectory(Path.Combine(_root, "my_bigball")).FullName;
        Directory.CreateDirectory(Path.Combine(myPartition, "lost+found"));
        await File.WriteAllTextAsync(Path.Combine(myPartition, "lost+found", "orphan"), "x",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(myPartition, "build.prop"), "ro.b=1\n",
            TestContext.Current.CancellationToken);
        var target = Directory.CreateDirectory(Path.Combine(_root, "bigball-system")).FullName;
        Directory.CreateDirectory(Path.Combine(target, "system"));
        await File.WriteAllTextAsync(Path.Combine(target, "system", "build.prop"), "ro.system=1\n",
            TestContext.Current.CancellationToken);

        await new MyPartitionMergeService().MergeAsync(myPartition, target, null,
            new MyPartitionMergeOptions { SkipMissingSidecars = true },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(target, "system", "my_bigball", "build.prop")));
        Assert.False(Directory.Exists(Path.Combine(target, "system", "my_bigball", "lost+found")));
    }

    [Fact]
    public async Task Merge_WithConsolidatedMetadata_FoldsPreservationIntoSystemManifest()
    {
        var myPartition = Directory.CreateDirectory(Path.Combine(_root, "my_product_a")).FullName;
        await File.WriteAllTextAsync(Path.Combine(myPartition, "build.prop"), "ro.a=1\n",
            TestContext.Current.CancellationToken);
        var target = Directory.CreateDirectory(Path.Combine(_root, "system_a")).FullName;
        Directory.CreateDirectory(Path.Combine(target, "system"));
        await File.WriteAllTextAsync(Path.Combine(target, "system", "build.prop"), "ro.system=1\n",
            TestContext.Current.CancellationToken);
        var metadataRoot = Directory.CreateDirectory(Path.Combine(_root, "metadata")).FullName;
        var myMetadata = Path.Combine(metadataRoot, "my_product_a");
        var systemMetadata = Path.Combine(metadataRoot, "system_a");
        await WritePreservationManifestAsync(myMetadata, "my_product_a", source =>
        {
            source.ParsedEntries.Add(new PreservedInodeEntry { Path = "", Mode = 0x41ED });
            source.ParsedEntries.Add(new PreservedInodeEntry
            {
                Path = "build.prop",
                Mode = 0x81A4,
                Uid = 1000,
                Gid = 1001,
                Xattrs = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["security.selinux"] = "u:object_r:my_product_file:s0"u8.ToArray()
                }
            });
            source.ParsedEntries.Add(new PreservedInodeEntry { Path = "bin/sh", Mode = 0xA1FF, SymlinkTarget = "toybox" });
            source.FsConfig[""] = new FsConfigEntry(0, 0, 0x41ED);
            source.FsConfig["build.prop"] = new FsConfigEntry(1000, 1001, 0x81A4);
            source.FileContexts["build.prop"] = "u:object_r:my_product_file:s0";
        });
        await WritePreservationManifestAsync(systemMetadata, "system_a", system =>
        {
            system.ParsedEntries.Add(new PreservedInodeEntry { Path = "", Mode = 0x41ED });
        });

        var result = await new MyPartitionMergeService().MergeAsync(myPartition, target, null,
            new MyPartitionMergeOptions
            {
                SkipMissingSidecars = true,
                MyMetadataDirectory = myMetadata,
                SystemMetadataDirectory = systemMetadata,
                SystemPartitionName = "system_a",
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.PreservationFolded);
        Assert.Equal(3, result.FoldedInodeEntries);
        var system = ReadPreservationManifest(systemMetadata);
        Assert.Contains(system.ParsedEntries, entry => entry.Path == "system/my_product" && entry.Mode == 0x41ED);
        Assert.Contains(system.ParsedEntries, entry => entry.Path == "system/my_product/build.prop"
            && entry.Uid == 1000 && entry.Gid == 1001
            && entry.Xattrs!["security.selinux"].AsSpan().SequenceEqual("u:object_r:my_product_file:s0"u8));
        Assert.Contains(system.ParsedEntries, entry => entry.Path == "system/my_product/bin/sh"
            && entry.SymlinkTarget == "toybox");
        Assert.Equal(new FsConfigEntry(1000, 1001, 0x81A4), system.FsConfig["system/my_product/build.prop"]);
        Assert.Equal("u:object_r:my_product_file:s0", system.FileContexts["system/my_product/build.prop"]);
        var reloaded = RepackMetadata.ReadFromAbsolute(Path.Combine(systemMetadata, RepackMetadata.FileName));
        Assert.NotNull(reloaded);
        Assert.Equal(reloaded!.FileSystemPreservation!["configurationHash"]!.GetValue<string>(),
            FileSystemPreservation.HashConfiguration(reloaded));
    }

    private static async Task WritePreservationManifestAsync(string directory, string partition,
        Action<FileSystemPreservationData> configure)
    {
        var payload = new FileSystemPreservationData
        {
            Format = "EXT4",
            Partition = partition,
            SourceLength = 1,
        };
        configure(payload);
        var metadata = new RepackMetadata
        {
            FormatName = "EXT4",
            SourceFileName = partition + ".img",
            SourceLength = 1,
            FileSystemPreservation = JsonSerializer.SerializeToNode(payload,
                FileSystemPreservationJsonContext.Default.FileSystemPreservationData)!.AsObject()
        };
        await metadata.SaveAsync(directory, TestContext.Current.CancellationToken);
    }

    private static FileSystemPreservationData ReadPreservationManifest(string directory)
        => RepackMetadata.ReadFromAbsolute(Path.Combine(directory, RepackMetadata.FileName))!
            .FileSystemPreservation!
            .Deserialize(FileSystemPreservationJsonContext.Default.FileSystemPreservationData)!;

    private sealed class RecordingProgress : IFirmwareProgress
    {
        public List<(double Percent, string Message)> Reports { get; } = [];

        public void Report(double percent, string message) => Reports.Add((percent, message));
    }
}