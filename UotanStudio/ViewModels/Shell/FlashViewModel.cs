using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services.Flash;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// <para>分区行在刷写流程中所处的阶段。</para>
/// The stage a partition row has reached in the flash run.
/// </summary>
public enum FlashRowState
{
    /// <summary><para>未参与本次操作。</para>The row takes no part in the current run.</summary>
    Idle,

    /// <summary><para>已排队，等待轮到该分区。</para>The row is queued and waits for its turn.</summary>
    Queued,

    /// <summary><para>正在执行刷写、擦除或校验。</para>The row is flashing, erasing or verifying now.</summary>
    Running,

    /// <summary><para>该行的操作已成功结束。</para>The row's operation finished successfully.</summary>
    Succeeded,

    /// <summary><para>该行的操作失败。</para>The row's operation failed.</summary>
    Failed,

    /// <summary><para>因取消或前序失败而跳过。</para>The row was skipped after a cancellation or an earlier failure.</summary>
    Skipped
}

/// <summary>
/// <para>烧录页面的单个分区条目：勾选状态、fastboot 命令、分区名、大小与镜像文件。</para>
/// A single partition row on the flash page: selection state, fastboot command, partition name, size and image file.
/// </summary>
public sealed partial class FlashPartitionEntry : ObservableObject
{
    /// <summary>The explicit operation represented by an imported script row, if any.</summary>
    public FlashStep? Step { get; init; }
    /// <summary><para>是否勾选该分区。</para>Whether the row is selected.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// <para>是否允许勾选；没有对应镜像的条目保持不可选，避免把空操作交给刷写流程。</para>
    /// Whether the row can be selected; rows without an image stay disabled so the flash flow never gets a no-op.
    /// </summary>
    [ObservableProperty]
    private bool _canSelect = true;

    /// <summary><para>该分区使用的 fastboot 命令。</para>The fastboot command used for this partition.</summary>
    [ObservableProperty]
    private string _command = string.Empty;

    /// <summary><para>分区名。</para>The partition name.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary><para>已格式化的镜像大小。</para>The formatted image size.</summary>
    [ObservableProperty]
    private string _size = string.Empty;

    /// <summary><para>镜像文件名。</para>The image file name.</summary>
    [ObservableProperty]
    private string _fileName = string.Empty;

    /// <summary><para>镜像完整路径，用于条目提示。</para>The full image path, used by the row tooltip.</summary>
    [ObservableProperty]
    private string _filePath = string.Empty;

    /// <summary><para>该行在刷写流程中所处的阶段。</para>The stage this row has reached in the flash run.</summary>
    [ObservableProperty]
    private FlashRowState _state;

    /// <summary><para>行内进度百分比，-1 表示当前没有可显示的进度。</para>The row progress percentage; -1 means there is no progress to show.</summary>
    [ObservableProperty]
    private int _percent = -1;

    /// <summary><para>行内状态文本，由刷写流程按当前语言写入。</para>The row status text, written by the flash run in the current language.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// <para>该行是否正在执行；界面据此显示行内进度条，避免在绑定里比较枚举。</para>
    /// Whether the row is executing; the UI shows its progress bar from this instead of comparing enums in bindings.
    /// </summary>
    public bool IsRunning => State == FlashRowState.Running;

    partial void OnStateChanged(FlashRowState value) => OnPropertyChanged(nameof(IsRunning));
}

/// <summary>
/// <para>烧录页面的界面状态：分区列表、搜索过滤与勾选汇总。</para>
/// Flash page state: the partition list, its search filter and the selection summary.
/// </summary>
/// <remarks>
/// <para>这里只保存界面状态，刷写后端接入后由服务填充 <see cref="Partitions"/>；页面不自行推断镜像归属。</para>
/// Only UI state lives here. The flash backend fills <see cref="Partitions"/> once wired; the page never infers
/// which image belongs to which partition on its own.
/// </remarks>
public sealed partial class FlashViewModel : ObservableObject
{
    private bool _updatingSelectAll;

    /// <summary><para>构造视图模型并挂接列表变更。</para>Creates the view model and hooks list changes.</summary>
    public FlashViewModel() => Partitions.CollectionChanged += OnPartitionsChanged;

    /// <summary><para>全部分区条目。</para>All partition entries.</summary>
    public ObservableCollection<FlashPartitionEntry> Partitions { get; } = [];

    /// <summary><para>按搜索词过滤后绑定到列表的分区条目。</para>The entries bound to the grid after applying the search text.</summary>
    public ObservableCollection<FlashPartitionEntry> VisiblePartitions { get; } = [];

    /// <summary><para>当前刷机包路径。</para>The current package path.</summary>
    [ObservableProperty]
    private string _packagePath = string.Empty;

    /// <summary><para>分区名搜索词。</para>The partition-name filter.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary><para>页面日志输出。</para>The page log output.</summary>
    [ObservableProperty]
    private string _logText = string.Empty;

    /// <summary><para>「全选」复选框状态。</para>The select-all check box state.</summary>
    [ObservableProperty]
    private bool _isSelectAllChecked;

    /// <summary><para>已勾选的分区数量。</para>The number of selected partitions.</summary>
    [ObservableProperty]
    private int _selectedCount;

    /// <summary><para>过滤后是否存在可显示的分区。</para>Whether the filtered list contains rows.</summary>
    [ObservableProperty]
    private bool _hasVisiblePartitions;

    /// <summary><para>整轮操作的总体进度百分比。</para>The overall progress percentage of the whole run.</summary>
    [ObservableProperty]
    private double _overallPercent;

    /// <summary><para>总体进度旁的说明文本。</para>The caption shown next to the overall progress bar.</summary>
    [ObservableProperty]
    private string _overallText = string.Empty;

    /// <summary><para>当前选中设备的描述文本，未选中时显示占位提示。</para>The selected device's description; a placeholder when nothing is selected.</summary>
    [ObservableProperty]
    private string _selectedDeviceText = string.Empty;

    /// <summary><para>页面是否正在执行会改变设备或列表状态的操作。</para>Whether the page is running an operation that changes the device or the list state.</summary>
    [ObservableProperty]
    private bool _isBusy;

    partial void OnSearchTextChanged(string value) => RefreshVisiblePartitions();

    partial void OnIsSelectAllCheckedChanged(bool value)
    {
        if (_updatingSelectAll)
        {
            return;
        }

        _updatingSelectAll = true;
        try
        {
            foreach (var entry in VisiblePartitions)
            {
                if (entry.CanSelect)
                {
                    entry.IsSelected = value;
                }
            }
        }
        finally
        {
            _updatingSelectAll = false;
        }

        UpdateSelectionState();
    }

    private void OnPartitionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (FlashPartitionEntry entry in e.OldItems)
            {
                entry.PropertyChanged -= OnEntryPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (FlashPartitionEntry entry in e.NewItems)
            {
                entry.PropertyChanged += OnEntryPropertyChanged;
            }
        }

        RefreshVisiblePartitions();
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlashPartitionEntry.IsSelected))
        {
            UpdateSelectionState();
        }
    }

    private void RefreshVisiblePartitions()
    {
        var query = SearchText.Trim();
        VisiblePartitions.Clear();
        foreach (var entry in Partitions)
        {
            if (query.Length == 0 || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                VisiblePartitions.Add(entry);
            }
        }

        HasVisiblePartitions = VisiblePartitions.Count > 0;
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        SelectedCount = Partitions.Count(entry => entry.IsSelected);
        var selectable = VisiblePartitions.Where(entry => entry.CanSelect).ToList();
        var allVisibleSelected = selectable.Count > 0 && selectable.All(entry => entry.IsSelected);
        _updatingSelectAll = true;
        try
        {
            IsSelectAllChecked = allVisibleSelected;
        }
        finally
        {
            _updatingSelectAll = false;
        }
    }
}