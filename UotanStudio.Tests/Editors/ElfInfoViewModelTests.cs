using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services.Workspace;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Editors;

[Collection("Workspace state")]
public sealed class ElfInfoViewModelTests
{
    [Theory]
    [InlineData("renamed.img")]
    [InlineData("renamed.txt")]
    [InlineData("lib.so")]
    [InlineData("executable")]
    public void ResolveMode_ValidHeader_RoutesByContent(string path)
    {
        Assert.Equal(EditorOpenMode.Elf, FileOpenMode.ResolveMode(path, ElfFixture.Create()));
    }

    [Theory]
    [InlineData("fake.elf")]
    [InlineData("fake.img")]
    [InlineData("fake.txt")]
    public void ResolveMode_MagicOnly_DoesNotOpenElfPanel(string path)
    {
        Assert.Equal(EditorOpenMode.Hex, FileOpenMode.ResolveMode(path, "\u007fELF"u8.ToArray()));
        Assert.NotEqual(EditorOpenMode.Elf, FileOpenMode.ResolveMode(path, "ordinary text"u8.ToArray()));
    }

    [Fact]
    public async Task LoadAsync_ValidThenInvalid_ClearsStaleMetadataAndReleasesFile()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("sample.elf", ElfFixture.Create());
        var broken = temp.WriteBytes("broken.elf", "\u007fELF"u8.ToArray());
        var document = new ElfInfoViewModel();
        try
        {
            await document.LoadAsync(path, TestContext.Current.CancellationToken);
            Assert.True(document.IsRecognized);
            Assert.Single(document.Segments);
            Assert.Equal(".text", document.Sections[2].Name);
            Assert.Contains(document.Fields, field => field.Value == "0x123456789ABCDEF0");
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

            await document.LoadAsync(broken, TestContext.Current.CancellationToken);

            Assert.False(document.IsRecognized);
            Assert.False(document.IsBusy);
            Assert.NotEmpty(document.ErrorMessage);
            Assert.Empty(document.Fields);
            Assert.Empty(document.Segments);
            Assert.Empty(document.Sections);
        }
        finally
        {
            document.Cleanup();
        }
    }

    [Fact]
    public async Task LoadAsync_Cancelled_DoesNotPublishMetadataOrError()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("sample.elf", ElfFixture.Create());
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.Cancel();
        var document = new ElfInfoViewModel();
        try
        {
            await document.LoadAsync(path, source.Token);

            Assert.False(document.IsBusy);
            Assert.False(document.IsRecognized);
            Assert.Empty(document.ErrorMessage);
            Assert.Empty(document.Fields);
        }
        finally
        {
            document.Cleanup();
        }
    }

    [Fact]
    public async Task OpenDocumentAsync_AutoAndSessionRestore_UseOneElfDocument()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("renamed.txt", ElfFixture.Create());
        using var workspace = new WorkspaceViewModel(new TestFactory());
        ElfInfoViewModel? document = null;
        try
        {
            await workspace.OpenDocumentAsync(path);
            var dock = workspace.FindDocumentsPanePublic()!;
            document = Assert.IsType<ElfInfoViewModel>(dock.ActiveDockable);
            Assert.True(document.IsRecognized);
            Assert.NotNull(ViewModelViewRegistry.Resolve(document.GetType()));
            Assert.True(WorkspaceSessionCapture.IsDocument(document));
            var snapshot = Assert.Single(WorkspaceSessionCapture.Documents(workspace.Layout, temp.Path));
            Assert.Equal("Elf", snapshot.Kind);
            Assert.Equal("renamed.txt", snapshot.RelativePath);

            await workspace.RestoreOpenDocumentAsync(snapshot, temp.Path);
            Assert.Same(document, dock.ActiveDockable);
            Assert.Single(dock.VisibleDockables!);

            using var restored = new WorkspaceViewModel(new TestFactory());
            await restored.RestoreOpenDocumentAsync(snapshot, temp.Path);
            var restoredDocument = Assert.IsType<ElfInfoViewModel>(restored.FindDocumentsPanePublic()!.ActiveDockable);
            try
            {
                Assert.True(restoredDocument.IsRecognized);
                Assert.Equal(path, restoredDocument.FilePath);
            }
            finally
            {
                restoredDocument.Cleanup();
            }
        }
        finally
        {
            document?.Cleanup();
        }
    }

    [Theory]
    [InlineData(EditorOpenMode.Text)]
    [InlineData(EditorOpenMode.Hex)]
    public async Task OpenDocumentAsync_ExplicitMode_PreservesUserChoice(EditorOpenMode mode)
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("sample.elf", ElfFixture.Create());
        using var workspace = new WorkspaceViewModel(new TestFactory());

        await workspace.OpenDocumentAsync(path, mode);

        var active = workspace.FindDocumentsPanePublic()!.ActiveDockable;
        if (mode == EditorOpenMode.Text)
        {
            Assert.Equal(EditorOpenMode.Text, Assert.IsType<FileViewModel>(active).OpenMode);
        }
        else
        {
            Assert.IsType<HexEditorViewModel>(active);
        }
    }

    private sealed class TestFactory : Dock.Model.Mvvm.Factory
    {
        public override IRootDock CreateLayout()
        {
            var documents = new DocumentDock { Id = "DocumentsPane", VisibleDockables = CreateList<IDockable>() };
            return new RootDock { VisibleDockables = CreateList<IDockable>(documents), ActiveDockable = documents };
        }
    }
}