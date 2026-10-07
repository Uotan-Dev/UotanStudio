using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Logging;

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
    /// <summary>
    /// <para>一次发布的最大挂起行数：达到阈值才重建 <see cref="LogText"/>，避免每追加一行就重分配整串并
    /// 重排整个日志框。</para>
    /// Maximum pending lines per publish; <see cref="LogText"/> is rebuilt only at this threshold so a single
    /// appended line never reallocates the whole string and re-lays out the log box.
    /// </summary>
    private const int LogFlushLineThreshold = 32;

    /// <summary>
    /// <para>一次发布的最大挂起字符数，与行数阈值取先到的一个，保证超长单行也能及时发布。</para>
    /// Maximum pending characters per publish; whichever threshold is reached first triggers the publish, so
    /// very long single lines still appear promptly.
    /// </summary>
    private const int LogFlushCharacterThreshold = 4096;

    /// <summary>
    /// <para><see cref="LogText"/> 保留的末尾字符数；超出部分只丢前缀，缓冲区随之收缩，长日志不会无界增长。</para>
    /// Tail window kept in <see cref="LogText"/>; beyond it only the prefix is dropped and the buffer shrinks
    /// with it, so a long flash run never grows unbounded.
    /// </summary>
    private const int LogRetainedCharacterLimit = 200_000;

    private bool _updatingSelectAll;

    /// <summary>
    /// <para>日志行缓冲：追加只写这里，发布时才生成整串。</para>
    /// Log line buffer; appending writes here and only a publish builds the full string.
    /// </summary>
    private readonly StringBuilder _logBuffer = new();

    /// <summary>
    /// <para>未发布的挂起行数，同时是定时器是否需要运行的依据。</para>
    /// Count of unpublished pending lines; also decides whether the flush timer has to run.
    /// </summary>
    private int _pendingLogLines;

    /// <summary>
    /// <para>自上次发布以来追加的字符数；只看增量，长日志才不会因缓冲区总长度而每行都发布一次。</para>
    /// Characters appended since the last publish; counting only that increment keeps a long log from
    /// publishing on every line once the buffer itself passes the threshold.
    /// </summary>
    private int _pendingLogCharacters;

    /// <summary>
    /// <para>批量发布定时器：100ms 内补发未达阈值的行，使单条提示不会因为合批而延迟显示；无挂起行时停止，
    /// 页面空闲不产生周期唤醒。</para>
    /// Batch publish timer: drains lines below the threshold within 100 ms so a lone notice is never delayed,
    /// and stops while nothing is pending so an idle page costs no periodic wakeups.
    /// </summary>
    private readonly DispatcherTimer _logFlushTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    /// <summary>
    /// <para>已订阅 <see cref="FlashPartitionEntry.PropertyChanged"/> 的条目；集合发出 Reset 通知时旧条目已经
    /// 不可见，必须靠这份记录退订，避免残留订阅。</para>
    /// Entries currently subscribed to <see cref="FlashPartitionEntry.PropertyChanged"/>; a Reset notification
    /// no longer exposes the old items, so this record is what lets them be unsubscribed.
    /// </summary>
    private readonly HashSet<FlashPartitionEntry> _subscribedEntries = [];

    /// <summary><para>构造视图模型并挂接列表变更与日志批量发布。</para>Creates the view model and hooks list changes and the batched log publish.</summary>
    public FlashViewModel()
    {
        Partitions.CollectionChanged += OnPartitionsChanged;
        _logFlushTimer.Tick += (_, _) => FlushPendingLog();
    }

    /// <summary><para>全部分区条目。</para>All partition entries.</summary>
    public LogEntryCollection<FlashPartitionEntry> Partitions { get; } = [];

    /// <summary><para>按搜索词过滤后绑定到列表的分区条目。</para>The entries bound to the grid after applying the search text.</summary>
    public LogEntryCollection<FlashPartitionEntry> VisiblePartitions { get; } = [];

    /// <summary><para>当前刷机包路径。</para>The current package path.</summary>
    [ObservableProperty]
    private string _packagePath = string.Empty;

    /// <summary><para>分区名搜索词。</para>The partition-name filter.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// <para>页面日志输出；由 <see cref="AppendLog"/> 按阈值批量发布，只反映缓冲区末尾
    /// <c>200000</c> 字符。</para>
    /// The page log output; published in batches by <see cref="AppendLog"/> and carrying only the last
    /// <c>200000</c> characters of the buffer.
    /// </summary>
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

    /// <summary>
    /// <para>追加一行日志：写入缓冲区，达到行数或字符阈值才发布整串，否则交给定时器补发。</para>
    /// Appends one log line to the buffer, publishing the full string only at the line or character threshold
    /// and leaving everything else to the flush timer.
    /// </summary>
    /// <param name="message"><para>日志行内容。</para>The log line text.</param>
    internal void AppendLog(string message)
    {
        _logBuffer.Append(message).Append(Environment.NewLine);
        _pendingLogLines++;
        _pendingLogCharacters += message.Length + Environment.NewLine.Length;
        if (_pendingLogLines >= LogFlushLineThreshold || _pendingLogCharacters >= LogFlushCharacterThreshold)
        {
            PublishLog();
            return;
        }

        if (!_logFlushTimer.IsEnabled)
        {
            _logFlushTimer.Start();
        }
    }

    /// <summary>
    /// <para>立即发布缓冲区里的全部挂起行；刷写结束、页面卸载和测试终态读取前调用，保证最后一行不会滞留。</para>
    /// Publishes every pending buffered line at once; called at run end points, on page detach and before a test
    /// reads the final state, so the last line never stays stuck in the buffer.
    /// </summary>
    internal void FlushPendingLog()
    {
        if (_pendingLogLines == 0)
        {
            _logFlushTimer.Stop();
            return;
        }

        PublishLog();
    }

    /// <summary>
    /// <para>清空日志：缓冲区与已发布的文本一起归零，避免下阈值时把旧内容重新发出去。</para>
    /// Clears the log: buffer and published text both go to empty so the next publish cannot resurrect old lines.
    /// </summary>
    internal void ClearLog()
    {
        _pendingLogLines = 0;
        _pendingLogCharacters = 0;
        _logBuffer.Clear();
        _logFlushTimer.Stop();
        LogText = string.Empty;
    }

    private void PublishLog()
    {
        _pendingLogLines = 0;
        _pendingLogCharacters = 0;
        _logFlushTimer.Stop();
        TrimLogBuffer();
        LogText = _logBuffer.ToString();
    }

    /// <summary>
    /// <para>把缓冲区收缩到末尾保留窗口，并按行边界丢弃前缀。丢弃处不插入提示行：现有双语键
    /// <c>Log_FileTooLarge</c> 与 <c>Node_Document_TooLarge</c> 描述的是日志文件与文档的容量上限，
    /// 用作尾窗提示会给出与实际不符的限制数字，而新增键会破坏语言字典对齐检查。</para>
    /// Shrinks the buffer to its retained tail window, dropping whole leading lines. No notice line is inserted:
    /// the existing bilingual keys <c>Log_FileTooLarge</c> and <c>Node_Document_TooLarge</c> describe log-file and
    /// document capacity limits, so reusing them here would state limits that do not match this window, while a new
    /// key would break the locale dictionary parity checks.
    /// </summary>
    private void TrimLogBuffer()
    {
        if (_logBuffer.Length <= LogRetainedCharacterLimit)
        {
            return;
        }

        var text = _logBuffer.ToString();
        var drop = text.Length - LogRetainedCharacterLimit;
        var boundary = text.IndexOf(Environment.NewLine, drop, StringComparison.Ordinal);
        if (boundary >= 0)
        {
            drop = boundary + Environment.NewLine.Length;
        }

        _logBuffer.Remove(0, drop);
    }

    private void OnPartitionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var entry in _subscribedEntries)
            {
                entry.PropertyChanged -= OnEntryPropertyChanged;
            }

            _subscribedEntries.Clear();
            foreach (var entry in Partitions)
            {
                if (_subscribedEntries.Add(entry))
                {
                    entry.PropertyChanged += OnEntryPropertyChanged;
                }
            }
        }
        else
        {
            if (e.OldItems is not null)
            {
                foreach (FlashPartitionEntry entry in e.OldItems)
                {
                    entry.PropertyChanged -= OnEntryPropertyChanged;
                    _subscribedEntries.Remove(entry);
                }
            }

            if (e.NewItems is not null)
            {
                foreach (FlashPartitionEntry entry in e.NewItems)
                {
                    if (_subscribedEntries.Add(entry))
                    {
                        entry.PropertyChanged += OnEntryPropertyChanged;
                    }
                }
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
        VisiblePartitions.Replace(query.Length == 0
            ? Partitions
            : Partitions.Where(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));

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