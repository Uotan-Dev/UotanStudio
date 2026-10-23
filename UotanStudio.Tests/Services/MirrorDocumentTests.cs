using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Messages;
using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Covers the per-device mirror document slots so several devices can stay mirrored at once, a
/// reopened device focuses its existing document, closed slots are rebuilt, and a device-list
/// selection change never retargets or refocuses an open mirror document.
/// <para>覆盖按设备分配的投屏文档槽位：多台设备可以同时投屏，重新打开同一设备时聚焦已有文档，
/// 关闭后的槽位可以重建，并且设备列表选中变化不会改写或重新聚焦已打开的投屏文档。</para>
/// </summary>
[Collection("Workspace state")]
public sealed class MirrorDocumentTests
{
    [Fact]
    public void OpenScreenMirrorDocument_DifferentDevices_CreatesDistinctDocuments()
    {
        using var workspace = new WorkspaceViewModel(new MirrorWorkspaceFactory());
        var dock = workspace.FindDocumentsPanePublic()!;

        var first = workspace.OpenScreenMirrorDocument("SERIAL-A")!;
        Assert.Same(first, dock.ActiveDockable);

        var second = workspace.OpenScreenMirrorDocument("SERIAL-B")!;
        Assert.NotSame(first, second);
        Assert.Same(second, dock.ActiveDockable);
        Assert.Same(dock, first.Owner);
        Assert.Same(dock, second.Owner);
        Assert.Equal(2, dock.VisibleDockables!.Count);
        Assert.Equal("ScreenMirrorDocument:SERIAL-A", first.Id);
        Assert.Equal("ScreenMirrorDocument:SERIAL-B", second.Id);

        var again = workspace.OpenScreenMirrorDocument("SERIAL-A");
        Assert.Same(first, again);
        Assert.Same(first, dock.ActiveDockable);
        Assert.Same(second, dock.VisibleDockables!.Single(document => document.Id == "ScreenMirrorDocument:SERIAL-B"));
        Assert.Equal(2, dock.VisibleDockables!.Count);
    }

    [Fact]
    public void OpenScreenMirrorDocument_UntargetedSlot_RemainsASingleDocument()
    {
        using var workspace = new WorkspaceViewModel(new MirrorWorkspaceFactory());

        var first = workspace.OpenScreenMirrorDocument();
        var second = workspace.OpenScreenMirrorDocument();

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void SelectedDeviceChanged_WithOpenMirror_DoesNotRetargetOrRefocusTheDocument()
    {
        var recipient = new object();
        var delivered = 0;
        WeakReferenceMessenger.Default.Register<SelectedDeviceChangedMessage>(recipient, (_, _) => delivered++);
        try
        {
            using var workspace = new WorkspaceViewModel(new MirrorWorkspaceFactory());
            var dock = workspace.FindDocumentsPanePublic()!;
            var first = workspace.OpenScreenMirrorDocument("SERIAL-A")!;

            WeakReferenceMessenger.Default.Send(new SelectedDeviceChangedMessage
            {
                Protocol = DeviceProtocolKind.Adb,
                Serial = "SERIAL-B"
            });

            Assert.Equal(1, delivered);
            Assert.Same(dock, first.Owner);
            Assert.Equal("ScreenMirrorDocument:SERIAL-A", first.Id);
            Assert.Equal(string.Empty, first.StatusText);
            Assert.Same(first, dock.ActiveDockable);
            Assert.Same(first, dock.VisibleDockables!.Single());
            Assert.DoesNotContain(dock.VisibleDockables!, document => document.Id == "ScreenMirrorDocument:SERIAL-B");
            Assert.Same(first, workspace.OpenScreenMirrorDocument("SERIAL-A"));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public void OpenScreenMirrorDocument_AfterDocumentClosedInDock_CreatesNewInstance()
    {
        using var workspace = new WorkspaceViewModel(new MirrorWorkspaceFactory());
        var dock = workspace.FindDocumentsPanePublic()!;
        var first = workspace.OpenScreenMirrorDocument("SERIAL-A")!;
        var second = workspace.OpenScreenMirrorDocument("SERIAL-B")!;

        dock.VisibleDockables!.Remove(first);
        first.Owner = null;

        var reopened = workspace.OpenScreenMirrorDocument("SERIAL-A");
        Assert.NotNull(reopened);
        var fresh = reopened!;

        Assert.NotSame(first, fresh);
        Assert.Same(dock, fresh.Owner);
        Assert.Equal("ScreenMirrorDocument:SERIAL-A", fresh.Id);
        Assert.Equal(2, dock.VisibleDockables!.Count);
        Assert.Same(second, dock.VisibleDockables!.Single(document => document.Id == "ScreenMirrorDocument:SERIAL-B"));
        Assert.Same(second, workspace.OpenScreenMirrorDocument("SERIAL-B"));
    }

    private sealed class MirrorWorkspaceFactory : Dock.Model.Mvvm.Factory
    {
        public override IRootDock CreateLayout()
        {
            var documents = new DocumentDock { Id = "DocumentsPane", VisibleDockables = CreateList<IDockable>() };
            return new RootDock { VisibleDockables = CreateList<IDockable>(documents), ActiveDockable = documents };
        }
    }
}