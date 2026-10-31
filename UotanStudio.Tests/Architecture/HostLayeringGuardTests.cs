using System.Text.RegularExpressions;
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

            offenders.AddRange(RepositoryLayout.Matches(path, content, ServicesToPresentationPattern()));
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

            offenders.AddRange(RepositoryLayout.Matches(path, content, ViewsUsingPattern()));
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

            var imported = ApplicationServiceNamespaces
                .Where(ns => Regex.IsMatch(content, "^\\s*using\\s+" + Regex.Escape(ns) + @"(?:\.|;)",
                    RegexOptions.CultureInvariant | RegexOptions.Multiline))
                .ToArray();
            if (imported.Length == 0)
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
    public void ServicesToPresentationPattern_Synthetic_ReportsBothNamespaces()
    {
        const string content = "using UotanStudio.ViewModels.Nodes;\nusing UotanStudio.Views.Controls;";
        Assert.Equal(2, RepositoryLayout.Matches("sample.cs", content, ServicesToPresentationPattern()).Count);
    }

    [GeneratedRegex(@"^\s*using\s+UotanStudio\.(?:ViewModels|Views)(?:\.|;)", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex ServicesToPresentationPattern();

    [GeneratedRegex(@"^\s*using\s+UotanStudio\.Views(?:\.|;)", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex ViewsUsingPattern();
}