using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services.Ui.Workspace;
using UotanStudio.Views.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

[Collection("Workspace panel state")]
public sealed class WorkspaceDockEmptyDocumentPaneTests
{
    [Fact]
    public void CreateDocumentDock_EmptyPane_IsNotCollapsable()
    {
        var factory = new WorkspaceDockFactory();

        var created = factory.CreateDocumentDock();

        Assert.False(created.IsCollapsable);
    }

    [Fact]
    public void LeavesRoomForSiblings_PanePinnedToWholeAxis_IsRejected()
    {
        var (documentDock, bottomDock) = CreateCenterColumn();

        Assert.False(WorkspaceAutoSaveService.LeavesRoomForSiblings(bottomDock, 1.0));
        Assert.True(WorkspaceAutoSaveService.LeavesRoomForSiblings(bottomDock, 0.25));
        Assert.True(WorkspaceAutoSaveService.LeavesRoomForSiblings(documentDock, 0.75));
    }

    [Fact]
    public void LeavesRoomForSiblings_LonePaneOnTheAxis_KeepsItsProportion()
    {
        var factory = new WorkspaceDockFactory();
        var documentDock = new DocumentDock
        {
            Id = "DocumentsPane",
            IsCollapsable = false,
            VisibleDockables = factory.CreateList<IDockable>()
        };
        var column = new ProportionalDock
        {
            Id = "WorkspaceCenterColumn",
            Orientation = Orientation.Vertical,
            VisibleDockables = factory.CreateList<IDockable>(documentDock)
        };
        documentDock.Owner = column;

        Assert.True(WorkspaceAutoSaveService.LeavesRoomForSiblings(documentDock, 1.0));
        Assert.True(WorkspaceAutoSaveService.LeavesRoomForSiblings(column, 1.0));
    }

    private static (DocumentDock Documents, ToolDock Bottom) CreateCenterColumn()
    {
        var factory = new WorkspaceDockFactory();
        var documentDock = new DocumentDock
        {
            Id = "DocumentsPane",
            IsCollapsable = false,
            VisibleDockables = factory.CreateList<IDockable>()
        };
        var bottomDock = new ToolDock
        {
            Id = "BottomPane",
            VisibleDockables = factory.CreateList<IDockable>(new Tool { Id = "Output" })
        };
        var column = new ProportionalDock
        {
            Id = "WorkspaceCenterColumn",
            Orientation = Orientation.Vertical,
            VisibleDockables = factory.CreateList<IDockable>(
                documentDock,
                new ProportionalDockSplitter(),
                bottomDock)
        };
        documentDock.Owner = column;
        bottomDock.Owner = column;
        return (documentDock, bottomDock);
    }
}