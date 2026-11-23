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
    public WorkspaceActiveWindow? ActiveWindow { get; set; }
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

/// <summary>
/// Identity of the window that was in the foreground when the session was saved. Documents carry their kind and
/// persisted path (or device key for a mirror); non-document docks carry their stable dock id.
/// <para>保存会话时处于前台的窗口标识。文档记录种类与持久化路径（投屏为设备键）；非文档停靠面板记录稳定的 Dock Id。</para>
/// </summary>
internal sealed class WorkspaceActiveWindow
{
    /// <summary>The persisted document kind, or <c>Dock</c> for a tool pane. <para>持久化的文档种类，工具面板为 <c>Dock</c>。</para></summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The document path/device key or the dockable id. <para>文档路径/设备键或停靠项 Id。</para></summary>
    public string Identity { get; set; } = string.Empty;

    /// <summary>The owning dock id for a tool pane, unused for documents. <para>工具面板所属的停靠区 Id，文档不使用。</para></summary>
    public string? DockId { get; set; }
}

internal static class WorkspaceSessionCapture
{
    /// <summary>
    /// Whether the dockable is a document-style item that is bulk-closed on a project switch.
    /// <para>判定停靠项是否为工程切换时批量关闭的文档型条目。</para>
    /// </summary>
    /// <param name="item">The dockable to test. <para>待判定的停靠项。</para></param>
    /// <returns>True when the item is a document. <para>该条目为文档时返回 true。</para></returns>
    internal static bool IsDocument(IDockable item) =>
        DocumentOpenRegistry.ByInstance(item)?.IsDocument == true;

    /// <summary>
    /// Whether the dockable is written to the workspace session, including log, QCN and mirror windows.
    /// <para>判定停靠项是否写入工作区会话，包括日志、QCN 与投屏窗口。</para>
    /// </summary>
    /// <param name="item">The dockable to test. <para>待判定的停靠项。</para></param>
    /// <returns>True when the item participates in session persistence. <para>该条目参与会话持久化时返回 true。</para></returns>
    internal static bool IsPersisted(IDockable item) =>
        DocumentOpenRegistry.ByInstance(item)?.PersistInSession == true;

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
            if (descriptor is not { PersistInSession: true } || descriptor.GetPath is not { } getPath)
            {
                continue;
            }

            var kind = descriptor.Kind;
            var path = getPath(item);
            if (!descriptor.AllowEmptyPath && string.IsNullOrWhiteSpace(path)) continue;
            result.Add(new()
            {
                Kind = kind,
                RelativePath = descriptor.PathIsIdentifier ? path ?? string.Empty
                    : path is null ? string.Empty
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

    /// <summary>
    /// Captures the foreground window: an active persisted document wins, otherwise the active non-document dock
    /// with a stable id is recorded so tool panes can be focused again on restore.
    /// <para>捕获前台窗口：优先取活动的持久化文档；否则记录带稳定 Id 的非文档停靠面板，以便还原时重新聚焦。</para>
    /// </summary>
    /// <param name="root">The dock layout root. <para>停靠布局根。</para></param>
    /// <param name="documents">The already captured document list. <para>已捕获的文档列表。</para></param>
    /// <returns>The active window identity, or null when nothing can be identified. <para>活动窗口标识；无法识别时为 null。</para></returns>
    internal static WorkspaceActiveWindow? ActiveWindow(IDockable? root, IReadOnlyList<OpenDocumentDto> documents)
    {
        if (documents.FirstOrDefault(d => d.IsActive) is { } active)
        {
            return new() { Kind = active.Kind, Identity = active.RelativePath };
        }

        foreach (var dock in Enumerate(root).OfType<IDock>())
        {
            if (dock.ActiveDockable is { } item && item is not IDock && !string.IsNullOrEmpty(item.Id))
            {
                return new() { Kind = "Dock", Identity = item.Id, DockId = dock.Id };
            }
        }

        return null;
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