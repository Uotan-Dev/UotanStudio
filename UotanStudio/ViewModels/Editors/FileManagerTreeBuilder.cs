using System.Collections.ObjectModel;
using UotanStudio.Services.FileManagement;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>扁平路径列表 → 目录树构建器：按 '/' 分组成树，缺失的中间目录自动合成，
/// 每个文件夹内目录优先、按名称排序。</para>
/// Flat path-list → tree builder: groups by '/', synthesizes missing intermediate directories, and
/// sorts each folder with directories first and names in ordinal order.
/// </summary>
internal static class FileManagerTreeBuilder
{
    /// <summary>
    /// <para>从扁平条目列表构建目录树，返回根层条目。</para>
    /// Builds the directory tree from the flat entry list, returning the root-level entries.
    /// </summary>
    public static List<FileManagerEntryViewModel> Build(IReadOnlyList<ImageBrowserEntry> entries)
    {
        var root = new List<FileManagerEntryViewModel>();
        var byPath = new Dictionary<string, FileManagerEntryViewModel>(StringComparer.Ordinal);

        // 第一遍：为所有真实条目创建节点，确保真实目录优先于合成目录。
        // First pass: create nodes for all real entries so real directories win over synthesized ones.
        foreach (var entry in entries)
        {
            byPath[entry.Path] = new FileManagerEntryViewModel(GetName(entry.Path), entry.Path, entry.Size, entry.Kind switch
            {
                ImageBrowserEntryKind.Directory => FileEntryKind.Directory,
                ImageBrowserEntryKind.Symlink => FileEntryKind.Symlink,
                _ => FileEntryKind.File,
            });
        }

        // 第二遍：挂载到父节点，缺失的中间目录按需合成。
        // Second pass: attach to parents, synthesizing missing intermediate directories on demand.
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
    /// <para>确保条目父链存在并返回直接父节点；缺失的祖先目录按需合成到树中，根级父返回 <c>null</c>。</para>
    /// Ensures the entry's parent chain exists and returns the immediate parent; missing ancestor
    /// directories are synthesized into the tree, and a root-level parent returns <c>null</c>.
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

        // 自直接父级向上收集缺失的祖先路径。
        // Collect missing ancestor paths upward from the immediate parent.
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
    /// <para>对子集合排序：目录优先，其后按名称序。</para>
    /// Sorts a child collection: directories first, then by name.
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
    /// <para>对根层列表排序：目录优先，其后按名称序。</para>
    /// Sorts the root list: directories first, then by name.
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
    /// <para>取路径最后一段作为名称。</para>
    /// Gets the last path segment as the name.
    /// </summary>
    private static string GetName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }
}