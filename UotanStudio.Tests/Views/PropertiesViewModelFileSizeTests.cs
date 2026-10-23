using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.UiAbstractions;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Views;

public sealed class PropertiesViewModelFileSizeTests
{
    [Fact]
    public async Task RefreshForTextDocument_LoadedJsonFile_ReportsActualFileSize()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("payload.json", "{\"name\":\"uotan\",\"value\":123}\n");
        var document = new FileViewModel();
        await document.LoadContentAsync(path);
        Assert.False(document.IsBinary);

        AssertFileSizeRow(path, "Dock_Properties_Text_FileSize",
            properties => properties.RefreshForTextDocument(document));
    }

    [Fact]
    public void RefreshForDtbDocument_WithoutRawBytes_ReportsActualFileSize()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("board.dtb", new byte[96]);
        var document = new DtbEditorViewModel { FilePath = path };

        AssertFileSizeRow(path, "Dock_Properties_Dtb_FileSize",
            properties => properties.RefreshForDtbDocument(document));
    }

    [Fact]
    public void RefreshForManifestDocument_WithoutRawBytes_ReportsActualFileSize()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("AndroidManifest.xml", new byte[128]);
        var document = new ManifestEditorViewModel { FilePath = path };

        AssertFileSizeRow(path, "Dock_Properties_Manifest_FileSize",
            properties => properties.RefreshForManifestDocument(document));
    }

    [Fact]
    public void RefreshForHexDocument_WithoutRawBytes_ReportsActualFileSize()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("boot.img", new byte[4096]);
        var document = new HexEditorViewModel { FilePath = path };

        AssertFileSizeRow(path, "Dock_Properties_Hex_FileSize",
            properties => properties.RefreshForHexDocument(document));
    }

    [Fact]
    public void RefreshForDexDocument_WithoutRawBytes_ReportsActualFileSize()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("classes.dex", new byte[512]);
        var document = new DexEditorViewModel { FilePath = path };

        AssertFileSizeRow(path, "Dock_Properties_Dex_FileSize",
            properties => properties.RefreshForDexDocument(document));
    }

    [Fact]
    public async Task LoadContentAsync_ForEmptyTextFile_ShowsZeroByteSize()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("empty.json", []);
        var document = new FileViewModel();
        await document.LoadContentAsync(path, EditorOpenMode.Text);

        var expected = string.Format(UotanStudio.Services.LanguageService.Instance.GetString("Dock_File_SizeFormat"), 0);
        Assert.Equal(expected, document.SizeDisplay);
    }

    [Fact]
    public async Task LoadContentAsync_ForTextFile_NotifiesSizeDisplay()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("data.json", "{}");
        var document = new FileViewModel();
        var changed = new List<string?>();
        document.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await document.LoadContentAsync(path);

        Assert.Contains(nameof(FileViewModel.SizeDisplay), changed);
    }

    private static void AssertFileSizeRow(string path, string resourceKey, Action<PropertiesViewModel> refresh)
    {
        var properties = new PropertiesViewModel();
        try
        {
            refresh(properties);
            var row = Assert.Single(properties.Properties, candidate => candidate.NameResourceKey == resourceKey);
            Assert.Equal($"{new FileInfo(path).Length:N0} bytes", row.Value);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(properties);
        }
    }
}