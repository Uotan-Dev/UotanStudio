using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// The kind of a filesystem entry.
/// <para>文件系统条目的类型。</para>
/// </summary>
public enum FileEntryKind
{
    /// <summary>
    /// A regular file.
    /// <para>普通文件。</para>
    /// </summary>
    File,

    /// <summary>
    /// A directory.
    /// <para>目录。</para>
    /// </summary>
    Directory,

    /// <summary>
    /// A symbolic link.
    /// <para>符号链接。</para>
    /// </summary>
    Symlink,
}

/// <summary>
/// File-manager document tree item, wrapping a filesystem entry inside an image for the tree view.
/// <para>文件管理器文档树节点条目，包装镜像内一个文件系统条目供 <c>TreeView</c> 展示。</para>
/// </summary>
public partial class FileManagerEntryViewModel : ObservableObject
{
    /// <summary>
    /// The display name of the entry (the last path segment).
    /// <para>条目显示名称（路径最后一段）。</para>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The entry's relative path inside the image ('/' separated, relative to the image root).
    /// <para>条目在镜像内的相对路径（使用 '/' 分隔，相对镜像根）。</para>
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// The entry size in bytes; 0 for directories.
    /// <para>条目大小（字节）；目录为 0。</para>
    /// </summary>
    public long Size { get; }

    /// <summary>
    /// The entry kind (file / directory / symlink).
    /// <para>条目类型（文件 / 目录 / 符号链接）。</para>
    /// </summary>
    public FileEntryKind Kind { get; }

    /// <summary>
    /// Whether the entry is a directory.
    /// <para>是否为目录。</para>
    /// </summary>
    public bool IsDirectory => Kind == FileEntryKind.Directory;

    /// <summary>
    /// Whether the entry is a symbolic link.
    /// <para>是否为符号链接。</para>
    /// </summary>
    public bool IsSymlink => Kind == FileEntryKind.Symlink;

    /// <summary>
    /// The child entries (tree-view hierarchy binding).
    /// <para>子条目集合（TreeView 层级绑定）。</para>
    /// </summary>
    public ObservableCollection<FileManagerEntryViewModel> Children { get; } = [];

    /// <summary>
    /// Only child directories are shown in the navigation tree.
    /// <para>目录树仅显示子目录。</para>
    /// </summary>
    public IEnumerable<FileManagerEntryViewModel> Directories => Children.Where(static entry => entry.IsDirectory);

    /// <summary>
    /// The parent directory, or null for the image root.
    /// <para>父目录，镜像根目录为 null。</para>
    /// </summary>
    public FileManagerEntryViewModel? Parent { get; internal set; }

    /// <summary>
    /// The human-readable size text of the entry (e.g. "1.5 MB").
    /// <para>条目大小的可读文本（如 "1.5 MB"）。</para>
    /// </summary>
    public string SizeText => FormatSize(Size);

    /// <summary>
    /// The localized kind text of the entry.
    /// <para>条目类型的本地化文本。</para>
    /// </summary>
    public string KindText => Kind switch
    {
        FileEntryKind.Directory => LanguageService.Instance.GetString("FileManager_KindDirectory"),
        FileEntryKind.Symlink => LanguageService.Instance.GetString("FileManager_KindSymlink"),
        _ => LanguageService.Instance.GetString("FileManager_KindFile"),
    };

    /// <summary>
    /// Whether the entry is expanded (tree-view linkage).
    /// <para>条目是否已展开（TreeView 联动）。</para>
    /// </summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>
    /// Whether the entry is selected (tree-view linkage).
    /// <para>条目是否被选中（TreeView 联动）。</para>
    /// </summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Creates a filesystem tree entry.<para>创建文件系统树条目。</para></summary>
    /// <param name="name">The display name.<para>显示名称。</para></param>
    /// <param name="path">The relative path inside the image ('/' separated).
    /// <para>镜像内相对路径（'/' 分隔）。</para></param>
    /// <param name="size">The entry size in bytes.<para>条目大小（字节）。</para></param>
    /// <param name="kind">The entry kind.<para>条目类型。</para></param>
    public FileManagerEntryViewModel(string name, string path, long size, FileEntryKind kind)
    {
        Name = name;
        Path = path;
        Size = size;
        Kind = kind;
    }

    /// <summary>
    /// Formats a byte count into a human-readable size string (e.g. "1.5 MB"); 0 bytes shows "0 B".
    /// <para>将字节数格式化为可读大小文本（如 "1.5 MB"）；0 字节显示 "0 B"。</para>
    /// </summary>
    private static string FormatSize(long bytes) => SizeFormatter.Format(bytes);
}