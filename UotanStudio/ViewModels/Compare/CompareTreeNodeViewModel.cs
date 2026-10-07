using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// <para>对比视图的目录树节点，浏览与文件夹对比共用一种模型：</para>
/// <para>· 浏览节点（<see cref="ForBrowsing"/>）只描述单侧的目录树，子项在首次展开时扫描磁盘，
/// 没有对比状态；</para>
/// <para>· 对比节点（<see cref="ForComparison"/>）由文件夹比对结果一次性构建，左右两侧的名称与
/// 存在性都记录在节点上，因此同一行天然对齐、缺失侧为空，两侧展开状态也共享。</para>
/// Directory-tree node shared by browsing and folder comparison. A browsing node
/// (<see cref="ForBrowsing"/>) describes one side's tree, scans children from disk on first expansion and
/// carries no comparison status. A comparison node (<see cref="ForComparison"/>) is built once from the
/// folder-comparison result and records both sides' names and existence, so rows stay aligned, the
/// missing side stays empty and both sides share one expansion state.
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

    /// <summary>
    /// <para>创建浏览节点：子项在首次展开时扫描磁盘，没有对比状态（不显示状态色）。</para>
    /// Creates a browsing node: children are scanned from disk on the first expansion and the node carries
    /// no comparison status (no status colouring).
    /// </summary>
    /// <param name="fullPath"><para>节点的绝对路径。</para>The node's absolute path.</param>
    /// <param name="isDirectory"><para>是否为目录。</para>Whether the node is a directory.</param>
    /// <param name="cancellationToken">
    /// <para>所属文件列的加载令牌；列内容被替换或视图关闭后，未完成的子目录扫描会被取消。</para>
    /// The owning file column's load token; a replaced column content or a closing view cancels pending
    /// child scans.
    /// </param>
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
    /// <para>创建对比节点：名称、相对路径、两侧存在性与比对状态来自文件夹比对结果，
    /// 子项由文档按结果层级装配，不再访问磁盘。</para>
    /// Creates a comparison node: name, relative path, both sides' existence and the comparison status
    /// come from the folder-comparison result, and children are assembled by the document from the same
    /// result without touching the disk again.
    /// </summary>
    /// <param name="name"><para>条目名称。</para>The entry name.</param>
    /// <param name="relativePath"><para>相对对比根目录的路径（'/' 分隔）。</para>The path relative to the comparison root ('/'-separated).</param>
    /// <param name="isDirectory"><para>是否为目录。</para>Whether the entry is a directory.</param>
    /// <param name="status"><para>比对状态；目录的状态随后按子树聚合。</para>The comparison status; a directory's status is later aggregated from its subtree.</param>
    /// <param name="existsOnLeft"><para>是否存在于左侧。</para>Whether the entry exists on the left.</param>
    /// <param name="existsOnRight"><para>是否存在于右侧。</para>Whether the entry exists on the right.</param>
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
    /// <para>条目名称（两侧相同，单侧条目的名称也保存在这里）。</para>
    /// The entry name (identical on both sides; a single-side entry keeps its name here too).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// <para>浏览节点的绝对路径；对比节点为空（对比节点靠相对路径在两侧定位）。</para>
    /// The absolute path of a browsing node; empty for a comparison node (which locates both sides by its
    /// relative path).
    /// </summary>
    public string FullPath { get; }

    /// <summary>
    /// <para>对比节点相对对比根目录的路径（'/' 分隔）；浏览节点为空。</para>
    /// A comparison node's path relative to the comparison root ('/'-separated); empty for a browsing node.
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// <para>是否为目录。</para>
    /// Whether the entry is a directory.
    /// </summary>
    public bool IsDirectory { get; }

    /// <summary>
    /// <para>左侧显示名称；左侧不存在时为空串（该侧渲染空单元格）。</para>
    /// The name shown on the left; empty when the entry is missing on the left (the side renders an empty
    /// cell).
    /// </summary>
    public string LeftName { get; }

    /// <summary>
    /// <para>右侧显示名称；右侧不存在时为空串。</para>
    /// The name shown on the right; empty when the entry is missing on the right.
    /// </summary>
    public string RightName { get; }

    /// <summary>
    /// <para>比对状态；浏览节点为 <c>null</c>（不显示状态色）。目录的状态按子树聚合：两侧都存在时，
    /// 子树里有任何非相同条目即为「不同」，因此最初始的文件夹也会整行变色。</para>
    /// The comparison status; <c>null</c> for browsing nodes (no status colouring). A directory's status is
    /// aggregated from its subtree: when both sides exist, any non-equal entry below makes it "different",
    /// so the initial folder is colourised too.
    /// </summary>
    public FolderItemStatus? Status { get; internal set; }

    /// <summary>
    /// <para>左侧文件大小（字节）；目录、单侧缺失或浏览节点为 -1。</para>
    /// The left file's size in bytes; -1 for directories, a missing side or a browsing node.
    /// </summary>
    public long LeftSize { get; } = -1;

    /// <summary>
    /// <para>右侧文件大小（字节）；目录、单侧缺失或浏览节点为 -1。</para>
    /// The right file's size in bytes; -1 for directories, a missing side or a browsing node.
    /// </summary>
    public long RightSize { get; } = -1;

    /// <summary>
    /// <para>子节点集合；浏览节点在首次展开时填充，对比节点在构建时填充。</para>
    /// The child nodes; a browsing node fills them on the first expansion and a comparison node during
    /// construction.
    /// </summary>
    public ObservableCollection<CompareTreeNodeViewModel> Children { get; } = [];

    /// <summary>
    /// <para>是否可展开：目录且（尚未扫描过子项或确有子项）。尚未扫描的目录先显示展开箭头，
    /// 展开后为空目录不再显示箭头。</para>
    /// Whether the node can be expanded: a directory that either has not been scanned yet or really has
    /// children. An unscanned directory shows the arrow first, and an empty one drops it after expanding.
    /// </summary>
    public bool IsExpandable => IsDirectory && (!_childrenLoaded || Children.Count > 0);

    /// <summary>
    /// <para>展开状态；两侧共享同一节点，因此文件夹对比中一侧展开即两侧同步展开。</para>
    /// The expansion state; both sides share one node, so expanding in a folder comparison expands both
    /// sides together.
    /// </summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>
    /// <para>按需扫描并填充子节点（目录优先，同级按名称排序）：仅在首次调用时枚举磁盘，
    /// 重复调用立即返回。命令令牌取消或目录不可读时保留空集合而不抛出。</para>
    /// Scans and populates children on demand (directories first, each group sorted by name); enumerates
    /// the disk only on the first call and returns immediately afterwards. A cancelled token or an
    /// unreadable directory leaves an empty collection instead of throwing.
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

    /// <summary>把一个对比子节点挂到本节点下（由文档装配对比树时调用）。</summary>
    internal void AddChild(CompareTreeNodeViewModel child) => Children.Add(child);

    /// <summary>
    /// <para>按「目录优先、同级按名称」排序子项（Windows 忽略大小写），保证两侧渲染顺序一致。</para>
    /// Sorts children with directories first and each group by name (case-insensitively on Windows) so both
    /// sides render the same order.
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
    /// <para>枚举一个目录的直接子项：目录在前、文件在后，各自按名称排序（Windows 忽略大小写）。</para>
    /// Enumerates one directory's direct children: directories first, then files, each group sorted by
    /// name (case-insensitively on Windows).
    /// </summary>
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

    /// <summary>取路径的显示名称；路径为空时返回空串，根目录返回路径本身。</summary>
    private static string GetDisplayName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}