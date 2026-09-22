using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models;
using UotanStudio.Messages;
using UotanStudio.Services.Projects;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Characterizes existing adapter policies before consolidating their implementations.</summary>
[Collection("Workspace state")]
public sealed class EntryPointCompatibilityTests
{
    [Fact]
    public async Task WorkspaceOpen_PreservesActivationAndLoadedRefreshWithoutDuplicateOnRefocus()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("sample.txt", "hello"u8.ToArray());
        using var workspace = new WorkspaceViewModel(new TestWorkspaceFactory());
        var recipient = new object();
        var documents = new List<FileViewModel?>();
        WeakReferenceMessenger.Default.Register<ActiveDocumentChangedMessage>(recipient, (_, message) => documents.Add(message.Document));
        try
        {
            await workspace.OpenDocumentAsync(path, EditorOpenMode.Text);
            Assert.Equal(2, documents.Count);
            Assert.Same(documents[0], documents[1]);
            Assert.Equal("hello", documents[1]!.Text);
            await workspace.OpenDocumentAsync(path, EditorOpenMode.Text);
            Assert.Equal(2, documents.Count);
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(recipient); }
    }

    [Fact]
    public async Task CanvasLoad_PreservesFactoryTitlesLocationsAndIdConnections()
    {
        var editor = new NodeEditorViewModel();
        try
        {
            await editor.LoadFromAsync(new WorkflowSnapshot
            {
                Nodes = [new() { TypeKey = "Flow.Execution.Start", InstanceId = "a", Title = "Saved title", X = 12, Y = 34 },
                    new() { TypeKey = "Flow.Execution.End", InstanceId = "b" }],
                Connections = [new() { SourceInstanceId = "A", TargetInstanceId = "B", SourceConnectorTitle = "Flow", TargetConnectorTitle = "Flow" }]
            });
            Assert.Equal("Start", editor.Nodes[0].Title);
            Assert.Equal(12, editor.Nodes[0].Location.X);
            Assert.Equal(34, editor.Nodes[0].Location.Y);
            var connection = Assert.Single(editor.Connections);
            Assert.Same(editor.Nodes[0].Output[0], connection.Source);
            Assert.Same(editor.Nodes[1].Input[0], connection.Target);
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(editor); }
    }

    [Fact]
    public async Task SparseUnpack_ProducesRawImageFile()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "system.img");
        using (var sparse = new SparseFile(4096, 4096))
        {
            sparse.AddFillChunk(0x01010101, 4096);
            using var stream = File.Create(path);
            sparse.WriteToStream(stream, sparse: true);
        }
        var output = Path.Combine(temp.Path, "editor");
        var result = await UotanStudio.Services.Firmware.FirmwareService.Instance
            .UnpackSparseImageAsync(path, output, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Summary);
        var raw = Assert.Single(Directory.GetFiles(output, "*.raw"));
        Assert.Equal(Enumerable.Repeat((byte)1, 4096), File.ReadAllBytes(raw));
    }

    [Fact]
    public async Task TypedOpen_DoesNotPublishLoadedDocumentAfterUserSwitchesAway()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("pending.bin", [1]);
        using var workspace = new WorkspaceViewModel(new TestWorkspaceFactory());
        var recipient = new object();
        var notifications = new List<ActiveDocumentChangedMessage>();
        var loaded = new TaskCompletionSource();
        WeakReferenceMessenger.Default.Register<ActiveDocumentChangedMessage>(recipient, (_, message) => notifications.Add(message));
        try
        {
            var pending = workspace.OpenOrActivateDocumentAsync(path,
                static (HexEditorViewModel document) => document.FilePath,
                static fullPath => new HexEditorViewModel { FilePath = fullPath },
                (_, _) => loaded.Task);
            var dock = workspace.FindDocumentsPanePublic()!;
            var other = new Document();
            dock.VisibleDockables!.Add(other);
            dock.ActiveDockable = other;
            var count = notifications.Count;
            loaded.SetResult();
            await pending;
            Assert.Equal(count, notifications.Count);
            Assert.Same(other, dock.ActiveDockable);
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(recipient); }
    }

    [Fact]
    public async Task TypedOpen_ReusesOnlyMatchingEditorAndLoadsItOnce()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("shared.bin", [1]);
        using var workspace = new WorkspaceViewModel(new TestWorkspaceFactory());
        await workspace.OpenDocumentAsync(path, EditorOpenMode.Text);
        var loads = 0;
        Task OpenHex() => workspace.OpenOrActivateDocumentAsync(path,
            static (HexEditorViewModel document) => document.FilePath,
            static fullPath => new HexEditorViewModel { FilePath = fullPath },
            (_, _) => { loads++; return Task.CompletedTask; });
        await OpenHex();
        var active = workspace.FindDocumentsPanePublic()!.ActiveDockable;
        await OpenHex();
        Assert.Equal(1, loads);
        Assert.Same(active, workspace.FindDocumentsPanePublic()!.ActiveDockable);
        Assert.Equal(2, workspace.FindDocumentsPanePublic()!.VisibleDockables!.Count);
    }

    [Fact]
    public async Task DirectoryPacking_PreservesCallerFailurePolicy()
    {
        var result = await UotanStudio.Services.Firmware.RegisteredFirmwareFormats.PackDirectoryAsync(
            "missing-format", "source", "output", TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            UotanStudio.Services.Firmware.RegisteredFirmwareFormats.PackDirectoryAsync(
                "missing-format", "source", "output", TestContext.Current.CancellationToken, throwIfUnsupported: true));
        Assert.Equal(result.Summary, error.Message);
    }

    private sealed class TestWorkspaceFactory : Dock.Model.Mvvm.Factory
    {
        public override IRootDock CreateLayout()
        {
            var documents = new DocumentDock { Id = "DocumentsPane", VisibleDockables = CreateList<IDockable>() };
            return new RootDock { VisibleDockables = CreateList<IDockable>(documents), ActiveDockable = documents };
        }
    }

}