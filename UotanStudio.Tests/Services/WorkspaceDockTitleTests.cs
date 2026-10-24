using System.Collections.ObjectModel;
using System.Globalization;
using Dock.Model;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Dock.Model.Mvvm.Core;
using UotanStudio.Services;
using UotanStudio.Services.Ui.Workspace;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

[Collection("Workspace state")]
public sealed class WorkspaceDockTitleTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");

    private static readonly IReadOnlyDictionary<string, string> PanelTitleKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WorkspacePanelService.ProjectExplorer] = "Dock_ProjectExplorer",
            [WorkspacePanelService.ProjectSetting] = "Dock_ProjectSetting",
            [WorkspacePanelService.PluginTool] = "Dock_Plugins",
            [WorkspacePanelService.ImagesView] = "Dock_ImagesView",
            [WorkspacePanelService.AiAssistant] = "Dock_AiAssistant",
            [WorkspacePanelService.Properties] = "Dock_Properties",
            [WorkspacePanelService.WorkspaceLog] = "Dock_Output",
            [WorkspacePanelService.AdbLog] = "Dock_AdbLog"
        };

    [Fact]
    public void Register_AppliesLocalizedTitle_ThenRefreshFollowsLanguageSwitch()
    {
        var language = LanguageService.Instance;
        var original = language.CurrentLanguage;
        try
        {
            language.ApplyLanguage("en-US");
            var panels = new WorkspacePanelService();
            var explorer = new Tool { Id = WorkspacePanelService.ProjectExplorer };
            var dock = new ToolDock
            {
                VisibleDockables = new ObservableCollection<IDockable> { explorer },
                ActiveDockable = explorer
            };
            explorer.Owner = dock;

            panels.Register(WorkspacePanelService.ProjectExplorer, explorer, dock, visible: true);

            Assert.Equal(language.GetTranslation("Dock_ProjectExplorer", English), explorer.Title);
            Assert.NotEqual(language.GetTranslation("Dock_ProjectExplorer", Chinese), explorer.Title);

            language.ApplyLanguage("zh-CN");

            Assert.Equal(language.GetTranslation("Dock_ProjectExplorer", Chinese), explorer.Title);
        }
        finally
        {
            language.ApplyLanguage(original);
        }
    }

    [Fact]
    public void RefreshLocalizedTitles_UpdatesEveryRegisteredPanel_FromTheCentralKeyMap()
    {
        var language = LanguageService.Instance;
        var original = language.CurrentLanguage;
        try
        {
            language.ApplyLanguage("en-US");
            var panels = new WorkspacePanelService();
            var dock = new ToolDock { VisibleDockables = new ObservableCollection<IDockable>() };
            var registered = new List<(string Key, Tool Dockable)>();
            foreach (var (panelKey, _) in PanelTitleKeys)
            {
                var tool = new Tool { Id = panelKey };
                tool.Owner = dock;
                dock.VisibleDockables.Add(tool);
                registered.Add((panelKey, tool));
                panels.Register(panelKey, tool, dock, visible: true);
            }

            language.ApplyLanguage("zh-CN");
            panels.RefreshLocalizedTitles();

            foreach (var (panelKey, tool) in registered)
            {
                var expected = language.GetTranslation(PanelTitleKeys[panelKey], Chinese);
                Assert.Equal(expected, tool.Title);
            }

            language.ApplyLanguage("en-US");
            panels.RefreshLocalizedTitles();

            foreach (var (panelKey, tool) in registered)
            {
                var expected = language.GetTranslation(PanelTitleKeys[panelKey], English);
                Assert.Equal(expected, tool.Title);
            }
        }
        finally
        {
            language.ApplyLanguage(original);
        }
    }

    [Fact]
    public void RefreshLocalizedTitles_UpdatesTitledDockables_ThatAreNotToggleablePanels()
    {
        var language = LanguageService.Instance;
        var original = language.CurrentLanguage;
        try
        {
            language.ApplyLanguage("en-US");
            var panels = new WorkspacePanelService();
            var documents = new DocumentDock { Id = "DocumentsPane" };

            panels.RegisterLocalizedTitle(documents, "Dock_Documents");

            Assert.Equal(language.GetTranslation("Dock_Documents", English), documents.Title);

            language.ApplyLanguage("zh-CN");
            panels.RefreshLocalizedTitles();

            Assert.Equal(language.GetTranslation("Dock_Documents", Chinese), documents.Title);
        }
        finally
        {
            language.ApplyLanguage(original);
        }
    }

    [Fact]
    public void Register_LeavesTitleOfUnknownPanelKey_Unchanged()
    {
        var panels = new WorkspacePanelService();
        var tool = new Tool { Id = "UnmappedPanel", Title = "Keep me" };
        var dock = new ToolDock { VisibleDockables = new ObservableCollection<IDockable> { tool } };
        tool.Owner = dock;

        panels.Register("UnmappedPanel", tool, dock, visible: true);

        Assert.Equal("Keep me", tool.Title);
    }

    [Fact]
    public void DockAsTool_MovesDocumentToToolDock_AndDockAsDocumentRestoresIt()
    {
        var factory = new WorkspaceDockFactory();
        var root = factory.CreateLayout();
        factory.InitLayout(root);
        try
        {
            var documentDock = Assert.IsAssignableFrom<IDocumentDock>(
                factory.FindDockable(root, static d => d.Id == "DocumentsPane"));
            var bottomDock = Assert.IsAssignableFrom<IToolDock>(
                factory.FindDockable(root, static d => d.Id == "BottomPane"));

            var document = new FileViewModel { Id = "Doc1", Title = "build.prop" };
            factory.AddDockable(documentDock, document);
            documentDock.ActiveDockable = document;

            factory.DockAsTool((object)document);

            Assert.Same(bottomDock, document.Owner);
            Assert.Contains(document, bottomDock.VisibleDockables!);
            Assert.DoesNotContain(document, documentDock.VisibleDockables!);

            factory.DockAsDocument(document);

            Assert.Same(documentDock, document.Owner);
            Assert.Contains(document, documentDock.VisibleDockables!);
            Assert.DoesNotContain(document, bottomDock.VisibleDockables!);
        }
        finally
        {
            factory.DisposeTerminal();
        }
    }

    [Fact]
    public void DockAsTool_RestoresToolPreviouslyDockedAsDocument_ToOriginalToolDock()
    {
        var factory = new WorkspaceDockFactory();
        var root = factory.CreateLayout();
        factory.InitLayout(root);
        try
        {
            var documentDock = Assert.IsAssignableFrom<IDocumentDock>(
                factory.FindDockable(root, static d => d.Id == "DocumentsPane"));
            var rightDock = Assert.IsAssignableFrom<IToolDock>(
                factory.FindDockable(root, static d => d.Id == "RightPane"));
            var propertiesTool = Assert.Single(rightDock.VisibleDockables!);

            factory.DockAsDocument(propertiesTool);
            Assert.Same(documentDock, propertiesTool.Owner);

            factory.DockAsTool(propertiesTool);
            Assert.Same(rightDock, propertiesTool.Owner);
            Assert.Contains(propertiesTool, rightDock.VisibleDockables!);
        }
        finally
        {
            factory.DisposeTerminal();
        }
    }

    [Fact]
    public void DockManager_AllowsDraggingWorkspaceDocument_IntoToolPanesAndDocumentPane()
    {
        var factory = new WorkspaceDockFactory();
        var root = factory.CreateLayout();
        factory.InitLayout(root);
        try
        {
            var documentDock = Assert.IsAssignableFrom<DocumentDock>(
                factory.FindDockable(root, static d => d.Id == "DocumentsPane"));
            Assert.False(documentDock.EnableGlobalDocking);

            var leftPane = Assert.IsAssignableFrom<IToolDock>(
                factory.FindDockable(root, static d => d.Id == "LeftPane"));
            var projectExplorer = Assert.IsAssignableFrom<ITool>(
                factory.FindDockable(root, static d => d.Id == WorkspacePanelService.ProjectExplorer));
            var rightTopPane = Assert.IsAssignableFrom<IToolDock>(
                factory.FindDockable(root, static d => d.Id == "RightPaneTop"));
            var imagesView = Assert.IsAssignableFrom<ITool>(
                factory.FindDockable(root, static d => d.Id == WorkspacePanelService.ImagesView));

            var doc1 = new FileViewModel { Id = "Doc1", Title = "build.prop" };
            var document = new FileViewModel { Id = "DocDrag", Title = "system.prop" };
            factory.AddDockable(documentDock, doc1);
            factory.AddDockable(documentDock, document);
            documentDock.ActiveDockable = document;

            var dockManager = new DockManager(new DockService());

            Assert.True(dockManager.ValidateDockable(document, leftPane, DragAction.Move, DockOperation.Fill, bExecute: false));
            Assert.True(dockManager.ValidateDockable(document, projectExplorer, DragAction.Move, DockOperation.Fill, bExecute: false));
            Assert.True(dockManager.ValidateDockable(document, rightTopPane, DragAction.Move, DockOperation.Fill, bExecute: false));
            Assert.True(dockManager.ValidateDockable(document, imagesView, DragAction.Move, DockOperation.Fill, bExecute: false));
            Assert.True(dockManager.ValidateDockable(document, documentDock, DragAction.Move, DockOperation.Right, bExecute: true));

            var allDocDocks = new List<IDocumentDock>();
            CollectDocumentDocks(root, allDocDocks);
            Assert.Equal(2, allDocDocks.Count);
        }
        finally
        {
            factory.DisposeTerminal();
        }
    }

    [Theory]
    [InlineData(DockOperation.Top)]
    [InlineData(DockOperation.Bottom)]
    public void ClosingLastDocumentInVerticalSplitDocumentDock_AutomaticallyCollapsesEmptyDocumentDock(DockOperation operation)
    {
        var factory = new WorkspaceDockFactory();
        var root = factory.CreateLayout();
        factory.InitLayout(root);
        try
        {
            var firstDock = Assert.IsAssignableFrom<IDocumentDock>(
                factory.FindDockable(root, static d => d.Id == "DocumentsPane"));

            var doc1 = new FileViewModel { Id = "Doc1", Title = "one.txt", CanClose = true };
            var doc2 = new FileViewModel { Id = "Doc2", Title = "two.txt", CanClose = true };
            factory.AddDockable(firstDock, doc1);
            factory.AddDockable(firstDock, doc2);
            firstDock.ActiveDockable = doc2;

            var dockManager = new DockManager(new DockService());
            Assert.True(dockManager.ValidateDockable(doc2, firstDock, DragAction.Move, operation, bExecute: true));

            var docDocksAfterSplit = new List<IDocumentDock>();
            CollectDocumentDocks(root, docDocksAfterSplit);
            Assert.Equal(2, docDocksAfterSplit.Count);
            Assert.All(docDocksAfterSplit, static d => Assert.True(d.IsCollapsable));

            var closingDock = Assert.IsAssignableFrom<IDocumentDock>(doc1.Owner);
            var remainingDockBeforeClose = Assert.IsAssignableFrom<IDocumentDock>(doc2.Owner);
            var bottomDock = Assert.IsAssignableFrom<IToolDock>(
                factory.FindDockable(root, static d => d.Id == "BottomPane"));
            var expectedRemainingProportion = 1 - bottomDock.Proportion;
            var expectedBottomProportion = bottomDock.Proportion;

            factory.RemoveDockable(doc1, collapse: true);

            var docDocksAfterFirstClose = new List<IDocumentDock>();
            CollectDocumentDocks(root, docDocksAfterFirstClose);
            var remainingDock = Assert.Single(docDocksAfterFirstClose);
            Assert.False(remainingDock.IsCollapsable);
            Assert.Equal("DocumentsPane", remainingDock.Id);
            Assert.Contains(doc2, remainingDock.VisibleDockables!);
            Assert.Equal(expectedRemainingProportion, remainingDock.Proportion, precision: 6);
            Assert.Equal(expectedBottomProportion, bottomDock.Proportion, precision: 6);

            factory.RemoveDockable(doc2, collapse: true);

            var docDocksAfterFinalClose = new List<IDocumentDock>();
            CollectDocumentDocks(root, docDocksAfterFinalClose);
            var finalDock = Assert.Single(docDocksAfterFinalClose);
            Assert.Empty(finalDock.VisibleDockables!);
        }
        finally
        {
            factory.DisposeTerminal();
        }
    }

    [Theory]
    [InlineData(DockOperation.Top)]
    [InlineData(DockOperation.Bottom)]
    public void ClosingEveryOriginalDocumentInVerticalSplit_CollapsesOnlyOriginalDocumentDock(DockOperation operation)
    {
        var factory = new WorkspaceDockFactory();
        var root = factory.CreateLayout();
        factory.InitLayout(root);
        try
        {
            var originalDock = Assert.IsAssignableFrom<IDocumentDock>(
                factory.FindDockable(root, static d => d.Id == "DocumentsPane"));
            var originalOne = new FileViewModel { Id = "Original1", Title = "one.txt", CanClose = true };
            var originalTwo = new FileViewModel { Id = "Original2", Title = "two.txt", CanClose = true };
            var splitDocument = new FileViewModel { Id = "Split", Title = "three.txt", CanClose = true };
            factory.AddDockable(originalDock, originalOne);
            factory.AddDockable(originalDock, originalTwo);
            factory.AddDockable(originalDock, splitDocument);
            originalDock.ActiveDockable = splitDocument;

            var dockManager = new DockManager(new DockService());
            Assert.True(dockManager.ValidateDockable(
                splitDocument, originalDock, DragAction.Move, operation, bExecute: true));
            var splitDock = Assert.IsAssignableFrom<IDocumentDock>(splitDocument.Owner);
            var bottomDock = Assert.IsAssignableFrom<IToolDock>(
                factory.FindDockable(root, static d => d.Id == "BottomPane"));
            var expectedBottomProportion = bottomDock.Proportion;

            factory.RemoveDockable(originalOne, collapse: true);
            Assert.Equal(2, CollectDocumentDocks(root).Count);
            factory.RemoveDockable(originalTwo, collapse: true);

            var remainingDock = Assert.Single(CollectDocumentDocks(root));
            Assert.Same(splitDock, remainingDock);
            Assert.Contains(splitDocument, remainingDock.VisibleDockables!);
            Assert.DoesNotContain(originalDock, WorkspaceSessionCapture.Enumerate(root));
            Assert.Equal(expectedBottomProportion, bottomDock.Proportion, precision: 6);
        }
        finally
        {
            factory.DisposeTerminal();
        }
    }

    [Theory]
    [InlineData(DockOperation.Top)]
    [InlineData(DockOperation.Bottom)]
    public void SplittingDocumentAgainstCenterColumn_CreatesAnotherDocumentDock(DockOperation operation)
    {
        var factory = new WorkspaceDockFactory();
        var root = factory.CreateLayout();
        factory.InitLayout(root);
        try
        {
            var originalDock = Assert.IsAssignableFrom<IDocumentDock>(
                factory.FindDockable(root, static d => d.Id == "DocumentsPane"));
            var centerColumn = Assert.IsAssignableFrom<IDock>(
                factory.FindDockable(root, static d => d.Id == "WorkspaceCenterColumn"));
            var originalDocument = new FileViewModel { Id = "Original", Title = "one.txt" };
            var splitDocument = new FileViewModel { Id = "Split", Title = "two.txt" };
            factory.AddDockable(originalDock, originalDocument);
            factory.AddDockable(originalDock, splitDocument);
            originalDock.ActiveDockable = splitDocument;

            var dockManager = new DockManager(new DockService());
            Assert.True(dockManager.ValidateDockable(
                splitDocument, centerColumn, DragAction.Move, operation, bExecute: true));

            Assert.IsAssignableFrom<IDocumentDock>(splitDocument.Owner);
            Assert.Equal(2, CollectDocumentDocks(root).Count);
        }
        finally
        {
            factory.DisposeTerminal();
        }
    }

    [Fact]
    public async Task CloseAllDockables_OriginalVerticalSplit_RemovesEmptyDockAndPreservesOutput()
    {
        var factory = new WorkspaceDockFactory();
        var root = factory.CreateLayout();
        factory.InitLayout(root);
        root.Window = new DockWindow();
        try
        {
            var originalDock = Assert.IsAssignableFrom<IDocumentDock>(
                factory.FindDockable(root, static d => d.Id == "DocumentsPane"));
            var originalOne = new FileViewModel { Id = "Original1", Title = "one.txt", CanClose = true };
            var originalTwo = new FileViewModel { Id = "Original2", Title = "two.txt", CanClose = true };
            var splitDocument = new FileViewModel { Id = "Split", Title = "three.txt", CanClose = true };
            factory.AddDockable(originalDock, originalOne);
            factory.AddDockable(originalDock, originalTwo);
            factory.AddDockable(originalDock, splitDocument);
            originalDock.ActiveDockable = splitDocument;

            var dockManager = new DockManager(new DockService());
            Assert.True(dockManager.ValidateDockable(
                splitDocument, originalDock, DragAction.Move, DockOperation.Bottom, bExecute: true));
            var bottomDock = Assert.IsAssignableFrom<IToolDock>(
                factory.FindDockable(root, static d => d.Id == "BottomPane"));
            var expectedBottomProportion = bottomDock.Proportion;

            factory.CloseAllDockables(originalTwo);
            await WaitForAsync(
                () => !WorkspaceSessionCapture.Enumerate(root).Contains(originalDock),
                TestContext.Current.CancellationToken);

            var remainingDock = Assert.Single(CollectDocumentDocks(root));
            Assert.Contains(splitDocument, remainingDock.VisibleDockables!);
            Assert.Equal(expectedBottomProportion, bottomDock.Proportion, precision: 6);
        }
        finally
        {
            factory.DisposeTerminal();
        }
    }

    [Theory]
    [InlineData(DockOperation.Left)]
    [InlineData(DockOperation.Right)]
    [InlineData(DockOperation.Top)]
    [InlineData(DockOperation.Bottom)]
    public void SplitScenarios_DoNotThrow(DockOperation operation)
    {
        var factory = new WorkspaceDockFactory();
        var root = factory.CreateLayout();
        factory.InitLayout(root);
        try
        {
            var dockManager = new DockManager(new DockService());
            var firstDock = Assert.IsAssignableFrom<IDocumentDock>(
                factory.FindDockable(root, static d => d.Id == "DocumentsPane"));
            var doc1 = new FileViewModel { Id = "Doc1", Title = "one.txt" };
            var doc2 = new FileViewModel { Id = "Doc2", Title = "two.txt" };
            var doc3 = new FileViewModel { Id = "Doc3", Title = "three.txt" };
            factory.AddDockable(firstDock, doc1);
            factory.AddDockable(firstDock, doc2);
            factory.AddDockable(firstDock, doc3);

            Assert.True(dockManager.ValidateDockable(doc2, firstDock, DragAction.Move, operation, bExecute: true));
            var secondDock = Assert.IsAssignableFrom<IDocumentDock>(doc2.Owner);

            // Move the only document of the split dock into another split of the first dock.
            Assert.True(dockManager.ValidateDockable(doc2, firstDock, DragAction.Move, operation, bExecute: true));

            var rightDock = Assert.IsAssignableFrom<IToolDock>(
                factory.FindDockable(root, static d => d.Id == "RightPane"));
            var properties = rightDock.VisibleDockables![0];
            var currentDocDock = Assert.IsAssignableFrom<IDocumentDock>(doc1.Owner);
            dockManager.ValidateDockable(properties, currentDocDock, DragAction.Move, operation, bExecute: true);

            var mainLayout = Assert.IsAssignableFrom<IDock>(
                factory.FindDockable(root, static d => d.Id == "WorkspaceMainLayout"));
            var bottom = Assert.IsAssignableFrom<IToolDock>(
                factory.FindDockable(root, static d => d.Id == "BottomPane"));
            dockManager.ValidateDockable(bottom.VisibleDockables![0], mainLayout, DragAction.Move, operation, bExecute: true);
            dockManager.ValidateDockable(doc3, mainLayout, DragAction.Move, operation, bExecute: true);
            dockManager.ValidateDockable(doc1, root, DragAction.Move, operation, bExecute: true);
            _ = secondDock;
        }
        finally
        {
            factory.DisposeTerminal();
        }
    }

    [Fact]
    public void ClosingLastDocumentInSingleDocumentDock_KeepsDocumentAndOutputProportions()
    {
        var factory = new WorkspaceDockFactory();
        var root = factory.CreateLayout();
        factory.InitLayout(root);
        try
        {
            var documentDock = Assert.IsAssignableFrom<IDocumentDock>(
                factory.FindDockable(root, static d => d.Id == "DocumentsPane"));
            var centerColumn = Assert.IsAssignableFrom<IProportionalDock>(
                factory.FindDockable(root, static d => d.Id == "WorkspaceCenterColumn"));
            var bottomDock = Assert.IsAssignableFrom<IToolDock>(
                factory.FindDockable(root, static d => d.Id == "BottomPane"));
            var document = new FileViewModel { Id = "Doc1", Title = "one.txt", CanClose = true };
            factory.AddDockable(documentDock, document);
            documentDock.ActiveDockable = document;
            var expectedBottomProportion = bottomDock.Proportion;

            factory.RemoveDockable(document, collapse: true);

            Assert.Empty(documentDock.VisibleDockables!);
            Assert.Same(centerColumn, documentDock.Owner);
            Assert.False(documentDock.IsCollapsable);
            Assert.Equal(expectedBottomProportion, bottomDock.Proportion, precision: 6);
        }
        finally
        {
            factory.DisposeTerminal();
        }
    }

    [Theory]
    [InlineData(DockOperation.Top)]
    [InlineData(DockOperation.Bottom)]
    public void FindDocumentsPane_AfterOriginalDockCollapses_ReturnsRemainingVisibleDock(DockOperation operation)
    {
        var factory = new WorkspaceDockFactory();
        var workspace = new WorkspaceViewModel(factory);
        var root = Assert.IsAssignableFrom<IRootDock>(workspace.Layout);
        root.Window = new DockWindow();
        try
        {
            var originalDock = Assert.IsAssignableFrom<IDocumentDock>(workspace.FindDocumentsPanePublic());
            var centerColumn = Assert.IsAssignableFrom<IDock>(
                factory.FindDockable(root, static d => d.Id == "WorkspaceCenterColumn"));
            var originalDocument = new FileViewModel { Id = "Original", Title = "one.txt" };
            var splitDocument = new FileViewModel { Id = "Split", Title = "two.txt" };
            factory.AddDockable(originalDock, originalDocument);
            factory.AddDockable(originalDock, splitDocument);
            originalDock.ActiveDockable = splitDocument;

            var dockManager = new DockManager(new DockService());
            Assert.True(dockManager.ValidateDockable(
                splitDocument, originalDock, DragAction.Move, operation, bExecute: true));
            factory.RemoveDockable(originalDocument, collapse: true);

            var remainingDock = Assert.Single(CollectDocumentDocks(root));
            Assert.DoesNotContain(originalDock, centerColumn.VisibleDockables!);
            originalDock.Owner = centerColumn;

            Assert.Same(remainingDock, workspace.FindDocumentsPanePublic());
        }
        finally
        {
            factory.DisposeTerminal();
        }
    }

    private static void CollectDocumentDocks(IDockable? current, List<IDocumentDock> result)
    {
        if (current is null)
        {
            return;
        }

        if (current is IDocumentDock documentDock)
        {
            result.Add(documentDock);
        }

        if (current is IDock { VisibleDockables: { } children })
        {
            for (var i = 0; i < children.Count; i++)
            {
                CollectDocumentDocks(children[i], result);
            }
        }
    }

    private static List<IDocumentDock> CollectDocumentDocks(IDockable? current)
    {
        var result = new List<IDocumentDock>();
        CollectDocumentDocks(current, result);
        return result;
    }

    private static async Task WaitForAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(10, cancellationToken);
        }

        Assert.True(predicate());
    }
}