using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Views.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class WorkspaceDockSplitPolicyTests
{
    [Fact]
    public void UseDocumentDock_ToolSource_KeepsToolDock()
    {
        var factory = new WorkspaceDockFactory();
        var tool = new Tool { Id = "ToolA" };
        var documentDock = new DocumentDock { Id = "DocumentsPane" };

        Assert.False(((IDockSplitPolicy)factory).UseDocumentDock(tool, documentDock, DockOperation.Left));
        Assert.False(((IDockSplitPolicy)factory).UseDocumentDock(new ToolDock { Id = "LeftPane" }, documentDock,
            DockOperation.Bottom));
    }

    [Fact]
    public void UseDocumentDock_ToolIsAnIDocument_StillResolvesToToolDock()
    {
        var factory = new WorkspaceDockFactory();
        var tool = new Tool { Id = "ToolA" };

        Assert.IsAssignableFrom<IDocument>(tool);

        Assert.False(((IDockSplitPolicy)factory).UseDocumentDock(tool, new ToolDock { Id = "RightPane" },
            DockOperation.Fill));
    }

    [Fact]
    public void UseDocumentDock_DocumentSource_UsesDocumentDock()
    {
        var factory = new WorkspaceDockFactory();
        var document = new Document { Id = "Doc1" };

        Assert.True(((IDockSplitPolicy)factory).UseDocumentDock(document, new ToolDock { Id = "LeftPane" },
            DockOperation.Left));
        Assert.True(((IDockSplitPolicy)factory).UseDocumentDock(document, new DocumentDock { Id = "DocumentsPane" },
            DockOperation.Right));
    }
}