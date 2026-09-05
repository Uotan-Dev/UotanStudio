using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>文件系统条目的类型。</para>
/// The kind of a filesystem entry.
/// </summary>
public enum FileEntryKind
{
    /// <summary>
    /// <para>普通文件。</para>
    /// A regular file.
    /// </summary>
    File,

    /// <summary>
    /// <para>目录。</para>
    /// A directory.
    /// </summary>
    Directory,

    /// <summary>
    /// <para>符号链接。</para>
    /// A symbolic link.
    /// </summary>
    Symlink,
}

/// <summary>
/// <para>文件管理器文档树节点条目，包装镜像内一个文件系统条目供 <c>TreeView</c> 展示。</para>
/// File-manager document tree item, wrapping a filesystem entry inside an image for the tree view.
/// </summary>
public partial class FileManagerEntryViewModel : ObservableObject
{
    /// <summary>
    /// <para>条目显示名称（路径最后一段）。</para>
    /// The display name of the entry (the last path segment).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// <para>条目在镜像内的相对路径（使用 '/' 分隔，相对镜像根）。</para>
    /// The entry's relative path inside the image ('/' separated, relative to the image root).
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// <para>条目大小（字节）；目录为 0。</para>
    /// The entry size in bytes; 0 for directories.
    /// </summary>
    public long Size { get; }

    /// <summary>
    /// <para>条目类型（文件 / 目录 / 符号链接）。</para>
    /// The entry kind (file / directory / symlink).
    /// </summary>
    public FileEntryKind Kind { get; }

    /// <summary>
    /// <para>是否为目录。</para>
    /// Whether the entry is a directory.
    /// </summary>
    public bool IsDirectory => Kind == FileEntryKind.Directory;

    /// <summary>
    /// <para>是否为符号链接。</para>
    /// Whether the entry is a symbolic link.
    /// </summary>
    public bool IsSymlink => Kind == FileEntryKind.Symlink;

    /// <summary>
    /// <para>子条目集合（TreeView 层级绑定）。</para>
    /// The child entries (tree-view hierarchy binding).
    /// </summary>
    public ObservableCollection<FileManagerEntryViewModel> Children { get; } = [];

    /// <summary>
    /// <para>目录树仅显示子目录。</para>
    /// Only child directories are shown in the navigation tree.
    /// </summary>
    public IEnumerable<FileManagerEntryViewModel> Directories => Children.Where(static entry => entry.IsDirectory);

    /// <summary>
    /// <para>父目录，镜像根目录为 null。</para>
    /// The parent directory, or null for the image root.
    /// </summary>
    public FileManagerEntryViewModel? Parent { get; internal set; }

    /// <summary>
    /// <para>条目大小的可读文本（如 "1.5 MB"）。</para>
    /// The human-readable size text of the entry (e.g. "1.5 MB").
    /// </summary>
    public string SizeText => FormatSize(Size);

    /// <summary>
    /// <para>条目类型的本地化文本。</para>
    /// The localized kind text of the entry.
    /// </summary>
    public string KindText => Kind switch
    {
        FileEntryKind.Directory => LanguageService.Instance.GetString("FileManager_KindDirectory"),
        FileEntryKind.Symlink => LanguageService.Instance.GetString("FileManager_KindSymlink"),
        _ => LanguageService.Instance.GetString("FileManager_KindFile"),
    };

    /// <summary>
    /// <para>条目是否已展开（TreeView 联动）。</para>
    /// Whether the entry is expanded (tree-view linkage).
    /// </summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>
    /// <para>条目是否被选中（TreeView 联动）。</para>
    /// Whether the entry is selected (tree-view linkage).
    /// </summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// <para>创建文件系统树条目。</para>
    /// Creates a filesystem tree entry.
    /// </summary>
    /// <param name="name">
    /// <para>显示名称。</para>
    /// The display name.
    /// </param>
    /// <param name="path">
    /// <para>镜像内相对路径（'/' 分隔）。</para>
    /// The relative path inside the image ('/' separated).
    /// </param>
    /// <param name="size">
    /// <para>条目大小（字节）。</para>
    /// The entry size in bytes.
    /// </param>
    /// <param name="kind">
    /// <para>条目类型。</para>
    /// The entry kind.
    /// </param>
    public FileManagerEntryViewModel(string name, string path, long size, FileEntryKind kind)
    {
        Name = name;
        Path = path;
        Size = size;
        Kind = kind;
    }

    /// <summary>
    /// <para>将字节数格式化为可读大小文本（如 "1.5 MB"）；0 字节显示 "0 B"。</para>
    /// Formats a byte count into a human-readable size string (e.g. "1.5 MB"); 0 bytes shows "0 B".
    /// </summary>
    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var abs = Math.Abs(bytes);
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        var value = (double)abs;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[0]}"
            : $"{value:0.##} {units[unit]}";
    }
}
