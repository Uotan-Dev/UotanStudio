using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Covers the per-device mirror document slots so several devices can stay mirrored at once and a
/// reopened device focuses its existing document.
/// <para>覆盖按设备分配的投屏文档槽位，使多台设备可以同时投屏，并且重新打开同一设备时聚焦已有文档。</para>
/// </summary>
[Collection("Workspace state")]
public sealed class MirrorDocumentTests
{
    [Fact]
    public void OpenScreenMirrorDocument_DifferentDevices_CreatesDistinctDocuments()
    {
        using var workspace = new WorkspaceViewModel(new MirrorWorkspaceFactory());

        var first = workspace.OpenScreenMirrorDocument("SERIAL-A");
        var second = workspace.OpenScreenMirrorDocument("SERIAL-B");
        var again = workspace.OpenScreenMirrorDocument("SERIAL-A");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Same(first, again);
        Assert.NotEqual(first.Id, second.Id);
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

    private sealed class MirrorWorkspaceFactory : Dock.Model.Mvvm.Factory
    {
        public override IRootDock CreateLayout()
        {
            var documents = new DocumentDock { Id = "DocumentsPane", VisibleDockables = CreateList<IDockable>() };
            return new RootDock { VisibleDockables = CreateList<IDockable>(documents), ActiveDockable = documents };
        }
    }
}