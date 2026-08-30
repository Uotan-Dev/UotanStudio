using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Lp;
using UotanStudio.Models;
using UotanStudio.Services;
using UotanStudio.Services.Flash;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>分区表编辑器视图模型：左侧分区列表 + 右侧属性编辑（分区名/起始/大小/类型），
/// 支持 Android LP super 元数据与 Rockchip parameter 两种数据源，可解析、可视化编辑与回写保存。</para>
/// Partition-table editor view model: a left partition list plus a right property pane
/// (name/start/size/type), supporting Android LP super metadata and Rockchip parameter files; it
/// parses, edits visually and saves the table back to the file.
/// </summary>
public partial class PartitionEditorViewModel : Document
{
    private PartitionTableSource _source;
    private LpMetadata? _lpMetadata;
    private GptTableInfo? _gpt;
    private MbrTableInfo? _mbr;
    private AmlogicTableInfo? _amlogic;
    private string _rockchipOriginal = string.Empty;
    private string _rockchipDevice = string.Empty;
    private string _rockchipSuffix = string.Empty;

    /// <summary>
    /// <para>保存按钮的本地化标题。</para>
    /// The localized title of the save button.
    /// </summary>
    public static string SaveLabel => LanguageService.Instance.GetString("Dock_Partition_Save");

    /// <summary>
    /// <para>属性面板“名称”字段的本地化标题。</para>
    /// The localized title of the name field in the properties pane.
    /// </summary>
    public static string NameLabel => LanguageService.Instance.GetString("Dock_Partition_Name");

    /// <summary>
    /// <para>属性面板“起始”字段的本地化标题。</para>
    /// The localized title of the start field in the properties pane.
    /// </summary>
    public static string StartLabel => LanguageService.Instance.GetString("Dock_Partition_Start");

    /// <summary>
    /// <para>属性面板“大小”字段的本地化标题。</para>
    /// The localized title of the size field in the properties pane.
    /// </summary>
    public static string SizeLabel => LanguageService.Instance.GetString("Dock_Partition_Size");

    /// <summary>
    /// <para>属性面板“类型”字段的本地化标题。</para>
    /// The localized title of the type field in the properties pane.
    /// </summary>
    public static string TypeLabel => LanguageService.Instance.GetString("Dock_Partition_Type");

    /// <summary>
    /// <para>左栏分区列表的本地化标题。</para>
    /// The localized title of the left partition list.
    /// </summary>
    public static string PartitionsLabel => LanguageService.Instance.GetString("Dock_Partition_Partitions");

    /// <summary>
    /// <para>属性面板的本地化标题。</para>
    /// The localized title of the properties pane.
    /// </summary>
    public static string PropertiesLabel => LanguageService.Instance.GetString("Dock_Partition_Properties");

    /// <summary>
    /// <para>未选中分区时的本地化提示。</para>
    /// The localized hint shown when no partition is selected.
    /// </summary>
    public static string NoSelectionLabel => LanguageService.Instance.GetString("Dock_Partition_NoSelection");

    /// <summary>
    /// <para>“类型”输入框的本地化占位提示。</para>
    /// The localized placeholder hint of the type input.
    /// </summary>
    public static string TypeHintLabel => LanguageService.Instance.GetString("Dock_Partition_TypeHint");

    /// <summary>
    /// <para>文档对应的文件路径。</para>
    /// The file path of the document.
    /// </summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>
    /// <para>是否正在后台解析（联动忙碌指示动画）。</para>
    /// Whether a background parse is running (drives the busy indicator animation).
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// <para>文档已修改状态。</para>
    /// Document dirty state.
    /// </summary>
    [ObservableProperty] private bool _isDirty;

    /// <summary>
    /// <para>最后一次解析/保存错误文本；为空表示无错误。</para>
    /// The last parse/save error text; empty means no error.
    /// </summary>
    [ObservableProperty] private string _compileError = string.Empty;

    /// <summary>
    /// <para>数据源描述（用于顶部状态栏）。</para>
    /// Data-source description (shown in the top status bar).
    /// </summary>
    [ObservableProperty] private string _sourceDescription = string.Empty;

    /// <summary>
    /// <para>当前选中的分区（左栏选择联动，驱动右栏属性面板）。</para>
    /// The currently selected partition (left-pane selection linkage, drives the right properties pane).
    /// </summary>
    [ObservableProperty] private PartitionItemViewModel? _selectedPartition;

    /// <summary>
    /// <para>左栏分区列表。</para>
    /// Left-pane partition list.
    /// </summary>
    public ObservableCollection<PartitionItemViewModel> Partitions { get; } = [];

    /// <summary>
    /// <para>保存命令：将当前编辑写回原文件。</para>
    /// Save command: writes the current edits back to the file.
    /// </summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// <para>分区总数；未加载时为 0。</para>
    /// Total partition count; 0 when not loaded.
    /// </summary>
    public int PartitionCount { get; private set; }

    /// <summary>
    /// <para>是否已选中分区（右栏属性面板可见性）。</para>
    /// Whether a partition is selected (drives the right properties-pane visibility).
    /// </summary>
    public bool HasSelection => SelectedPartition is not null;

    /// <summary>
    /// <para>创建分区表编辑器视图模型并初始化保存命令。</para>
    /// Creates the partition-table editor view model and initializes the save command.
    /// </summary>
    public PartitionEditorViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    /// <summary>
    /// <para>选中分区变化：刷新属性面板可见性。</para>
    /// Selected partition changed: refreshes the properties-pane visibility.
    /// </summary>
    partial void OnSelectedPartitionChanged(PartitionItemViewModel? value) => OnPropertyChanged(nameof(HasSelection));

    /// <summary>
    /// <para>异步加载分区表：先尝试 LP super 元数据，失败后回落到 Rockchip parameter 文本，
    /// 两者都失败则视为不支持。</para>
    /// Asynchronously loads a partition table: tries LP super metadata first, then falls back to
    /// Rockchip parameter text; neither is treated as unsupported.
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
            // LP super 元数据优先；读失败（非 LP）再试 Rockchip parameter。
            // LP super metadata first; on failure (not an LP image) try a Rockchip parameter.
            LpMetadata? lp = null;
            try
            {
                lp = await Task.Run(() => MetadataReader.ReadFromImageFile(path), default).ConfigureAwait(true);
            }
            catch
            {
                lp = null;
            }

            if (lp is not null)
            {
                _source = PartitionTableSource.Lp;
                _lpMetadata = lp;
                BuildLpItems(lp);
                SourceDescription = $"LP super metadata: {lp.Partitions.Count} partition(s), {lp.Groups.Count} group(s)";
            }
            else
            {
                var tableLoaded = await TryLoadTableAsync(path).ConfigureAwait(true);
                if (tableLoaded)
                {
                    // 数据源与条目已由 TryLoadTableAsync 设置（GPT / MBR / Amlogic）。
                    // The source and entries have been set by TryLoadTableAsync (GPT / MBR / Amlogic).
                }
                else
                {
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
                        CompileError = "Not a recognized partition table: expected LP super metadata, GPT, MBR, Amlogic or a Rockchip parameter file (CMDLINE: mtdparts=...). 无法识别的分区表：需要 LP super 元数据、GPT、MBR、Amlogic 或 Rockchip parameter 文件。";
                        return;
                    }

                    _source = PartitionTableSource.Rockchip;
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
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>保存：校验编辑结果并回写原文件（LP 经 <see cref="MetadataWriter"/>，Rockchip 重建
    /// <c>CMDLINE: mtdparts=...</c> 行）。</para>
    /// Saves: validates the edits and writes them back to the file (LP via <see cref="MetadataWriter"/>,
    /// Rockchip by rebuilding the <c>CMDLINE: mtdparts=...</c> line).
    /// </summary>
    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        var items = Partitions.ToList();
        foreach (var item in items)
        {
            if (!item.TryApply(out var error))
            {
                CompileError = error ?? "Invalid partition edit. 分区编辑无效。";
                return;
            }
        }

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
                    SaveGpt(items);
                    break;
                case PartitionTableSource.Mbr:
                    SaveMbr(items);
                    break;
                case PartitionTableSource.Amlogic:
                    SaveAmlogic(items);
                    break;
                default:
                    throw new InvalidOperationException("No partition table loaded. 未加载分区表。");
            }

            IsDirty = false;
            CompileError = string.Empty;
            LogService.Instance.Write(LogLevel.Info, "PartitionEditor", $"Saved partition table to {FilePath}");
        }
        catch (Exception ex)
        {
            CompileError = $"Failed to save partition table: {ex.Message} 保存分区表失败。";
        }
    }

    /// <summary>
    /// <para>释放文档持有的模型与视图数据（文档关闭时调用）。</para>
    /// Releases the model and view data held by this document (called when the document is closed).
    /// </summary>
    public void Cleanup()
    {
        SelectedPartition = null;
        Partitions.Clear();
        _lpMetadata = null;
        _gpt = null;
        _mbr = null;
        _amlogic = null;
        _rockchipOriginal = string.Empty;
        _rockchipDevice = string.Empty;
        _rockchipSuffix = string.Empty;
        FilePath = null;
        SourceDescription = string.Empty;
        CompileError = string.Empty;
        IsDirty = false;
        PartitionCount = 0;
    }

    private void BuildLpItems(LpMetadata metadata)
    {
        Partitions.Clear();
        for (var i = 0; i < metadata.Partitions.Count; i++)
        {
            var partition = metadata.Partitions[i];
            var extentStart = (int)partition.FirstExtentIndex;
            var extentCount = (int)partition.NumExtents;
            var extents = metadata.Extents.Skip(extentStart).Take(extentCount).ToList();

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
                group));
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
                group: null));
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
            if (GptTable.HasSignature(bytes))
            {
                var info = GptTable.Parse(bytes);
                _source = PartitionTableSource.Gpt;
                _gpt = info;
                BuildGptItems(info);
                SourceDescription = $"GPT: {info.Entries.Count} partition(s), disk {info.DiskSizeBytes} bytes";
                return true;
            }

            if (MbrTable.HasSignature(bytes) && !AmlogicTable.HasMagic(bytes))
            {
                var info = MbrTable.Parse(bytes);
                _source = PartitionTableSource.Mbr;
                _mbr = info;
                BuildMbrItems(info);
                SourceDescription = $"MBR: {info.Partitions.Count} partition(s)";
                return true;
            }

            if (AmlogicTable.HasMagic(bytes))
            {
                var info = AmlogicTable.Parse(bytes);
                _source = PartitionTableSource.Amlogic;
                _amlogic = info;
                BuildAmlogicItems(info);
                SourceDescription = $"Amlogic: {info.Partitions.Count} partition(s)";
                return true;
            }
        }
        catch
        {
            // 解析失败则回退到 Rockchip 检测；错误经 CompileError 呈现。
            // Parse failure falls back to the Rockchip detection; errors surface via CompileError.
        }

        return false;
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

    private void BuildGptItems(GptTableInfo info)
    {
        Partitions.Clear();
        for (var i = 0; i < info.Entries.Count; i++)
        {
            var entry = info.Entries[i];
            Partitions.Add(new PartitionItemViewModel(
                new PartitionEntry
                {
                    Name = entry.Name,
                    StartSector = entry.FirstLba,
                    SizeSectors = entry.LastLba - entry.FirstLba + 1,
                    Type = entry.TypeGuid.ToString("D")
                },
                i,
                group: null));
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
                    Type = "0x" + partition.Type.ToString("X2", CultureInfo.InvariantCulture)
                },
                i,
                group: null));
        }
    }

    private void BuildAmlogicItems(AmlogicTableInfo info)
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
                    Type = "0x" + partition.Type.ToString("X2", CultureInfo.InvariantCulture)
                },
                i,
                group: null));
        }
    }

    private void SaveGpt(IReadOnlyList<PartitionItemViewModel> items)
    {
        if (_gpt is null)
        {
            throw new InvalidOperationException("No GPT table loaded. 未加载 GPT 分区表。");
        }

        if (items.Count != _gpt.Entries.Count)
        {
            throw new InvalidDataException("Partition count changed; add/remove is not supported. 分区数量发生变化，暂不支持增删分区。");
        }

        var entries = new List<GptEntry>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var original = _gpt.Entries[i];
            entries.Add(new GptEntry
            {
                TypeGuid = Guid.TryParse(item.Type, out var parsed) ? parsed : original.TypeGuid,
                UniqueGuid = original.UniqueGuid,
                FirstLba = item.StartSector,
                LastLba = checked(item.StartSector + item.SizeSectors - 1),
                Attributes = original.Attributes,
                Name = item.Name
            });
        }

        File.WriteAllBytes(FilePath!, GptTable.Build(_gpt.DiskSizeBytes, _gpt.DiskGuid, entries));
    }

    private void SaveMbr(IReadOnlyList<PartitionItemViewModel> items)
    {
        if (_mbr is null)
        {
            throw new InvalidOperationException("No MBR table loaded. 未加载 MBR 分区表。");
        }

        if (items.Count != _mbr.Partitions.Count)
        {
            throw new InvalidDataException("Partition count changed; add/remove is not supported. 分区数量发生变化，暂不支持增删分区。");
        }

        var entries = new List<PartitionEntry>(items.Count);
        var bootable = new List<bool>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var original = _mbr.Partitions[i];
            var type = MbrTable.TryParseType(item.Type, out var parsed) ? parsed : original.Type;
            entries.Add(new PartitionEntry
            {
                Name = item.Name,
                StartSector = item.StartSector,
                SizeSectors = item.SizeSectors,
                Type = "0x" + type.ToString("X2", CultureInfo.InvariantCulture)
            });
            bootable.Add(original.Bootable);
        }

        File.WriteAllBytes(FilePath!, MbrTable.Build(_mbr.DiskSignature, entries, bootable));
    }

    private void SaveAmlogic(IReadOnlyList<PartitionItemViewModel> items)
    {
        if (_amlogic is null)
        {
            throw new InvalidOperationException("No Amlogic table loaded. 未加载 Amlogic 分区表。");
        }

        if (items.Count != _amlogic.Partitions.Count)
        {
            throw new InvalidDataException("Partition count changed; add/remove is not supported. 分区数量发生变化，暂不支持增删分区。");
        }

        var entries = new List<PartitionEntry>(items.Count);
        var bootable = new List<bool>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var original = _amlogic.Partitions[i];
            var type = MbrTable.TryParseType(item.Type, out var parsed) ? parsed : original.Type;
            entries.Add(new PartitionEntry
            {
                Name = item.Name,
                StartSector = item.StartSector,
                SizeSectors = item.SizeSectors,
                Type = "0x" + type.ToString("X2", CultureInfo.InvariantCulture)
            });
            bootable.Add(original.Bootable);
        }

        File.WriteAllBytes(FilePath!, AmlogicTable.Build(_amlogic.DiskSignature, entries, bootable));
    }

    private void SaveLp(IReadOnlyList<PartitionItemViewModel> items)
    {
        if (_lpMetadata is null)
        {
            throw new InvalidOperationException("No LP metadata loaded. 未加载 LP 元数据。");
        }

        if (items.Count != _lpMetadata.Partitions.Count)
        {
            throw new InvalidDataException("Partition count changed; add/remove is not supported. 分区数量发生变化，暂不支持增删分区。");
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var partition = _lpMetadata.Partitions[i];
            var nameBytes = Encoding.UTF8.GetBytes(item.Name);
            if (nameBytes.Length > 36)
            {
                throw new InvalidDataException($"Partition name '{item.Name}' exceeds 36 bytes. 分区名称超过 36 字节。");
            }
            _lpMetadata.Partitions[i] = partition with { Name = nameBytes };

            var extentStart = (int)partition.FirstExtentIndex;
            var extentCount = (int)partition.NumExtents;
            if (extentCount <= 0)
            {
                continue;
            }

            // 起始偏移：所有 extent 平移 delta，保持相对布局。
            // Start offset: shift all extents by the delta, preserving the relative layout.
            var first = _lpMetadata.Extents[extentStart];
            var delta = (long)item.StartSector - (long)first.TargetData;
            for (var e = extentStart; e < extentStart + extentCount; e++)
            {
                var extent = _lpMetadata.Extents[e];
                var newTarget = (long)extent.TargetData + delta;
                if (newTarget < 0)
                {
                    throw new InvalidDataException($"Start of partition '{item.Name}' would go negative. 分区起始为负。");
                }
                _lpMetadata.Extents[e] = extent with { TargetData = (ulong)newTarget };
            }

            // 大小：总大小的差值调整到最后一个 extent。
            // Size: apply the total-size delta to the last extent.
            ulong total = 0;
            for (var e = extentStart; e < extentStart + extentCount; e++)
            {
                total += _lpMetadata.Extents[e].NumSectors;
            }
            if (total != item.SizeSectors)
            {
                var lastIndex = extentStart + extentCount - 1;
                var last = _lpMetadata.Extents[lastIndex];
                var newLast = (long)last.NumSectors + (long)item.SizeSectors - (long)total;
                if (newLast < 0)
                {
                    throw new InvalidDataException($"Size of partition '{item.Name}' too small for its extent layout. 分区大小小于其 extent 布局。");
                }
                _lpMetadata.Extents[lastIndex] = last with { NumSectors = (ulong)newLast };
            }

            // 类型：仅当为 linear/zero 时写回第一个 extent 的目标类型，未知值保持原样。
            // Type: only linear/zero is written back to the first extent's target type; unknown values are kept.
            var targetType = item.Type switch
            {
                "linear" => MetadataFormat.LP_TARGET_TYPE_LINEAR,
                "zero" => MetadataFormat.LP_TARGET_TYPE_ZERO,
                _ => first.TargetType
            };
            var updated = _lpMetadata.Extents[extentStart];
            _lpMetadata.Extents[extentStart] = updated with { TargetType = targetType };
        }

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
}

/// <summary>
/// <para>分区表条目视图模型（左栏列表与右栏属性编辑的绑定对象）。</para>
/// Partition-table entry view model (the binding object for the left list and the right properties pane).
/// </summary>
public partial class PartitionItemViewModel : ObservableObject
{
    private readonly PartitionEntry _entry;

    /// <summary>
    /// <para>创建分区条目视图模型。</para>
    /// Creates a partition-entry view model.
    /// </summary>
    /// <param name="entry">条目数据。 The entry data.</param>
    /// <param name="index">条目在表中的索引。 The entry index in the table.</param>
    /// <param name="group">条目所属组（LP；Rockchip 为 <c>null</c>）。 The owning group (LP; <c>null</c> for Rockchip).</param>
    public PartitionItemViewModel(PartitionEntry entry, int index, string? group)
    {
        _entry = entry;
        Index = index;
        Group = group;
        Name = entry.Name;
        Start = PartitionTableFormat.FormatSectors(entry.StartSector);
        Size = PartitionTableFormat.FormatSectors(entry.SizeSectors);
        Type = entry.Type;
    }

    /// <summary>
    /// <para>条目在表中的索引。</para>
    /// The entry index in the table.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// <para>条目所属组（LP；Rockchip 为 <c>null</c>）。</para>
    /// The owning group (LP; <c>null</c> for Rockchip).
    /// </summary>
    public string? Group { get; }

    /// <summary>
    /// <para>分区名称（可编辑）。</para>
    /// The partition name (editable).
    /// </summary>
    [ObservableProperty] private string _name;

    /// <summary>
    /// <para>分区起始显示值（可编辑，0x 十六进制）。</para>
    /// The partition start display value (editable, 0x-hex).
    /// </summary>
    [ObservableProperty] private string _start;

    /// <summary>
    /// <para>分区大小显示值（可编辑，0x 十六进制）。</para>
    /// The partition size display value (editable, 0x-hex).
    /// </summary>
    [ObservableProperty] private string _size;

    /// <summary>
    /// <para>分区类型（可编辑；LP 写回 extent 目标类型，Rockchip 为展示字段）。</para>
    /// The partition type (editable; written back to the LP extent target type, display-only for Rockchip).
    /// </summary>
    [ObservableProperty] private string _type;

    /// <summary>
    /// <para>解析后的起始扇区（<see cref="TryApply"/> 成功后的当前值）。</para>
    /// The parsed start sector (current value after a successful <see cref="TryApply"/>).
    /// </summary>
    public ulong StartSector => _entry.StartSector;

    /// <summary>
    /// <para>解析后的大小扇区（<see cref="TryApply"/> 成功后的当前值）。</para>
    /// The parsed size in sectors (current value after a successful <see cref="TryApply"/>).
    /// </summary>
    public ulong SizeSectors => _entry.SizeSectors;

    /// <summary>
    /// <para>校验并应用当前编辑值到条目数据；失败时输出错误文本并返回 <c>false</c>。</para>
    /// Validates and applies the current edit values to the entry data; returns <c>false</c> with an
    /// error message when a value is invalid.
    /// </summary>
    /// <param name="error">校验错误文本；成功时为 <c>null</c>。 The validation error message; <c>null</c> on success.</param>
    public bool TryApply(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(Name))
        {
            error = "Partition name must not be empty. 分区名称不能为空。";
            return false;
        }
        if (Name.Length > 36)
        {
            error = $"Partition name too long (max 36 bytes): '{Name}'. 分区名称过长。";
            return false;
        }
        if (!PartitionTableFormat.TryParseSectors(Start, out var start))
        {
            error = $"Invalid start value: '{Start}'. 起始值无效。";
            return false;
        }
        if (!PartitionTableFormat.TryParseSectors(Size, out var size))
        {
            error = $"Invalid size value: '{Size}'. 大小值无效。";
            return false;
        }

        _entry.Name = Name;
        _entry.StartSector = start;
        _entry.SizeSectors = size;
        _entry.Type = Type;
        return true;
    }
}
