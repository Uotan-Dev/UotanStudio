using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// The compare document's folder part: flattens both directory sides into one aligned row list and
/// handles copying. Same-name entries share a row; a single-side entry leaves the other side blank.
/// <para>对比文档的文件夹对比部分：把两侧目录压平成同一份对齐行列表并处理复制。同名条目同一行，单侧条目在另一侧留空。</para>
/// </summary>
public sealed partial class CompareViewModel
{
    /// <summary>
    /// Compares the two directories, builds the comparison tree and flattens it into an aligned row list;
    /// a directory's status is aggregated from its subtree.
    /// <para>比对两个目录，构建对比树并压平成对齐行列表；目录状态按其子树聚合。</para>
    /// </summary>
    private async Task CompareFoldersAsync()
    {
        var ct = BeginLoad();
        IsBusy = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try
        {
            var result = await _comparison.CompareDirectoriesAsync(_leftPath, _rightPath, ct);
            ct.ThrowIfCancellationRequested();

            EqualCount = result.EqualCount;
            DifferentCount = result.DifferentCount;
            LeftOnlyCount = result.LeftOnlyCount;
            RightOnlyCount = result.RightOnlyCount;
            _folderSummaryText = string.Format(
                LanguageService.Instance.GetString("Compare_FolderSummary"),
                result.EqualCount,
                result.DifferentCount,
                result.LeftOnlyCount,
                result.RightOnlyCount);

            _compareTreeRoot = BuildCompareTree(result, _leftPath, _rightPath);
            _compareTreeRoot.IsExpanded = true;
            Mode = CompareMode.Folder;
            Rows = ComparisonRowBuilder.ForTree([_compareTreeRoot], compared: true);
            SelectedIndex = -1;
            StatsText = _folderSummaryText;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = UserError.Describe(ex);
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
        finally
        {
            if (ct == _loadCts.Token)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// Builds the comparison tree from the result: the root holds each side's own folder name, other nodes
    /// attach by relative path, children are sorted and directory statuses aggregated bottom-up.
    /// <para>由比对结果构建对比树：根节点各取本侧目录名，其余节点按相对路径挂载，最后排序并自下而上聚合目录状态。</para>
    /// </summary>
    /// <param name="result">The folder-comparison result. <para>目录比对结果。</para></param>
    /// <param name="leftRoot">The left comparison root. <para>左侧对比根目录。</para></param>
    /// <param name="rightRoot">The right comparison root. <para>右侧对比根目录。</para></param>
    /// <returns>The comparison tree's root node.<para>对比树的根节点。</para></returns>
    private static CompareTreeNodeViewModel BuildCompareTree(FolderCompareResult result, string leftRoot, string rightRoot)
    {
        var root = CompareTreeNodeViewModel.ForComparison(
            name: DisplayNameOf(leftRoot),
            relativePath: string.Empty,
            isDirectory: true,
            status: FolderItemStatus.Equal,
            leftName: DisplayNameOf(leftRoot),
            rightName: DisplayNameOf(rightRoot));

        var directories = new Dictionary<string, CompareTreeNodeViewModel>(StringComparer.Ordinal)
        {
            [string.Empty] = root,
        };

        foreach (var item in result.Items)
        {
            var parentRelative = ParentRelativePathOf(item.RelativePath);
            var parent = directories.TryGetValue(parentRelative, out var found) ? found : root;
            var name = NameOf(item.RelativePath);
            var node = CompareTreeNodeViewModel.ForComparison(
                name,
                item.RelativePath,
                item.IsDirectory,
                item.Status,
                leftName: item.Status == FolderItemStatus.RightOnly ? string.Empty : name,
                rightName: item.Status == FolderItemStatus.LeftOnly ? string.Empty : name,
                leftSize: item.LeftSize,
                rightSize: item.RightSize);
            parent.AddChild(node);
            if (item.IsDirectory)
            {
                directories[item.RelativePath] = node;
            }
        }

        AggregateStatus(root);
        return root;
    }

    /// <summary>
    /// Recursively sorts children and aggregates directory statuses: a directory present on both sides is
    /// different when any descendant differs, and files keep the comparison result's status.
    /// <para>递归排序子项并聚合目录状态：两侧都存在的目录只要子树中有非相同项即为「不同」，文件沿用比对结果的状态。</para>
    /// </summary>
    /// <param name="node">The subtree root to process.<para>要处理的子树根节点。</para></param>
    /// <returns>The node's effective status.<para>本节点的有效状态。</para></returns>
    private static FolderItemStatus AggregateStatus(CompareTreeNodeViewModel node)
    {
        node.SortChildren();

        if (!node.IsDirectory)
        {
            return node.Status ?? FolderItemStatus.Equal;
        }

        if (node.Status is FolderItemStatus.LeftOnly or FolderItemStatus.RightOnly)
        {
            return node.Status.Value;
        }

        var differs = false;
        foreach (var child in node.Children)
        {
            if (AggregateStatus(child) != FolderItemStatus.Equal)
            {
                differs = true;
            }
        }

        node.Status = differs ? FolderItemStatus.Different : FolderItemStatus.Equal;
        return node.Status.Value;
    }

    /// <summary>The parent relative path of a relative path; empty for a top-level entry.<para>取相对路径的父级相对路径；顶层条目返回空串。</para></summary>
    /// <param name="relativePath">The '/'-separated relative path.<para>'/' 分隔的相对路径。</para></param>
    /// <returns>The parent's relative path, empty for top-level entries.<para>父级的相对路径；顶层条目为空串。</para></returns>
    private static string ParentRelativePathOf(string relativePath)
    {
        var index = relativePath.LastIndexOf('/');
        return index < 0 ? string.Empty : relativePath[..index];
    }

    /// <summary>Extracts the last segment of a relative path as the entry name.<para>取相对路径的最后一段作为名称。</para></summary>
    /// <param name="relativePath">The '/'-separated relative path.<para>'/' 分隔的相对路径。</para></param>
    /// <returns>The name segment after the last '/'.<para>最后一个 '/' 之后的名称段。</para></returns>
    private static string NameOf(string relativePath)
    {
        var index = relativePath.LastIndexOf('/');
        return index < 0 ? relativePath : relativePath[(index + 1)..];
    }

    /// <summary>Extracts a directory's display name; a root directory returns the path itself.<para>取目录的显示名称；根目录返回路径本身。</para></summary>
    /// <param name="path">The directory path.<para>目录路径。</para></param>
    /// <returns>The display name, or the path itself for a root.<para>显示名称；根目录时返回路径本身。</para></returns>
    private static string DisplayNameOf(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>
    /// Copy-selected-left-to-right command (re-compares afterwards so both sides' statuses refresh).
    /// <para>把选中条目从左复制到右命令（复制成功后重新比对以刷新两侧状态）。</para>
    /// </summary>
    [RelayCommand]
    private async Task CopyLeftToRightAsync() => await CopyAsync(fromLeftToRight: true);

    /// <summary>
    /// Copy-selected-right-to-left command (re-compares afterwards so both sides' statuses refresh).
    /// <para>把选中条目从右复制到左命令（复制成功后重新比对以刷新两侧状态）。</para>
    /// </summary>
    [RelayCommand]
    private async Task CopyRightToLeftAsync() => await CopyAsync(fromLeftToRight: false);

    /// <summary>
    /// Copies the selected entry from the source side to the other side: a folder comparison copies the file
    /// and re-compares, a text comparison copies the selected rows' text to the other side's same rows.
    /// <para>把选中条目从源侧复制到另一侧：目录对比复制文件并重新比对，文本对比把选中行的文本复制到另一侧的同行。</para>
    /// </summary>
    /// <param name="fromLeftToRight">True when copying left to right; false otherwise.<para>从左复制到右为 true，反之为 false。</para></param>
    private async Task CopyAsync(bool fromLeftToRight)
    {
        if (Mode != CompareMode.Folder)
        {
            CopySelectedTextRows(fromLeftToRight);
            return;
        }

        await CopyFolderEntryAsync(fromLeftToRight);
    }

    /// <summary>
    /// Copies the file selected on the source side to the other side and re-compares afterwards.
    /// <para>把源侧选中的文件复制到另一侧，成功后重新比对。</para>
    /// </summary>
    /// <param name="fromLeftToRight">True when copying left to right; false otherwise.<para>从左复制到右为 true，反之为 false。</para></param>
    private async Task CopyFolderEntryAsync(bool fromLeftToRight)
    {
        if (Mode != CompareMode.Folder || Rows is null
            || SelectedIndex < 0 || SelectedIndex >= Rows.Count)
        {
            return;
        }

        var node = Rows[SelectedIndex].TreeNode;
        if (node is null || node.IsDirectory || node.RelativePath.Length == 0)
        {
            return;
        }

        var sourceIsLeft = fromLeftToRight;
        var status = node.Status ?? FolderItemStatus.Equal;
        var missingOnTarget = sourceIsLeft
            ? status == FolderItemStatus.RightOnly
            : status == FolderItemStatus.LeftOnly;
        if (missingOnTarget)
        {
            return;
        }

        try
        {
            if (fromLeftToRight)
            {
                await _comparison.CopyAsync(_leftPath, _rightPath, node.RelativePath, CancellationToken.None);
            }
            else
            {
                await _comparison.CopyAsync(_rightPath, _leftPath, node.RelativePath, CancellationToken.None);
            }

            await CompareFoldersAsync();
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = UserError.Describe(ex);
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
    }
}