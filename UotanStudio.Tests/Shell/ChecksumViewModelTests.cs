using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Shell;

public sealed class ChecksumViewModelTests
{
    [Fact]
    public async Task VerifyAsync_SelectedFile_FillsLowercaseHexRows()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("abc.txt", "abc");
        var viewModel = new ChecksumViewModel { FilePath = path };

        await viewModel.VerifyAsync();

        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", viewModel.Md5Value);
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", viewModel.Sha1Value);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", viewModel.Sha256Value);
        Assert.Equal("352441c2", viewModel.CrcValue);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.HasAnyValue);
    }

    [Fact]
    public async Task VerifyAsync_UncheckedAlgorithm_ClearsItsRowOnly()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("abc.txt", "abc");
        var viewModel = new ChecksumViewModel { FilePath = path, Md5Selected = false };

        await viewModel.VerifyAsync();

        Assert.Equal(string.Empty, viewModel.Md5Value);
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", viewModel.Sha1Value);
    }

    [Fact]
    public async Task VerifyAsync_NoAlgorithmSelected_ClearsAllRowsWithoutComputing()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("abc.txt", "abc");
        var viewModel = new ChecksumViewModel
        {
            FilePath = path,
            Md5Selected = false,
            Sha1Selected = false,
            Sha256Selected = false,
            CrcSelected = false,
        };

        await viewModel.VerifyAsync();

        Assert.Equal(string.Empty, viewModel.Md5Value);
        Assert.Equal(string.Empty, viewModel.Sha1Value);
        Assert.Equal(string.Empty, viewModel.Sha256Value);
        Assert.Equal(string.Empty, viewModel.CrcValue);
        Assert.False(viewModel.HasAnyValue);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task VerifyAsync_WithoutFile_DoesNothing()
    {
        var viewModel = new ChecksumViewModel();

        await viewModel.VerifyAsync();

        Assert.Equal(string.Empty, viewModel.Md5Value);
        Assert.False(viewModel.CanVerify);
    }

    [Fact]
    public async Task PastedText_MixedCaseValue_MatchesAsCorrect()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("abc.txt", "abc");
        var viewModel = new ChecksumViewModel { FilePath = path };
        await viewModel.VerifyAsync();

        viewModel.PastedText = "900150983CD24FB0D6963F7D28E17F72".ToUpperInvariant();

        Assert.Equal(ChecksumMatchResult.Correct, viewModel.MatchResult);
        Assert.True(viewModel.PasteIsCorrect);
    }

    [Fact]
    public async Task PastedText_UnknownValue_ReportsWrong()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("abc.txt", "abc");
        var viewModel = new ChecksumViewModel { FilePath = path };
        await viewModel.VerifyAsync();

        viewModel.PastedText = "00000000000000000000000000000000";

        Assert.Equal(ChecksumMatchResult.Wrong, viewModel.MatchResult);
    }

    [Fact]
    public async Task SetFilePath_SwitchingFile_ClearsResultsAndPastedText()
    {
        using var directory = new TempDirectory();
        var first = directory.Write("abc.txt", "abc");
        var second = directory.Write("def.txt", "def");
        var viewModel = new ChecksumViewModel { FilePath = first };
        await viewModel.VerifyAsync();

        viewModel.PastedText = viewModel.Md5Value;
        viewModel.SetFilePath(second);

        Assert.Equal(string.Empty, viewModel.Md5Value);
        Assert.Equal(string.Empty, viewModel.PastedText);
        Assert.Equal(ChecksumMatchResult.None, viewModel.MatchResult);
        Assert.False(viewModel.HasAnyValue);
    }
}