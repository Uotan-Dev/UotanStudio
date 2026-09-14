using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

public sealed partial class CompareViewModel
{
    /// <summary>
    /// <para>刷新命令：在文件夹模式下重新比对并重建行与计数。</para>
    /// Refresh command: re-compares in folder mode and rebuilds the rows and counts.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Mode != CompareMode.Folder)
        {
            return;
        }

        var ct = BeginLoad();
        IsBusy = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try
        {
            // 右侧留空（单路径拖入等待第二次拖入）：不比对，自行递归枚举左侧目录生成仅左侧行。
            // With the right side empty (single-path drop awaiting a second drop) no comparison runs;
            // the left directory is recursively enumerated into left-only rows.
            if (string.IsNullOrWhiteSpace(RightPath))
            {
                var entries = await _comparison.ReadDirectoryAsync(LeftPath, ct);
                ct.ThrowIfCancellationRequested();
                var rows = await Task.Run(() => ComparisonRowBuilder.ForDirectory(entries), ct);
                ct.ThrowIfCancellationRequested();
                _lastFolderResult = null;
                _folderItems = [];
                Rows = rows;
                EqualCount = 0;
                DifferentCount = 0;
                LeftOnlyCount = rows.Count;
                RightOnlyCount = 0;
                StatsText = LanguageService.Instance.GetString("Compare_DropRightFolder");
                SelectedIndex = -1;
                return;
            }

            var result = await _comparison.CompareDirectoriesAsync(LeftPath, RightPath, ct);
            ct.ThrowIfCancellationRequested();

            EqualCount = result.EqualCount;
            DifferentCount = result.DifferentCount;
            LeftOnlyCount = result.LeftOnlyCount;
            RightOnlyCount = result.RightOnlyCount;
            _lastFolderResult = result;
            StatsText = string.Empty;
            ApplyFolderFilters();
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
            // Clear busy only while this load is still the current one: a cancelled older load
            // must not clear the newer load's busy state.
            if (ct == _loadCts.Token)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// <para>把选中条目从左复制到右命令（复制成功后刷新比对）。</para>
    /// Copy-selected-left-to-right command (refreshes the comparison after the copy succeeds).
    /// </summary>
    [RelayCommand]
    private async Task CopyLeftToRightAsync() => await CopyAsync(fromLeftToRight: true);

    /// <summary>
    /// <para>把选中条目从右复制到左命令（复制成功后刷新比对）。</para>
    /// Copy-selected-right-to-left command (refreshes the comparison after the copy succeeds).
    /// </summary>
    [RelayCommand]
    private async Task CopyRightToLeftAsync() => await CopyAsync(fromLeftToRight: false);

    /// <summary>
    /// <para>删除左目录中选中文件命令（删除成功后刷新比对）。</para>
    /// Delete-selected-in-left command (refreshes the comparison after the deletion succeeds).
    /// </summary>
    [RelayCommand]
    private async Task DeleteLeftAsync()
    {
        if (SelectedIndex < 0 || SelectedIndex >= _folderItems.Count || _folderItems[SelectedIndex].Status == FolderItemStatus.RightOnly)
        {
            return;
        }

        await DeleteAsync(_folderItems[SelectedIndex], LeftPath);
    }

    /// <summary>
    /// <para>删除右目录中选中文件命令（删除成功后刷新比对）。</para>
    /// Delete-selected-in-right command (refreshes the comparison after the deletion succeeds).
    /// </summary>
    [RelayCommand]
    private async Task DeleteRightAsync()
    {
        if (SelectedIndex < 0 || SelectedIndex >= _folderItems.Count || _folderItems[SelectedIndex].Status == FolderItemStatus.LeftOnly)
        {
            return;
        }

        await DeleteAsync(_folderItems[SelectedIndex], RightPath);
    }

    /// <summary>
    /// <para>按过滤开关重建文件夹行，并维护与行平行的底层条目列表（供复制/删除定位）。</para>
    /// Rebuilds the folder rows by the active filter toggles and keeps a parallel underlying-item list
    /// (for copy/delete targeting).
    /// </summary>
    private void ApplyFolderFilters()
    {
        _folderItems = [];
        if (_lastFolderResult is null)
        {
            Rows = null;
            return;
        }

        var rows = new List<CompareRowViewModel>();
        var items = new List<FolderCompareItem>();
        foreach (var item in _lastFolderResult.Items)
        {
            if (ShouldShow(item))
            {
                rows.Add(CompareRowViewModel.ForFolder(item));
                items.Add(item);
            }
        }

        _folderItems = items;
        Rows = rows;
        SelectedIndex = -1;
    }

    /// <summary>
    /// <para>判断文件夹条目是否按当前过滤开关显示。</para>
    /// Determines whether a folder item is shown under the active filter toggles.
    /// </summary>
    private bool ShouldShow(FolderCompareItem item) => item.Status switch
    {
        FolderItemStatus.Equal => ShowEqual,
        FolderItemStatus.Different => ShowDifferent,
        FolderItemStatus.LeftOnly => ShowLeftOnly,
        FolderItemStatus.RightOnly => ShowRightOnly,
        _ => true,
    };

    /// <summary>
    /// <para>把选中文件从一侧复制到另一侧，成功后刷新比对。</para>
    /// Copies the selected file from one side to the other, refreshing the comparison afterwards.
    /// </summary>
    /// <param name="fromLeftToRight">从左复制到右为 true，反之为 false。
    /// <para>true when copying left to right; false otherwise.</para></param>
    private async Task CopyAsync(bool fromLeftToRight)
    {
        if (Mode != CompareMode.Folder || SelectedIndex < 0 || SelectedIndex >= _folderItems.Count)
        {
            return;
        }

        var item = _folderItems[SelectedIndex];
        if (item.IsDirectory || (fromLeftToRight && item.Status == FolderItemStatus.RightOnly)
            || (!fromLeftToRight && item.Status == FolderItemStatus.LeftOnly))
        {
            return;
        }

        try
        {
            if (fromLeftToRight)
            {
                await _comparison.CopyAsync(LeftPath, RightPath, item.RelativePath, CancellationToken.None);
            }
            else
            {
                await _comparison.CopyAsync(RightPath, LeftPath, item.RelativePath, CancellationToken.None);
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
    }

    /// <summary>
    /// <para>删除指定目录下的选中文件，成功后刷新比对。</para>
    /// Deletes the selected file under the given directory, refreshing the comparison afterwards.
    /// </summary>
    /// <param name="item">被删除的文件夹条目。<para>The folder item to delete.</para></param>
    /// <param name="directory">所属目录路径。<para>The owning directory path.</para></param>
    private async Task DeleteAsync(FolderCompareItem item, string directory)
    {
        if (item.IsDirectory)
        {
            return;
        }

        try
        {
            await _comparison.DeleteAsync(directory, item.RelativePath, CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
    }

    /// <summary>
    /// <para>清理文件夹模式的缓存状态（计数与条目映射），用于切换到文件模式前。</para>
    /// Clears the folder-mode cached state (counts and the item map) before switching to file modes.
    /// </summary>
    private void ResetFolderState()
    {
        _lastFolderResult = null;
        _folderItems = [];
        Rows = null;
        EqualCount = 0;
        DifferentCount = 0;
        LeftOnlyCount = 0;
        RightOnlyCount = 0;
    }

}