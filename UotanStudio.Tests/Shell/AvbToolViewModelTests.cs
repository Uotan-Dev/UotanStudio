using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Services.Keys;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Shell;

public sealed class AvbToolViewModelTests
{
    [Fact]
    public async Task AddFooterAsync_DisabledAvbOptions_AreWrittenToSignedOutput()
    {
        using var directory = new TempDirectory();
        var input = directory.WriteBytes("boot.img", new byte[8192]);
        var library = new KeyLibraryService(Path.Combine(directory.Path, "keys"));
        var key = library.Generate("avb-signing", ManagedKeyKind.Avb, 2048);
        var viewModel = new AvbToolViewModel(library)
        {
            SelectedKey = key,
            DisableVerity = true,
            DisableAvbVerification = true,
        };
        viewModel.AddImages([input]);

        await viewModel.AddFooterAsync();

        Assert.True(File.Exists(viewModel.FooterOutputPath), viewModel.Status);
        Assert.Equal(8192, new FileInfo(input).Length);
        var footer = Assert.IsType<AvbFooter>(AvbFooter.TryReadFile(viewModel.FooterOutputPath));
        await using var stream = File.OpenRead(viewModel.FooterOutputPath);
        stream.Position = checked((long)footer.VbmetaOffset);
        var headerBytes = new byte[AvbVBMetaImageHeader.Size];
        await stream.ReadExactlyAsync(headerBytes, TestContext.Current.CancellationToken);
        var header = AvbVBMetaImageHeader.FromBytes(headerBytes);

        Assert.Equal((uint)(AvbVbmetaFlag.HashtreeDisabled | AvbVbmetaFlag.VerificationDisabled), header.Flags);
    }

    [Fact]
    public void SelectedImageChanged_DefaultOutputPathFollowsImageUntilCustomized()
    {
        using var directory = new TempDirectory();
        var firstImage = directory.WriteBytes("boot.img", []);
        var secondImage = directory.WriteBytes("vendor_boot.img", []);
        var viewModel = new AvbToolViewModel(new KeyLibraryService(Path.Combine(directory.Path, "keys")));

        viewModel.AddImages([firstImage, secondImage]);

        Assert.Equal(firstImage + ".avb.img", viewModel.FooterOutputPath);

        viewModel.SelectedImage = secondImage;

        Assert.Equal(secondImage + ".avb.img", viewModel.FooterOutputPath);

        var customPath = directory.Path + "/custom-output.img";
        viewModel.FooterOutputPath = customPath;
        viewModel.SelectedImage = firstImage;

        Assert.Equal(customPath, viewModel.FooterOutputPath);
    }

    [Fact]
    public void LoadDisableAvbTarget_SetsFilePathOutputDirAndTabIndex()
    {
        using var directory = new TempDirectory();
        var filePath = directory.WriteBytes("vbmeta.img", new byte[512]);
        var viewModel = new AvbToolViewModel();

        viewModel.LoadDisableAvbTarget(filePath);

        Assert.Equal(filePath, viewModel.DisableAvbFilePath);
        Assert.Equal(Path.Combine(directory.Path, "vbmeta_disable-verity-verification.img"), viewModel.DisableAvbOutputPath);
        Assert.Equal(directory.Path, viewModel.DisableAvbOutputDir);
        Assert.Equal(0, viewModel.SelectedTabIndex);
    }

    [Fact]
    public async Task DisableVerificationAsync_CreatesOutputWithSuffixAndPatchesFlags()
    {
        using var directory = new TempDirectory();
        var imageBytes = new byte[512];
        "AVB0"u8.CopyTo(imageBytes);
        var inputPath = directory.WriteBytes("vbmeta.img", imageBytes);
        var outputSubdir = Path.Combine(directory.Path, "out");

        var viewModel = new AvbToolViewModel
        {
            DisableAvbFilePath = inputPath,
            DisableAvbOutputDir = outputSubdir,
            SelectedDisableCommand = "--disable-verity --disable-verification"
        };

        await viewModel.DisableVerificationAsync();

        var expectedOutputPath = Path.Combine(outputSubdir, "vbmeta_disable-verity-verification.img");
        Assert.True(File.Exists(expectedOutputPath), viewModel.Status);

        // 原文件未改变
        var originalBytes = await File.ReadAllBytesAsync(inputPath, TestContext.Current.CancellationToken);
        Assert.Equal(0u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(originalBytes.AsSpan(120, 4)));

        // 输出文件已被修改对应标志位
        var patchedBytes = await File.ReadAllBytesAsync(expectedOutputPath, TestContext.Current.CancellationToken);
        Assert.Equal((uint)(AvbVbmetaFlag.HashtreeDisabled | AvbVbmetaFlag.VerificationDisabled),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(patchedBytes.AsSpan(120, 4)));
    }

    [Fact]
    public async Task DisableVerificationAsync_CancelDisable_ClearsFlagsInPatchedImage()
    {
        using var directory = new TempDirectory();
        var imageBytes = new byte[512];
        "AVB0"u8.CopyTo(imageBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(imageBytes.AsSpan(120, 4), 0x03);
        var inputPath = directory.WriteBytes("vbmeta.img", imageBytes);
        var outputSubdir = Path.Combine(directory.Path, "out");

        var viewModel = new AvbToolViewModel
        {
            DisableAvbFilePath = inputPath,
            DisableAvbOutputDir = outputSubdir,
            SelectedDisableCommand = "取消禁用"
        };

        await viewModel.DisableVerificationAsync();

        var expectedOutputPath = Path.Combine(outputSubdir, "vbmeta_enable-verity-verification.img");
        Assert.True(File.Exists(expectedOutputPath), viewModel.Status);

        var originalBytes = await File.ReadAllBytesAsync(inputPath, TestContext.Current.CancellationToken);
        Assert.Equal(0x03u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(originalBytes.AsSpan(120, 4)));

        var patchedBytes = await File.ReadAllBytesAsync(expectedOutputPath, TestContext.Current.CancellationToken);
        Assert.Equal(0x00u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(patchedBytes.AsSpan(120, 4)));
    }

    [Fact]
    public async Task DisableVerificationAsync_VerificationDisabledOnly_OverwritesExistingVerityFlagTo02()
    {
        using var directory = new TempDirectory();
        var imageBytes = new byte[512];
        "AVB0"u8.CopyTo(imageBytes);
        // 初始写入 0x01 (HashtreeDisabled)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(imageBytes.AsSpan(120, 4), 0x01);
        var inputPath = directory.WriteBytes("vbmeta.img", imageBytes);
        var outputSubdir = Path.Combine(directory.Path, "out");

        var viewModel = new AvbToolViewModel
        {
            DisableAvbFilePath = inputPath,
            DisableAvbOutputDir = outputSubdir,
            SelectedDisableCommand = "--disable-verification"
        };

        await viewModel.DisableVerificationAsync();

        var expectedOutputPath = Path.Combine(outputSubdir, "vbmeta_disable-verification.img");
        Assert.True(File.Exists(expectedOutputPath), viewModel.Status);

        // 原文件保持 0x01
        var originalBytes = await File.ReadAllBytesAsync(inputPath, TestContext.Current.CancellationToken);
        Assert.Equal(0x01u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(originalBytes.AsSpan(120, 4)));

        // 输出文件必须是且仅是 0x02，Bit 0 必须被清为 0
        var patchedBytes = await File.ReadAllBytesAsync(expectedOutputPath, TestContext.Current.CancellationToken);
        Assert.Equal(0x02u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(patchedBytes.AsSpan(120, 4)));
    }
}