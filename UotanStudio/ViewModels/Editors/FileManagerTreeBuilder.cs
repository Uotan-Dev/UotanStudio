using System.Collections.ObjectModel;
using UotanStudio.Services.FileManagement;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// Flat path-list → tree builder: groups by '/', synthesizes missing intermediate directories, and
/// sorts each folder with directories first and names in ordinal order.
/// <para>扁平路径列表 → 目录树构建器：按 '/' 分组成树，缺失的中间目录自动合成，
/// 每个文件夹内目录优先、按名称排序。</para>
/// </summary>
internal static class FileManagerTreeBuilder
{
    /// <summary>
    /// Builds the directory tree from the flat entry list, returning the root-level entries.
    /// <para>从扁平条目列表构建目录树，返回根层条目。</para>
    /// </summary>
    public static List<FileManagerEntryViewModel> Build(IReadOnlyList<ImageBrowserEntry> entries)
    {
        List<FileManagerEntryViewModel> root = [];
        var byPath = new Dictionary<string, FileManagerEntryViewModel>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            byPath[entry.Path] = new FileManagerEntryViewModel(GetName(entry.Path), entry.Path, entry.Size, entry.Kind switch
            {
                ImageBrowserEntryKind.Directory => FileEntryKind.Directory,
                ImageBrowserEntryKind.Symlink => FileEntryKind.Symlink,
                _ => FileEntryKind.File,
            });
        }

        foreach (var entry in entries)
        {
            var node = byPath[entry.Path];
            var parent = EnsureParent(byPath, root, entry.Path);
            if (parent is null)
            {
                root.Add(node);
            }
            else
            {
                parent.Children.Add(node);
            }
        }

        foreach (var node in byPath.Values)
        {
            SortChildren(node.Children);
        }

        SortList(root);
        return root;
    }

    /// <summary>
    /// Ensures the entry's parent chain exists and returns the immediate parent; missing ancestor
    /// directories are synthesized into the tree, and a root-level parent returns <c>null</c>.
    /// <para>确保条目父链存在并返回直接父节点；缺失的祖先目录按需合成到树中，根级父返回 <c>null</c>。</para>
    /// </summary>
    private static FileManagerEntryViewModel? EnsureParent(
        Dictionary<string, FileManagerEntryViewModel> byPath,
        List<FileManagerEntryViewModel> root,
        string path)
    {
        var slash = path.LastIndexOf('/');
        if (slash < 0)
        {
            return null;
        }

        var parentPath = path[..slash];

        var missing = new Stack<string>();
        var cursor = parentPath;
        while (cursor.Length > 0 && !byPath.ContainsKey(cursor))
        {
            missing.Push(cursor);
            var idx = cursor.LastIndexOf('/');
            cursor = idx < 0 ? string.Empty : cursor[..idx];
        }

        var parent = cursor.Length == 0 ? null : byPath[cursor];
        while (missing.Count > 0)
        {
            var dirPath = missing.Pop();
            var directory = new FileManagerEntryViewModel(GetName(dirPath), dirPath, 0, FileEntryKind.Directory);
            byPath[dirPath] = directory;
            if (parent is null)
            {
                root.Add(directory);
            }
            else
            {
                parent.Children.Add(directory);
            }

            parent = directory;
        }

        return parent;
    }

    /// <summary>
    /// Sorts a child collection: directories first, then by name.
    /// <para>对子集合排序：目录优先，其后按名称序。</para>
    /// </summary>
    private static void SortChildren(ObservableCollection<FileManagerEntryViewModel> children)
    {
        var sorted = children
            .OrderByDescending(static c => c.IsDirectory)
            .ThenBy(static c => c.Name, StringComparer.Ordinal)
            .ToList();
        children.Clear();
        foreach (var item in sorted)
        {
            children.Add(item);
        }
    }

    /// <summary>
    /// Sorts the root list: directories first, then by name.
    /// <para>对根层列表排序：目录优先，其后按名称序。</para>
    /// </summary>
    private static void SortList(List<FileManagerEntryViewModel> list)
    {
        list.Sort(static (a, b) =>
        {
            var byKind = b.IsDirectory.CompareTo(a.IsDirectory);
            return byKind != 0 ? byKind : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Gets the last path segment as the name.
    /// <para>取路径最后一段作为名称。</para>
    /// </summary>
    private static string GetName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }
}