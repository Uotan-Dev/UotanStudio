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
}