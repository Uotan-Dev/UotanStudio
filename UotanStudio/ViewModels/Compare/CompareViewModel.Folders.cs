using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// <para>对比文档的文件夹对比部分：把两侧目录压平成同一份对齐行列表（同名条目同一行、单侧条目在另一侧
/// 留空、差异整行变色），并处理复制。该部分与文档共享同一份状态，不是独立的业务模块。</para>
/// The compare document's folder-comparison part: flattens both directory sides into one aligned row list
/// (same-name entries share a row, single-side entries stay blank on the other side, differing rows are coloured
/// as a whole) and handles copying. It shares the document's state and is not a separate business module.
/// </summary>
public sealed partial class CompareViewModel
{
    /// <summary>
    /// <para>比对两个目录并把结果装配成一棵对比树，再压平成两栏共享的对齐行列表：</para>
    /// <para>· 行顺序与缩进由对比树决定，子项紧跟在父项后面，两侧完全一致；</para>
    /// <para>· 节点记录左右两侧的名称、图标与大小，单侧缺失的那一列自动留空；</para>
    /// <para>· 状态「不同」的行两侧整行变色，单侧条目按侧加淡色背景；</para>
    /// <para>· 目录状态按子树聚合（子树里有任何非相同条目即为不同），因此最初始的文件夹也会整行变色。</para>
    /// Compares the two directories, assembles the result into a comparison tree and flattens it into the aligned
    /// row list both columns share: the tree defines row order and indentation (children follow their parent,
    /// identically on both sides), each node records both sides' names, glyphs and sizes so a missing side stays
    /// blank, "different" rows are coloured as a whole while single-side entries tint their own side, and a
    /// directory's status is aggregated from its subtree (any non-equal entry below makes it different) so the
    /// initial folder is colourised too.
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
            // 对比一开始就展开最初始的文件夹：视图里立刻显示目录内的条目（根行仍保留，便于看到整棵对比的汇总状态），
            // 而不是只显示一行文件夹名等着用户展开。
            // The initial folder is expanded from the start: the view shows the directory's entries immediately (the root row stays so the
            // aggregated status of the whole comparison remains visible) instead of a single folder row waiting to be expanded.
            _compareTreeRoot.IsExpanded = true;
            Mode = CompareMode.Folder;
            Rows = ComparisonRowBuilder.ForTree([_compareTreeRoot], compared: true);
            SelectedIndex = -1;
            StatsText = _folderSummaryText;
        }
        catch (OperationCanceledException)
        {
            // 取消：保持当前状态，下次刷新覆盖。
            // Cancelled: keep the current state; the next refresh overwrites it.
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
        finally
        {
            // 仅当仍是当前加载（未被更新的加载取代）时清除 busy：被取消的旧加载
            // 不得把新加载的 busy 状态置 false。
            // Clear busy only while this load is still the current one: a cancelled older load must not clear the
            // newer load's busy state.
            if (ct == _loadCts.Token)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// <para>由比对结果构建对比树：根节点是最初始的文件夹（左右名称各取各自的目录名），其余节点按相对路径
    /// 逐级挂载，最后统一排序（目录优先、同级按名称）并自下而上聚合目录状态。</para>
    /// Builds the comparison tree from the comparison result: the root is the initial folder (each side's own
    /// directory name), the remaining nodes are attached level by level by relative path, and the tree is then
    /// sorted (directories first, then by name) and its directory statuses aggregated bottom-up.
    /// </summary>
    /// <param name="result"><para>目录比对结果。</para>The folder-comparison result.</param>
    /// <param name="leftRoot"><para>左侧对比根目录。</para>The left comparison root.</param>
    /// <param name="rightRoot"><para>右侧对比根目录。</para>The right comparison root.</param>
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
    /// <para>递归排序子项并聚合目录状态：单侧目录保留单侧状态（整行橙/蓝），两侧都存在的目录只要有任何一个
    /// 非相同子项就标记为「不同」（整行红），否则为「相同」；文件沿用比对结果的状态。返回本节点的有效状态。</para>
    /// Recursively sorts children and aggregates directory statuses: a single-side directory keeps its single-side
    /// status (whole row orange/blue), a directory present on both sides is marked different when any child is not
    /// equal (whole row red) and equal otherwise, and files keep the comparison result's status. Returns this
    /// node's effective status.
    /// </summary>
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

    /// <summary>取相对路径的父级相对路径；顶层条目返回空串。</summary>
    private static string ParentRelativePathOf(string relativePath)
    {
        var index = relativePath.LastIndexOf('/');
        return index < 0 ? string.Empty : relativePath[..index];
    }

    /// <summary>取相对路径的最后一段作为名称。</summary>
    private static string NameOf(string relativePath)
    {
        var index = relativePath.LastIndexOf('/');
        return index < 0 ? relativePath : relativePath[(index + 1)..];
    }

    /// <summary>取目录的显示名称；根目录（如 <c>C:\</c>）返回路径本身。</summary>
    private static string DisplayNameOf(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>
    /// <para>把选中条目从左复制到右命令（复制成功后重新比对以刷新两侧状态）。</para>
    /// Copy-selected-left-to-right command (re-compares afterwards so both sides' statuses refresh).
    /// </summary>
    [RelayCommand]
    private async Task CopyLeftToRightAsync() => await CopyAsync(fromLeftToRight: true);

    /// <summary>
    /// <para>把选中条目从右复制到左命令（复制成功后重新比对以刷新两侧状态）。</para>
    /// Copy-selected-right-to-left command (re-compares afterwards so both sides' statuses refresh).
    /// </summary>
    [RelayCommand]
    private async Task CopyRightToLeftAsync() => await CopyAsync(fromLeftToRight: false);

    /// <summary>
    /// <para>复制按钮：目录对比把源侧选中的文件复制到另一侧（成功后重新比对），文本对比把选中行的文本复制到
    /// 同一行的另一侧。</para>
    /// Copy button: a folder comparison copies the file selected on the source side to the other side (and re-compares
    /// afterwards), while a text comparison copies the selected rows' text to the same rows on the other side.
    /// </summary>
    /// <param name="fromLeftToRight">从左复制到右为 true，反之为 false。
    /// <para>true when copying left to right; false otherwise.</para></param>
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
    /// <para>把源侧选中的文件复制到另一侧，成功后重新比对。</para>
    /// Copies the file selected on the source side to the other side and re-compares afterwards.
    /// </summary>
    /// <param name="fromLeftToRight">从左复制到右为 true，反之为 false。
    /// <para>true when copying left to right; false otherwise.</para></param>
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
            // 目标侧才是源（选中的是另一侧不存在的条目）：该方向的复制没有源文件。
            // The target side is the source (the selection does not exist on the other side): this direction has
            // nothing to copy.
            return;
        }

        try
        {
            // 方向决定源与目标：从左复制到右用左目录作源，反之用右目录作源。
            // The direction picks source and target: left-to-right copies from the left directory and vice versa.
            if (fromLeftToRight)
            {
                await _comparison.CopyAsync(_leftPath, _rightPath, node.RelativePath, CancellationToken.None);
            }
            else
            {
                await _comparison.CopyAsync(_rightPath, _leftPath, node.RelativePath, CancellationToken.None);
            }

            // 复制改变两侧状态（仅单侧 → 相同/不同），必须整对重新比对。
            // The copy changes both sides' statuses (single-side → equal/different), so the whole pair has to be
            // compared again.
            await CompareFoldersAsync();
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
    }
}
