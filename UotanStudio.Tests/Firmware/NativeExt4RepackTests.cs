using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Native;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class NativeExt4RepackTests
{
    [Fact]
    public async Task MakeExt4fsAsync_RejectsBlockSizeOutsideSupportedSet()
    {
        var tools = new NativeFileSystemTools();
        using var temp = new TempDirectory();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => tools.MakeExt4fsAsync(
            temp.Path, Path.Combine(temp.Path, "out.img"),
            new MakeExt4fsOptions { BlockSize = 512 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MakeExt4fsAsync_RejectsRelativeMountPoint()
    {
        var tools = new NativeFileSystemTools();
        using var temp = new TempDirectory();
        await Assert.ThrowsAsync<ArgumentException>(() => tools.MakeExt4fsAsync(
            temp.Path, Path.Combine(temp.Path, "out.img"),
            new MakeExt4fsOptions { MountPoint = "system" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MakeExt4fsAsync_RejectsMissingSourceDirectoryBeforeAnyToolRun()
    {
        var tools = new NativeFileSystemTools();
        using var temp = new TempDirectory();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => tools.MakeExt4fsAsync(
            Path.Combine(temp.Path, "absent"), Path.Combine(temp.Path, "out.img"),
            new MakeExt4fsOptions(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MakeExt4fsAsync_RejectsMissingFsConfigSidecar()
    {
        var tools = new NativeFileSystemTools();
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "root"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => tools.MakeExt4fsAsync(
            Path.Combine(temp.Path, "root"), Path.Combine(temp.Path, "out.img"),
            new MakeExt4fsOptions { FsConfigPath = Path.Combine(temp.Path, "absent-fs_config") },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task E2fsDroidAsync_RejectsCapacityThatIsNotBlockAligned()
    {
        var tools = new NativeFileSystemTools();
        using var temp = new TempDirectory();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => tools.E2fsDroidAsync(
            temp.Path, Path.Combine(temp.Path, "out.img"),
            new E2fsDroidOptions { ImageSizeBytes = 65_537 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task E2fsDroidAsync_RejectsUnknownFsConfigSelector()
    {
        var tools = new NativeFileSystemTools();
        using var temp = new TempDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => tools.E2fsDroidAsync(
            temp.Path, Path.Combine(temp.Path, "out.img"),
            new E2fsDroidOptions { ImageSizeBytes = 65_536, FsConfig = "not-a-token" },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task E2fsDroidAsync_RejectsMissingFileContextsSidecar()
    {
        var tools = new NativeFileSystemTools();
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "root"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => tools.E2fsDroidAsync(
            Path.Combine(temp.Path, "root"), Path.Combine(temp.Path, "out.img"),
            new E2fsDroidOptions { ImageSizeBytes = 8L * 1024 * 1024, FileContextsPath = Path.Combine(temp.Path, "absent-contexts") },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task E2fsDroidAsync_RejectsUnsafeMkfsProfileName()
    {
        var tools = new NativeFileSystemTools();
        using var temp = new TempDirectory();
        await Assert.ThrowsAsync<ArgumentException>(() => tools.E2fsDroidAsync(
            temp.Path, Path.Combine(temp.Path, "out.img"),
            new E2fsDroidOptions { ImageSizeBytes = 65_536, MkfsProfile = "android; rm" },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MakeExt4fs_SyntheticStagingTree_ProducesValidExt4SuperblockWhenNativeToolsAvailable()
    {
        Assert.SkipUnless(NativeOracleGate.NativeToolsEnabled,
            $"Set {NativeOracleGate.NativeToolsVariable}=1 to run the native make_ext4fs wrapper; mounted read-write plus fsck acceptance runs in the kernel mount validation environment.");
        var tools = CreateTools();
        var probe = await tools.RunAsync("make_ext4fs", [], TestContext.Current.CancellationToken);
        Assert.SkipUnless(probe.ExitCode != 127,
            "make_ext4fs is not installed in the selected native environment; mounted read-write plus fsck acceptance runs in the kernel mount validation environment.");
        using var temp = CreateStagingTree();
        var output = Path.Combine(temp.Path, "legacy.img");
        var result = await tools.MakeExt4fsAsync(Path.Combine(temp.Path, "root"), output,
            new MakeExt4fsOptions { TimestampSeconds = 1_230_768_000 }, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        AssertExt4Superblock(output);
    }

    [Fact]
    public async Task E2fsDroid_SyntheticStagingTree_ProducesValidExt4SuperblockWhenNativeToolsAvailable()
    {
        Assert.SkipUnless(NativeOracleGate.NativeToolsEnabled,
            $"Set {NativeOracleGate.NativeToolsVariable}=1 to run the native mke2fs plus e2fsdroid wrapper; mounted read-write plus fsck acceptance runs in the kernel mount validation environment.");
        var tools = CreateTools();
        foreach (var tool in new[] { "mke2fs", "e2fsdroid" })
        {
            var probe = await tools.RunAsync(tool, [], TestContext.Current.CancellationToken);
            Assert.SkipUnless(probe.ExitCode != 127,
                $"{tool} is not installed in the selected native environment; mounted read-write plus fsck acceptance runs in the kernel mount validation environment.");
        }
        var dryRun = await tools.RunAsync("mke2fs",
            ["-n", "-q", "-F", "-t", "ext4", "-T", "android", "-b", "4096", "unused.img", "1024"],
            TestContext.Current.CancellationToken);
        Assert.SkipUnless(dryRun.ExitCode == 0,
            "mke2fs in the selected native environment lacks the android profile; mounted read-write plus fsck acceptance runs in the kernel mount validation environment.");
        using var temp = CreateStagingTree();
        var output = Path.Combine(temp.Path, "modern.img");
        var result = await tools.E2fsDroidAsync(Path.Combine(temp.Path, "root"), output,
            new E2fsDroidOptions
            {
                ImageSizeBytes = 8L * 1024 * 1024,
                MountPoint = "/system",
                SdkVersion = 29,
                FixedTimestampSeconds = 1_230_768_000,
                Unpartitioned = true
            }, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        AssertExt4Superblock(output);
    }

    private static NativeFileSystemTools CreateTools() => new(OperatingSystem.IsWindows() ? "Alpine" : null);

    private static TempDirectory CreateStagingTree()
    {
        var temp = new TempDirectory();
        temp.WriteBytes("root/payload.bin", [1, 2, 3, 4, 5, 6, 7, 8]);
        temp.WriteBytes("root/system/build.prop", "ro.product.name=uotan\n"u8.ToArray());
        return temp;
    }

    private static void AssertExt4Superblock(string path)
    {
        Assert.True(new FileInfo(path).Length > 1080);
        using var image = File.OpenRead(path);
        image.Position = 1024 + 56;
        Span<byte> magic = stackalloc byte[2];
        image.ReadExactly(magic);
        Assert.Equal(0xEF53, BinaryPrimitives.ReadUInt16LittleEndian(magic));
    }
}