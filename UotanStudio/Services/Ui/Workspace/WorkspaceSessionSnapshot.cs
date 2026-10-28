using Dock.Model.Controls;
using Dock.Model.Core;
using UotanStudio.Projects;
using UotanStudio.Services.Ui.Workspace.Documents;
using UotanStudio.ViewModels.Compare;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Services.Ui.Workspace;

internal sealed class WorkspaceSessionSnapshot
{
    public int Version { get; set; } = 1;
    public string? ProjectPath { get; set; }
    public Dictionary<string, bool> Panels { get; set; } = [];
    public List<OpenDocumentDto> Documents { get; set; } = [];
    public List<WorkspaceDockState> Docks { get; set; } = [];
    public WorkflowSnapshot? DraftWorkflow { get; set; }
    public ProjectSettingsDto? DraftPackingSettings { get; set; }
    public double NodeZoom { get; set; } = 1;
    public bool NodeMinimap { get; set; } = true;
}

internal sealed class WorkspaceDockState
{
    public string Id { get; set; } = string.Empty;
    public double? Proportion { get; set; }
    public string? ActiveId { get; set; }
}

internal static class WorkspaceSessionCapture
{
    /// <summary>
    /// Whether the dockable is a document-style item (same scope as <see cref="Documents"/>).
    /// <para>判定停靠项是否为文档型条目（与 <see cref="Documents"/> 的快照口径一致）。</para>
    /// </summary>
    /// <param name="item">The dockable to test. <para>待判定的停靠项。</para></param>
    /// <returns>True when the item is a document. <para>该条目为文档时返回 true。</para></returns>
    internal static bool IsDocument(IDockable item) =>
        DocumentOpenRegistry.ByInstance(item)?.IsDocument == true;

    internal static IEnumerable<IDockable> Enumerate(IDockable? root)
    {
        var visited = new HashSet<IDockable>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<IDockable>();
        if (root is not null) pending.Push(root);
        while (pending.TryPop(out var item))
        {
            if (!visited.Add(item)) continue;
            yield return item;
            if (item is IDock dock && dock.VisibleDockables is { } children)
                foreach (var child in children.Reverse()) pending.Push(child);
            if (item is IRootDock { Windows: { } windows })
                foreach (var window in windows)
                    if (window.Layout is { } layout) pending.Push(layout);
        }
    }

    internal static List<OpenDocumentDto> Documents(IDockable? root, string rootDirectory)
    {
        List<OpenDocumentDto> result = [];
        foreach (var item in Enumerate(root))
        {
            var descriptor = DocumentOpenRegistry.ByInstance(item);
            if (descriptor is not { IsDocument: true } || descriptor.GetPath is not { } getPath)
            {
                continue;
            }

            var kind = descriptor.Kind;
            var path = getPath(item);
            if (kind != "Compare" && string.IsNullOrWhiteSpace(path)) continue;
            result.Add(new()
            {
                Kind = kind,
                RelativePath = path is null ? string.Empty : Path.GetRelativePath(rootDirectory, Path.GetFullPath(path)),
                OpenMode = item is FileViewModel text ? (int)text.OpenMode : 0,
                IsActive = item.Owner is IDock owner && ReferenceEquals(owner.ActiveDockable, item),
                CompareLeftPath = item is CompareViewModel compare ? compare.LeftPath : null,
                CompareRightPath = item is CompareViewModel compareRight ? compareRight.RightPath : null
            });
        }
        return result;
    }

    internal static List<WorkspaceDockState> Docks(IDockable? root) => [.. Enumerate(root)
        .OfType<IDock>().Where(d => !string.IsNullOrEmpty(d.Id))
        .Select(d => new WorkspaceDockState
        {
            Id = d.Id!,
            Proportion = double.IsFinite(d.Proportion) ? d.Proportion : null,
            ActiveId = d.ActiveDockable?.Id
        })];
}