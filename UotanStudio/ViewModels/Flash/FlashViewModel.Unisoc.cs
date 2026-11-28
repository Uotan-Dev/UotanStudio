using System.Globalization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Comm.Protocols.Unisoc;
using UotanStudio.Firmware.VendorFirmware.Pac;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Ui;
using UotanStudio.UiAbstractions;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// UNISOC page actions: loader-stage status readout, the PAC-package-driven flash and partition read-back the
/// shared action buttons run, and the partition-table read that fills the main grid from the device's GPT or
/// native table.
/// <para>紫光展锐页面动作：加载阶段状态读取、通用动作按钮对 PAC 包执行的烧录与分区回读，以及从设备 GPT 或原生表
/// 填充主网格的分区表读取。</para>
/// </summary>
public sealed partial class FlashViewModel
{
    /// <summary>Cooperative bound for one UNISOC status query, matching the monitor's own bound.
    /// <para>单次紫光展锐状态查询的协作式时限，与监视服务自身的时限一致。</para></summary>
    private const int UnisocStatusTimeoutMs = 5000;

    /// <summary>Byte offset of the PAC magic word inside the package header.
    /// <para>包头内 PAC 魔数字的偏移。</para></summary>
    private const int UnisocPacMagicOffset = 2116;

    /// <summary>Header bytes a PAC probe must read to reach the magic word.
    /// <para>PAC 探测必须读取以到达魔数字的头部长度。</para></summary>
    private const int UnisocPacProbeLength = 2124;

    /// <summary>Grid command of every PAC step that writes a payload. <para>每个写入负载的 PAC 步骤的网格命令。</para></summary>
    private const string UnisocFlashCommand = "flash";

    /// <summary>Grid command of every PAC step that erases without a payload.
    /// <para>每个不含负载的擦除 PAC 步骤的网格命令。</para></summary>
    private const string UnisocEraseCommand = "erase";

    /// <summary>Status line of the Unisoc section; the stage readout and every operation write it.
    /// <para>紫光展锐分类的状态行；阶段读取与每个操作都会写入。</para></summary>
    [ObservableProperty]
    private string _unisocStatusText = string.Empty;

    /// <summary>Stage-aware hint under the status line; it says what the current loader stage offers and what still
    /// blocks the storage actions. <para>状态行下方随阶段变化的提示行；说明当前加载阶段能做什么，以及存储动作还差什么。</para></summary>
    [ObservableProperty]
    private string _unisocModeHint = string.Empty;

    /// <summary>Selected erase mode index: keep data, partition erase or full erase.
    /// <para>所选擦除模式索引：保留数据、分区擦除或整片擦除。</para></summary>
    [ObservableProperty]
    private int _unisocFlashModeIndex = 1;

    /// <summary>Selected post-flash action index: reboot or power off.
    /// <para>所选烧录后动作索引：重启或关机。</para></summary>
    [ObservableProperty]
    private int _unisocPostActionIndex;

    /// <summary>Whether the loaded package is a UNISOC PAC whose plan owns the grid rows.
    /// <para>已加载的包是否为其计划拥有网格行的紫光展锐 PAC。</para></summary>
    private bool _unisocPackage;

    /// <summary>The PAC rows the user pointed at an external image file, so the run writes that file instead of the
    /// packaged payload; cleared whenever the package is reloaded.
    /// <para>用户指向外部镜像文件的 PAC 行，运行时就写入该文件而不是包内负载；包重新加载时清空。</para></summary>
    private readonly HashSet<FlashPartitionEntry> _unisocExternalRows = [];

    /// <summary>Whether the selected endpoint answers as FDL2, which the storage actions require.
    /// <para>所选端点是否以 FDL2 应答；存储动作都要求该阶段。</para></summary>
    private bool _unisocIsFdl2;

    /// <summary>Status the selected endpoint answered most recently, kept per device key so re-selecting the same
    /// endpoint republishes it without another handshake.
    /// <para>所选端点最近一次应答的状态，按设备键保存，使重新选中同一端点时直接重发而无需再次握手。</para></summary>
    private (string Key, UnisocDeviceStatus Status)? _unisocStatusCache;

    /// <summary>
    /// UNISOC section: the pane and every button answer only on a UNISOC endpoint, so each carries that condition
    /// instead of a page-wide lookup; the header stays present for every endpoint.
    /// <para>紫光展锐分类：面板与每个按钮只在紫光展锐端点上应答，因此各自持有该条件，而不是依赖页面级查表；表头对任何端点都保持呈现。</para>
    /// </summary>
    private void RegisterUnisocControlGates()
    {
        RegisterGate("UnisocExpander", FlashControlGate.SectionHeader("UnisocExpander"));
        RegisterGate("UnisocPane", FlashControlGate.Unisoc("UnisocPane", busyDisabled: true));
    }

    /// <summary>
    /// Queries the selected endpoint once and publishes the stage, stage hint and status table. The stage also
    /// updates the device badge, because discovery cannot tell the loader stages apart. A cached answer for the
    /// same device key republishes without another handshake.
    /// <para>查询一次所选端点并发布阶段、阶段提示与状态表。阶段同时更新设备徽章，因为发现阶段无法区分加载阶段。
    /// 同一设备键的缓存应答直接重发而无需再次握手。</para>
    /// </summary>
    internal async void RefreshUnisocDeviceStatus()
    {
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Unisoc } device)
        {
            UnisocStatusText = string.Empty;
            UnisocModeHint = string.Empty;
            _unisocIsFdl2 = false;
            _unisocStatusCache = null;
            RefreshDeviceUiGate();
            return;
        }

        if (_unisocStatusCache is { } cached && string.Equals(cached.Key, device.Key, StringComparison.Ordinal))
        {
            ApplyUnisocStatus(cached.Status);
            return;
        }

        var language = _localizer;
        var epoch = BeginDeviceStatusRefresh();
        try
        {
            using var timeout = new CancellationTokenSource(UnisocStatusTimeoutMs);
            var status = await Task.Run(() => App.DeviceMonitor.QueryUnisocStatusAsync(device, timeout.Token)
                .GetAwaiter().GetResult(), timeout.Token).ConfigureAwait(true);
            if (SelectedDevice?.Key != device.Key || epoch != _rkStatusRefreshEpoch)
            {
                return;
            }

            _unisocStatusCache = (device.Key, status);
            ApplyUnisocStatus(status);
        }
        catch (OperationCanceledException)
        {
            UnisocStatusText = language.GetString("Flash_UnisocStatusTimeout");
            UnisocModeHint = language.GetString("Flash_UnisocHintUnknown");
            _unisocIsFdl2 = false;
            _unisocStatusCache = null;
            RefreshDeviceUiGate();
        }
        catch (Exception exception)
        {
            UnisocStatusText = language.GetString("Flash_UnisocStatusFailed") + " " + exception.Message;
            UnisocModeHint = language.GetString("Flash_UnisocHintUnknown");
            _unisocIsFdl2 = false;
            _unisocStatusCache = null;
            RefreshDeviceUiGate();
        }
    }

    /// <summary>
    /// Reports whether the file is a UNISOC PAC package: the <c>.pac</c> extension is accepted without opening the
    /// file, any other name must carry the package magic word at the fixed header offset.
    /// <para>判断文件是否为紫光展锐 PAC 包：<c>.pac</code> 扩展名不经打开文件即接受，其他名称必须在固定头部偏移处
    /// 携带包魔数字。</para>
    /// </summary>
    private static async Task<bool> IsUnisocPackageFileAsync(string path, CancellationToken cancellationToken)
    {
        if (Path.GetExtension(path).Equals(".pac", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var header = new byte[UnisocPacProbeLength];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = 0;
        while (read < header.Length)
        {
            var chunk = await stream.ReadAsync(header.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (chunk == 0)
            {
                return false;
            }

            read += chunk;
        }

        return header[UnisocPacMagicOffset] == 0xFA && header[UnisocPacMagicOffset + 1] == 0xFF &&
            header[UnisocPacMagicOffset + 2] == 0xFA && header[UnisocPacMagicOffset + 3] == 0xFF;
    }

    /// <summary>
    /// Loads a PAC package into the main grid: the plan's flashable partition images become tickable rows and the
    /// shared flash action later reads the package from the page-wide package path.
    /// <para>把 PAC 包装进主网格：计划中可刷写的分区镜像成为可勾选行，通用烧录动作随后从页面级包路径读取该包。</para>
    /// </summary>
    private async Task ApplyUnisocPackageAsync(string path, CancellationToken cancellation)
    {
        using var plan = await Task.Run(() => UnisocFlashPlan.Load(path, cancellation), cancellation)
            .ConfigureAwait(true);
        if (path != _loadedPath)
        {
            return;
        }

        _unisocPackage = true;
        _unisocExternalRows.Clear();
        Partitions.Apply(0, BuildUnisocPlanRows(plan));
        AppendLog($"{plan.Images.Count} " + _localizer.GetString("Flash_ImagesLoaded"));
    }

    /// <summary>
    /// Confirms a destructive erase the user just ticked. The tick is reverted immediately and only restored once the
    /// user accepts the confirmation, so the grid never shows an erase as selected before it was agreed to.
    /// <para>确认用户刚刚勾选的破坏性擦除。勾选会立即撤销，只在用户接受确认后才恢复，因此列表绝不会在得到同意前
    /// 把擦除显示为已勾选。</para>
    /// </summary>
    /// <param name="entry">The erase row that just became selected. <para>刚刚被勾选的擦除行。</para></param>
    /// <remarks>
    /// Reverting first keeps the state machine monotonic: a cancelled confirmation leaves the row unticked exactly as
    /// the user found it, and a second click cannot race the first one into two dialogs. The wipe phrase is the same one
    /// a full erase asks for, because both destroy data the user did not otherwise choose to overwrite.
    /// <para>先撤销使状态机保持单调：取消确认后该行回到用户原来看到的未勾选状态，第二次点击也不会与第一次竞态弹出两个窗口。
    /// 确认短语与整片擦除相同，因为两者都会销毁用户并未选择覆盖的数据。</para>
    /// </remarks>
    private async Task ConfirmDestructiveEraseSelectionAsync(FlashPartitionEntry entry)
    {
        _suppressDestructiveEraseCheck = true;
        try
        {
            entry.IsSelected = false;
        }
        finally
        {
            _suppressDestructiveEraseCheck = false;
        }

        UpdateSelectionState();
        if (TopLevel.GetTopLevel(Surface) is not Window owner)
        {
            return;
        }

        var confirmed = await DialogService.ConfirmDangerousAsync(owner, new DangerousConfirmRequest
        {
            TabTitle = _localizer.GetString("Flash_Unisoc"),
            Title = _localizer.GetString("Flash_UnisocEraseConfirmTitle"),
            Detail = string.Format(
                CultureInfo.CurrentCulture,
                _localizer.GetString("Flash_UnisocEraseConfirmDetail"),
                entry.Name),
            ConfirmWord = UnisocFlashOptions.WipeConfirmationPhrase,
            DeviceIdentifier = _deviceViewModel?.SelectedDevice?.ConfirmationIdentity
        }).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        _suppressDestructiveEraseCheck = true;
        try
        {
            entry.IsSelected = true;
        }
        finally
        {
            _suppressDestructiveEraseCheck = false;
        }

        UpdateSelectionState();
    }

    /// <summary>
    /// Builds one tickable grid row per plan entry in scheme order, filling the main grid's columns from the scheme: the
    /// storage medium comes from the product's <c>FlashTypeID</c>, the command from the step <c>Type</c>, the offset from
    /// <c>Block/Base</c> and the size from <c>Block/Size</c> or the payload length. The file cell names the packaged
    /// member as <c>PAC:&lt;name&gt;</c> and still lets the user point the row at an external image instead. The row name
    /// stays the plan entry name because the flash path matches ticked rows against that same name. An erase entry is
    /// flagged as destructive so select-all skips it and selecting it asks for confirmation.
    /// <para>按 scheme 顺序为每个计划条目构建一行可勾选网格行，并用 scheme 填充主网格各列：存储介质取自产品的
    /// <c>FlashTypeID</c>，命令取自步骤 <c>Type</c>，偏移取自 <c>Block/Base</c>，大小取自 <c>Block/Size</c> 或负载长度。
    /// 文件列以 <c>PAC:&lt;名称&gt;</c> 命名包内成员，同时仍允许用户把该行改指外部镜像。行名沿用计划条目名，因为
    /// 烧录路径以同一个名字匹配已勾选行。擦除条目被标记为破坏性，因此全选跳过它，勾选它需要确认。</para>
    /// </summary>
    private List<FlashPartitionEntry> BuildUnisocPlanRows(UnisocFlashPlan plan)
        => BuildUnisocPlanRows(plan, FormatUnisocRemainingSpace());

    /// <summary>
    /// Builds the grid rows for one plan with the remaining-space label supplied, so the mapping is verifiable without
    /// a constructed view model or a live localizer.
    /// <para>用外部提供的剩余空间标签构建一个计划的网格行，使该映射无需构造视图模型或实时本地化器即可验证。</para>
    /// </summary>
    /// <param name="plan">The loaded plan. <para>已加载的计划。</para></param>
    /// <param name="remainingSpaceLabel">Label for the partition that fills the device.
    /// <para>占满设备剩余空间分区的标签。</para></param>
    /// <returns>The grid rows. <para>网格行。</para></returns>
    internal static List<FlashPartitionEntry> BuildUnisocPlanRows(UnisocFlashPlan plan, string remainingSpaceLabel)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Dictionary<string, SprdPacPartition> declared = new(StringComparer.OrdinalIgnoreCase);
        foreach (var partition in plan.Partitions)
        {
            declared[partition.id] = partition;
        }

        List<FlashPartitionEntry> rows = [];
        rows.Add(CreateUnisocLoaderRow("FDL1", plan.Loaders.FirstStage, plan.Loaders.FirstStageAddress, plan.PackagePath));
        rows.Add(CreateUnisocLoaderRow("FDL2", plan.Loaders.SecondStage, plan.Loaders.SecondStageAddress, plan.PackagePath));
        foreach (var image in plan.Images)
        {
            var packaged = image.PackagedFileName;
            rows.Add(new FlashPartitionEntry
            {
                IsScriptStep = false,
                IsDestructiveErase = image.EraseOnly,
                Name = image.Name,
                StorageMedium = DescribeUnisocStorageMedium(image.FlashType),
                Command = image.EraseOnly ? UnisocEraseCommand : UnisocFlashCommand,
                Offset = DescribeUnisocOffset(image, declared, remainingSpaceLabel),
                Size = DescribeUnisocEntrySize(image),
                FileName = packaged.Length > 0 ? FormatUnisocPackagedFileName(packaged) : string.Empty,
                FilePath = packaged.Length > 0 ? plan.PackagePath : string.Empty,
                AcceptsFile = true
            });
        }

        return rows;
    }

    /// <summary>
    /// Builds the grid row for one PAC loader. The row is a locked script row: it states which boot image the
    /// package uploads and at which RAM address, but no storage action writes it to a partition.
    /// <para>为一个 PAC 加载器构建网格行。该行是锁定的脚本行：说明包上传哪个引导镜像、载入哪个 RAM 地址，
    /// 但没有任何存储动作会把它写入分区。</para>
    /// </summary>
    /// <param name="command">The fixed row command, <c>FDL1</c> or <c>FDL2</c>.
    /// <para>固定的行命令，<c>FDL1</c> 或 <c>FDL2</c>。</para></param>
    /// <param name="entry">The loader entry the package carries. <para>包携带的加载器条目。</para></param>
    /// <param name="address">The confirmed RAM load address. <para>确认的 RAM 加载地址。</para></param>
    /// <param name="packagePath">The package the loader streams from. <para>加载器流式读取的来源包。</para></param>
    /// <returns>The loader row. <para>加载器行。</para></returns>
    private static FlashPartitionEntry CreateUnisocLoaderRow(
        string command, PacEntry entry, uint address, string packagePath) => new()
        {
            IsScriptStep = true,
            IsDestructiveErase = false,
            IsSelected = true,
            Name = command,
            StorageMedium = string.Empty,
            Command = command,
            Offset = string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", address),
            Size = FormatByteSize(entry.Length),
            FileName = FormatUnisocPackagedFileName(entry.Name),
            FilePath = packagePath,
            AcceptsFile = false
        };

    /// <summary>
    /// Names the storage medium the package declares: the scheme's <c>FlashTypeID</c> is <c>1</c> for Nand flash and
    /// <c>0</c> for Nor flash per the official XML header, and any other value is reported verbatim so an unknown
    /// medium is never mislabelled as one of the two known ones.
    /// <para>给出包声明的存储介质：按官方 XML 头注释，scheme 的 <c>FlashTypeID</c> 为 <c>1</c> 表示 Nand flash、
    /// <c>0</c> 表示 Nor flash，其它取值按原样上报，绝不把未知介质错标为这两种之一。</para>
    /// </summary>
    /// <param name="flashType">The declared <c>FlashTypeID</c>. <para>声明的 <c>FlashTypeID</c>。</param>
    /// <returns>The medium label. <para>介质标签。</para></returns>
    private static string DescribeUnisocStorageMedium(int flashType) => flashType switch
    {
        0 => "NOR flash",
        1 => "NAND flash",
        _ => string.Format(CultureInfo.InvariantCulture, "FlashTypeID {0}", flashType)
    };

    /// <summary>
    /// Describes the offset cell of one PAC row from the partition table the package declares. The package states only
    /// partition names and sizes, so the position of each partition is what the official tool's rebuilt table would use:
    /// the accumulated sizes of the partitions declared before it. A row whose partition appears in that table therefore
    /// shows the derived byte offset; a row outside it falls back to the scheme <c>Block/Base</c>, and the two loader rows
    /// keep their main-RAM addresses because they are loaded into RAM rather than written to a partition. Nothing here
    /// forces a position at flash time — the flash pass still addresses partitions by name.
    /// <para>从包声明的分区表描述一个 PAC 行的偏移列。包只给出分区名与大小，因此每个分区的位次就是官方工具重建表时使用的
    /// 值：在它之前声明的分区大小之和。分区出现在该表中的行因此显示推导出的字节偏移；不在表中的行回退到 scheme 的
    /// <c>Block/Base</c>，而两个加载器行保留其主存地址，因为它们被载入 RAM 而不是写入分区。此处的结果在烧录时不强制
    /// 任何位置——烧录流程仍按分区名寻址。</para>
    /// </summary>
    /// <param name="image">The plan entry. <para>计划条目。</para></param>
    /// <param name="declared">The declared partitions by identifier. <para>按标识索引的已声明分区。</para></param>
    /// <returns>The derived offset, the scheme base, or an empty string when neither applies.
    /// <para>推导出的偏移、scheme 基址；两者都不适用时为空字符串。</para></returns>
    private static string DescribeUnisocOffset(
        UnisocFlashImage image, IReadOnlyDictionary<string, SprdPacPartition> declared, string remainingSpaceLabel)
    {
        if (image.Partition.Length > 0 && declared.TryGetValue(image.Partition, out var partition))
        {
            return partition.fillsRemainingSpace
                ? remainingSpaceLabel
                : partition.offset is { } offset ? FormatUnisocBase(offset) : string.Empty;
        }

        if (image.Partition.Length > 0)
        {
            return image.Partition;
        }

        return image.Offset > 0 ? FormatUnisocBase(image.Offset) : string.Empty;
    }

    /// <summary>
    /// Labels the partition that fills whatever the device has left. The package declares it as <c>0xFFFFFFFF</c>, so no
    /// offset or size can be derived from package data alone and the cell says so instead of showing a wrong number.
    /// <para>标注占满设备剩余空间的分区。包以 <c>0xFFFFFFFF</c> 声明它，因此无法仅凭包内数据推导偏移或大小，该单元格
    /// 如实说明而不是显示一个错误数字。</para>
    /// </summary>
    /// <returns>The localized label. <para>本地化标签。</para></returns>
    private string FormatUnisocRemainingSpace() =>
        _localizer.GetString("Flash_UnisocRemainingSpace");

    /// <summary>
    /// Formats the scheme <c>Block/Base</c> the way the official product list shows it, as a hexadecimal literal.
    /// <para>按官方产品列表的呈现方式把 scheme 的 <c>Block/Base</c> 格式化为十六进制字面量。</para>
    /// </summary>
    /// <param name="offset">The declared base. <para>声明的基址。</para></param>
    /// <returns>The hexadecimal text. <para>十六进制文本。</returns>
    private static string FormatUnisocBase(long offset) =>
        string.Format(CultureInfo.InvariantCulture, "0x{0:X}", offset);

    /// <summary>
    /// Describes the size cell of one PAC row: the payload length when the entry carries one, otherwise the
    /// <c>Block/Size</c> the scheme declares, so an erase operation still shows the extent it addresses while an
    /// undeclared (<c>0x0</c>) extent stays blank exactly as the official list renders it.
    /// <para>描述一个 PAC 行的大小列：条目携带负载时用负载长度，否则用 scheme 声明的 <c>Block/Size</c>，使擦除操作
    /// 仍显示其寻址范围，而未声明（<c>0x0</c>）的范围保持空白，与官方列表的渲染一致。</para>
    /// </summary>
    /// <param name="image">The plan entry. <para>计划条目。</param>
    /// <returns>The size text, or an empty string when no extent is known. <para>大小文本；范围未知时为空字符串。</returns>
    private static string DescribeUnisocEntrySize(UnisocFlashImage image)
    {
        // The official list fills this cell from Block/Size and blanks a declared 0x0, which most images declare. That
        // value is the extent the scheme addresses, so it wins when present; otherwise the payload length is the only
        // real extent and is shown instead, and an operation with neither stays blank.
        // 官方列表用 Block/Size 填该单元格并把声明的 0x0 置空，而多数镜像声明的正是 0x0。该值是 scheme 寻址的范围，
        // 有值时优先；否则负载长度是唯一真实范围并据此显示，两者都没有的操作保持空白。
        if (image.DeclaredSize > 0)
        {
            return FormatUnisocBase(image.DeclaredSize);
        }

        return image.EraseOnly ? string.Empty : FormatByteSize(image.Length);
    }

    /// <summary>
    /// Renders the packaged file a row would write, prefixed so the cell reads as a package member rather than an
    /// absolute path the user could try to open. <para>渲染该行将写入的包内文件，并加前缀使该单元格读作包成员而不是
    /// 用户可以尝试打开的绝对路径。</para>
    /// </summary>
    /// <param name="fileName">The packaged file name. <para>包内文件名。</para></param>
    /// <returns>The prefixed label. <para>带前缀的标签。</returns>
    private static string FormatUnisocPackagedFileName(string fileName) =>
        string.Format(CultureInfo.InvariantCulture, "PAC:{0}", fileName);

    /// <summary>
    /// Runs one PAC package against the endpoint after the typed confirmation: the plan's loaders bring the link to
    /// FDL2, the selected erase mode is applied and the partitions ticked in the main grid (every matching partition
    /// when no row is ticked) are written, then the completion action runs. A full erase additionally requires the
    /// typed wipe confirmation.
    /// <para>在输入确认短语后对端点运行一个 PAC 包：计划的加载器把链路提升到 FDL2，应用所选擦除模式并写入主网格
    /// 勾选的分区（未勾选时写全部匹配分区），最后执行收尾动作。整片擦除还要求输入擦除确认短语。</para>
    /// </summary>
    internal async Task RunUnisocFlashAsync()
    {
        if (_busy || _confirming)
        {
            return;
        }

        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Unisoc } device)
        {
            return;
        }

        // Freeze the exact target before either dialog: the dialogs and the flash must name and act on the same
        // device even if the selection changes while a confirmation is open.
        // 在任一对话框之前冻结精确目标：即使确认期间选择发生变化，对话框与刷写也必须指向同一台设备。
        var frozenDeviceKey = device.Key;
        var frozenIdentity = $"{device.DisplayName} · [{frozenDeviceKey}]";

        if (TopLevel.GetTopLevel(Surface) is not Window owner)
        {
            return;
        }

        if (!_unisocPackage || !string.Equals(PackagePath, _loadedPath, StringComparison.Ordinal))
        {
            AppendLog(_localizer.GetString("Flash_PackageChanged"));
            return;
        }

        var language = _localizer;
        var mode = UnisocFlashModeIndex switch
        {
            0 => UnisocFlashMode.KeepData,
            2 => UnisocFlashMode.FullErase,
            _ => UnisocFlashMode.PartitionErase,
        };
        if (mode == UnisocFlashMode.FullErase)
        {
            bool wipeConfirmed;
            _confirming = true;
            try
            {
                wipeConfirmed = await DialogService.ConfirmDangerousAsync(owner, new DangerousConfirmRequest
                {
                    TabTitle = language.GetString("Flash_Unisoc"),
                    Title = language.GetString("Flash_UnisocFlash"),
                    Detail = language.GetString("Flash_UnisocWipeConfirmDetail"),
                    ConfirmWord = UnisocFlashOptions.WipeConfirmationPhrase,
                    DeviceIdentifier = frozenIdentity
                }).ConfigureAwait(true);
            }
            finally
            {
                _confirming = false;
            }

            if (!wipeConfirmed) return;
        }

        var options = new UnisocFlashOptions
        {
            Mode = mode,
            FlashConfirmation = UnisocFlashOptions.ConfirmationPhrase,
            WipeConfirmation = mode == UnisocFlashMode.FullErase ? UnisocFlashOptions.WipeConfirmationPhrase : null,
            Completion = UnisocPostActionIndex == 1 ? UnisocCompletionMode.PowerOff : UnisocCompletionMode.Reboot,
        };
        bool confirmed;
        _confirming = true;
        try
        {
            confirmed = await DialogService.ConfirmDangerousAsync(owner, new DangerousConfirmRequest
            {
                TabTitle = language.GetString("Flash_Unisoc"),
                Title = language.GetString("Flash_UnisocFlash"),
                Detail = language.GetString("Flash_UnisocFlashConfirmDetail"),
                ConfirmWord = UnisocFlashOptions.ConfirmationPhrase,
                DeviceIdentifier = frozenIdentity
            }).ConfigureAwait(true);
        }
        finally
        {
            _confirming = false;
        }

        if (!confirmed) return;

        if (SelectedDevice is not { Key: var currentKey } ||
            !string.Equals(currentKey, frozenDeviceKey, StringComparison.Ordinal))
        {
            AppendLog(_localizer.GetString("Flash_PackageChanged"));
            return;
        }

        var selected = Partitions
            .Where(row => row.IsSelected && row.Name.Length > 0 && !row.IsScriptStep)
            .Select(row => row.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // A row the user pointed at an external file writes that file instead of the packaged payload; every other row
        // keeps streaming straight out of the package. The dictionary is keyed by plan entry name, which is also the
        // row name the flasher matches on.
        // 用户指向外部文件的行写入该文件，而不是包内负载；其余行仍直接从包流式读取。字典以计划条目名为键，也正是
        // 烧写器匹配所用的行名。
        Dictionary<string, string> external = new(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Partitions)
        {
            if (!row.IsSelected || row.Name.Length == 0 || row.IsScriptStep || row.FilePath.Length == 0)
            {
                continue;
            }

            if (!_unisocExternalRows.Contains(row))
            {
                continue;
            }

            external[row.Name] = row.FilePath;
        }
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        SetBusy(true);
        _deviceViewModel?.BeginSelectionHold();
        try
        {
            var progress = new Progress<UnisocFlashProgress>(report =>
            {
                UnisocStatusText = FormatUnisocFlashProgress(report, language);
                if (report.Total > 0)
                {
                    ReportBusyTransfer(report.BytesCompleted, report.Total);
                }
            });
            await App.DeviceMonitor.FlashUnisocAsync(device, PackagePath, options, selected, progress,
                cancellation.Token, external).ConfigureAwait(true);
            UnisocStatusText = language.GetString("Flash_UnisocFlashDone");
            AppendLog(language.GetString("Flash_UnisocFlashDone"));
            await App.DeviceMonitor.ForceRefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            AppendLog(language.GetString("Flash_Canceled"));
        }
        catch (Exception exception)
        {
            AppendError(exception);
        }
        finally
        {
            SetBusy(false);
            _flashCancellation = null;
            _unisocStatusCache = null;
            RefreshUnisocDeviceStatus();
            UpdateEdlButton();
        }
    }


    /// <summary>
    /// Erases the partitions ticked in the main grid on the endpoint, without writing an image. The typed flash
    /// confirmation is required because erasing destroys the stored contents of every selected partition. Loader rows
    /// and rows without a partition name are skipped, and the plan's loaders bring the link to FDL2 first.
    /// <para>在端点上擦除主网格中勾选的分区而不写入镜像。由于擦除会销毁每个所选分区的已存内容，必须先输入刷写确认短语。
    /// 加载器行与没有分区名的行被跳过，且先由计划的加载器把链路提升到 FDL2。</para>
    /// </summary>
    internal async Task RunUnisocEraseAsync()
    {
        if (_busy || _confirming)
        {
            return;
        }

        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Unisoc } device)
        {
            AppendLog(_localizer.GetString("Flash_ProtocolMismatch"));
            return;
        }

        if (string.IsNullOrWhiteSpace(PackagePath) || !File.Exists(PackagePath))
        {
            AppendLog(_localizer.GetString("Flash_UnisocNeedPackage"));
            return;
        }

        var selected = Partitions
            .Where(row => row.IsSelected && !row.IsScriptStep && row.Name.Length > 0)
            .Select(row => row.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selected.Length == 0)
        {
            AppendLog(_localizer.GetString("Flash_NoPartitionSelected"));
            return;
        }

        var language = _localizer;
        if (TopLevel.GetTopLevel(Surface) is not Window owner)
        {
            return;
        }

        bool confirmed;
        _confirming = true;
        try
        {
            confirmed = await DialogService.ConfirmDangerousAsync(owner, new DangerousConfirmRequest
            {
                TabTitle = language.GetString("Flash_Unisoc"),
                Title = language.GetString("Flash_UnisocEraseConfirmTitle"),
                Detail = string.Format(
                    CultureInfo.CurrentCulture,
                    language.GetString("Flash_UnisocEraseConfirmDetail"),
                    string.Join(", ", selected)),
                ConfirmWord = UnisocFlashOptions.ConfirmationPhrase,
                DeviceIdentifier = _deviceViewModel?.SelectedDevice?.ConfirmationIdentity
            }).ConfigureAwait(true);
        }
        finally
        {
            _confirming = false;
        }

        if (!confirmed)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        SetBusy(true);
        try
        {
            var progress = new Progress<UnisocFlashProgress>(report =>
            {
                UnisocStatusText = FormatUnisocFlashProgress(report, language);
                if (report.Total > 0)
                {
                    ReportBusyTransfer(report.BytesCompleted, report.Total);
                }
            });
            await App.DeviceMonitor.EraseUnisocPartitionsAsync(device, PackagePath, selected,
                UnisocFlashOptions.ConfirmationPhrase, progress, cancellation.Token).ConfigureAwait(true);
            UnisocStatusText = language.GetString("Flash_UnisocFlashDone");
            AppendLog(language.GetString("Flash_UnisocFlashDone"));
            await App.DeviceMonitor.ForceRefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            AppendLog(language.GetString("Flash_Canceled"));
        }
        catch (Exception exception)
        {
            AppendError(exception);
        }
        finally
        {
            SetBusy(false);
            _flashCancellation = null;
            _unisocStatusCache = null;
            RefreshUnisocDeviceStatus();
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Reads the selected endpoint's loader-stage identity for the shared read-info action: the answers fill the
    /// device status table, the Unisoc status line and the stage hint, and the stage refreshes the device badge.
    /// <para>为通用读取信息动作读取所选端点的加载阶段身份：应答填入设备状态表、紫光展锐状态行与阶段提示，阶段同时
    /// 刷新设备管理器徽章。</para>
    /// </summary>
    internal async Task RefreshUnisocDeviceTableAsync(DetectedDevice device)
    {
        var language = _localizer;
        var epoch = BeginDeviceStatusRefresh();
        try
        {
            using var timeout = new CancellationTokenSource(UnisocStatusTimeoutMs);
            var status = await Task.Run(() => App.DeviceMonitor.QueryUnisocStatusAsync(device, timeout.Token)
                .GetAwaiter().GetResult(), timeout.Token).ConfigureAwait(true);
            if (SelectedDevice?.Key != device.Key || epoch != _rkStatusRefreshEpoch)
            {
                return;
            }

            _unisocStatusCache = (device.Key, status);
            ReplaceDeviceStatus(device.Key, BuildUnisocStatusRows(status, language));
        }
        catch (OperationCanceledException)
        {
            ReplaceDeviceStatus(device.Key, []);
        }
        catch (Exception exception)
        {
            _log.Write(LogLevel.Debug, "FlashViewModel", $"Unisoc info read failed: {exception.Message}");
            ReplaceDeviceStatus(device.Key, []);
        }
    }

    /// <summary>
    /// Reads the selected endpoint's partition table into the main grid: the validated GPT carries byte extents,
    /// the native table only carries the device's own size values, so its rows stay display-only until a unit is
    /// confirmed.
    /// <para>把所选端点的分区表读入主网格：校验通过的 GPT 携带字节范围；原生表只携带设备自带的大小值，在单位确认前
    /// 其行保持仅展示。</para>
    /// </summary>
    internal async Task RunUnisocPartitionTableAsync(DetectedDevice device)
    {
        var language = _localizer;
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        SetBusy(true);
        try
        {
            var targets = await App.DeviceMonitor.ResolveUnisocPartitionsAsync(device, cancellation.Token)
                .ConfigureAwait(true);
            if (SelectedDevice?.Key != device.Key)
            {
                return;
            }

            var rows = targets.Select(target => new FlashPartitionEntry
            {
                IsScriptStep = false,
                Name = target.Name,
                Offset = target.Offset is { } offset ? FormatByteSize(offset) : string.Empty,
                Size = target.Length is { } length ? FormatByteSize(length)
                    : target.NativeSizeValue is { } raw ? $"{raw}" : string.Empty,
                AcceptsFile = false
            }).ToList();
            Partitions.Apply(0, rows);
            AppendLog($"{rows.Count} {language.GetString("Flash_PartitionsLoaded")}");
        }
        catch (OperationCanceledException)
        {
            AppendLog(language.GetString("Flash_Canceled"));
        }
        catch (Exception exception)
        {
            AppendError(exception);
        }
        finally
        {
            SetBusy(false);
            _flashCancellation = null;
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Reads the partitions ticked in the main grid back to a chosen folder: GPT-backed rows carry the byte range
    /// to pull, native-table rows without confirmed extents are skipped, and every request streams into a temporary
    /// sibling that is moved into place only when complete.
    /// <para>把主网格勾选的分区回读到所选目录：GPT 行携带要拉取的字节范围；没有确认范围的原生表行被跳过；每个请求
    /// 流式写入同目录临时文件，完成后再移动到位。</para>
    /// </summary>
    internal async Task RunUnisocReadbackAsync()
    {
        if (_busy || _confirming ||
            SelectedDevice is not { Protocol: DeviceProtocolKind.Unisoc } device ||
            TopLevel.GetTopLevel(Surface) is not Window owner)
        {
            return;
        }

        var selected = Partitions.Where(row => row.IsSelected && row.Name.Length > 0 && !row.IsScriptStep).ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var language = _localizer;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = language.GetString("Flash_ReadbackSelected"),
            AllowMultiple = false
        }).ConfigureAwait(true);
        if (_busy || _confirming || folders.Count != 1 ||
            folders[0].TryGetLocalPath() is not { } destination)
        {
            return;
        }

        Dictionary<string, (long Offset, long Bytes)> mapping;
        try
        {
            mapping = await ResolveUnisocPartitionMapAsync(device).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppendError(exception);
            return;
        }

        List<UnisocPartitionReadRequest> requests = [];
        List<FlashPartitionEntry> mapped = [];
        foreach (var row in selected)
        {
            if (!mapping.TryGetValue(row.Name, out var range))
            {
                row.State = FlashRowState.Skipped;
                continue;
            }

            mapped.Add(row);
            requests.Add(new UnisocPartitionReadRequest(row.Name, range.Offset, range.Bytes,
                Path.Combine(destination, $"{row.Name}.img")));
        }

        if (requests.Count == 0)
        {
            AppendLog(language.GetString("Flash_UnisocReadbackNoMapping"));
            return;
        }

        SetRowsQueued(mapped);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        SetBusy(true);
        try
        {
            var progress = new Progress<UnisocFlashProgress>(report =>
            {
                var row = mapped.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, report.Target, StringComparison.OrdinalIgnoreCase));
                if (row is null || report.Total <= 0)
                {
                    return;
                }

                row.TransferBytes = report.BytesCompleted;
                var percent = (int)Math.Clamp(report.BytesCompleted * 100d / report.Total, 0d, 100d);
                if (percent != row.Percent)
                {
                    row.Percent = percent;
                }
            });
            await App.DeviceMonitor.ReadUnisocPartitionsAsync(device, requests, progress,
                cancellation.Token).ConfigureAwait(true);
            foreach (var request in requests)
            {
                AppendLog(string.Format(CultureInfo.InvariantCulture,
                    language.GetString("Flash_UnisocReadbackDone"), request.Name, request.DestinationPath));
            }
        }
        catch (OperationCanceledException)
        {
            MarkUnfinishedSkipped();
            AppendLog(language.GetString("Flash_Canceled"));
        }
        catch (Exception exception)
        {
            AppendError(exception);
            foreach (var row in mapped.Where(row => row.State == FlashRowState.Running))
            {
                SetRowFinished(row, success: false);
            }
        }
        finally
        {
            MarkUnfinishedSkipped();
            UpdateOverall();
            SetBusy(false);
            _flashCancellation = null;
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Resolves the partition-to-byte-range mapping the read-back uses: the validated GPT when the device carries
    /// one, otherwise nothing — native tables report no offsets, so they cannot back a range read.
    /// <para>解析回读使用的分区到字节范围映射：设备携带校验通过的 GPT 时用它，否则没有映射——原生表不报告偏移，
    /// 无法支撑范围读取。</para>
    /// </summary>
    private async Task<Dictionary<string, (long Offset, long Bytes)>> ResolveUnisocPartitionMapAsync(
        DetectedDevice device)
    {
        var targets = await App.DeviceMonitor.ResolveUnisocPartitionsAsync(device).ConfigureAwait(true);
        return targets
            .Where(target => target.Offset is { } && target.Length is { } length && length > 0)
            .GroupBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => (group.First().Offset!.Value, group.First().Length!.Value),
                StringComparer.OrdinalIgnoreCase);
    }

    private void ApplyUnisocStatus(UnisocDeviceStatus status)
    {
        var language = _localizer;
        UnisocStatusText = BuildUnisocStatusLine(status, language);
        UnisocModeHint = language.GetString(_unisocIsFdl2 ? "Flash_UnisocHintFdl2" : "Flash_UnisocHint");
        _unisocIsFdl2 = status.Stage == UnisocBootStage.Fdl2;
        ReplaceDeviceStatus(SelectedDevice?.Key, BuildUnisocStatusRows(status, language));
        if (_deviceViewModel?.SelectedDevice is { } info)
        {
            info.UnisocStage = status.Stage switch
            {
                UnisocBootStage.BootRom => "BootRom",
                UnisocBootStage.Fdl1 => "FDL1",
                UnisocBootStage.Fdl2 => "FDL2",
                _ => status.Stage.ToString(),
            };
        }

        RefreshDeviceUiGate();
    }

    private static string BuildUnisocStatusLine(UnisocDeviceStatus status, ILocalizer language)
    {
        var stage = status.Stage switch
        {
            UnisocBootStage.BootRom => language.GetString("Flash_UnisocStageBootRom"),
            UnisocBootStage.Fdl1 => language.GetString("Flash_UnisocStageFdl1"),
            UnisocBootStage.Fdl2 => language.GetString("Flash_UnisocStageFdl2"),
            _ => status.Stage.ToString(),
        };
        List<string> parts = [stage];
        if (status.Version is { } version && version.Length > 0)
        {
            parts.Add(status.VersionText);
        }

        if (status.SecureBoot && status.Stage == UnisocBootStage.BootRom)
        {
            parts.Add(language.GetString("Flash_UnisocSecure"));
        }

        return string.Join(" · ", parts);
    }

    private static List<FlashDeviceStatusEntry> BuildUnisocStatusRows(UnisocDeviceStatus status, ILocalizer language)
    {
        List<FlashDeviceStatusEntry> rows =
        [
            new()
            {
                Key = FlashDeviceStatusRowKeys.UnisocInfo,
                Name = language.GetString("Flash_UnisocInfo_Stage"),
                Value = status.Stage.ToString()
            },
            new()
            {
                Key = FlashDeviceStatusRowKeys.UnisocInfo,
                Name = language.GetString("Flash_UnisocInfo_Version"),
                Value = status.VersionText
            },
        ];
        if (status.Stage == UnisocBootStage.BootRom)
        {
            rows.Add(new FlashDeviceStatusEntry
            {
                Key = FlashDeviceStatusRowKeys.UnisocInfo,
                Name = language.GetString("Flash_UnisocInfo_SecureBoot"),
                Value = language.GetString(status.SecureBoot ? "Flash_UnisocSecure" : "Flash_UnisocOpen")
            });
        }

        return rows;
    }

    private static string FormatUnisocFlashProgress(UnisocFlashProgress report, ILocalizer language)
    {
        var phase = report.Phase switch
        {
            UnisocFlashPhase.Connect => language.GetString("Flash_UnisocPhaseConnect"),
            UnisocFlashPhase.Erase => language.GetString("Flash_UnisocPhaseErase"),
            UnisocFlashPhase.Flash => language.GetString("Flash_UnisocPhaseFlash"),
            UnisocFlashPhase.Skip => language.GetString("Flash_UnisocPhaseSkip"),
            UnisocFlashPhase.Done => language.GetString("Flash_UnisocPhaseDone"),
            _ => language.GetString("Flash_UnisocPhaseFlash")
        };
        var target = report.Target ?? string.Empty;
        if (report.Total > 0)
        {
            return string.Format(language.GetString("Flash_UnisocFlashProgress"), phase, target,
                report.BytesCompleted * 100 / report.Total);
        }

        return string.Format(language.GetString("Flash_UnisocFlashProgressNoPercent"), phase, target);
    }

}