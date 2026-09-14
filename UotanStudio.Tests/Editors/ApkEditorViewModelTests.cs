using UotanStudio.ViewModels.Editors;
using Xunit;

namespace UotanStudio.Tests.Editors;

public sealed class ApkEditorViewModelTests
{
    [Fact]
    public void FormatFileSize_UsesCorrectBinaryUnit()
    {
        Assert.Equal("1 KB", ApkEditorViewModel.FormatFileSize(1024));
        Assert.Equal("32 MB", ApkEditorViewModel.FormatFileSize(32L * 1024 * 1024));
        Assert.DoesNotContain("GB", ApkEditorViewModel.FormatFileSize(32L * 1024 * 1024));
    }
}