using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Logging;
using UotanStudio.UiAbstractions;
namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// The stage a partition row has reached in the flash run.
/// <para>分区行在刷写流程中所处的阶段。</para>
/// </summary>
public enum FlashRowState
{
    /// <summary>The row takes no part in the current run. <para>未参与本次操作。</para></summary>
    Idle,

    /// <summary>The row is queued and waits for its turn. <para>已排队，等待轮到该分区。</para></summary>
    Queued,

    /// <summary>The row is flashing, erasing or verifying now. <para>正在执行刷写、擦除或校验。</para></summary>
    Running,

    /// <summary>The row's operation finished successfully. <para>该行的操作已成功结束。</para></summary>
    Succeeded,

    /// <summary>The row's operation failed. <para>该行的操作失败。</para></summary>
    Failed,

    /// <summary>The row was skipped after a cancellation or an earlier failure. <para>因取消或前序失败而跳过。</para></summary>
    Skipped
}

/// <summary>
/// A single partition row on the flash page: selection state, fastboot command, partition name, size and image file.
/// <para>烧录页面的单个分区条目：勾选状态、fastboot 命令、分区名、大小与镜像文件。</para>
/// </summary>
public sealed partial class FlashPartitionEntry : ObservableObject
{
    /// <summary>The explicit operation represented by an imported script row, if any.
    /// <para>导入脚本行所表示的显式操作；没有则为空。</para>
    /// </summary>
    public FlashStep? Step { get; set; }
    /// <summary>
    /// Whether the row is a package script step — a boot-chain <c>stage</c> transfer or a storage-phase OEM write —
    /// rather than a storage partition target. Its check box is disabled and the storage selection sets skip it,
    /// because no action runs a step individually; the row exists to show what the package's own commands do and
    /// where they land.
    /// <para>该行是否为包脚本步骤——引导链 <c>stage</c> 传输或存储段落 OEM 写入——而不是存储分区目标。其复选框禁用、
    /// 存储选择集合跳过它，因为没有任何动作会单独执行步骤；该行用于展示包自身命令的行为与落点。</para>
    /// </summary>
    public bool IsScriptStep { get; set; }

    /// <summary>Whether the row is selected. <para>是否勾选该分区。</para></summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The fastboot command used for this partition. <para>该分区使用的 fastboot 命令。</para></summary>
    [ObservableProperty]
    private string _command = string.Empty;

    /// <summary>The operation command, falling back to flash for rows without an explicit command. <para>操作命令；未显式指定时回退为 flash。</para></summary>
    public string EffectiveCommand => Step?.Command ?? (string.IsNullOrWhiteSpace(Command) ? "flash" : Command);

    /// <summary>The partition name. <para>分区名。</para></summary>
    [ObservableProperty]
    private string _name = string.Empty;

    partial void OnNameChanged(string value)
    {
        if (Step is { } step)
        {
            Step = step with { Partition = value };
        }
    }

    partial void OnCommandChanged(string value)
    {
        if (Step is { } step)
        {
            Step = step with { Command = value };
        }
    }

    /// <summary>The formatted image size. <para>已格式化的镜像大小。</para></summary>
    [ObservableProperty]
    private string _size = string.Empty;

    /// <summary>
    /// The partition size in bytes declared by the package layout; 0 or negative when the table leaves the size
    /// open-ended, so a read-back of this row cannot bound its transfer at a known size.
    /// <para>包布局声明的分区字节大小；分区表未给出确定大小时为 0 或负数，此时该行的回读无法按已知大小限定传输长度。</para>
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Bytes the current run transfers for this row, taken from the staged image or the phase total it reports; 0 when
    /// the run has not established a measurable length, which excludes the row from the aggregate transfer speed.
    /// <para>本次运行为该行传输的字节数，取自暂存镜像或阶段上报的总量；运行尚未确定可度量长度时为 0，该行不参与
    /// 汇总传输速度。</para>
    /// </summary>
    public long TransferBytes { get; set; }

    /// <summary>The partition offset or sector offset. <para>分区偏移量或扇区偏移。</para></summary>
    [ObservableProperty]
    private string _offset = string.Empty;

    /// <summary>
    /// The storage medium this row writes to, for example <c>EMMC</c>, <c>UFS</c> or <c>LUN 3</c>; empty when the
    /// package does not determine one.
    /// <para>该行写入的存储介质，例如 <c>EMMC</c>、<c>UFS</c> 或 <c>LUN 3</c>；刷机包无法确定时为空。</para>
    /// </summary>
    [ObservableProperty]
    private string _storageMedium = string.Empty;

    /// <summary>The image file name. <para>镜像文件名。</para></summary>
    [ObservableProperty]
    private string _fileName = string.Empty;

    /// <summary>Whether this row has an image file name. <para>该行是否已有镜像文件名。</para></summary>
    public bool HasFileName => !string.IsNullOrEmpty(FileName);

    /// <summary>
    /// Whether this row's operation takes an image file, so a row without one may offer the choose-file prompt. A row
    /// that erases, reboots, switches a slot or only names a partition has nothing to pick, so its file cell stays blank
    /// instead of inviting a file the operation would ignore.
    /// <para>该行的操作是否需要镜像文件，因此没有文件的行才可以提示选择文件。擦除、重启、切换槽位或仅列出分区的行没有可选的
    /// 文件，其文件列保持空白，而不是提示一个操作根本不会使用的文件。</para>
    /// </summary>
    [ObservableProperty]
    private bool _acceptsFile = true;

    /// <summary>
    /// Whether the file cell offers the choose-file prompt: the operation takes a file and the row still has none.
    /// <para>文件列是否显示选择文件提示：操作需要文件而该行还没有文件。</para>
    /// </summary>
    public bool CanChooseFile => AcceptsFile && !HasFileName;

    /// <summary>
    /// Whether a command needs an image file; the empty command falls back to <c>flash</c>, which does. A
    /// <c>wipe_super</c> row is also checked for its <c>super_empty</c> image before it runs, so it needs one too.
    /// <para>命令是否需要镜像文件；空命令回退为 <c>flash</c>，因此需要。<c>wipe_super</c> 行在执行前同样会校验
    /// <c>super_empty</c> 镜像，因此也需要。</para>
    /// </summary>
    /// <param name="command">Command token from a script or package. <para>来自脚本或包的命令标记。</para></param>
    /// <returns><see langword="true"/> for <c>flash</c>, <c>wipe_super</c> and the empty fallback.
    /// <para><c>flash</c>、<c>wipe_super</c> 与空回退都为 <see langword="true"/>。</para></returns>
    internal static bool CommandTakesFile(string? command) =>
        string.IsNullOrWhiteSpace(command) ||
        command.Equals("flash", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("wipe_super", StringComparison.OrdinalIgnoreCase);

    partial void OnFileNameChanged(string value)
    {
        OnPropertyChanged(nameof(HasFileName));
        OnPropertyChanged(nameof(CanChooseFile));
    }

    partial void OnAcceptsFileChanged(bool value) => OnPropertyChanged(nameof(CanChooseFile));

    /// <summary>The full image path, used by the row tooltip. <para>镜像完整路径，用于条目提示。</para></summary>
    [ObservableProperty]
    private string _filePath = string.Empty;

    /// <summary>The stage this row has reached in the flash run. <para>该行在刷写流程中所处的阶段。</para></summary>
    [ObservableProperty]
    private FlashRowState _state;

    /// <summary>The row progress percentage; -1 means there is no progress to show. <para>行内进度百分比，-1 表示当前没有可显示的进度。</para></summary>
    [ObservableProperty]
    private int _percent = -1;

    /// <summary>
    /// Whether the row's transfer has no measurable fraction, so its bar animates instead of showing a percentage.
    /// <para>该行的传输没有可度量的比例，进度条改为滚动显示而不显示百分比。</para>
    /// </summary>
    [ObservableProperty]
    private bool _isIndeterminate;

    /// <summary>The row status text, written by the flash run in the current language. <para>行内状态文本，由刷写流程按当前语言写入。</para></summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// Whether the row is executing; the UI shows its progress bar from this instead of comparing enums in bindings.
    /// <para>该行是否正在执行；界面据此显示行内进度条，避免在绑定里比较枚举。</para>
    /// </summary>
    public bool IsRunning => State == FlashRowState.Running;

    partial void OnStateChanged(FlashRowState value) => OnPropertyChanged(nameof(IsRunning));
}

/// <summary>
/// Flash page state and orchestration: the partition list, its search filter, the selection summary and every
/// device-facing action the page starts.
/// <para>烧录页面的状态与编排：分区列表、搜索过滤、勾选汇总，以及页面发起的全部面向设备动作。</para>
/// </summary>
/// <remarks>
/// The partition rows come from the selected package; the view model never infers which image belongs to which
/// partition on its own. Device operations run through the flash services against the selected device's target.
/// <para>分区行来自所选刷机包；视图模型不自行推断镜像归属。设备操作经刷写服务针对所选设备的设备目标执行。</para>
/// </remarks>
public sealed partial class FlashViewModel : ObservableObject
{
    /// <summary>
    /// Maximum pending lines per publish; <see cref="LogText"/> is rebuilt only at this threshold so a single
    /// appended line never reallocates the whole string and re-lays out the log box.
    /// <para>一次发布的最大挂起行数：达到阈值才重建 <see cref="LogText"/>，避免每追加一行就重分配整串并
    /// 重排整个日志框。</para>
    /// </summary>
    private const int LogFlushLineThreshold = 32;

    /// <summary>
    /// Maximum pending characters per publish; whichever threshold is reached first triggers the publish, so
    /// very long single lines still appear promptly.
    /// <para>一次发布的最大挂起字符数，与行数阈值取先到的一个，保证超长单行也能及时发布。</para>
    /// </summary>
    private const int LogFlushCharacterThreshold = 4096;

    /// <summary>
    /// Tail window kept in <see cref="LogText"/>; beyond it only the prefix is dropped and the buffer shrinks
    /// with it, so a long flash run never grows unbounded.
    /// <para><see cref="LogText"/> 保留的末尾字符数；超出部分只丢前缀，缓冲区随之收缩，长日志不会无界增长。</para>
    /// </summary>
    private const int LogRetainedCharacterLimit = 200_000;

    private bool _updatingSelectAll;

    /// <summary>
    /// Log line buffer; appending writes here and only a publish builds the full string.
    /// <para>日志行缓冲：追加只写这里，发布时才生成整串。</para>
    /// </summary>
    private readonly StringBuilder _logBuffer = new();

    /// <summary>
    /// Count of unpublished pending lines; also decides whether the flush timer has to run.
    /// <para>未发布的挂起行数，同时是定时器是否需要运行的依据。</para>
    /// </summary>
    private int _pendingLogLines;

    /// <summary>
    /// Characters appended since the last publish; counting only that increment keeps a long log from
    /// publishing on every line once the buffer itself passes the threshold.
    /// <para>自上次发布以来追加的字符数；只看增量，长日志才不会因缓冲区总长度而每行都发布一次。</para>
    /// </summary>
    private int _pendingLogCharacters;

    /// <summary>
    /// Batch publish timer: drains lines below the threshold within 100 ms so a lone notice is never delayed,
    /// and stops while nothing is pending so an idle page costs no periodic wakeups.
    /// <para>批量发布定时器：100ms 内补发未达阈值的行，使单条提示不会因为合批而延迟显示；无挂起行时停止，
    /// 页面空闲不产生周期唤醒。</para>
    /// </summary>
    private DispatcherTimer? _logFlushTimer;

    /// <summary>
    /// Entries currently subscribed to <see cref="FlashPartitionEntry.PropertyChanged"/>; a Reset notification
    /// replaces the item collection, so this record keeps the entries that still need unsubscribing.
    /// <para>已订阅 <see cref="FlashPartitionEntry.PropertyChanged"/> 的条目；集合发出 Reset 通知时会替换条目集合，
    /// 必须靠这份记录退订，避免残留订阅。</para>
    /// </summary>
    private readonly HashSet<FlashPartitionEntry> _subscribedEntries = [];

    /// <summary>The flash backend used by every device-facing action on the page. <para>页面所有面向设备动作使用的刷写后端。</para></summary>
    private readonly IFlashService _flashService;

    /// <summary>Localized string lookup supplied by the composition root. <para>由组合根提供的本地化字符串查询。</para></summary>
    private readonly ILocalizer _localizer;

    /// <summary>Unified log sink supplied by the composition root. <para>由组合根提供的统一日志接收端。</para></summary>
    private readonly IUnifiedLog _log;

    /// <summary>Creates the view model and hooks list changes and the batched log publish. <para>构造视图模型并挂接列表变更与日志批量发布。</para></summary>
    public FlashViewModel() : this(FlashService.Default)
    {
    }

    /// <summary>
    /// Creates the view model against a specific flash backend.
    /// <para>以指定的刷写后端创建视图模型。</para>
    /// </summary>
    /// <param name="flashService">The flash backend to use.<para>要使用的刷写后端。</para></param>
    /// <param name="localizer">Localized string lookup; null uses the shared language service.
    /// <para>本地化字符串查询；为 null 时使用共享语言服务。</para></param>
    /// <param name="log">Unified log sink; null uses the shared log service.
    /// <para>统一日志接收端；为 null 时使用共享日志服务。</para></param>
    internal FlashViewModel(IFlashService flashService, ILocalizer? localizer = null, IUnifiedLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(flashService);
        _flashService = flashService;
        _localizer = localizer ?? LanguageService.Instance;
        _log = log ?? LogService.Instance;
        Partitions.CollectionChanged += OnPartitionsChanged;
        LoadSectionExpansion();
        LoadEdlSignatureSetting();
    }

    private DispatcherTimer CreateLogFlushTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => FlushPendingLog();
        return timer;
    }

    /// <summary>All partition entries. <para>全部分区条目。</para></summary>
    public LogEntryCollection<FlashPartitionEntry> Partitions { get; } = [];

    /// <summary>The entries bound to the grid after applying the search text. <para>按搜索词过滤后绑定到列表的分区条目。</para></summary>
    public LogEntryCollection<FlashPartitionEntry> VisiblePartitions { get; } = [];

    /// <summary>The current package path. <para>当前刷机包路径。</para></summary>
    [ObservableProperty]
    private string _packagePath = string.Empty;

    /// <summary>The partition-name filter. <para>分区名搜索词。</para></summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// The page log output; published in batches by <see cref="AppendLog"/> and carrying only the last
    /// <c>200000</c> characters of the buffer.
    /// <para>页面日志输出；由 <see cref="AppendLog"/> 按阈值批量发布，只反映缓冲区末尾
    /// <c>200000</c> 字符。</para>
    /// </summary>
    [ObservableProperty]
    private string _logText = string.Empty;

    /// <summary>The select-all check box state. <para>「全选」复选框状态。</para></summary>
    [ObservableProperty]
    private bool _isSelectAllChecked;

    /// <summary>The number of selected partitions. <para>已勾选的分区数量。</para></summary>
    [ObservableProperty]
    private int _selectedCount;

    /// <summary>Whether any partition rows are selected. <para>是否选中了分区行。</para></summary>
    [ObservableProperty]
    private bool _hasSelectedPartitions;

    /// <summary>Whether the filtered list contains rows. <para>过滤后是否存在可显示的分区。</para></summary>
    [ObservableProperty]
    private bool _hasVisiblePartitions;

    /// <summary>The overall progress percentage of the whole run. <para>整轮操作的总体进度百分比。</para></summary>
    [ObservableProperty]
    private double _overallPercent;

    /// <summary>The caption shown next to the overall progress bar. <para>总体进度旁的说明文本。</para></summary>
    [ObservableProperty]
    private string _overallText = string.Empty;

    /// <summary>
    /// Whether the overall bar has no measurable fraction and should animate instead of holding a value; a transfer
    /// whose total is unknown (a gzip source whose uncompressed length the package does not state) reports only bytes
    /// sent, so the bar shows motion rather than a fake percentage.
    /// <para>总体进度条没有可度量的比例、应改为滚动显示而非固定数值；总长度未知的传输（包未给出解压后长度的 gzip 源）只上报已
    /// 发送字节，进度条显示运动而不是伪造的百分比。</para>
    /// </summary>
    [ObservableProperty]
    private bool _isOverallIndeterminate;

    /// <summary>Whether the page is running an operation that changes the device or the list state. <para>页面是否正在执行会改变设备或列表状态的操作。</para></summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// Whether the SpacemiT package flash run reads every written partition back and compares it against the source
    /// image; bound to the SpacemiT-only option check box and passed to the flasher as its verification switch.
    /// <para>进迭时空烧录包烧录运行是否把每个写入的分区回读并与源镜像比对；绑定到进迭时空专属选项复选框，
    /// 并作为校验开关传给烧录器。</para>
    /// </summary>
    [ObservableProperty]
    private bool _verifyAfterFlash;

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
            // Script-step rows carry no individually runnable action, so select-all leaves their disabled boxes alone.
            // 脚本步骤行没有可单独执行的动作，因此全选不触碰其禁用的复选框。
            foreach (var entry in VisiblePartitions)
            {
                if (!entry.IsScriptStep)
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
    /// Appends one log line to the buffer at trace level, publishing the full string only at the line or character
    /// threshold and leaving everything else to the flush timer.
    /// <para>以 trace 级别追加一行日志到缓冲区，达到行数或字符阈值才发布整串，否则交给定时器补发。</para>
    /// </summary>
    /// <param name="message">The log line text. <para>日志行内容。</para></param>
    internal void AppendLog(string message) => AppendLog(message, LogLevel.Trace);

    /// <summary>
    /// Appends one log line at error level; used when an exception ends an action.
    /// <para>以 error 级别追加一行日志；异常结束某个动作时使用。</para>
    /// </summary>
    /// <param name="message">The log line text. <para>日志行内容。</para></param>
    internal void AppendError(string message) => AppendLog(message, LogLevel.Error);

    /// <summary>
    /// Appends one log line to the failure report: the same text goes to the page log at error level and to the
    /// unified log, so a failed flash action is visible in the global log window instead of only on this page.
    /// <para>追加一行失败记录：文本同时以 error 级别写入页面日志与统一日志，使失败的刷写动作在全局日志窗口中可见，
    /// 而不只出现在本页面。</para>
    /// </summary>
    /// <param name="exception">The exception that ended the action. <para>结束该动作的异常。</para></param>
    private void AppendError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var detail = exception.InnerException is { } inner
            ? $"{exception.Message} ({inner.Message})"
            : exception.Message;
        var text = string.Format(CultureInfo.CurrentCulture, _localizer.GetString("Flash_Log_Failure"),
            $"{exception.GetType().Name}: {detail}");
        AppendLog(text, LogLevel.Error);
    }

    /// <summary>
    /// Appends one log line at the given level and forwards it to the unified log, so every line the page shows is
    /// also collected by the global log window at the same severity.
    /// <para>按给定级别追加一行日志并转发到统一日志，使页面显示的每一行都以相同严重级别进入全局日志窗口。</para>
    /// </summary>
    /// <param name="message">The log line text. <para>日志行内容。</para></param>
    /// <param name="level">The severity the line is reported with. <para>该行上报的严重级别。</para></param>
    private void AppendLog(string message, LogLevel level)
    {
        _log.Write(level, "Flash", message);
        _logBuffer.Append(message).Append(Environment.NewLine);
        _pendingLogLines++;
        _pendingLogCharacters += message.Length + Environment.NewLine.Length;
        if (_pendingLogLines >= LogFlushLineThreshold || _pendingLogCharacters >= LogFlushCharacterThreshold)
        {
            PublishLog();
            return;
        }

        _logFlushTimer ??= CreateLogFlushTimer();
        if (!_logFlushTimer.IsEnabled)
        {
            _logFlushTimer.Start();
        }
    }

    /// <summary>
    /// Publishes every pending buffered line at once; called at run end points, on page detach and before a test
    /// reads the final state, so the last line never stays stuck in the buffer.
    /// <para>立即发布缓冲区里的全部挂起行；刷写结束、页面卸载和测试终态读取前调用，保证最后一行不会滞留。</para>
    /// </summary>
    internal void FlushPendingLog()
    {
        if (_pendingLogLines == 0)
        {
            _logFlushTimer?.Stop();
            return;
        }

        PublishLog();
    }

    /// <summary>
    /// Clears the log: buffer and published text both go to empty so the next publish cannot resurrect old lines.
    /// <para>清空日志：缓冲区与已发布的文本一起归零，避免下阈值时把旧内容重新发出去。</para>
    /// </summary>
    internal void ClearLog()
    {
        _pendingLogLines = 0;
        _pendingLogCharacters = 0;
        _logBuffer.Clear();
        _logFlushTimer?.Stop();
        LogText = string.Empty;
    }

    private void PublishLog()
    {
        _pendingLogLines = 0;
        _pendingLogCharacters = 0;
        _logFlushTimer?.Stop();
        TrimLogBuffer();
        LogText = _logBuffer.ToString();
    }

    /// <summary>
    /// Shrinks the buffer to its retained tail window, dropping whole leading lines. No notice line is inserted:
    /// the existing bilingual keys <c>Log_FileTooLarge</c> and <c>Node_Document_TooLarge</c> describe log-file and
    /// document capacity limits, so reusing them here would state limits that do not match this window, while a new
    /// key would break the locale dictionary parity checks.
    /// <para>把缓冲区收缩到末尾保留窗口，并按行边界丢弃前缀。丢弃处不插入提示行：现有双语键
    /// <c>Log_FileTooLarge</c> 与 <c>Node_Document_TooLarge</c> 描述的是日志文件与文档的容量上限，
    /// 用作尾窗提示会给出与实际不符的限制数字，而新增键会破坏语言字典对齐检查。</para>
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
        RefreshRootPartitionOptions();
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlashPartitionEntry.IsSelected))
        {
            UpdateSelectionState();
            if (sender is FlashPartitionEntry { IsSelected: true } selected)
            {
                OnEntrySelected(selected);
            }
        }
        else if (e.PropertyName is nameof(FlashPartitionEntry.Command) or nameof(FlashPartitionEntry.FilePath) &&
            sender is FlashPartitionEntry edited)
        {
            UpdateRowValidityText(edited);
        }
    }

    /// <summary>
    /// Runs the check a row owns when the user selects it; only the rows that carry a file instead of an image have one.
    /// <para>用户勾选某行时运行该行自带的检查；只有承载文件而非镜像的行才有这项检查。</para>
    /// </summary>
    /// <param name="entry">The row that just became selected.
    /// <para>刚刚被勾选的行。</para></param>
    /// <remarks>
    /// A loader row is checked here rather than when its file was picked, because the same row can be selected after a
    /// re-import, after the file changed on disk, or after the user edited another column; the verdict is published on
    /// the row so the grid states it instead of leaving the user to infer validity from the flash outcome.
    /// <para>loader 行在这里检查而不是在选文件时检查，因为同一行可能在重新导入后、磁盘文件变化后或用户编辑了其它列之后被
    /// 勾选；结论发布在行上，使列表直接说明有效性，而不是让用户从刷写结果反推。</para>
    /// </remarks>
    private void OnEntrySelected(FlashPartitionEntry entry)
    {
        if (entry.Command is not (LoaderCommand or ParameterCommand) || entry.FilePath.Length == 0)
        {
            return;
        }

        UpdateSpecialFileRowStatus(entry);
    }

    /// <summary>
    /// Publishes whether a row has the file the flash path needs, so an edited row states its own validity.
    /// <para>发布某行是否持有刷写路径所需的文件，使被编辑的行直接说明自身是否有效。</para>
    /// </summary>
    /// <param name="entry">Row whose command or file path just changed.
    /// <para>命令或文件路径刚刚变化的行。</para></param>
    /// <remarks>
    /// The in-row picker checks the file itself, so the re-check this property change would start is
    /// suppressed; one action never reports the same verdict twice.
    /// <para>行内选文件由选择器自己检查一次；抑制这次属性变化触发的重查，同一次动作才不会写出两行相同结论。</para>
    /// </remarks>
    private void UpdateRowValidityText(FlashPartitionEntry entry)
    {
        if (entry.Command is LoaderCommand or ParameterCommand)
        {
            if (entry.FilePath.Length == 0)
            {
                entry.StatusText = string.Empty;
                return;
            }

            if (_suppressSpecialFileRowCheck)
            {
                return;
            }

            UpdateSpecialFileRowStatus(entry);
            return;
        }

        entry.StatusText = entry.FilePath.Length == 0 || File.Exists(entry.FilePath)
            ? string.Empty
            : _localizer.GetString("Flash_FileMissing");
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
        HasSelectedPartitions = SelectedCount > 0;
        // Select-all reflects the rows a user can actually check; the disabled script-step rows never count.
        // 全选反映用户真正能勾选的行；禁用的脚本步骤行不参与判断。
        var selectable = VisiblePartitions.Where(entry => !entry.IsScriptStep).ToArray();
        var allVisibleSelected = selectable.Length > 0 && selectable.All(entry => entry.IsSelected);
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