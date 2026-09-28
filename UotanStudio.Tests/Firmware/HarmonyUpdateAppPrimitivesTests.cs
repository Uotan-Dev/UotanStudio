using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Workflow;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class HarmonyUpdateAppPrimitivesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-harmony-primitives-" + Guid.NewGuid().ToString("N"));

    public HarmonyUpdateAppPrimitivesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ExtractSuperPartitions_UnknownNameRequested_ExtractsPresentAndReportsMissing()
    {
        var super = await BuildSuperAsync([("system", "SYSTEM-DATA"u8.ToArray()), ("cust", "CUST-DATA"u8.ToArray())]);
        var output = Path.Combine(_root, "super-out");

        var result = await new HarmonyUpdateAppPrimitives().ExtractSuperPartitionsAsync(super, output,
            ["system", "cust", "hw_product"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["system", "cust"], result.ExtractedPartitions);
        Assert.Equal(["hw_product"], result.MissingPartitions);
        var systemBytes = await File.ReadAllBytesAsync(Path.Combine(output, "system.img"),
            TestContext.Current.CancellationToken);
        Assert.Equal("SYSTEM-DATA"u8.ToArray(), systemBytes[..11]);
        Assert.True(systemBytes[11..].All(byteValue => byteValue == 0));
        Assert.False(File.Exists(Path.Combine(output, "hw_product.img")));
    }

    [Fact]
    public async Task ExtractSuperPartitions_AllNamesAbsent_SucceedsWithNothingExtracted()
    {
        var super = await BuildSuperAsync([("system", "DATA"u8.ToArray())]);

        var result = await new HarmonyUpdateAppPrimitives().ExtractSuperPartitionsAsync(super,
            Path.Combine(_root, "empty-out"), ["vendor", "hw_odm"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.ExtractedPartitions);
        Assert.Equal(["vendor", "hw_odm"], result.MissingPartitions);
    }

    [Fact]
    public async Task ExtractSuperPartitions_EmptySelection_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new HarmonyUpdateAppPrimitives().ExtractSuperPartitionsAsync(
            Path.Combine(_root, "unused.img"), Path.Combine(_root, "unused-out"), [], cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task StageInitFileSet_SyntheticFileSet_MirrorsTreeIntoStagingDirectory()
    {
        var systemTree = Directory.CreateDirectory(Path.Combine(_root, "stage", "system")).FullName;
        var fileSet = Directory.CreateDirectory(Path.Combine(_root, "stage", "fileset")).FullName;
        await File.WriteAllTextAsync(Path.Combine(fileSet, "init"), "init-binary",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(fileSet, "etc"));
        await File.WriteAllTextAsync(Path.Combine(fileSet, "etc", "hw_init.rc"), "on boot",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(fileSet, "lib"));
        await File.WriteAllTextAsync(Path.Combine(fileSet, "lib", "libfs_mgr.so"), "fs-mgr-binary",
            TestContext.Current.CancellationToken);

        var staged = await new HarmonyUpdateAppPrimitives().StageInitFileSetAsync(systemTree, fileSet,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, staged);
        Assert.Equal("init-binary", await File.ReadAllTextAsync(Path.Combine(systemTree, "init", "init"),
            TestContext.Current.CancellationToken));
        Assert.Equal("on boot", await File.ReadAllTextAsync(Path.Combine(systemTree, "init", "etc", "hw_init.rc"),
            TestContext.Current.CancellationToken));
        Assert.Equal("fs-mgr-binary", await File.ReadAllTextAsync(Path.Combine(systemTree, "init", "lib", "libfs_mgr.so"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StageInitFileSet_EscapingRelativeTarget_IsRejected()
    {
        var systemTree = Directory.CreateDirectory(Path.Combine(_root, "escape", "system")).FullName;
        var fileSet = Directory.CreateDirectory(Path.Combine(_root, "escape", "fileset")).FullName;
        await File.WriteAllTextAsync(Path.Combine(fileSet, "init"), "x", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => new HarmonyUpdateAppPrimitives().StageInitFileSetAsync(
            systemTree, fileSet, "../outside", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StageInitFileSet_MissingSystemTree_FailsWithDirectoryNotFound()
    {
        var fileSet = Directory.CreateDirectory(Path.Combine(_root, "missing", "fileset")).FullName;
        await File.WriteAllTextAsync(Path.Combine(fileSet, "init"), "x", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new HarmonyUpdateAppPrimitives().StageInitFileSetAsync(
            Path.Combine(_root, "missing", "system"), fileSet, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegenerateMergeMetadata_SyntheticSidecars_AreRebasedInPlace()
    {
        var config = Directory.CreateDirectory(Path.Combine(_root, "hos-config")).FullName;
        await File.WriteAllTextAsync(Path.Combine(config, "file_contexts"),
            "/ u:object_r:rootfs:s0\n/cust/app u:object_r:system_file:s0\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(config, "fs_config"),
            "/ 0 0 0755 0\ncust-app 0 2000 0755 0\n", TestContext.Current.CancellationToken);

        var result = await new HarmonyUpdateAppPrimitives().RegenerateMergeMetadataAsync(config, "/system",
            "my_cust", TestContext.Current.CancellationToken);

        var contexts = await File.ReadAllTextAsync(Path.Combine(config, "file_contexts"),
            TestContext.Current.CancellationToken);
        var fsConfig = await File.ReadAllTextAsync(Path.Combine(config, "fs_config"),
            TestContext.Current.CancellationToken);
        Assert.Contains("/system/my_cust/cust/app u:object_r:system_file:s0", contexts, StringComparison.Ordinal);
        Assert.Contains("/system/my_cust u:object_r:system_file:s0", contexts, StringComparison.Ordinal);
        Assert.Contains("system/my_cust/cust-app 0 2000 0755 0", fsConfig, StringComparison.Ordinal);
        Assert.Contains("system/my_cust 0 0 0755", fsConfig, StringComparison.Ordinal);
        Assert.Equal(1, result.FileContextPrefixed);
        Assert.Equal(1, result.FsConfigPrefixed);
    }

    private async Task<string> BuildSuperAsync((string Name, byte[] Payload)[] partitions)
    {
        var images = Directory.CreateDirectory(Path.Combine(_root, "super-" + Guid.NewGuid().ToString("N"))).FullName;
        foreach (var (name, payload) in partitions)
            await File.WriteAllBytesAsync(Path.Combine(images, name + ".img"), payload,
                TestContext.Current.CancellationToken);
        var super = Path.Combine(_root, "super-" + Guid.NewGuid().ToString("N") + ".img");

        var result = await new LpFormatHandler().PackAsync(images, super, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        return super;
    }
}