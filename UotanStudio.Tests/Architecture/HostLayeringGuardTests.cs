using Xunit;

namespace UotanStudio.Tests.Architecture;

public partial class HostLayeringGuardTests
{
    private static readonly string[] ApplicationServiceNamespaces =
    [
        "UotanStudio.Services.Flash",
        "UotanStudio.Services.Firmware",
        "UotanStudio.Services.Projects",
        "UotanStudio.Services.Patching",
        "UotanStudio.Services.PatchPackages",
        "UotanStudio.Services.Delivery",
        "UotanStudio.Services.Devices",
        "UotanStudio.Services.VisualPlugins",
        "UotanStudio.Services.Interactions",
        "UotanStudio.Services.Workspace",
        "UotanStudio.Services.Mirroring",
        "UotanStudio.Services.Scrcpy",
        "UotanStudio.Services.Scripting",
        "UotanStudio.Services.Network",
        "UotanStudio.Services.Keys",
        "UotanStudio.Services.NativeBin",
        "UotanStudio.Services.Mcp",
    ];

    private static readonly string[] PresentationNamespaces =
    [
        "UotanStudio.ViewModels",
        "UotanStudio.Views",
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> FrozenViewExceptions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["UotanStudio/Views/Compare/CompareView.Transfer.cs"] = Set("UotanStudio.Services.Projects"),
            ["UotanStudio/Views/Controls/FindReplacePanel.axaml.cs"] = Set("UotanStudio.Services.Workspace"),
            ["UotanStudio/Views/Controls/OpenAsMenuItem.cs"] = Set("UotanStudio.Services.Workspace"),
            ["UotanStudio/Views/Controls/PackImageSelectionView.axaml.cs"] = Set("UotanStudio.Services.Delivery", "UotanStudio.Services.Projects"),
            ["UotanStudio/Views/Devices/QrPairingWindow.axaml.cs"] = Set("UotanStudio.Services.Devices"),
            ["UotanStudio/Views/Interactions/InteractionFormView.cs"] = Set("UotanStudio.Services.Interactions"),
            ["UotanStudio/Views/Nodes/CompositeInterfaceWindow.cs"] = Set("UotanStudio.Services.VisualPlugins"),
            ["UotanStudio/Views/Nodes/NodeEditorView.Interactions.cs"] = Set("UotanStudio.Services.Interactions"),
            ["UotanStudio/Views/Nodes/NodeEditorView.axaml.cs"] = Set("UotanStudio.Services.VisualPlugins"),
            ["UotanStudio/Views/Shell/ApkSigningWindow.axaml.cs"] = Set("UotanStudio.Services.Keys"),
            ["UotanStudio/Views/Shell/BootPatchWindow.axaml.cs"] = Set("UotanStudio.Services.PatchPackages"),
            ["UotanStudio/Views/Shell/DeliveryView.Operations.cs"] = Set("UotanStudio.Services.Delivery", "UotanStudio.Services.Projects"),
            ["UotanStudio/Views/Shell/DeliveryView.SigningKeys.cs"] = Set("UotanStudio.Services.Keys"),
            ["UotanStudio/Views/Shell/DeliveryView.axaml.cs"] = Set("UotanStudio.Services.Firmware", "UotanStudio.Services.Projects"),
            ["UotanStudio/Views/Shell/KeyManagerWindow.axaml.cs"] = Set("UotanStudio.Services.Keys"),
            ["UotanStudio/Views/Shell/NewProjectWindow.axaml.cs"] = Set("UotanStudio.Services.Firmware", "UotanStudio.Services.Projects"),
            ["UotanStudio/Views/Shell/VisualPluginManagerWindow.axaml.cs"] = Set("UotanStudio.Services.Keys", "UotanStudio.Services.VisualPlugins"),
            ["UotanStudio/Views/Workspace/ImageView.axaml.cs"] = Set("UotanStudio.Services.Projects"),
            ["UotanStudio/Views/Workspace/ProjectExplorerView.SplitVolumes.cs"] = Set("UotanStudio.Services.Firmware"),
            ["UotanStudio/Views/Workspace/ProjectExplorerView.axaml.cs"] = Set("UotanStudio.Services.Projects"),
            ["UotanStudio/Views/Workspace/WorkspaceDockFactory.cs"] = Set("UotanStudio.Services.Workspace"),
            ["UotanStudio/Views/Workspace/WorkspaceView.SearchOperations.cs"] = Set("UotanStudio.Services.Projects", "UotanStudio.Services.Workspace"),
            ["UotanStudio/Views/Workspace/WorkspaceView.axaml.cs"] = Set("UotanStudio.Services.Workspace"),
        };

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

    [Fact]
    public void Services_DoNotReferenceViewModelsOrViews()
    {
        var offenders = new List<string>();
        foreach (var (path, content) in RepositoryLayout.SourceFiles("UotanStudio"))
        {
            if (!path.StartsWith("UotanStudio/Services/", StringComparison.Ordinal)
                || path.StartsWith("UotanStudio/Services/Ui/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var name in RepositoryLayout.ForbiddenImports(content, PresentationNamespaces))
            {
                offenders.Add($"{path}: imports {name}");
            }
        }

        Assert.True(offenders.Count == 0, "Services except Services/Ui must not reference ViewModels or Views:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void ViewModels_DoNotReferenceViews()
    {
        var offenders = new List<string>();
        foreach (var (path, content) in RepositoryLayout.SourceFiles("UotanStudio"))
        {
            if (!path.StartsWith("UotanStudio/ViewModels/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var name in RepositoryLayout.ForbiddenImports(content, ["UotanStudio.Views"]))
            {
                offenders.Add($"{path}: imports {name}");
            }
        }

        Assert.True(offenders.Count == 0, "ViewModels must not reference Views:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Views_DoNotImportApplicationServiceNamespacesOutsideTheFrozenAllowList()
    {
        var offenders = new List<string>();
        var seenExceptions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, content) in RepositoryLayout.SourceFiles("UotanStudio"))
        {
            if (!path.StartsWith("UotanStudio/Views/", StringComparison.Ordinal))
            {
                continue;
            }

            var imported = RepositoryLayout.ForbiddenImports(content, ApplicationServiceNamespaces);
            if (imported.Count == 0)
            {
                continue;
            }

            if (FrozenViewExceptions.TryGetValue(path, out var allowed))
            {
                seenExceptions.Add(path);
                foreach (var ns in imported.Where(ns => !allowed.Contains(ns)))
                {
                    offenders.Add($"{path}: imports {ns}");
                }

                continue;
            }

            foreach (var ns in imported)
            {
                offenders.Add($"{path}: imports {ns}");
            }
        }

        foreach (var stale in FrozenViewExceptions.Keys.Where(path => !seenExceptions.Contains(path)))
        {
            offenders.Add($"{stale}: frozen exception no longer matches any import");
        }

        Assert.True(offenders.Count == 0, "Views may only use application services through ViewModels or the frozen allow-list:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void ImportedNamespaces_Synthetic_ReportsMergedStaticAliasedAndGlobalDirectives()
    {
        const string content = "using System;\n"
            + "using UotanStudio.Services.Devices;\n"
            + "using System.Text;using UotanStudio.ViewModels.Nodes;\rusing UotanStudio.Views.Shell;\n"
            + "using static UotanStudio.Services.Keys.KeyLibraryService;\n"
            + "using Keys = UotanStudio.Services.Keys;\n"
            + "global using UotanStudio.Services.Workspace;\n";
        Assert.Equal(
            ["System", "UotanStudio.Services.Devices", "System.Text", "UotanStudio.ViewModels.Nodes", "UotanStudio.Views.Shell",
                "UotanStudio.Services.Keys.KeyLibraryService", "UotanStudio.Services.Keys", "UotanStudio.Services.Workspace"],
            RepositoryLayout.ImportedNamespaces(content));
    }

    [Fact]
    public void ImportedNamespaces_Synthetic_LeavesStatementsAndLookalikeRootsUnreported()
    {
        const string content = "using var stream = File.OpenRead(path);\n"
            + "using (var reader = new StreamReader(stream))\n"
            + "using UotanStudio.Services.DevicesExtra;\n";
        Assert.Empty(RepositoryLayout.ForbiddenImports(content, ["UotanStudio.Services.Devices"]));
    }

    [Fact]
    public void ForbiddenImports_Synthetic_ReportsTheForbiddenRootBeneathItsNamespace()
    {
        var offenders = RepositoryLayout.ForbiddenImports(
            "using UotanStudio.ViewModels;\nusing UotanStudio.Views.Controls;\nusing UotanStudio.Services.Devices;\n",
            PresentationNamespaces);
        Assert.Equal(["UotanStudio.ViewModels", "UotanStudio.Views"], offenders);
    }
}