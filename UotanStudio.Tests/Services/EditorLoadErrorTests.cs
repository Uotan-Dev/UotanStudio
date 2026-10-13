using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.Editors;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Data source of the editor pages' top error banner: a failed load must leave error text on the view model,
/// while a successful load clears it.
/// <para>编辑器页面顶部错误条的数据来源：加载失败必须在视图模型上留下错误文本，成功加载则清空。</para>
/// </summary>
public sealed class EditorLoadErrorTests
{
    [Fact]
    public async Task LoadContentAsync_ForMissingFile_SetsCompileError()
    {
        using var temp = new TempDirectory();
        var viewModel = new FileViewModel();

        await viewModel.LoadContentAsync(Path.Combine(temp.Path, "missing.bin"));

        Assert.False(string.IsNullOrEmpty(viewModel.CompileError));
    }

    [Fact]
    public async Task LoadContentAsync_ForTextFile_ClearsPreviousCompileError()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("notes.txt", "hello"u8.ToArray());
        var viewModel = new FileViewModel { CompileError = "stale error" };

        await viewModel.LoadContentAsync(path, EditorOpenMode.Text);

        Assert.Equal(string.Empty, viewModel.CompileError);
        Assert.Equal("hello", viewModel.Text);
    }

    [Fact]
    public async Task LoadAsync_ForMissingImage_SetsCompileError()
    {
        using var temp = new TempDirectory();
        var viewModel = new HexEditorViewModel();

        await viewModel.LoadAsync(Path.Combine(temp.Path, "missing.img"));

        Assert.False(string.IsNullOrEmpty(viewModel.CompileError));
    }
}