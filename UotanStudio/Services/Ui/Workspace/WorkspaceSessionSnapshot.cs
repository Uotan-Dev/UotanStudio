using Dock.Model.Controls;
using Dock.Model.Core;
using UotanStudio.Pipeline.Projects;
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

    /// <summary>
    /// Captures the open documents, storing each path relative to <paramref name="rootDirectory"/> or absolute
    /// when the base is <see langword="null"/>.
    /// <para>捕获已打开文档；路径相对 <paramref name="rootDirectory"/> 存放，基准为 <see langword="null"/> 时存绝对路径。</para>
    /// </summary>
    /// <param name="root">The dock layout root. <para>停靠布局根。</para></param>
    /// <param name="rootDirectory">
    /// Base directory the paths are stored relative to; <see langword="null"/> stores absolute paths so the
    /// snapshot never depends on the process working directory.
    /// <para>路径的相对基准目录；为 <see langword="null"/> 时存绝对路径，使快照不依赖进程工作目录。</para>
    /// </param>
    /// <returns>One DTO per open document. <para>每个已打开文档一个 DTO。</para></returns>
    internal static List<OpenDocumentDto> Documents(IDockable? root, string? rootDirectory)
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
                RelativePath = path is null ? string.Empty
                    : rootDirectory is null ? Path.GetFullPath(path)
                    : Path.GetRelativePath(rootDirectory, Path.GetFullPath(path)),
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