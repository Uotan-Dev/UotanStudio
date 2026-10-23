using System.Collections.ObjectModel;
using System.Globalization;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;
using UotanStudio.Services.Workspace;
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
}