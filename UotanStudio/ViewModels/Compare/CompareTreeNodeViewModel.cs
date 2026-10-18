using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// Directory-tree node shared by browsing and folder comparison: browsing nodes scan children on first
/// expansion, comparison nodes come from the folder-comparison result and record both sides' names.
/// <para>浏览与文件夹对比共用的目录树节点：浏览节点在首次展开时扫描子项，对比节点来自文件夹比对结果并记录两侧名称。</para>
/// </summary>
public sealed partial class CompareTreeNodeViewModel : ObservableObject
{
    private readonly CancellationToken _cancellationToken;
    private bool _childrenLoaded;

    private CompareTreeNodeViewModel(
        string name,
        string fullPath,
        string relativePath,
        bool isDirectory,
        FolderItemStatus? status,
        string leftName,
        string rightName,
        long leftSize,
        long rightSize,
        bool childrenLoaded,
        CancellationToken cancellationToken)
    {
        Name = name;
        FullPath = fullPath;
        RelativePath = relativePath;
        IsDirectory = isDirectory;
        Status = status;
        LeftName = leftName;
        RightName = rightName;
        LeftSize = leftSize;
        RightSize = rightSize;
        _childrenLoaded = childrenLoaded;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Creates a browsing node: children are scanned from disk on the first expansion and the node carries no comparison status (no status colouring).
    /// <para>创建浏览节点：子项在首次展开时扫描磁盘，没有对比状态（不显示状态色）。</para></summary>
    /// <param name="fullPath">The node's absolute path.<para>节点的绝对路径。</para></param>
    /// <param name="isDirectory">Whether the node is a directory.<para>是否为目录。</para></param>
    /// <param name="cancellationToken">The owning file column's load token; a replaced column content or a closing view cancels pending child scans.
    /// <para>所属文件列的加载令牌；列内容被替换或视图关闭后，未完成的子目录扫描会被取消。</para></param>
    /// <returns>The created browsing node.<para>创建的浏览节点。</para></returns>
    public static CompareTreeNodeViewModel ForBrowsing(
        string fullPath,
        bool isDirectory,
        CancellationToken cancellationToken = default)
    {
        var name = GetDisplayName(fullPath);
        return new CompareTreeNodeViewModel(
            name, fullPath, string.Empty, isDirectory, status: null,
            leftName: name, rightName: string.Empty, leftSize: -1, rightSize: -1,
            childrenLoaded: false, cancellationToken);
    }

    /// <summary>
    /// Creates a comparison node: name, relative path, both sides' names and sizes, and the comparison
    /// status all come from the folder-comparison result.
    /// <para>创建对比节点：名称、相对路径、两侧名称与大小、比对状态全部来自文件夹比对结果。</para>
    /// </summary>
    /// <param name="name">The entry name. <para>条目名称。</para></param>
    /// <param name="relativePath">The path relative to the comparison root ('/'-separated). <para>相对对比根目录的路径（'/' 分隔）。</para></param>
    /// <param name="isDirectory">Whether the entry is a directory. <para>是否为目录。</para></param>
    /// <param name="status">The comparison status; a directory's status is later aggregated from its subtree. <para>比对状态；目录的状态随后按子树聚合。</para></param>
    /// <param name="leftName">The name recorded for the left side.<para>记录到左侧的名称。</para></param>
    /// <param name="rightName">The name recorded for the right side.<para>记录到右侧的名称。</para></param>
    /// <param name="leftSize">The left file's size in bytes, or -1.<para>左侧文件的字节大小，或为 -1。</para></param>
    /// <param name="rightSize">The right file's size in bytes, or -1.<para>右侧文件的字节大小，或为 -1。</para></param>
    /// <returns>The created comparison node.<para>创建的对比节点。</para></returns>
    internal static CompareTreeNodeViewModel ForComparison(
        string name,
        string relativePath,
        bool isDirectory,
        FolderItemStatus status,
        string leftName,
        string rightName,
        long leftSize = -1,
        long rightSize = -1)
    {
        return new CompareTreeNodeViewModel(
            name, string.Empty, relativePath, isDirectory, status,
            leftName, rightName, leftSize, rightSize, childrenLoaded: true, default);
    }

    /// <summary>
    /// The entry name (identical on both sides; a single-side entry keeps its name here too).
    /// <para>条目名称（两侧相同，单侧条目的名称也保存在这里）。</para>
    /// </summary>
    /// <value>The entry's name.<para>条目的名称。</para></value>
    public string Name { get; }

    /// <summary>
    /// The absolute path of a browsing node; empty for a comparison node (which locates both sides by its
    /// relative path).
    /// <para>浏览节点的绝对路径；对比节点为空（对比节点靠相对路径在两侧定位）。</para>
    /// </summary>
    /// <value>The absolute path, or empty for a comparison node.<para>绝对路径；对比节点为空。</para></value>
    public string FullPath { get; }

    /// <summary>
    /// A comparison node's path relative to the comparison root ('/'-separated); empty for a browsing node.
    /// <para>对比节点相对对比根目录的路径（'/' 分隔）；浏览节点为空。</para>
    /// </summary>
    /// <value>The '/'-separated relative path, or empty for a browsing node.<para>'/' 分隔的相对路径；浏览节点为空。</para></value>
    public string RelativePath { get; }

    /// <summary>
    /// Whether the entry is a directory.
    /// <para>是否为目录。</para>
    /// </summary>
    /// <value>True when the entry is a directory.<para>条目为目录时为真。</para></value>
    public bool IsDirectory { get; }

    /// <summary>
    /// The name recorded for the left side; empty when the entry is missing on the left.
    /// <para>左侧名称；条目在左侧不存在时为空串。</para>
    /// </summary>
    /// <value>The left side's name, empty when the entry is missing there.<para>左侧名称；条目在左侧缺失时为空。</para></value>
    public string LeftName { get; }

    /// <summary>
    /// The name recorded for the right side; empty when the entry is missing on the right.
    /// <para>右侧名称；条目在右侧不存在时为空串。</para>
    /// </summary>
    /// <value>The right side's name, empty when the entry is missing there.<para>右侧名称；条目在右侧缺失时为空。</para></value>
    public string RightName { get; }

    /// <summary>
    /// The comparison status; <c>null</c> for browsing nodes. A directory's status is aggregated from its
    /// subtree: any non-equal entry below makes it different.
    /// <para>比对状态；浏览节点为 <c>null</c>。目录的状态按子树聚合：子树中有任何非相同条目即为「不同」。</para>
    /// </summary>
    /// <value>The comparison status, or null for a browsing node.<para>比对状态；浏览节点为空。</para></value>
    public FolderItemStatus? Status { get; internal set; }

    /// <summary>
    /// The left file's size in bytes; -1 for directories, a missing side or a browsing node.
    /// <para>左侧文件大小（字节）；目录、单侧缺失或浏览节点为 -1。</para>
    /// </summary>
    /// <value>The left file's size in bytes, or -1.<para>左侧文件的字节大小，或为 -1。</para></value>
    public long LeftSize { get; } = -1;

    /// <summary>
    /// The right file's size in bytes; -1 for directories, a missing side or a browsing node.
    /// <para>右侧文件大小（字节）；目录、单侧缺失或浏览节点为 -1。</para>
    /// </summary>
    /// <value>The right file's size in bytes, or -1.<para>右侧文件的字节大小，或为 -1。</para></value>
    public long RightSize { get; } = -1;

    /// <summary>
    /// The child nodes; a browsing node fills them on the first expansion and a comparison node during
    /// construction.
    /// <para>子节点集合；浏览节点在首次展开时填充，对比节点在构建时填充。</para>
    /// </summary>
    /// <value>The child nodes of this node.<para>本节点的子节点集合。</para></value>
    public ObservableCollection<CompareTreeNodeViewModel> Children { get; } = [];

    /// <summary>
    /// Whether the node can be expanded: a directory that either has not been scanned yet or really has
    /// children. An unscanned directory reports expandable first and stops after an empty scan.
    /// <para>是否可展开：目录且（尚未扫描过子项或确有子项）。尚未扫描的目录先视为可展开，扫描后若为空则不再可展开。</para>
    /// </summary>
    /// <value>True when the node can be expanded.<para>节点可展开时为真。</para></value>
    public bool IsExpandable => IsDirectory && (!_childrenLoaded || Children.Count > 0);

    /// <summary>
    /// The expansion state; both sides share one node, so expanding in a folder comparison expands both
    /// sides together.
    /// <para>展开状态；两侧共享同一节点，因此文件夹对比中一侧展开即两侧同步展开。</para>
    /// </summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>
    /// Scans and populates children on demand (directories first, each group sorted by name), enumerating the
    /// disk only on the first call and leaving the collection empty when cancelled or unreadable.
    /// <para>按需扫描并填充子节点（目录优先、各组按名称排序）：仅首次调用时枚举磁盘，取消或目录不可读时保留空集合。</para>
    /// </summary>
    public async Task EnsureChildrenLoadedAsync()
    {
        if (_childrenLoaded || !IsDirectory)
        {
            return;
        }

        _childrenLoaded = true;
        var path = FullPath;
        var token = _cancellationToken;
        try
        {
            var children = await Task.Run(() => EnumerateChildren(path, token), token);
            token.ThrowIfCancellationRequested();
            Children.Clear();
            foreach (var child in children)
            {
                Children.Add(child);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            Children.Clear();
        }
    }

    /// <summary>Attaches a comparison child node under this node.<para>把一个对比子节点挂到本节点下。</para></summary>
    /// <param name="child">The child node to attach.<para>要挂载的子节点。</para></param>
    internal void AddChild(CompareTreeNodeViewModel child) => Children.Add(child);

    /// <summary>
    /// Sorts children with directories first and each group by name (case-insensitively on Windows) so both
    /// sides render the same order.
    /// <para>按「目录优先、同级按名称」排序子项（Windows 忽略大小写），保证两侧渲染顺序一致。</para>
    /// </summary>
    internal void SortChildren()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var sorted = Children
            .OrderByDescending(child => child.IsDirectory)
            .ThenBy(child => child.Name, comparer)
            .ToList();
        Children.Clear();
        foreach (var child in sorted)
        {
            Children.Add(child);
        }
    }

    /// <summary>
    /// Enumerates one directory's direct children: directories first, then files, each group sorted by
    /// name (case-insensitively on Windows).
    /// <para>枚举一个目录的直接子项：目录在前、文件在后，各自按名称排序（Windows 忽略大小写）。</para>
    /// </summary>
    /// <param name="path">The directory to enumerate.<para>要枚举的目录路径。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    /// <returns>The child nodes, directories first then files.<para>子节点列表，目录在前、文件在后。</para></returns>
    private static List<CompareTreeNodeViewModel> EnumerateChildren(string path, CancellationToken token)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        List<CompareTreeNodeViewModel> directories = [];
        List<CompareTreeNodeViewModel> files = [];

        foreach (var directory in Directory.EnumerateDirectories(path))
        {
            token.ThrowIfCancellationRequested();
            directories.Add(ForBrowsing(directory, isDirectory: true, token));
        }

        foreach (var file in Directory.EnumerateFiles(path))
        {
            token.ThrowIfCancellationRequested();
            files.Add(ForBrowsing(file, isDirectory: false, token));
        }

        directories.Sort((left, right) => comparer.Compare(left.Name, right.Name));
        files.Sort((left, right) => comparer.Compare(left.Name, right.Name));
        directories.AddRange(files);
        return directories;
    }

    /// <summary>
    /// Extracts a path's display name, returning the path itself for a root and an empty string for an empty path.
    /// <para>取路径的显示名称；路径为空时返回空串，根目录返回路径本身。</para>
    /// </summary>
    /// <param name="path">The path to name.<para>要取名称的路径。</para></param>
    /// <returns>The display name for the path.<para>该路径的显示名称。</para></returns>
    private static string GetDisplayName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}