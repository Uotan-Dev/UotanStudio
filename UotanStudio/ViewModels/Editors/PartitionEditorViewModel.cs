using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.BlockImage;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.BlockImage.Mbr;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Amlogic;
using UotanStudio.Models;
using UotanStudio.Services;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Workspace.Documents;
using UotanStudio.UiAbstractions;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// Partition-table editor view model: a GNOME Disks style visual partition band at the top (segments
/// laid out by sector proportion, clicking selects), a left partition list plus a right properties
/// pane (name/start/size/type); it parses Android LP super metadata, GPT, MBR, Amlogic and Rockchip
/// parameter files, edits, adds/removes partitions and saves the table back to the file.
/// <para>分区表编辑器视图模型：顶部为 GNOME Disks 风格的可视化分区段布局（按扇区比例排布，
/// 点击联动选择），下方左侧分区列表 + 右侧属性编辑（分区名/起始/大小/类型），
/// 支持 Android LP super 元数据、GPT、MBR、Amlogic 与 Rockchip parameter 多种数据源，
/// 可解析、可视化编辑、增删分区并回写保存。</para>
/// </summary>
public partial class PartitionEditorViewModel : Document, ITool, ICommandSaveableDocument
{
    private PartitionTableSource _source;
    private LpMetadata? _lpMetadata;
    private GptTable? _gpt;
    private MbrTableInfo? _mbr;
    private AmlogicPartitionTableInfo? _amlogic;
    private string _rockchipOriginal = string.Empty;
    private string _rockchipDevice = string.Empty;
    private string _rockchipSuffix = string.Empty;
    private ulong _capacitySectors;
    private bool _isLoaded;
    private byte[]? _gptImage;
    private PartitionTableCopyItemViewModel? _activeTableCopy;
    private bool _suppressTableCopySelection;

    /// <summary>
    /// The localized title of the save button.
    /// <para>保存按钮的本地化标题。</para>
    /// </summary>
    public static string SaveLabel => LanguageService.Instance.GetString("Dock_Partition_Save");

    /// <summary>
    /// The localized title of the name field in the properties pane.
    /// <para>属性面板“名称”字段的本地化标题。</para>
    /// </summary>
    public static string NameLabel => LanguageService.Instance.GetString("Dock_Partition_Name");

    /// <summary>
    /// The localized title of the start field in the properties pane.
    /// <para>属性面板“起始”字段的本地化标题。</para>
    /// </summary>
    public static string StartLabel => LanguageService.Instance.GetString("Dock_Partition_Start");

    /// <summary>
    /// The localized title of the size field in the properties pane.
    /// <para>属性面板“大小”字段的本地化标题。</para>
    /// </summary>
    public static string SizeLabel => LanguageService.Instance.GetString("Dock_Partition_Size");

    /// <summary>
    /// The localized title of the type field in the properties pane.
    /// <para>属性面板“类型”字段的本地化标题。</para>
    /// </summary>
    public static string TypeLabel => LanguageService.Instance.GetString("Dock_Partition_Type");

    /// <summary>
    /// The localized title of the left partition list.
    /// <para>左栏分区列表的本地化标题。</para>
    /// </summary>
    public static string PartitionsLabel => LanguageService.Instance.GetString("Dock_Partition_Partitions");

    /// <summary>
    /// The localized title of the properties pane.
    /// <para>属性面板的本地化标题。</para>
    /// </summary>
    public static string PropertiesLabel => LanguageService.Instance.GetString("Dock_Partition_Properties");

    /// <summary>
    /// The localized hint shown when no partition is selected.
    /// <para>未选中分区时的本地化提示。</para>
    /// </summary>
    public static string NoSelectionLabel => LanguageService.Instance.GetString("Dock_Partition_NoSelection");

    /// <summary>
    /// The localized placeholder hint of the type input.
    /// <para>“类型”输入框的本地化占位提示。</para>
    /// </summary>
    public static string TypeHintLabel => LanguageService.Instance.GetString("Dock_Partition_TypeHint");

    /// <summary>
    /// The localized title of the add-partition button.
    /// <para>“添加分区”按钮的本地化标题。</para>
    /// </summary>
    public static string AddLabel => LanguageService.Instance.GetString("Dock_Partition_Add");

    /// <summary>
    /// The localized title of the remove-partition button.
    /// <para>“删除分区”按钮的本地化标题。</para>
    /// </summary>
    public static string RemoveLabel => LanguageService.Instance.GetString("Dock_Partition_Remove");

    /// <summary>
    /// The localized text of an unallocated gap segment.
    /// <para>未分配间隙段的本地化文本。</para>
    /// </summary>
    public static string UnallocatedLabel => LanguageService.Instance.GetString("Dock_Partition_Unallocated");

    /// <summary>
    /// The file path of the document.
    /// <para>文档对应的文件路径。</para>
    /// </summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>
    /// The table copy currently shown and edited; drives the table selector and the save target.
    /// <para>当前显示与编辑的表副本；驱动分区表选择器与保存目标。</para>
    /// </summary>
    [ObservableProperty] private PartitionTableCopyItemViewModel? _selectedTableCopy;

    /// <summary>
    /// Table copies carried by the loaded image; empty for single-table sources, which hides the selector.
    /// <para>已加载镜像携带的表副本；单表来源为空，此时选择器隐藏。</para>
    /// </summary>
    public ObservableCollection<PartitionTableCopyItemViewModel> TableCopies { get; } = [];

    /// <summary>
    /// Whether the selector is offered, i.e. the image carries more than one table copy.
    /// <para>是否显示选择器，即镜像携带多于一份表副本。</para>
    /// </summary>
    public bool HasTableCopies => TableCopies.Count > 1;

    /// <summary>
    /// Whether a background parse is running (drives the busy indicator animation).
    /// <para>是否正在后台解析（联动忙碌指示动画）。</para>
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// Document dirty state.
    /// <para>文档已修改状态。</para>
    /// </summary>
    [ObservableProperty] private bool _isDirty;

    partial void OnIsDirtyChanged(bool value) => IsModified = value;

    /// <summary>
    /// The last parse/save error text; empty means no error.
    /// <para>最后一次解析/保存错误文本；为空表示无错误。</para>
    /// </summary>
    [ObservableProperty] private string _compileError = string.Empty;

    /// <summary>
    /// Data-source description (shown in the top status bar).
    /// <para>数据源描述（用于顶部状态栏）。</para>
    /// </summary>
    [ObservableProperty] private string _sourceDescription = string.Empty;

    /// <summary>
    /// The currently selected partition (linked with the left list and the visual band clicks, drives
    /// the right properties pane).
    /// <para>当前选中的分区（左栏选择与分区段布局点击联动，驱动右栏属性面板）。</para>
    /// </summary>
    [ObservableProperty] private PartitionItemViewModel? _selectedPartition;

    /// <summary>
    /// Capacity/extent summary text (shown above the visual partition band).
    /// <para>容量/范围摘要文本（分区段布局上方的本地化标签）。</para>
    /// </summary>
    [ObservableProperty] private string _capacityText = string.Empty;

    /// <summary>
    /// Left-pane partition list.
    /// <para>左栏分区列表。</para>
    /// </summary>
    public ObservableCollection<PartitionItemViewModel> Partitions { get; } = [];

    /// <summary>
    /// Segments of the visual partition band (including unallocated gaps), ordered by start sector.
    /// <para>可视化分区段布局的段列表（含未分配间隙），按起始扇区排序。</para>
    /// </summary>
    public ObservableCollection<PartitionSegmentViewModel> Segments { get; } = [];

    /// <summary>
    /// Save command: writes the current edits back to the file.
    /// <para>保存命令：将当前编辑写回原文件。</para>
    /// </summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// Add-partition command: appends a new partition entry (starting at the aligned end of the
    /// current last partition).
    /// <para>添加分区命令：在列表末尾追加一个新分区条目（起始取当前最大结束扇区的对齐值）。</para>
    /// </summary>
    public RelayCommand AddPartitionCommand { get; }

    /// <summary>
    /// Remove-partition command: removes the selected partition entry from the list.
    /// <para>删除分区命令：从列表移除当前选中的分区条目。</para>
    /// </summary>
    public RelayCommand RemovePartitionCommand { get; }

    /// <summary>
    /// Total partition count; 0 when not loaded.
    /// <para>分区总数；未加载时为 0。</para>
    /// </summary>
    public int PartitionCount { get; private set; }

    /// <summary>
    /// Whether a partition is selected (drives the right properties-pane visibility).
    /// <para>是否已选中分区（右栏属性面板可见性）。</para>
    /// </summary>
    public bool HasSelection => SelectedPartition is not null;

    /// <summary>
    /// Creates the partition-table editor view model and initializes the save command.
    /// <para>创建分区表编辑器视图模型并初始化保存命令。</para>
    /// </summary>
    public PartitionEditorViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        AddPartitionCommand = new RelayCommand(AddPartition, () => _isLoaded && !IsBusy);
        RemovePartitionCommand = new RelayCommand(RemoveSelectedPartition, () => _isLoaded && SelectedPartition is not null);
    }

    /// <summary>
    /// Selected partition changed: refreshes the properties-pane visibility, the visual band highlight
    /// and the remove command availability.
    /// <para>选中分区变化：刷新属性面板可见性、分区段布局高亮与删除命令可用态。</para>
    /// </summary>
    partial void OnSelectedPartitionChanged(PartitionItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        foreach (var segment in Segments)
        {
            segment.IsSelected = ReferenceEquals(segment.Item, value);
        }

        RemovePartitionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Asynchronously loads a partition table: tries LP super metadata first, then falls back to
    /// Rockchip parameter text; neither is treated as unsupported.
    /// <para>异步加载分区表：先尝试 LP super 元数据，失败后回落到 Rockchip parameter 文本，
    /// 两者都失败则视为不支持。</para>
    /// </summary>
    /// <param name="path">分区表文件路径。<para>The partition-table file path.</para></param>
    public async Task LoadAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        FilePath = path;
        Title = Path.GetFileName(path);

        IsBusy = true;
        try
        {
            _capacitySectors = 0;
            _isLoaded = false;
            ResetTableCopies();

            LpMetadata? lp = null;
            try
            {
                lp = await Task.Run(() =>
                {
                    FirmwareFormatRegistry.RegisterAll();
                    return FormatDetector.Detect(path) is LpFormatHandler
                        ? MetadataReader.ReadFromImageFile(path)
                        : null;
                }, default).ConfigureAwait(true);
            }
            catch
            {
                lp = null;
            }

            if (lp is not null)
            {
                _source = PartitionTableSource.Lp;
                _lpMetadata = lp;
                _capacitySectors = lp.BlockDevices.Count > 0
                    ? lp.BlockDevices[0].Size / (ulong)MetadataFormat.LP_SECTOR_SIZE
                    : 0;
                BuildLpItems(lp);
                SourceDescription = $"LP super metadata: {lp.Partitions.Count} partition(s), {lp.Groups.Count} group(s)";
            }
            else
            {
                var tableLoaded = await TryLoadTableAsync(path).ConfigureAwait(true);
                if (tableLoaded)
                {
                }
                else
                {
                    if (!DocumentFormatPolicy.IsRockchipParameter(path))
                    {
                        CompileError = LanguageService.Instance.GetString("UserError_PartitionUnrecognized");
                        return;
                    }

                    IReadOnlyList<RockchipPartition>? rockchip;
                    try
                    {
                        rockchip = await Task.Run(() => RockchipParameterParser.Parse(path), default).ConfigureAwait(true);
                    }
                    catch
                    {
                        rockchip = null;
                    }

                    if (rockchip is null)
                    {
                        CompileError = LanguageService.Instance.GetString("UserError_PartitionUnrecognized");
                        return;
                    }

                    _source = PartitionTableSource.Rockchip;
                    _capacitySectors = 0;
                    _rockchipOriginal = await File.ReadAllTextAsync(path).ConfigureAwait(true);
                    var cmdline = FindCmdline(_rockchipOriginal);
                    (_rockchipDevice, _rockchipSuffix) = cmdline is null
                        ? (string.Empty, string.Empty)
                        : ExtractLayout(cmdline);
                    BuildRockchipItems(rockchip);
                    SourceDescription = $"Rockchip parameter: {rockchip.Count} partition(s) (mtdparts)";
                }
            }

            PartitionCount = Partitions.Count;
            CompileError = string.Empty;
            IsDirty = false;
            _isLoaded = true;
            RebuildBand();
            AddPartitionCommand.NotifyCanExecuteChanged();
            RemovePartitionCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Saves: validates the edits and writes them back to the file (LP via <see cref="MetadataWriter"/>,
    /// Rockchip by rebuilding the <c>CMDLINE: mtdparts=...</c> line).
    /// <para>保存：校验编辑结果并回写原文件（LP 经 <see cref="MetadataWriter"/>，Rockchip 重建
    /// <c>CMDLINE: mtdparts=...</c> 行）。</para>
    /// </summary>
    public async Task SaveAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        var items = Partitions.ToList();
        foreach (var item in items)
        {
            if (!item.TryApply(out var error))
            {
                CompileError = error ?? LanguageService.Instance.GetString("UserError_PartitionInvalidEdit");
                return;
            }
        }

        if (TryFindOverlap(items, out var overlapFirst, out var overlapSecond))
        {
            CompileError = LanguageService.Instance.GetString("UserError_PartitionOverlap", overlapFirst, overlapSecond);
            return;
        }

        IsBusy = true;
        try
        {
            switch (_source)
            {
                case PartitionTableSource.Lp:
                    SaveLp(items);
                    break;
                case PartitionTableSource.Rockchip:
                    await SaveRockchipAsync(items).ConfigureAwait(true);
                    break;
                case PartitionTableSource.Gpt:
                    await SaveGptAsync(items).ConfigureAwait(true);
                    break;
                case PartitionTableSource.Mbr:
                    await SaveMbrAsync(items).ConfigureAwait(true);
                    break;
                case PartitionTableSource.Amlogic:
                    await SaveAmlogicAsync(items).ConfigureAwait(true);
                    break;
                default:
                    throw new InvalidOperationException("No partition table loaded.");
            }

            IsDirty = false;
            CompileError = string.Empty;
            RebuildBand();
            LogService.Instance.Write(LogLevel.Info, "PartitionEditor", $"Saved partition table to {FilePath}");
        }
        catch (Exception ex)
        {
            CompileError = UserError.Describe(ex, "UserError_SavePartitionFailed");
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Releases the model and view data held by this document (called when the document is closed).
    /// <para>释放文档持有的模型与视图数据（文档关闭时调用）。</para>
    /// </summary>
    public void Cleanup()
    {
        SelectedPartition = null;
        Partitions.Clear();
        Segments.Clear();
        _lpMetadata = null;
        _gpt = null;
        ResetTableCopies();
        _mbr = null;
        _amlogic = null;
        _rockchipOriginal = string.Empty;
        _rockchipDevice = string.Empty;
        _rockchipSuffix = string.Empty;
        _capacitySectors = 0;
        _isLoaded = false;
        FilePath = null;
        SourceDescription = string.Empty;
        CapacityText = string.Empty;
        CompileError = string.Empty;
        IsDirty = false;
        PartitionCount = 0;
        AddPartitionCommand.NotifyCanExecuteChanged();
        RemovePartitionCommand.NotifyCanExecuteChanged();
    }

    private void BuildLpItems(LpMetadata metadata)
    {
        Partitions.Clear();
        for (var i = 0; i < metadata.Partitions.Count; i++)
        {
            var partition = metadata.Partitions[i];
            var extentStart = (int)partition.FirstExtentIndex;
            var extentCount = (int)partition.NumExtents;
            var extents = new List<LpMetadataExtent>(extentCount);
            for (var j = 0; j < extentCount && extentStart + j < metadata.Extents.Count; j++)
                extents.Add(metadata.Extents[extentStart + j]);

            ulong start = 0;
            ulong size = 0;
            var type = "linear";
            if (extents.Count > 0)
            {
                start = extents[0].TargetData;
                foreach (var extent in extents)
                {
                    size += extent.NumSectors;
                }
                type = extents[0].TargetType == MetadataFormat.LP_TARGET_TYPE_ZERO ? "zero" : "linear";
            }

            var group = partition.GroupIndex < metadata.Groups.Count
                ? metadata.Groups[(int)partition.GroupIndex].NameString
                : string.Empty;

            Partitions.Add(new PartitionItemViewModel(
                new PartitionEntry
                {
                    Name = partition.NameString,
                    StartSector = start,
                    SizeSectors = size,
                    Type = type
                },
                i,
                group,
                MarkDirtyAndRefresh));
        }
    }

    private void BuildRockchipItems(IReadOnlyList<RockchipPartition> partitions)
    {
        Partitions.Clear();
        for (var i = 0; i < partitions.Count; i++)
        {
            var p = partitions[i];
            Partitions.Add(new PartitionItemViewModel(
                new PartitionEntry
                {
                    Name = p.Name,
                    StartSector = (ulong)p.OffsetSectors,
                    SizeSectors = (ulong)p.SizeSectors,
                    Type = "mtd"
                },
                i,
                group: null,
                MarkDirtyAndRefresh));
        }
    }

    private async Task<bool> TryLoadTableAsync(string path)
    {
        var bytes = await ReadBoundedTableAsync(path).ConfigureAwait(true);
        if (bytes is null)
        {
            return false;
        }

        try
        {
            if (GptImageEditor.TryRead(bytes, null, out var gpt, out var source))
            {
                _source = PartitionTableSource.Gpt;
                LoadTableCopies(bytes, source);
                _gpt = gpt;
                _capacitySectors = gpt.BlockCount;
                BuildGptItems(gpt);
                SourceDescription = DescribeGptTable(gpt, bytes);
                return true;
            }

            if (GptImageEditor.HasSignature(bytes))
            {
                return false;
            }

            if (AmlogicPartitionTable.HasMagic(bytes))
            {
                var info = AmlogicPartitionTable.Parse(bytes);
                _source = PartitionTableSource.Amlogic;
                _amlogic = info;
                _capacitySectors = 0;
                BuildAmlogicItems(info);
                SourceDescription = $"Amlogic: {info.Partitions.Count} partition(s)";
                return true;
            }

            if (MbrTable.HasPartitionTable(bytes))
            {
                var info = MbrTable.Parse(bytes);
                _source = PartitionTableSource.Mbr;
                _mbr = info;
                _capacitySectors = 0;
                BuildMbrItems(info);
                SourceDescription = $"MBR: {info.Partitions.Count} partition(s)";
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    /// <summary>
    /// Table-copy selection changed: reloads the partition list from the selected copy of the loaded image.
    /// Edits in flight block the switch so no table is silently replaced.
    /// <para>表副本选择变化：从已加载镜像的选中副本重新载入分区列表。存在未保存修改时拒绝切换，
    /// 避免静默替换任何一张表。</para>
    /// </summary>
    /// <param name="value">The newly selected copy, or <c>null</c> while the list is rebuilt. <para>新选中的副本；列表重建期间为 <c>null</c>。</para></param>
    partial void OnSelectedTableCopyChanged(PartitionTableCopyItemViewModel? value)
    {
        if (_suppressTableCopySelection || value is null || _source != PartitionTableSource.Gpt)
        {
            return;
        }

        if (IsDirty)
        {
            CompileError = LanguageService.Instance.GetString("Dock_Partition_CopyDirty");
            RestoreTableCopySelection();
            return;
        }

        if (_gptImage is null || !GptImageEditor.TryRead(_gptImage, value.HeaderLba, out var table, out var source))
        {
            CompileError = LanguageService.Instance.GetString("UserError_PartitionUnrecognized");
            RestoreTableCopySelection();
            return;
        }

        _activeTableCopy = TableCopies.FirstOrDefault(item => item.HeaderLba == source.HeaderLba) ?? value;
        _gpt = table;
        _capacitySectors = table.BlockCount;
        BuildGptItems(table);
        SelectedPartition = null;
        IsDirty = false;
        CompileError = string.Empty;
        PartitionCount = Partitions.Count;
        SourceDescription = DescribeGptTable(table, _gptImage);
        RebuildBand();
    }

    private void ResetTableCopies()
    {
        _suppressTableCopySelection = true;
        try
        {
            TableCopies.Clear();
            SelectedTableCopy = null;
            _activeTableCopy = null;
        }
        finally
        {
            _suppressTableCopySelection = false;
        }

        _gptImage = null;
        OnPropertyChanged(nameof(HasTableCopies));
    }

    private void LoadTableCopies(byte[] bytes, GptTableCopy source)
    {
        _suppressTableCopySelection = true;
        try
        {
            TableCopies.Clear();
            var copies = GptImageEditor.DescribeCopies(bytes);
            _gptImage = copies.Count > 1 ? bytes : null;
            for (var index = 0; index < copies.Count; index++)
            {
                TableCopies.Add(new PartitionTableCopyItemViewModel(index + 1, copies[index]));
            }

            _activeTableCopy = TableCopies.FirstOrDefault(item => item.HeaderLba == source.HeaderLba);
            SelectedTableCopy = _activeTableCopy;
        }
        finally
        {
            _suppressTableCopySelection = false;
        }

        OnPropertyChanged(nameof(HasTableCopies));
    }

    private void RestoreTableCopySelection()
    {
        _suppressTableCopySelection = true;
        try
        {
            SelectedTableCopy = _activeTableCopy;
        }
        finally
        {
            _suppressTableCopySelection = false;
        }
    }

    private static string DescribeGptTable(GptTable table, byte[]? bytes)
    {
        var description = $"GPT: {table.Partitions.Count} partition(s), disk {table.BlockCount * (ulong)table.BlockSize} bytes";
        return bytes is null ? description : description + DescribeGptCopies(bytes);
    }

    private static string DescribeGptCopies(byte[] bytes)
    {
        var copies = GptImageEditor.DescribeCopies(bytes);
        return copies.Count <= 1
            ? string.Empty
            : $", {copies.Count} table copies: "
                + string.Join(", ", copies.Select(copy => string.Create(
                    CultureInfo.InvariantCulture, $"LBA {copy.HeaderLba} ({copy.PartitionCount})")));
    }

    private static async Task<byte[]?> ReadBoundedTableAsync(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 64L * 1024 * 1024)
            {
                return null;
            }

            var buffer = new byte[checked((int)info.Length)];
            using var stream = File.OpenRead(path);
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read)).ConfigureAwait(true);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            return buffer;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatPartitionType(byte type) => string.Create(
        4,
        type,
        static (span, value) =>
        {
            span[0] = '0';
            span[1] = 'x';
            value.TryFormat(span[2..], out _, "X2", CultureInfo.InvariantCulture);
        });

    private void BuildGptItems(GptTable table)
    {
        Partitions.Clear();
        for (var i = 0; i < table.Partitions.Count; i++)
        {
            var entry = table.Partitions[i];
            Partitions.Add(new PartitionItemViewModel(
                new PartitionEntry
                {
                    Name = entry.Name,
                    StartSector = entry.FirstLba,
                    SizeSectors = entry.LastLba >= entry.FirstLba ? (entry.LastLba - entry.FirstLba) + 1 : 0,
                    Type = entry.TypeId.ToString("D")
                },
                i,
                group: null,
                MarkDirtyAndRefresh));
        }
    }

    private void BuildMbrItems(MbrTableInfo info)
    {
        Partitions.Clear();
        for (var i = 0; i < info.Partitions.Count; i++)
        {
            var partition = info.Partitions[i];
            Partitions.Add(new PartitionItemViewModel(
                new PartitionEntry
                {
                    Name = $"p{i + 1}",
                    StartSector = partition.StartLba,
                    SizeSectors = partition.SizeSectors,
                    Type = FormatPartitionType(partition.Type)
                },
                i,
                group: null,
                MarkDirtyAndRefresh));
        }
    }

    private void BuildAmlogicItems(AmlogicPartitionTableInfo info)
    {
        Partitions.Clear();
        for (var i = 0; i < info.Partitions.Count; i++)
        {
            var partition = info.Partitions[i];
            Partitions.Add(new PartitionItemViewModel(
                new PartitionEntry
                {
                    Name = partition.Name,
                    StartSector = partition.StartLba,
                    SizeSectors = partition.SizeSectors,
                    Type = FormatPartitionType(partition.Type)
                },
                i,
                group: null,
                MarkDirtyAndRefresh));
        }
    }

    private async Task SaveGptAsync(IReadOnlyList<PartitionItemViewModel> items)
    {
        if (_gpt is null)
        {
            throw new InvalidOperationException("No GPT table loaded.");
        }

        var image = await ReadBoundedTableAsync(FilePath!).ConfigureAwait(true)
            ?? throw new InvalidDataException("The GPT source file is no longer readable as a bounded table image.");

        var partitions = new List<GptPartition>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var original = item.OriginalIndex >= 0 && item.OriginalIndex < _gpt.Partitions.Count
                ? _gpt.Partitions[item.OriginalIndex]
                : null;
            partitions.Add(new GptPartition(
                i,
                Guid.TryParse(item.Type, out var parsed) ? parsed : original?.TypeId ?? GptLayout.DefaultTypeGuid,
                original?.Id ?? Guid.NewGuid(),
                item.StartSector,
                checked(item.StartSector + item.SizeSectors - 1),
                original?.Attributes ?? 0,
                item.Name,
                ByteOffsetOf(item.StartSector),
                ByteLengthOf(item.SizeSectors)));
        }

        await WriteTableAsync(FilePath!, GptImageEditor.Patch(image, partitions)).ConfigureAwait(true);
    }

    private async Task SaveMbrAsync(IReadOnlyList<PartitionItemViewModel> items)
    {
        if (_mbr is null)
        {
            throw new InvalidOperationException("No MBR table loaded.");
        }

        var entries = new List<PartitionEntry>(items.Count);
        var bootable = new List<bool>(items.Count);
        foreach (var item in items)
        {
            var original = item.OriginalIndex >= 0 && item.OriginalIndex < _mbr.Partitions.Count
                ? _mbr.Partitions[item.OriginalIndex]
                : null;
            var type = MbrTable.TryParseType(item.Type, out var parsed) ? parsed : original?.Type ?? MbrTable.DefaultLinuxType;
            entries.Add(new PartitionEntry
            {
                Name = item.Name,
                StartSector = item.StartSector,
                SizeSectors = item.SizeSectors,
                Type = FormatPartitionType(type)
            });
            bootable.Add(original?.Bootable ?? false);
        }

        await WriteTableAsync(FilePath!, MbrTable.Build(_mbr.DiskSignature, entries, bootable)).ConfigureAwait(true);
    }

    private async Task SaveAmlogicAsync(IReadOnlyList<PartitionItemViewModel> items)
    {
        if (_amlogic is null)
        {
            throw new InvalidOperationException("No Amlogic table loaded.");
        }

        var entries = new List<PartitionEntry>(items.Count);
        var bootable = new List<bool>(items.Count);
        foreach (var item in items)
        {
            var original = item.OriginalIndex >= 0 && item.OriginalIndex < _amlogic.Partitions.Count
                ? _amlogic.Partitions[item.OriginalIndex]
                : null;
            var type = MbrTable.TryParseType(item.Type, out var parsed) ? parsed : original?.Type ?? AmlogicPartitionTable.DefaultType;
            entries.Add(new PartitionEntry
            {
                Name = item.Name,
                StartSector = item.StartSector,
                SizeSectors = item.SizeSectors,
                Type = FormatPartitionType(type)
            });
            bootable.Add(original?.Bootable ?? false);
        }

        await WriteTableAsync(FilePath!, AmlogicPartitionTable.Build(_amlogic.DiskSignature, entries, bootable)).ConfigureAwait(true);
    }

    private static long ByteOffsetOf(ulong firstSector) =>
        firstSector > ((ulong)long.MaxValue / (ulong)PartitionTableFormat.SectorSize)
            ? long.MaxValue
            : (long)(firstSector * (ulong)PartitionTableFormat.SectorSize);

    private static long ByteLengthOf(ulong sizeSectors) =>
        sizeSectors > ((ulong)long.MaxValue / (ulong)PartitionTableFormat.SectorSize)
            ? long.MaxValue
            : (long)(sizeSectors * (ulong)PartitionTableFormat.SectorSize);

    private static async Task WriteTableAsync(string path, byte[] bytes)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The partition-table target has no parent directory.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(true);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void SaveLp(IReadOnlyList<PartitionItemViewModel> items)
    {
        if (_lpMetadata is null)
        {
            throw new InvalidOperationException("No LP metadata loaded.");
        }

        var partitions = new List<LpMetadataPartition>(items.Count);
        List<LpMetadataExtent> extents = [];
        foreach (var item in items)
        {
            var nameBytes = Encoding.UTF8.GetBytes(item.Name);
            if (nameBytes.Length > 36)
            {
                throw new InvalidDataException($"Partition name '{item.Name}' exceeds 36 bytes.");
            }

            if (item.OriginalIndex < 0 || item.OriginalIndex >= _lpMetadata.Partitions.Count)
            {
                partitions.Add(new LpMetadataPartition
                {
                    Name = nameBytes,
                    Attributes = 0,
                    FirstExtentIndex = (uint)extents.Count,
                    NumExtents = 1,
                    GroupIndex = 0
                });
                extents.Add(new LpMetadataExtent
                {
                    NumSectors = item.SizeSectors,
                    TargetType = item.Type == "zero" ? MetadataFormat.LP_TARGET_TYPE_ZERO : MetadataFormat.LP_TARGET_TYPE_LINEAR,
                    TargetData = item.StartSector,
                    TargetSource = 0
                });
                continue;
            }

            var partition = _lpMetadata.Partitions[item.OriginalIndex];
            var sourceStart = (int)partition.FirstExtentIndex;
            var sourceCount = (int)partition.NumExtents;
            var firstIndex = extents.Count;
            for (var e = 0; e < sourceCount && sourceStart + e < _lpMetadata.Extents.Count; e++)
            {
                extents.Add(_lpMetadata.Extents[sourceStart + e]);
            }

            var copied = extents.Count - firstIndex;
            if (copied > 0)
            {
                var first = extents[firstIndex];

                var delta = (long)item.StartSector - (long)first.TargetData;
                ulong total = 0;
                for (var i = 0; i < copied; i++)
                {
                    var extent = extents[firstIndex + i];
                    var newTarget = (long)extent.TargetData + delta;
                    if (newTarget < 0)
                    {
                        throw new InvalidDataException($"Start of partition '{item.Name}' would go negative.");
                    }

                    total += extent.NumSectors;
                    extents[firstIndex + i] = extent with { TargetData = (ulong)newTarget };
                }

                if (total != item.SizeSectors)
                {
                    var lastIndex = firstIndex + copied - 1;
                    var last = extents[lastIndex];
                    var newLast = (long)last.NumSectors + (long)item.SizeSectors - (long)total;
                    if (newLast < 0)
                    {
                        throw new InvalidDataException($"Size of partition '{item.Name}' too small for its extent layout.");
                    }

                    extents[lastIndex] = last with { NumSectors = (ulong)newLast };
                }

                var targetType = item.Type switch
                {
                    "linear" => MetadataFormat.LP_TARGET_TYPE_LINEAR,
                    "zero" => MetadataFormat.LP_TARGET_TYPE_ZERO,
                    _ => first.TargetType
                };
                extents[firstIndex] = extents[firstIndex] with { TargetType = targetType };
            }

            partitions.Add(partition with
            {
                Name = nameBytes,
                FirstExtentIndex = (uint)firstIndex,
                NumExtents = (uint)copied
            });
        }

        _lpMetadata.Partitions = partitions;
        _lpMetadata.Extents = extents;
        MetadataWriter.WriteToImageFile(FilePath!, _lpMetadata);
    }

    private async Task SaveRockchipAsync(IReadOnlyList<PartitionItemViewModel> items)
    {
        var entries = new List<string>(items.Count);
        foreach (var item in items)
        {
            entries.Add(
                $"{PartitionTableFormat.FormatSectors(item.SizeSectors)}@{PartitionTableFormat.FormatSectors(item.StartSector)}({item.Name})");
        }

        var cmdlineValue = "mtdparts=" + _rockchipDevice + ":" + string.Join(",", entries) + _rockchipSuffix;
        var rebuilt = RebuildParameterText(_rockchipOriginal, cmdlineValue);
        await File.WriteAllTextAsync(FilePath!, rebuilt).ConfigureAwait(true);
    }

    private static string? FindCmdline(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("CMDLINE:", StringComparison.OrdinalIgnoreCase))
            {
                return line["CMDLINE:".Length..].Trim();
            }
        }
        return null;
    }

    private static (string Device, string Suffix) ExtractLayout(string cmdline)
    {
        var mtdIdx = cmdline.IndexOf("mtdparts=", StringComparison.OrdinalIgnoreCase);
        if (mtdIdx < 0)
        {
            return (string.Empty, string.Empty);
        }
        var after = cmdline[(mtdIdx + "mtdparts=".Length)..];
        var colonIdx = after.IndexOf(':');
        var device = colonIdx >= 0 ? after[..colonIdx] : string.Empty;
        var entriesPart = colonIdx >= 0 ? after[(colonIdx + 1)..] : after;
        var lastColon = entriesPart.LastIndexOf(':');
        var suffix = lastColon >= 0 ? entriesPart[lastColon..] : string.Empty;
        return (device, suffix);
    }

    private static string RebuildParameterText(string original, string newCmdlineValue)
    {
        var lineBreak = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("CMDLINE:", StringComparison.OrdinalIgnoreCase))
            {
                var indent = line[..line.IndexOf(trimmed)];
                var mtdIdx = trimmed.IndexOf("mtdparts=", StringComparison.OrdinalIgnoreCase);
                if (mtdIdx < 0)
                {
                    continue;
                }
                lines[i] = indent + trimmed[..mtdIdx] + newCmdlineValue;
                break;
            }
        }
        return string.Join(lineBreak, lines);
    }

    private void MarkDirtyAndRefresh()
    {
        IsDirty = true;
        RebuildBand();
    }

    private void AddPartition()
    {
        ulong nextEnd = 0;
        var hasEnd = false;
        foreach (var item in Partitions)
        {
            var geometry = EffectiveGeometry(item);
            var end = SaturatingEnd(geometry.Start, geometry.Size);
            if (!hasEnd || end > nextEnd)
            {
                nextEnd = end;
                hasEnd = true;
            }
        }

        var start = hasEnd
            ? AlignUp(nextEnd)
            : _source switch
            {
                PartitionTableSource.Lp => 4096UL,
                PartitionTableSource.Gpt => GptLayout.FirstUsableLba,
                _ => 2048UL
            };
        var size = _capacitySectors > start && _capacitySectors - start >= 2048 ? _capacitySectors - start : 2048UL;
        var type = _source switch
        {
            PartitionTableSource.Lp => "linear",
            PartitionTableSource.Rockchip => "mtd",
            PartitionTableSource.Gpt => GptLayout.DefaultTypeGuid.ToString("D"),
            _ => "0x83"
        };
        var group = _lpMetadata is { Groups.Count: > 0 } ? _lpMetadata.Groups[0].NameString : null;
        var added = new PartitionItemViewModel(
            new PartitionEntry
            {
                Name = $"partition_{Partitions.Count + 1}",
                StartSector = start,
                SizeSectors = size,
                Type = type
            },
            -1,
            group,
            MarkDirtyAndRefresh);
        Partitions.Add(added);
        PartitionCount = Partitions.Count;
        SelectedPartition = added;
        MarkDirtyAndRefresh();
    }

    private void RemoveSelectedPartition()
    {
        var selected = SelectedPartition;
        if (selected is null)
        {
            return;
        }

        Partitions.Remove(selected);
        SelectedPartition = null;
        PartitionCount = Partitions.Count;
        MarkDirtyAndRefresh();
    }

    /// <summary>
    /// Rebuilds the visual partition band from the current edits: effective values prefer parseable
    /// input text and fall back to the last applied entry values; gap segments including the leading
    /// and trailing unallocated regions are generated as well.
    /// <para>按当前编辑值重建可视化分区段布局：有效值优先取可解析的输入文本，解析失败回落到
    /// 上次已应用的条目值；间隙段与首尾未分配区域一并生成。</para>
    /// </summary>
    private void RebuildBand()
    {
        var ordered = Partitions
            .Select(item => (Item: item, Geometry: EffectiveGeometry(item)))
            .OrderBy(t => t.Geometry.Start)
            .ToList();

        double maxEnd = 0;
        foreach (var (_, geometry) in ordered)
        {
            maxEnd = Math.Max(maxEnd, (double)geometry.Start + geometry.Size);
        }

        var baseSectors = Math.Max(_capacitySectors, Math.Max(maxEnd, 1));

        Segments.Clear();
        double cursor = 0;
        var colorIndex = 0;
        foreach (var (item, geometry) in ordered)
        {
            var start = (double)geometry.Start;
            if (start > cursor)
            {
                Segments.Add(CreateGap((start - cursor) / baseSectors));
                cursor = start;
            }

            var widthRatio = Math.Max((double)geometry.Size, 0) / baseSectors;
            var segment = new PartitionSegmentViewModel(
                item,
                item.Name,
                item.Name,
                PartitionTableFormat.FormatSectors(geometry.Start),
                string.Join('\n',
                    item.Name,
                    $"{StartLabel}: {PartitionTableFormat.FormatSectors(geometry.Start)} ({FormatBytes(geometry.Start * (ulong)PartitionTableFormat.SectorSize)})",
                    $"{SizeLabel}: {PartitionTableFormat.FormatSectors(geometry.Size)} ({FormatBytes(geometry.Size * (ulong)PartitionTableFormat.SectorSize)})"),
                PartitionSegmentViewModel.BrushFor(colorIndex++),
                widthRatio)
            {
                IsSelected = ReferenceEquals(SelectedPartition, item)
            };
            Segments.Add(segment);
            cursor = Math.Max(cursor, start + geometry.Size);
        }

        if (cursor < baseSectors)
        {
            Segments.Add(CreateGap((baseSectors - cursor) / baseSectors));
        }

        var shown = _capacitySectors > 0 ? _capacitySectors : maxEnd > 0 ? checked((ulong)maxEnd) : 0UL;
        CapacityText = shown == 0
            ? string.Empty
            : LanguageService.Instance.GetString(
                _capacitySectors > 0 ? "Dock_Partition_CapacityFormat" : "Dock_Partition_UsedFormat",
                PartitionTableFormat.FormatSectors(shown),
                FormatBytes(shown * (ulong)PartitionTableFormat.SectorSize));
    }

    private PartitionSegmentViewModel CreateGap(double widthRatio) => new(
        null,
        string.Empty,
        UnallocatedLabel,
        string.Empty,
        UnallocatedLabel,
        PartitionSegmentViewModel.GapFill,
        widthRatio);

    private static (ulong Start, ulong Size) EffectiveGeometry(PartitionItemViewModel item)
    {
        var start = PartitionTableFormat.TryParseSectors(item.Start, out var parsedStart) ? parsedStart : item.StartSector;
        var size = PartitionTableFormat.TryParseSectors(item.Size, out var parsedSize) ? parsedSize : item.SizeSectors;
        return (start, size);
    }

    private static bool TryFindOverlap(IReadOnlyList<PartitionItemViewModel> items, out string firstName, out string secondName)
    {
        firstName = string.Empty;
        secondName = string.Empty;
        double maxEnd = -1;
        var maxEndName = string.Empty;
        foreach (var (item, geometry) in items
                     .Select(i => (Item: i, Geometry: (Start: i.StartSector, Size: i.SizeSectors)))
                     .OrderBy(t => t.Geometry.Start))
        {
            var end = (double)geometry.Start + geometry.Size;
            if (geometry.Size > 0 && (double)geometry.Start < maxEnd)
            {
                firstName = maxEndName;
                secondName = item.Name;
                return true;
            }

            if (end > maxEnd)
            {
                maxEnd = end;
                maxEndName = item.Name;
            }
        }

        return false;
    }

    private static ulong AlignUp(ulong sectors) =>
        sectors > ulong.MaxValue - 7 ? sectors : (sectors + 7) / 8 * 8;

    private static ulong SaturatingEnd(ulong start, ulong size) =>
        size > ulong.MaxValue - start ? ulong.MaxValue : start + size;

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}

/// <summary>
/// One segment of the visual partition band (a partition or an unallocated gap); the width is the
/// sector-count ratio of the layout base.
/// <para>可视化分区段布局中的一个段（分区或未分配间隙），宽度按扇区占比计算。</para>
/// </summary>
public sealed partial class PartitionSegmentViewModel : ObservableObject, IBandSegmentLayout
{
    private static readonly IBrush GapBrush = CreateBrush("#8494A8", 0x66);
    private static readonly IBrush[] Palette =
    [
        CreateBrush("#4C8DF6"),
        CreateBrush("#3DBE7B"),
        CreateBrush("#E8A33D"),
        CreateBrush("#A86FD6"),
        CreateBrush("#2FB8C9"),
        CreateBrush("#E0687A"),
        CreateBrush("#89B83F"),
        CreateBrush("#C97AB8")
    ];

    /// <summary>
    /// Creates a band segment view model.
    /// <para>创建分区段视图模型。</para>
    /// </summary>
    /// <param name="item">对应的分区条目；间隙段为 <c>null</c>。 The owning partition entry; <c>null</c> for gaps.</param>
    /// <param name="text">条内显示的文本。 The text shown inside the bar segment.</param>
    /// <param name="labelText">标签行显示的文本。 The text shown in the label row.</param>
    /// <param name="offsetText">标签行中的偏移文本。 The offset text in the label row.</param>
    /// <param name="tooltip">悬停提示文本。 The tooltip text.</param>
    /// <param name="fill">段填充画刷。 The segment fill brush.</param>
    /// <param name="bandWidthRatio">段宽度占比（0..1）。 The segment width ratio (0..1).</param>
    public PartitionSegmentViewModel(PartitionItemViewModel? item, string text, string labelText, string offsetText, string tooltip, IBrush fill, double bandWidthRatio)
    {
        Item = item;
        Text = text;
        LabelText = labelText;
        OffsetText = offsetText;
        Tooltip = tooltip;
        Fill = fill;
        BandWidthRatio = bandWidthRatio;
    }

    /// <summary>
    /// The owning partition entry; <c>null</c> for gaps.
    /// <para>对应的分区条目；间隙段为 <c>null</c>。</para>
    /// </summary>
    public PartitionItemViewModel? Item { get; }

    /// <summary>
    /// The text shown inside the bar segment (partition name; empty for gaps).
    /// <para>条内显示文本（分区名；间隙为空）。</para>
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// The text shown in the label row.
    /// <para>标签行显示的文本。</para>
    /// </summary>
    public string LabelText { get; }

    /// <summary>
    /// The offset text in the label row.
    /// <para>标签行中的偏移文本。</para>
    /// </summary>
    public string OffsetText { get; }

    /// <summary>
    /// The tooltip text.
    /// <para>悬停提示文本。</para>
    /// </summary>
    public string Tooltip { get; }

    /// <summary>
    /// The segment fill brush.
    /// <para>段填充画刷。</para>
    /// </summary>
    public IBrush Fill { get; }

    /// <summary>
    /// The fraction of the available width this segment occupies (0..1).
    /// <para>段宽度占可用总宽的比例（0..1）。</para>
    /// </summary>
    public double BandWidthRatio { get; }

    /// <summary>
    /// Whether this segment is an unallocated gap.
    /// <para>是否为未分配间隙段。</para>
    /// </summary>
    public bool BandIsGap => Item is null;

    /// <summary>
    /// Whether this segment belongs to the currently selected partition.
    /// <para>是否为当前选中分区对应的段。</para>
    /// </summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// Returns the palette brush for the given segment ordinal.
    /// <para>按序取分区段配色。</para>
    /// </summary>
    public static IBrush BrushFor(int index) => Palette[index % Palette.Length];

    /// <summary>
    /// The fill brush of unallocated gaps.
    /// <para>未分配间隙的填充画刷。</para>
    /// </summary>
    public static IBrush GapFill => GapBrush;

    private static IBrush CreateBrush(string hexColor, byte alpha = 0xFF)
    {
        var color = Color.Parse(hexColor);
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }
}

/// <summary>
/// Partition-table entry view model (the binding object for the left list and the right properties pane).
/// <para>分区表条目视图模型（左栏列表与右栏属性编辑的绑定对象）。</para>
/// </summary>
public partial class PartitionItemViewModel : ObservableObject
{
    private readonly PartitionEntry _entry;
    private readonly Action? _onChanged;

    /// <summary>
    /// Creates a partition-entry view model.
    /// <para>创建分区条目视图模型。</para>
    /// </summary>
    /// <param name="entry">条目数据。 The entry data.</param>
    /// <param name="index">条目在原表中的索引；会话内新增的条目为 <c>-1</c>。 The entry index in the original table; <c>-1</c> for entries added in this session.</param>
    /// <param name="group">条目所属组（LP；Rockchip 为 <c>null</c>）。 The owning group (LP; <c>null</c> for Rockchip).</param>
    /// <param name="onChanged">属性变更时的回调（标记文档为脏并刷新分区段布局）。 Callback invoked on property change (marks document dirty and refreshes the band).
    /// </param>
    public PartitionItemViewModel(PartitionEntry entry, int index, string? group, Action? onChanged = null)
    {
        _entry = entry;
        _onChanged = onChanged;
        OriginalIndex = index;
        Group = group;
        Name = entry.Name;
        Start = PartitionTableFormat.FormatSectors(entry.StartSector);
        Size = PartitionTableFormat.FormatSectors(entry.SizeSectors);
        Type = entry.Type;
    }

    /// <summary>
    /// The entry index in the original table; <c>-1</c> for entries added in this session (the save
    /// path uses it to carry per-entry metadata such as GUIDs and bootable flags).
    /// <para>条目在原表中的索引；会话内新增的条目为 <c>-1</c>（保存时据此保留原 GUID、可引导标志等
    /// 每条目元数据）。</para>
    /// </summary>
    public int OriginalIndex { get; }

    /// <summary>
    /// The owning group (LP; <c>null</c> for Rockchip).
    /// <para>条目所属组（LP；Rockchip 为 <c>null</c>）。</para>
    /// </summary>
    public string? Group { get; }

    /// <summary>
    /// The partition name (editable).
    /// <para>分区名称（可编辑）。</para>
    /// </summary>
    [ObservableProperty] private string _name;
    partial void OnNameChanged(string value) => _onChanged?.Invoke();

    /// <summary>
    /// The partition start display value (editable, 0x-hex).
    /// <para>分区起始显示值（可编辑，0x 十六进制）。</para>
    /// </summary>
    [ObservableProperty] private string _start;
    partial void OnStartChanged(string value) => _onChanged?.Invoke();

    /// <summary>
    /// The partition size display value (editable, 0x-hex).
    /// <para>分区大小显示值（可编辑，0x 十六进制）。</para>
    /// </summary>
    [ObservableProperty] private string _size;
    partial void OnSizeChanged(string value) => _onChanged?.Invoke();

    /// <summary>
    /// The partition type (editable; written back to the LP extent target type, display-only for Rockchip).
    /// <para>分区类型（可编辑；LP 写回 extent 目标类型，Rockchip 为展示字段）。</para>
    /// </summary>
    [ObservableProperty] private string _type;
    partial void OnTypeChanged(string value) => _onChanged?.Invoke();

    /// <summary>
    /// The parsed start sector (current value after a successful <see cref="TryApply"/>).
    /// <para>解析后的起始扇区（<see cref="TryApply"/> 成功后的当前值）。</para>
    /// </summary>
    public ulong StartSector => _entry.StartSector;

    /// <summary>
    /// The parsed size in sectors (current value after a successful <see cref="TryApply"/>).
    /// <para>解析后的大小扇区（<see cref="TryApply"/> 成功后的当前值）。</para>
    /// </summary>
    public ulong SizeSectors => _entry.SizeSectors;

    /// <summary>
    /// Validates and applies the current edit values to the entry data; returns <c>false</c> with an
    /// error message when a value is invalid.
    /// <para>校验并应用当前编辑值到条目数据；失败时输出错误文本并返回 <c>false</c>。</para>
    /// </summary>
    /// <param name="error">校验错误文本；成功时为 <c>null</c>。 The validation error message; <c>null</c> on success.</param>
    public bool TryApply(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(Name))
        {
            error = LanguageService.Instance.GetString("UserError_PartitionNameRequired");
            return false;
        }
        if (Name.Length > 36)
        {
            error = LanguageService.Instance.GetString("UserError_PartitionNameLong", Name);
            return false;
        }
        if (!PartitionTableFormat.TryParseSectors(Start, out var start))
        {
            error = LanguageService.Instance.GetString("UserError_PartitionStartInvalid", Start);
            return false;
        }
        if (!PartitionTableFormat.TryParseSectors(Size, out var size))
        {
            error = LanguageService.Instance.GetString("UserError_PartitionSizeInvalid", Size);
            return false;
        }

        _entry.Name = Name;
        _entry.StartSector = start;
        _entry.SizeSectors = size;
        _entry.Type = Type;
        return true;
    }
}

/// <summary>
/// One selectable GPT table copy inside a container image, shown by the partition-table selector.
/// <para>容器镜像中一份可选中的 GPT 表副本，供分区表选择器显示。</para>
/// </summary>
public sealed class PartitionTableCopyItemViewModel
{
    /// <summary>
    /// Creates a selector item from a copy reported by the firmware reader.
    /// <para>根据固件读取器上报的副本创建选择器条目。</para>
    /// </summary>
    /// <param name="ordinal">One-based position of the copy inside the container. <para>副本在容器中的序号（从 1 开始）。</para></param>
    /// <param name="copy">The located copy and its partition count. <para>已定位的副本及其分区数。</para></param>
    public PartitionTableCopyItemViewModel(int ordinal, GptTableCopy copy)
    {
        HeaderLba = copy.HeaderLba;
        PartitionCount = copy.PartitionCount;
        Display = LanguageService.Instance.GetString("Dock_Partition_CopyFormat", ordinal, copy.HeaderLba, copy.PartitionCount);
    }

    /// <summary>
    /// The logical block address holding this copy's header.
    /// <para>持有该副本头部的逻辑块地址。</para>
    /// </summary>
    public ulong HeaderLba { get; }

    /// <summary>
    /// The populated partitions found in this copy.
    /// <para>该副本中已使用的分区数。</para>
    /// </summary>
    public int PartitionCount { get; }

    /// <summary>
    /// The localized text shown in the selector.
    /// <para>选择器显示的本地化文本。</para>
    /// </summary>
    public string Display { get; }
}