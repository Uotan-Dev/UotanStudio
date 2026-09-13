using Dock.Model.Controls;
using Dock.Model.Core;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Compare;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Services.Workspace;

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
    /// <para>判定停靠项是否为文档型面板（与 <see cref="Documents"/> 的快照口径一致）。</para>
    /// Whether the dockable is a document panel (same scope as <see cref="Documents"/>).
    /// </summary>
    internal static bool IsDocument(IDockable item) => item switch
    {
        FileViewModel or FirmwareInfoViewModel or FileManagerEditorViewModel or DtbEditorViewModel
            or DexEditorViewModel or ManifestEditorViewModel or ArscEditorViewModel or HexEditorViewModel
            or ApkEditorViewModel or PartitionEditorViewModel or CompareViewModel => true,
        _ => false
    };

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
        var result = new List<OpenDocumentDto>();
        foreach (var item in Enumerate(root))
        {
            var (kind, path) = item switch
            {
                FileViewModel file => ("Text", file.FilePath),
                FirmwareInfoViewModel file => ("Firmware", file.FilePath),
                FileManagerEditorViewModel file => ("FileManager", file.FilePath),
                DtbEditorViewModel file => ("Dtb", file.FilePath),
                DexEditorViewModel file => ("Dex", file.FilePath),
                ManifestEditorViewModel file => ("Manifest", file.FilePath),
                ArscEditorViewModel file => ("Arsc", file.FilePath),
                HexEditorViewModel file => ("Hex", file.FilePath),
                ApkEditorViewModel file => ("Apk", file.FilePath),
                PartitionEditorViewModel file => ("Partition", file.FilePath),
                CompareViewModel => ("Compare", (string?)null),
                _ => (string.Empty, (string?)null)
            };
            if (kind.Length == 0 || kind != "Compare" && string.IsNullOrWhiteSpace(path)) continue;
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

    internal static List<WorkspaceDockState> Docks(IDockable? root) => Enumerate(root)
        .OfType<IDock>().Where(d => !string.IsNullOrEmpty(d.Id))
        .Select(d => new WorkspaceDockState
        {
            Id = d.Id!, Proportion = double.IsFinite(d.Proportion) ? d.Proportion : null,
            ActiveId = d.ActiveDockable?.Id
        }).ToList();
}
