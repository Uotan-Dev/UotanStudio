using UotanStudio.Firmware.BoundedContexts.Patching;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Workflow;
using UotanStudio.Firmware.SharedKernel;
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
        var config = Directory.CreateDirectory(Path.Combine(_root, "config")).FullName;
        await File.WriteAllTextAsync(Path.Combine(config, "file_contexts"),
            "/ u:object_r:rootfs:s0\n/app1/bin u:object_r:system_file:s0\n/weird(/.*)? u:object_r:system_file:s0\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(config, "fs_config"),
            "/ 0 0 0755 0\nbin/tool 0 2000 0755 0\n", TestContext.Current.CancellationToken);
        var progress = new RecordingProgress();

        var result = await new HonorMyPartitionMergeService().MergeAsync(myPartition, target, config,
            progress: progress, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(target, "system", "my_product"), result.MovedRoot);
        Assert.Equal("binary", await File.ReadAllTextAsync(Path.Combine(target, "system", "my_product", "bin", "tool"),
            TestContext.Current.CancellationToken));
        Assert.Contains("/system/my_product/app1/bin u:object_r:system_file:s0",
            await File.ReadAllTextAsync(Path.Combine(config, "file_contexts"), TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        Assert.Contains("system/my_product/bin/tool 0 2000 0755 0",
            await File.ReadAllTextAsync(Path.Combine(config, "fs_config"), TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        Assert.Contains("import /my_product/build.prop",
            await File.ReadAllTextAsync(Path.Combine(target, "system", "my_product", "build.prop"),
                TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Single(result.MetacharacterWarnings);
        Assert.True(result.BuildPropImported);
        Assert.Equal(100d, progress.Reports[^1].Percent);
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

        await Assert.ThrowsAsync<IOException>(() => new HonorMyPartitionMergeService().MergeAsync(
            myPartition, target, config,
            new HonorMyPartitionMergeOptions { Overwrite = false }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(File.Exists(Path.Combine(myPartition, "build.prop")));
    }

    private sealed class RecordingProgress : IFirmwareProgress
    {
        public List<(double Percent, string Message)> Reports { get; } = [];

        public void Report(double percent, string message) => Reports.Add((percent, message));
    }
}