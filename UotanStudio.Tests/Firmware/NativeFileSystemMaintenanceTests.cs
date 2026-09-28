using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.Native;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class NativeFileSystemMaintenanceTests
{
    [Fact]
    public async Task BuildAsync_AndroidF2fsProfile_ProducesCheckedFeatureSetAndFiles()
    {
        var tools = await AvailableToolsAsync("mkfs.f2fs");
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "build.prop"), "ro.product.name=uotan\n",
            TestContext.Current.CancellationToken);
        var output = Path.Combine(temp.Path, "android-f2fs.img");

        await AndroidF2fsImageBuilder.BuildAsync(source, output, 128L * 1024 * 1024, tools,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(128L * 1024 * 1024, new FileInfo(output).Length);
        await using var input = File.OpenRead(output);
        input.Position = 1024 + 2180;
        var data = new byte[4];
        await input.ReadExactlyAsync(data, TestContext.Current.CancellationToken);
        Assert.Equal(0x4018u, BinaryPrimitives.ReadUInt32LittleEndian(data) & 0x4018u);
        var tree = await tools.RunAsync("fsck.f2fs", ["-t", tools.PathForTool(output, TestContext.Current.CancellationToken)],
            TestContext.Current.CancellationToken);
        NativeFileSystemTools.RequireSuccess("fsck.f2fs", tree);
        Assert.Contains("build.prop", tree.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_AndroidF2fsWithLz4_EnablesCompressionFeature()
    {
        var tools = await AvailableToolsAsync("mkfs.f2fs");
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "data.bin"), new byte[256 * 1024],
            TestContext.Current.CancellationToken);
        var output = Path.Combine(temp.Path, "compressed.img");

        await AndroidF2fsImageBuilder.BuildAsync(source, output, 128L * 1024 * 1024, tools,
            enableCompression: true, compressionAlgorithm: CompressionAlgorithm.Lz4,
            cancellationToken: TestContext.Current.CancellationToken);

        await using var input = File.OpenRead(output);
        input.Position = 1024 + 2180;
        var data = new byte[4];
        await input.ReadExactlyAsync(data, TestContext.Current.CancellationToken);
        Assert.Equal(0x6018u, BinaryPrimitives.ReadUInt32LittleEndian(data) & 0x6018u);
    }

    [Fact]
    public async Task BuildImage_AndroidF2fsSettings_RoutesThroughNativeProfile()
    {
        await AvailableToolsAsync("mkfs.f2fs");
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "build.prop"), "ro.product.name=uotan\n",
            TestContext.Current.CancellationToken);
        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(new ProjectSettingsDto
        {
            ImageType = "F2FS",
            F2fsAndroidNativeProfile = true,
            F2fsAndroidImageSizeBytes = 128L * 1024 * 1024,
            F2fsNativeWslDistribution = OperatingSystem.IsWindows() ? "Alpine" : null,
        });
        var dto = settings.ToDto();
        Assert.True(dto.F2fsAndroidNativeProfile);
        Assert.Equal(128L * 1024 * 1024, dto.F2fsAndroidImageSizeBytes);
        var output = Path.Combine(temp.Path, "result.img");

        var result = PackingService.BuildImage(source, output, settings);

        Assert.True(result.Success, result.Summary);
        await using var stream = File.OpenRead(output);
        stream.Position = 1024 + 2180;
        var data = new byte[4];
        await stream.ReadExactlyAsync(data, TestContext.Current.CancellationToken);
        Assert.Equal(0x4018u, BinaryPrimitives.ReadUInt32LittleEndian(data) & 0x4018u);
    }

    [Fact]
    public async Task BuildAsync_NativeLoaderFailure_PreservesExistingOutput()
    {
        var tools = await AvailableToolsAsync("mkfs.f2fs");
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        var output = temp.WriteBytes("old.img", "existing"u8.ToArray());

        await Assert.ThrowsAnyAsync<Exception>(() => AndroidF2fsImageBuilder.BuildAsync(source, output,
            128L * 1024 * 1024, tools, fsConfigPath: Path.Combine(temp.Path, "missing.fs_config"),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("existing"u8.ToArray(),
            await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MinimizeAsync_ExistingExt4Image_ShrinksCopyWithoutMutatingSource()
    {
        var tools = await AvailableToolsAsync("mke2fs");
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "vendor.img");
        await CreateExt4Async(source, tools);
        var hash = await HashAsync(source);
        var destination = Path.Combine(temp.Path, "vendor-small.img");

        await Ext4ImageMaintenance.MinimizeAsync(source, destination, tools,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(new FileInfo(destination).Length < new FileInfo(source).Length);
        Assert.Equal(hash, await HashAsync(source));
        NativeFileSystemTools.RequireSuccess("e2fsck", await tools.RunAsync(
            "e2fsck", ["-fn", tools.PathForTool(destination, TestContext.Current.CancellationToken)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MinimizeAsync_SparseExt4Input_ProducesRawCopy()
    {
        var tools = await AvailableToolsAsync("mke2fs");
        using var temp = new TempDirectory();
        var raw = Path.Combine(temp.Path, "raw.img");
        await CreateExt4Async(raw, tools);
        var source = Path.Combine(temp.Path, "sparse.img");
        SparseImageConverter.ConvertRawToSparse(raw, source);
        var original = await HashAsync(source);
        var output = Path.Combine(temp.Path, "min.img");

        await Ext4ImageMaintenance.MinimizeAsync(source, output, tools,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(original, await HashAsync(source));
        Assert.True(new FileInfo(output).Length < new FileInfo(raw).Length);
        NativeFileSystemTools.RequireSuccess("e2fsck", await tools.RunAsync("e2fsck",
            ["-fn", tools.PathForTool(output, TestContext.Current.CancellationToken)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MinimizeAsync_DamagedExt4Image_KeepsExistingDestination()
    {
        var tools = await AvailableToolsAsync("mke2fs");
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "damaged.img");
        await CreateExt4Async(source, tools);
        await using (var stream = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = 1024 + 12;
            await stream.WriteAsync(new byte[4], TestContext.Current.CancellationToken);
        }
        var original = await HashAsync(source);
        var destination = temp.WriteBytes("existing.img", "keep me"u8.ToArray());

        await Assert.ThrowsAnyAsync<Exception>(() => Ext4ImageMaintenance.MinimizeAsync(source, destination,
            tools, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("keep me"u8.ToArray(), await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Equal(original, await HashAsync(source));
    }

    [Fact]
    public async Task MinimizeAsync_RepairRequested_FixesOnlyStagedImage()
    {
        var tools = await AvailableToolsAsync("mke2fs");
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source.img");
        await CreateExt4Async(source, tools);
        await using (var stream = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = 1024 + 12;
            await stream.WriteAsync(new byte[4], TestContext.Current.CancellationToken);
        }
        var original = await HashAsync(source);
        var destination = Path.Combine(temp.Path, "repaired.img");

        await Ext4ImageMaintenance.MinimizeAsync(source, destination, tools, repairBeforeResize: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(original, await HashAsync(source));
        Assert.True(new FileInfo(destination).Length < new FileInfo(source).Length);
        NativeFileSystemTools.RequireSuccess("e2fsck", await tools.RunAsync("e2fsck",
            ["-fn", tools.PathForTool(destination, TestContext.Current.CancellationToken)], TestContext.Current.CancellationToken));
    }

    private static async Task<NativeFileSystemTools> AvailableToolsAsync(string name)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") != "1")
        {
            Assert.Skip("Set UOTAN_NATIVE_TOOLS=1 on Windows with Alpine WSL to run native mkfs/e2fsck maintenance checks.");
        }
        var tools = new NativeFileSystemTools(OperatingSystem.IsWindows() ? "Alpine" : null);
        try
        {
            var result = await tools.RunAsync(name, ["-V"], TestContext.Current.CancellationToken);
            if (result.ExitCode != 0)
            {
                Assert.Skip($"{name} is unavailable in the native environment.");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip($"{name} is unavailable in the native environment.");
        }
        return tools;
    }

    private static async Task CreateExt4Async(string path, NativeFileSystemTools tools)
    {
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(128L * 1024 * 1024);
        }
        NativeFileSystemTools.RequireSuccess("mke2fs", await tools.RunAsync("mke2fs",
            ["-F", "-t", "ext4", tools.PathForTool(path, TestContext.Current.CancellationToken)], TestContext.Current.CancellationToken));
    }

    private static async Task<byte[]> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken);
    }
}