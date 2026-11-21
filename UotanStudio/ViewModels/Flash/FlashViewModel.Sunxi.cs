using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Comm.Protocols.Sunxi;
using UotanStudio.Firmware.VendorFirmware.Allwinner.CardImage;
using UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Ui;
using UotanStudio.UiAbstractions;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// Allwinner page actions: status readout, the FEL memory tools, the FEL-to-FES service switch driven by a
/// first-stage loader and a service payload, and the package-driven FES flash and partition read-back the
/// shared action buttons run against an IMAGEWTY package loaded into the main grid.
/// <para>全志页面动作：状态读取、FEL 内存工具、由一级加载器与服务负载驱动的 FEL 到 FES 切换，以及通用动作按钮
/// 对已载入主网格的 IMAGEWTY 包执行的 FES 烧录与分区回读。</para>
/// </summary>
public sealed partial class FlashViewModel
{
    /// <summary>Cooperative bound for one Sunxi status query, matching the monitor's own bound.
    /// <para>单次全志状态查询的协作式时限，与监视服务自身的时限一致。</para></summary>
    private const int SunxiStatusTimeoutMs = 5000;

    /// <summary>Largest payload the page hands to one FEL memory operation; larger files stay out of the page.
    /// <para>页面交给单次 FEL 内存操作的最大负载；更大的文件不允许进入页面。</para></summary>
    private const long SunxiMemoryLimitBytes = 64 * 1024 * 1024;

    /// <summary>Byte size of one Sunxi MBR sector; the plan's first sectors count in these units.
    /// <para>Sunxi MBR 单扇区的字节大小；计划中的首扇区以该单位计数。</para></summary>
    private const long SunxiSectorSize = 512;

    /// <summary>First-stage loader image chosen for the service switch; empty while none is chosen.
    /// <para>为服务切换所选的一级加载器镜像；未选择时为空。</para></summary>
    [ObservableProperty]
    private string _sunxiFes1Path = string.Empty;

    /// <summary>Service payload chosen for the service switch; empty while none is chosen.
    /// <para>为服务切换所选的服务负载；未选择时为空。</para></summary>
    [ObservableProperty]
    private string _sunxiUbootPath = string.Empty;

    /// <summary>Status line of the Sunxi section; the identity readout and every operation write it.
    /// <para>全志分类的状态行；身份读取与每个操作都会写入。</para></summary>
    [ObservableProperty]
    private string _sunxiStatusText = string.Empty;

    /// <summary>Mode-aware hint under the status line; it says what the current FEL or FES mode offers and what
    /// still blocks the storage actions. <para>状态行下方随模式变化的提示行；说明当前 FEL 或 FES 模式能做什么，
    /// 以及存储动作还差什么。</para></summary>
    [ObservableProperty]
    private string _sunxiModeHint = string.Empty;

    /// <summary>Memory address used by the read and write actions. <para>读取与写入动作使用的内存地址。</para></summary>
    [ObservableProperty]
    private string _sunxiAddress = "0x40000000";

    /// <summary>Byte count used by the read action. <para>读取动作使用的字节数。</para></summary>
    [ObservableProperty]
    private string _sunxiLength = "256";

    /// <summary>Entry address used by the execute action. <para>执行动作使用的入口地址。</para></summary>
    [ObservableProperty]
    private string _sunxiExecuteAddress = "0x40000000";

    /// <summary>Device tree uploaded into the service slot before U-Boot; empty when the image embeds one.
    /// <para>在执行 U-Boot 前上传到服务槽位的设备树；镜像自带时为空。</para></summary>
    [ObservableProperty]
    private string _sunxiDtbPath = string.Empty;

    /// <summary>Compiled system configuration uploaded into its service slot; empty skips it.
    /// <para>上传到服务槽位的编译后系统配置；为空时跳过。</para></summary>
    [ObservableProperty]
    private string _sunxiSysConfigPath = string.Empty;

    /// <summary>Board configuration uploaded into its service slot; empty skips it.
    /// <para>上传到服务槽位的板级配置；为空时跳过。</para></summary>
    [ObservableProperty]
    private string _sunxiBoardConfigPath = string.Empty;

    /// <summary>Whether the loaded package is an Allwinner IMAGEWTY image whose plan owns the grid rows; the
    /// shared flash action reads the package from the page-wide package path.
    /// <para>已加载的包是否为其计划拥有网格行的 IMAGEWTY 全志镜像；通用烧录动作从页面级包路径读取该包。</para></summary>
    private bool _sunxiPackage;

    /// <summary>Status the selected endpoint answered most recently, kept per device key so re-selecting the same
    /// endpoint republishes it without another handshake; the refresh button clears it.
    /// <para>所选端点最近一次应答的状态，按设备键保存，使重新选中同一端点时直接重发而无需再次握手；刷新按钮会将其清除。</para></summary>
    private (string Key, SunxiDeviceStatus Status)? _sunxiStatusCache;

    /// <summary>Selected erase mode index: full erase, partition erase, no erase or keep user data.
    /// <para>所选擦除模式索引：整片擦除、分区擦除、不擦除或保留用户数据。</para></summary>
    [ObservableProperty]
    private int _sunxiFlashModeIndex;

    /// <summary>Selected post-flash action index: reboot or power off.
    /// <para>所选烧录后动作索引：重启或关机。</para></summary>
    [ObservableProperty]
    private int _sunxiPostActionIndex;

    private bool _sunxiIsFes;

    partial void OnSunxiFes1PathChanged(string value) => UpdateSunxiBootButton();

    partial void OnSunxiUbootPathChanged(string value) => UpdateSunxiBootButton();

    /// <summary>Whether both service images are chosen. <para>两个服务镜像是否都已选择。</para></summary>
    internal bool HasSunxiServiceImages => SunxiFes1Path.Length > 0 && SunxiUbootPath.Length > 0;

    private void UpdateSunxiBootButton()
    {
        if (_surface is not null)
        {
            SetControlEnabled("SunxiBootFesButton", HasSunxiServiceImages);
        }
    }

    /// <summary>
    /// Allwinner section: the pane and every button answer only on an Allwinner endpoint, so each carries that condition
    /// instead of a page-wide lookup; the header stays present for every endpoint.
    /// <para>全志分类：面板与每个按钮只在全志端点上应答，因此各自持有该条件，而不是依赖页面级查表；表头对任何端点都保持呈现。</para>
    /// </summary>
    private void RegisterSunxiControlGates()
    {
        RegisterGate("SunxiExpander", FlashControlGate.SectionHeader("SunxiExpander"));
        RegisterGate("SunxiPane", FlashControlGate.Section("SunxiPane", FlashDeviceMode.Sunxi, busyDisabled: true));
        RegisterGate("SunxiRefreshButton", FlashControlGate.Sunxi("SunxiRefreshButton"));
        RegisterGate("SunxiChooseFes1Button", FlashControlGate.Sunxi("SunxiChooseFes1Button"));
        RegisterGate("SunxiChooseUbootButton", FlashControlGate.Sunxi("SunxiChooseUbootButton"));
        RegisterGate("SunxiBootFesButton", FlashControlGate.Sunxi("SunxiBootFesButton"));
        RegisterGate("SunxiReadMemoryButton", FlashControlGate.Sunxi("SunxiReadMemoryButton"));
        RegisterGate("SunxiWriteMemoryButton", FlashControlGate.Sunxi("SunxiWriteMemoryButton"));
        RegisterGate("SunxiExecuteButton", FlashControlGate.Sunxi("SunxiExecuteButton"));
        RegisterGate("SunxiChooseDtbButton", FlashControlGate.Sunxi("SunxiChooseDtbButton"));
        RegisterGate("SunxiChooseSysConfigButton", FlashControlGate.Sunxi("SunxiChooseSysConfigButton"));
        RegisterGate("SunxiChooseBoardConfigButton", FlashControlGate.Sunxi("SunxiChooseBoardConfigButton"));
    }

    /// <summary>
    /// Queries the selected endpoint once and publishes the identity, mode, mode hint and — in FES mode — the
    /// storage answers. The mode also updates the device badge and the device status table, because discovery
    /// cannot tell FEL from FES. A cached answer for the same device key republishes without another handshake;
    /// the refresh button forces a fresh query.
    /// <para>查询一次所选端点并发布身份、模式、模式提示，以及 FES 模式下的存储应答。模式同时更新设备徽章与设备状态表，
    /// 因为发现阶段无法区分 FEL 与 FES。同一设备键的缓存应答直接重发而无需再次握手；刷新按钮强制重新查询。</para>
    /// </summary>
    internal async void RefreshSunxiDeviceStatus()
    {
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Sunxi } device)
        {
            SunxiStatusText = string.Empty;
            SunxiModeHint = string.Empty;
            _sunxiIsFes = false;
            _sunxiStatusCache = null;
            RefreshDeviceUiGate();
            return;
        }

        if (_sunxiStatusCache is { } cached && string.Equals(cached.Key, device.Key, StringComparison.Ordinal))
        {
            ApplySunxiStatus(cached.Status);
            return;
        }

        var language = _localizer;
        var epoch = BeginDeviceStatusRefresh();
        try
        {
            using var timeout = new CancellationTokenSource(SunxiStatusTimeoutMs);
            var status = await Task.Run(() => App.DeviceMonitor.QuerySunxiStatusAsync(device, timeout.Token)
                .GetAwaiter().GetResult(), timeout.Token).ConfigureAwait(true);
            if (SelectedDevice?.Key != device.Key || epoch != _rkStatusRefreshEpoch)
            {
                return;
            }

            _sunxiStatusCache = (device.Key, status);
            ApplySunxiStatus(status);
        }
        catch (OperationCanceledException)
        {
            SunxiStatusText = language.GetString("Flash_SunxiStatusTimeout");
            SunxiModeHint = language.GetString("Flash_SunxiHintUnknown");
            _sunxiIsFes = false;
            _sunxiStatusCache = null;
            RefreshDeviceUiGate();
        }
        catch (Exception exception)
        {
            SunxiStatusText = language.GetString("Flash_SunxiStatusFailed") + " " + exception.Message;
            SunxiModeHint = language.GetString("Flash_SunxiHintUnknown");
            _sunxiIsFes = false;
            _sunxiStatusCache = null;
            RefreshDeviceUiGate();
        }
    }

    /// <summary>Re-reads the selected endpoint and refreshes its device badge; the click always forces a fresh
    /// handshake instead of replaying the cached one.
    /// <para>重新读取所选端点并刷新其设备徽章；点击总是强制重新握手而不是重放缓存。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件来源。</para></param>
    /// <param name="e">Event arguments. <para>事件参数。</para></param>
    internal void OnSunxiRefreshClick(object? sender, RoutedEventArgs e)
    {
        _sunxiStatusCache = null;
        RefreshSunxiDeviceStatus();
    }

    /// <summary>
    /// Chooses the first-stage loader image and enables the switch only once both images are present.
    /// <para>选择一级加载器镜像，并只在两个镜像都就绪后启用切换。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件来源。</para></param>
    /// <param name="e">Event arguments. <para>事件参数。</para></param>
    internal async void OnSunxiChooseFes1Click(object? sender, RoutedEventArgs e)
    {
        SunxiFes1Path = await PickSunxiImageAsync("Flash_SunxiChooseFes1").ConfigureAwait(true);
    }

    /// <summary>
    /// Chooses the service payload and enables the switch only once both images are present.
    /// <para>选择服务负载，并只在两个镜像都就绪后启用切换。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件来源。</para></param>
    /// <param name="e">Event arguments. <para>事件参数。</para></param>
    internal async void OnSunxiChooseUbootClick(object? sender, RoutedEventArgs e)
    {
        SunxiUbootPath = await PickSunxiImageAsync("Flash_SunxiChooseUboot").ConfigureAwait(true);
    }

    /// <summary>
    /// Runs the FEL-to-FES switch after the typed confirmation: clears the DRAM parameter area, runs the first-stage
    /// loader, uploads the service payload and waits for the re-enumerated device to answer the FES handshake.
    /// <para>在输入确认短语后执行 FEL 到 FES 切换：清空 DRAM 参数区、运行一级加载器、上传服务负载，并等待重枚举
    /// 设备应答 FES 握手。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件来源。</para></param>
    /// <param name="e">Event arguments. <para>事件参数。</para></param>
    internal async void OnSunxiBootFesClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Sunxi } device) return;
        if (TopLevel.GetTopLevel(Surface) is not Window owner) return;
        if (!HasSunxiServiceImages) return;

        var language = _localizer;
        bool confirmed;
        _confirming = true;
        try
        {
            confirmed = await DialogService.ConfirmDangerousAsync(owner, new DangerousConfirmRequest
            {
                TabTitle = language.GetString("Flash_Sunxi"),
                Title = language.GetString("Flash_SunxiBootFes"),
                Detail = language.GetString("Flash_SunxiBootConfirmDetail"),
                ConfirmWord = "FES",
                DeviceIdentifier = _deviceViewModel?.SelectedDevice?.ConfirmationIdentity
            }).ConfigureAwait(true);
        }
        finally
        {
            _confirming = false;
        }

        if (!confirmed) return;

        byte[] fes1;
        SunxiServiceImages images;
        try
        {
            fes1 = await File.ReadAllBytesAsync(SunxiFes1Path).ConfigureAwait(true);
            images = new SunxiServiceImages(
                await File.ReadAllBytesAsync(SunxiUbootPath).ConfigureAwait(true),
                await ReadOptionalSunxiFileAsync(SunxiDtbPath).ConfigureAwait(true),
                await ReadOptionalSunxiFileAsync(SunxiSysConfigPath).ConfigureAwait(true),
                await ReadOptionalSunxiFileAsync(SunxiBoardConfigPath).ConfigureAwait(true));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppendError(exception);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        SetBusy(true);
        _deviceViewModel?.BeginSelectionHold();
        try
        {
            var progress = new Progress<string>(phase => SunxiStatusText = phase switch
            {
                "fes1" => language.GetString("Flash_SunxiPhaseFes1"),
                "service" => language.GetString("Flash_SunxiPhaseService"),
                _ => language.GetString("Flash_SunxiPhaseStatus"),
            });
            var status = await App.DeviceMonitor.RunSunxiBootServiceAsync(device, fes1, images, progress,
                cancellation.Token).ConfigureAwait(true);
            _sunxiStatusCache = (device.Key, status);
            ApplySunxiStatus(status);
            AppendLog(language.GetString("Flash_SunxiBootDone"));
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
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Reports whether the file is an Allwinner image the Sunxi plan can drive, so the shared package loader hands
    /// it to that plan instead of the single-image fallback. Both shapes count: a plaintext <c>IMAGEWTY</c> vendor
    /// package and an SD-card image, which the format registry recognizes from its MBR, GPT and <c>eGON.BT0</c>
    /// markers. The decision comes from the registered handlers rather than from a bare-magic test, so a new
    /// Allwinner container shape only has to be registered once.
    /// <para>判断文件是否为全志计划可驱动的全志镜像，使通用包加载器把它交给该计划而不是单镜像回退。两种形态都算：
    /// 明文 <c>IMAGEWTY</c> 厂商包与 SD 卡镜像，后者由格式注册表依据 MBR、GPT 与 <c>eGON.BT0</c> 标记识别。
    /// 判定来自已注册处理器而非裸魔数判断，因此新增一种全志容器形态只需注册一次。</para>
    /// </summary>
    /// <param name="path">Candidate image. <para>候选镜像。</para></param>
    /// <param name="cancellationToken">Cancels the header read. <para>取消头部读取。</para></param>
    /// <returns><see langword="true"/> when the registry routes the file to either Allwinner handler.
    /// <para>注册表把该文件路由到任一全志处理器时为 <see langword="true"/>。</para></returns>
    private static async Task<bool> IsSunxiImageFileAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var read = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
            if (read == header.Length && AllwinnerImageWtyReader.HasPlainMagic(header))
            {
                return true;
            }
        }

        return await Task.Run(() =>
        {
            Firmware.Application.Formats.FirmwareFormatRegistry.RegisterAll();
            return Firmware.Core.FormatDetector.Detect(path) is
                Firmware.VendorFirmware.Allwinner.CardImage.SunxiCardImageFormatHandler;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads an IMAGEWTY package into the main grid: the plan's table, partition payloads and boot variants become
    /// rows — partitions stay tickable for a partial flash, the table and boot variants stay display-only script
    /// rows — and the shared flash action later reads the package from the shared package path.
    /// <para>把 IMAGEWTY 包装进主网格：计划的表、分区负载与引导变体成为行——分区保持可勾选以支持部分刷入，表与
    /// 引导变体保持仅展示的脚本行——通用烧录动作随后从页面级包路径读取该包。</para>
    /// </summary>
    private async Task ApplySunxiPackageAsync(string path, CancellationToken cancellation)
    {
        using var plan = await Task.Run(() => SunxiFlashPlan.Load(path, cancellation), cancellation)
            .ConfigureAwait(true);
        if (path != _loadedPath)
        {
            return;
        }

        _sunxiPackage = true;
        Partitions.Apply(0, BuildSunxiPlanRows(plan));
        AppendLog($"{plan.Steps.Count(step => step.Kind == SunxiFlashStepKind.Partition)} " +
            _localizer.GetString("Flash_ImagesLoaded"));
    }

    /// <summary>
    /// Builds one grid row per plan step: partition steps stay tickable for a partial flash, the table and boot
    /// variants become display-only script rows, and every row carries its offset and payload size.
    /// <para>按计划步骤逐条构建网格行：分区步骤保持可勾选以支持部分刷入，表与引导变体成为仅展示的脚本行，
    /// 每行都携带偏移与负载大小。</para>
    /// </summary>
    private static List<FlashPartitionEntry> BuildSunxiPlanRows(SunxiFlashPlan plan)
    {
        List<FlashPartitionEntry> rows = [];
        foreach (var step in plan.Steps)
        {
            rows.Add(new FlashPartitionEntry
            {
                IsScriptStep = step.Kind != SunxiFlashStepKind.Partition,
                Name = step.Name,
                Offset = FormatByteSize(step.FirstSector * SunxiSectorSize),
                Size = FormatByteSize(step.ByteCount),
                AcceptsFile = false
            });
        }

        return rows;
    }

    /// <summary>
    /// Runs one IMAGEWTY package against a FES endpoint after the typed confirmation: erase by the selected mode,
    /// write the table, the partitions ticked in the main grid (every partition when no row is ticked) and the
    /// boot images, and request the completion action. The package comes from the shared package path the grid
    /// rows were loaded from.
    /// <para>在输入确认短语后，用所选模式擦除，写入分区表、主网格勾选的分区（未勾选时写全部分区）与引导镜像，
    /// 并请求完成动作。包来自网格行加载时使用的页面级包路径。</para>
    /// </summary>
    internal async Task RunSunxiFlashAsync()
    {
        if (_busy || _confirming)
        {
            return;
        }

        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Sunxi } device)
        {
            return;
        }

        if (TopLevel.GetTopLevel(Surface) is not Window owner)
        {
            return;
        }

        if (!_sunxiPackage || !string.Equals(PackagePath, _loadedPath, StringComparison.Ordinal))
        {
            AppendLog(_localizer.GetString("Flash_PackageChanged"));
            return;
        }

        if (!_sunxiIsFes)
        {
            AppendLog(_localizer.GetString("Flash_SunxiFlashNeedsFes"));
            return;
        }

        var language = _localizer;
        var partitions = Partitions.Where(row => row.IsSelected && row.Name.Length > 0 && !row.IsScriptStep)
            .Select(row => row.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var options = BuildSunxiFlashOptions(partitions);
        bool confirmed;
        _confirming = true;
        try
        {
            confirmed = await DialogService.ConfirmDangerousAsync(owner, new DangerousConfirmRequest
            {
                TabTitle = language.GetString("Flash_Sunxi"),
                Title = language.GetString("Flash_SunxiFlash"),
                Detail = language.GetString("Flash_SunxiFlashConfirmDetail"),
                ConfirmWord = "FLASH",
                DeviceIdentifier = _deviceViewModel?.SelectedDevice?.ConfirmationIdentity
            }).ConfigureAwait(true);
        }
        finally
        {
            _confirming = false;
        }

        if (!confirmed) return;

        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        SetBusy(true);
        _deviceViewModel?.BeginSelectionHold();
        string? assembledImage = null;
        try
        {
            var progress = new Progress<SunxiFlashProgress>(report =>
            {
                SunxiStatusText = FormatSunxiFlashProgress(report, language);
                if (report is { BytesCompleted: { } done, BytesTotal: { } total })
                {
                    ReportBusyTransfer(done, total);
                }
            });

            // Vendor .fex directories cannot be flashed directly, so they are assembled into one SD-card image
            // first. A container or an existing card image is written as it is.
            // 厂商 .fex 目录无法直接烧录，因此先组装成一张 SD 卡镜像。容器或既有的卡镜像按原样写入。
            var imagePath = PackagePath;
            if (Directory.Exists(imagePath))
            {
                SunxiStatusText = language.GetString("Flash_SunxiAssembling");
                assembledImage = Path.Combine(Path.GetTempPath(),
                    $"uotan-sunxi-flash-{Guid.NewGuid():N}.img");
                var assemblyProgress = new Progress<SunxiCardAssemblyProgress>(report =>
                {
                    if (report is { BytesCompleted: { } done, BytesTotal: { } total } && total > 0)
                    {
                        ReportBusyTransfer(done, total);
                    }
                });
                var assemblyTarget = assembledImage;
                await Task.Run(() => SunxiCardAssembler.Assemble(PackagePath, assemblyTarget,
                    capacityBytes: null, progress: assemblyProgress, cancellation.Token),
                    cancellation.Token).ConfigureAwait(true);
                imagePath = assembledImage;
            }

            await App.DeviceMonitor.FlashSunxiAsync(device, imagePath, options, progress,
                cancellation.Token).ConfigureAwait(true);
            SunxiStatusText = language.GetString("Flash_SunxiFlashDone");
            AppendLog(language.GetString("Flash_SunxiFlashDone"));
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
            _sunxiStatusCache = null;
            DeleteAssembledImage(assembledImage);
            RefreshSunxiDeviceStatus();
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Removes the card image assembled for a directory flash, so a completed or failed run leaves nothing behind.
    /// <para>删除为目录烧录组装出的卡镜像，使完成或失败都不留残留文件。</para>
    /// </summary>
    /// <param name="path">Temporary image path, or <see langword="null"/> when nothing was assembled.
    /// <para>临时镜像路径；未组装任何内容时为 <see langword="null"/>。</para></param>
    private static void DeleteAssembledImage(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Reads the selected endpoint's identity for the shared read-info action: the answers fill the device status
    /// table, the Sunxi status line and the mode hint, and the mode refreshes the device manager badge.
    /// <para>为通用读取信息动作读取所选端点的身份：应答填入设备状态表、全志状态行与模式提示，模式同时刷新设备
    /// 管理器徽章。</para>
    /// </summary>
    internal async Task RefreshSunxiDeviceTableAsync(DetectedDevice device)
    {
        var language = _localizer;
        var epoch = BeginDeviceStatusRefresh();
        try
        {
            using var timeout = new CancellationTokenSource(SunxiStatusTimeoutMs);
            var status = await Task.Run(() => App.DeviceMonitor.QuerySunxiStatusAsync(device, timeout.Token)
                .GetAwaiter().GetResult(), timeout.Token).ConfigureAwait(true);
            if (SelectedDevice?.Key != device.Key || epoch != _rkStatusRefreshEpoch)
            {
                return;
            }

            _sunxiStatusCache = (device.Key, status);
            ApplySunxiStatus(status);
        }
        catch (OperationCanceledException)
        {
            PublishSunxiTableFailure(device.Key, language.GetString("Flash_SunxiStatusTimeout"));
        }
        catch (Exception exception)
        {
            PublishSunxiTableFailure(device.Key,
                language.GetString("Flash_SunxiStatusFailed") + " " + exception.Message);
        }
    }

    /// <summary>
    /// Reads the ticked partition rows back into a chosen directory: the sector mapping comes from the loaded
    /// IMAGEWTY package's plan when one is loaded, otherwise from the vendor MBR the FES endpoint reports; rows
    /// the mapping does not name are skipped with a log line.
    /// <para>把勾选的分区行回读到所选目录：已加载 IMAGEWTY 包时扇区映射来自包计划，否则来自 FES 端点上报的
    /// 厂商 MBR；映射没有点名的行经日志跳过。</para>
    /// </summary>
    private async Task RunSunxiReadbackAsync()
    {
        if (_busy || _confirming ||
            SelectedDevice is not { Protocol: DeviceProtocolKind.Sunxi } device ||
            TopLevel.GetTopLevel(Surface) is not Window owner)
        {
            return;
        }

        if (!_sunxiIsFes)
        {
            AppendLog(_localizer.GetString("Flash_SunxiReadbackNeedsFes"));
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

        Dictionary<string, (uint Sector, long Bytes)> mapping;
        try
        {
            mapping = await ResolveSunxiPartitionMapAsync(device).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppendError(exception);
            return;
        }

        List<SunxiPartitionReadRequest> requests = [];
        List<FlashPartitionEntry> mapped = [];
        foreach (var row in selected)
        {
            if (!mapping.TryGetValue(row.Name, out var range))
            {
                row.State = FlashRowState.Skipped;
                continue;
            }

            mapped.Add(row);
            requests.Add(new SunxiPartitionReadRequest(row.Name, range.Sector, range.Bytes,
                Path.Combine(destination, $"{row.Name}.img")));
        }

        if (requests.Count == 0)
        {
            AppendLog(language.GetString("Flash_SunxiReadbackNoMapping"));
            return;
        }

        SetRowsQueued(mapped);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        SetBusy(true);
        try
        {
            var progress = new Progress<SunxiFlashProgress>(report =>
            {
                var row = mapped.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, report.Target, StringComparison.OrdinalIgnoreCase));
                if (row is null || report is not { BytesCompleted: { } done, BytesTotal: { } total } || total <= 0)
                {
                    return;
                }

                row.TransferBytes = done;
                var percent = (int)Math.Clamp(done * 100d / total, 0d, 100d);
                if (percent != row.Percent)
                {
                    row.Percent = percent;
                }
            });
            await App.DeviceMonitor.ReadSunxiPartitionsAsync(device, requests, progress,
                cancellation.Token).ConfigureAwait(true);
            foreach (var request in requests)
            {
                AppendLog(string.Format(CultureInfo.InvariantCulture,
                    language.GetString("Flash_SunxiReadbackDone"), request.Name, request.DestinationPath));
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
    /// Resolves the partition-to-sector mapping the read-back and the table read use: the loaded package's plan
    /// when the grid rows came from it, otherwise the vendor MBR the FES endpoint reports.
    /// <para>解析回读与分区表读取使用的分区到扇区映射：网格行来自已加载包时用包计划，否则用 FES 端点上报的
    /// 厂商 MBR。</para>
    /// </summary>
    private async Task<Dictionary<string, (uint Sector, long Bytes)>> ResolveSunxiPartitionMapAsync(
        DetectedDevice device)
    {
        if (_sunxiPackage && string.Equals(PackagePath, _loadedPath, StringComparison.Ordinal))
        {
            using var plan = await Task.Run(() => SunxiFlashPlan.Load(PackagePath)).ConfigureAwait(true);
            return plan.Steps
                .Where(step => step.Kind == SunxiFlashStepKind.Partition)
                .GroupBy(step => step.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => (group.First().FirstSector, group.First().ByteCount),
                    StringComparer.OrdinalIgnoreCase);
        }

        var table = await App.DeviceMonitor.ReadSunxiMbrAsync(device).ConfigureAwait(true);
        var parsed = SunxiMbrTable.Parse(table);
        return parsed.Partitions
            .Where(partition => partition.SectorCount > 0)
            .GroupBy(partition => partition.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => ((uint)group.First().FirstSector,
                    (long)(group.First().SectorCount * (ulong)SunxiSectorSize)),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the selected endpoint's partition table into the main grid: the loaded IMAGEWTY package's plan
    /// describes what a partial flash writes, and a FES endpoint without a package contributes the vendor MBR
    /// read off its own storage.
    /// <para>把所选端点的分区表读入主网格：已加载的 IMAGEWTY 包计划描述部分刷入会写入的内容；没有包的 FES
    /// 端点贡献从其自身存储读出的厂商 MBR。</para>
    /// </summary>
    private async Task RunSunxiPartitionTableAsync(DetectedDevice device)
    {
        var language = _localizer;
        if (_sunxiPackage && string.Equals(PackagePath, _loadedPath, StringComparison.Ordinal))
        {
            using var cancellation = new CancellationTokenSource();
            _flashCancellation = cancellation;
            SetBusy(true);
            try
            {
                using var plan = await Task.Run(() => SunxiFlashPlan.Load(PackagePath, cancellation.Token),
                    cancellation.Token).ConfigureAwait(true);
                if (SelectedDevice?.Key != device.Key)
                {
                    return;
                }

                var planRows = BuildSunxiPlanRows(plan)
                    .Where(row => !row.IsScriptStep)
                    .ToList();
                Partitions.Apply(0, planRows);
                AppendLog($"{planRows.Count} {language.GetString("Flash_PartitionsLoaded")}");
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

            return;
        }

        if (!_sunxiIsFes)
        {
            AppendLog(language.GetString("Flash_SunxiTableNeedsSource"));
            return;
        }

        using var readCancellation = new CancellationTokenSource();
        _flashCancellation = readCancellation;
        SetBusy(true);
        try
        {
            var table = await App.DeviceMonitor.ReadSunxiMbrAsync(device, readCancellation.Token)
                .ConfigureAwait(true);
            var parsed = SunxiMbrTable.Parse(table);
            var rows = parsed.Partitions
                .Where(partition => partition.SectorCount > 0)
                .Select(partition => new FlashPartitionEntry
                {
                    Name = partition.Name,
                    Offset = FormatByteSize(partition.FirstSector * SunxiSectorSize),
                    Size = FormatByteSize((long)partition.SectorCount * SunxiSectorSize),
                    AcceptsFile = false
                })
                .ToList();
            if (rows.Count == 0)
            {
                AppendLog(language.GetString("Flash_PartitionTableUnavailable"));
                return;
            }

            ResetLoadedPackageState();
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
    /// Chooses the optional device tree uploaded into the service slot.
    /// <para>选择上传到服务槽位的可选设备树。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件来源。</para></param>
    /// <param name="e">Event arguments. <para>事件参数。</para></param>
    internal async void OnSunxiChooseDtbClick(object? sender, RoutedEventArgs e)
    {
        SunxiDtbPath = await PickSunxiImageAsync("Flash_SunxiChooseDtb").ConfigureAwait(true);
    }

    /// <summary>
    /// Chooses the optional compiled system configuration uploaded into the service slot.
    /// <para>选择上传到服务槽位的可选编译后系统配置。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件来源。</para></param>
    /// <param name="e">Event arguments. <para>事件参数。</para></param>
    internal async void OnSunxiChooseSysConfigClick(object? sender, RoutedEventArgs e)
    {
        SunxiSysConfigPath = await PickSunxiImageAsync("Flash_SunxiChooseSysConfig").ConfigureAwait(true);
    }

    /// <summary>
    /// Chooses the optional board configuration uploaded into the service slot.
    /// <para>选择上传到服务槽位的可选板级配置。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件来源。</para></param>
    /// <param name="e">Event arguments. <para>事件参数。</para></param>
    internal async void OnSunxiChooseBoardConfigClick(object? sender, RoutedEventArgs e)
    {
        SunxiBoardConfigPath = await PickSunxiImageAsync("Flash_SunxiChooseBoardConfig").ConfigureAwait(true);
    }

    private static async Task<byte[]> ReadOptionalSunxiFileAsync(string path) =>
        path.Length > 0 ? await File.ReadAllBytesAsync(path).ConfigureAwait(false) : [];

    private SunxiFlashOptions BuildSunxiFlashOptions(IReadOnlyList<string> partitions)
    {
        var mode = SunxiFlashModeIndex switch
        {
            1 => SunxiFlashMode.PartitionErase,
            2 => SunxiFlashMode.Partition,
            3 => SunxiFlashMode.KeepData,
            _ => SunxiFlashMode.FullErase
        };
        return new SunxiFlashOptions
        {
            FlashConfirmation = SunxiFlashOptions.RequiredConfirmation,
            Mode = mode,
            Partitions = partitions,
            CompletionMode = SunxiPostActionIndex == 1 ? SunxiToolMode.PowerOff : SunxiToolMode.Reboot
        };
    }

    private static string FormatSunxiFlashProgress(SunxiFlashProgress report, ILocalizer language)
    {
        var phase = report.Phase switch
        {
            "erase" => language.GetString("Flash_SunxiPhaseErase"),
            "download" => language.GetString("Flash_SunxiPhaseDownload"),
            "verify" => language.GetString("Flash_SunxiPhaseVerify"),
            "skip" => language.GetString("Flash_SunxiPhaseSkip"),
            "tool" => language.GetString("Flash_SunxiPhaseTool"),
            "done" => language.GetString("Flash_SunxiPhaseDone"),
            _ => language.GetString("Flash_SunxiPhaseDownload")
        };
        var target = report.Target ?? string.Empty;
        if (report.BytesCompleted is { } completed && report.BytesTotal is { } total && total > 0)
        {
            return string.Format(language.GetString("Flash_SunxiFlashProgress"), phase, target,
                completed * 100 / total);
        }

        return string.Format(language.GetString("Flash_SunxiFlashProgressNoPercent"), phase, target);
    }

    /// <summary>
    /// Reads the requested memory range and saves it to a chosen file through a temporary sibling and an atomic move.
    /// <para>读取请求的内存区间，并经同目录临时文件与原子移动保存到所选文件。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件来源。</para></param>
    /// <param name="e">Event arguments. <para>事件参数。</para></param>
    internal async void OnSunxiReadMemoryClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Sunxi } device) return;
        if (!TryReadSunxiRange(out var address, out var length)) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = _localizer.GetString("Flash_SunxiReadMemory"),
            DefaultExtension = "bin",
            SuggestedFileName = $"sunxi_{address:X8}_{length:X}.bin",
            FileTypeChoices = [FilePickerFileTypes.All]
        });
        if (file is null || file.TryGetLocalPath() is not { } path) return;

        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        SetBusy(true);
        try
        {
            var data = await App.DeviceMonitor.ReadSunxiMemoryAsync(device, address, length, cancellation.Token)
                .ConfigureAwait(true);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
            var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, data, cancellation.Token).ConfigureAwait(true);
                File.Move(temporary, path, overwrite: true);
            }
            catch
            {
                TryDeleteFile(temporary);
                throw;
            }

            AppendLog($"{_localizer.GetString("Flash_SunxiReadMemory")}: " +
                $"0x{address:X8}, {FormatByteSize(data.Length)} -> {Path.GetFileName(path)}");
        }
        catch (OperationCanceledException)
        {
            AppendLog(_localizer.GetString("Flash_Canceled"));
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
    /// Writes a chosen file into device memory at the address box.
    /// <para>把所选文件写入地址框指定的设备内存。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件来源。</para></param>
    /// <param name="e">Event arguments. <para>事件参数。</para></param>
    internal async void OnSunxiWriteMemoryClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Sunxi } device) return;
        if (!TryParseSunxiAddress(SunxiAddress, out var address))
        {
            AppendLog(_localizer.GetString("Flash_SunxiAddressInvalid"));
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString("Flash_SunxiWriteMemory"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (files.Count != 1 || files[0].TryGetLocalPath() is not { } path || !File.Exists(path)) return;
        var info = new FileInfo(path);
        if (info.Length <= 0)
        {
            return;
        }

        if (info.Length > SunxiMemoryLimitBytes)
        {
            AppendLog(string.Format(_localizer.GetString("Flash_SunxiMemoryTooLarge"),
                FormatByteSize(SunxiMemoryLimitBytes)));
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        SetBusy(true);
        try
        {
            var data = await File.ReadAllBytesAsync(path, cancellation.Token).ConfigureAwait(true);
            await App.DeviceMonitor.WriteSunxiMemoryAsync(device, address, data, cancellation.Token)
                .ConfigureAwait(true);
            AppendLog($"{_localizer.GetString("Flash_SunxiWriteMemory")}: {Path.GetFileName(path)} -> " +
                $"0x{address:X8}, {FormatByteSize(data.Length)}");
        }
        catch (OperationCanceledException)
        {
            AppendLog(_localizer.GetString("Flash_Canceled"));
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
    /// Calls the entry address the host already wrote, after a typed confirmation; executing uploaded code can leave
    /// the endpoint in any state, so it is gated like a flash action.
    /// <para>在输入确认短语后调用主机已写入的入口地址；执行已上传代码会让端点进入任意状态，因此像烧录动作一样门控。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件来源。</para></param>
    /// <param name="e">Event arguments. <para>事件参数。</para></param>
    internal async void OnSunxiExecuteClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Sunxi } device) return;
        if (TopLevel.GetTopLevel(Surface) is not Window owner) return;
        if (!TryParseSunxiAddress(SunxiExecuteAddress, out var address))
        {
            AppendLog(_localizer.GetString("Flash_SunxiAddressInvalid"));
            return;
        }

        var language = _localizer;
        bool confirmed;
        _confirming = true;
        try
        {
            confirmed = await DialogService.ConfirmDangerousAsync(owner, new DangerousConfirmRequest
            {
                TabTitle = language.GetString("Flash_Sunxi"),
                Title = language.GetString("Flash_SunxiExecute"),
                Detail = string.Format(language.GetString("Flash_SunxiExecuteConfirmDetail"), address),
                ConfirmWord = "EXEC",
                DeviceIdentifier = _deviceViewModel?.SelectedDevice?.ConfirmationIdentity
            }).ConfigureAwait(true);
        }
        finally
        {
            _confirming = false;
        }

        if (!confirmed) return;

        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        SetBusy(true);
        try
        {
            await App.DeviceMonitor.ExecuteSunxiAsync(device, address, cancellation.Token).ConfigureAwait(true);
            AppendLog($"{language.GetString("Flash_SunxiExecute")}: 0x{address:X8}");
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
    /// Publishes one status read: the status line and mode hint in the Sunxi section, the device status table,
    /// the mode into the device manager badge and the refreshed gates, because the FES answer opens the shared
    /// flash and read-back actions.
    /// <para>发布一次状态读取：全志区的状态行与模式提示、设备状态表、设备管理器徽章中的模式以及刷新后的门控，
    /// 因为 FES 应答会打开通用烧录与回读动作。</para>
    /// </summary>
    private void ApplySunxiStatus(SunxiDeviceStatus status)
    {
        var language = _localizer;
        SunxiStatusText = BuildSunxiStatusLine(status, language);
        SunxiModeHint = language.GetString(status.IsFes ? "Flash_SunxiHintFes" : "Flash_SunxiHintFel");
        _sunxiIsFes = status.IsFes;
        ReplaceDeviceStatus(SelectedDevice?.Key, BuildSunxiStatusRows(status, language));
        if (_deviceViewModel?.SelectedDevice is { } info)
        {
            info.SunxiMode = status.IsFes ? "FES" : "FEL";
        }

        RefreshDeviceUiGate();
    }

    /// <summary>Publishes a failed table read: the failure lands in the status line, the hint reports the mode
    /// is unknown and the rows stay empty. <para>发布一次失败的表格读取：失败写入状态行，提示行报告模式未知，
    /// 状态行保持为空。</para></summary>
    private void PublishSunxiTableFailure(string? deviceKey, string message)
    {
        _sunxiStatusCache = null;
        SunxiStatusText = message;
        SunxiModeHint = _localizer.GetString("Flash_SunxiHintUnknown");
        _sunxiIsFes = false;
        ReplaceDeviceStatus(deviceKey, []);
        RefreshDeviceUiGate();
    }

    private static string BuildSunxiStatusLine(SunxiDeviceStatus status, ILocalizer language)
    {
        var mode = status.IsFes
            ? language.GetString("Flash_SunxiModeFes")
            : language.GetString("Flash_SunxiModeFel");
        List<string> parts = [
            status.ChipName,
            mode,
            $"FW 0x{status.Identity.Firmware:X}"
        ];
        if (status.IsFes)
        {
            parts.Add(string.Format(language.GetString("Flash_SunxiStorageFormat"), status.StorageType ?? 0));
            if (status.CapacityBytes is { } capacity)
            {
                parts.Add(FormatByteSize(capacity));
            }

            if (status.SecureWord is { } secure)
            {
                parts.Add(string.Format(language.GetString("Flash_SunxiSecureFormat"), secure));
            }
        }

        return string.Join(" · ", parts);
    }

    private static List<FlashDeviceStatusEntry> BuildSunxiStatusRows(SunxiDeviceStatus status, ILocalizer language)
    {
        List<FlashDeviceStatusEntry> rows =
        [
            new()
            {
                Key = FlashDeviceStatusRowKeys.SunxiInfo,
                Name = language.GetString("Flash_SunxiInfo_Chip"),
                Value = status.ChipName
            },
            new()
            {
                Key = FlashDeviceStatusRowKeys.SunxiInfo,
                Name = language.GetString("Flash_SunxiInfo_Mode"),
                Value = language.GetString(status.IsFes ? "Flash_SunxiModeFes" : "Flash_SunxiModeFel")
            },
            new()
            {
                Key = FlashDeviceStatusRowKeys.SunxiInfo,
                Name = language.GetString("Flash_SunxiInfo_SoCId"),
                Value = $"0x{status.Identity.Id:X8}"
            },
            new()
            {
                Key = FlashDeviceStatusRowKeys.SunxiInfo,
                Name = language.GetString("Flash_SunxiInfo_Firmware"),
                Value = $"0x{status.Identity.Firmware:X}"
            },
        ];
        if (status.IsFes)
        {
            rows.Add(new FlashDeviceStatusEntry
            {
                Key = FlashDeviceStatusRowKeys.SunxiInfo,
                Name = language.GetString("Flash_SunxiInfo_StorageType"),
                Value = $"0x{status.StorageType ?? 0:X}"
            });
            if (status.CapacityBytes is { } capacity)
            {
                rows.Add(new FlashDeviceStatusEntry
                {
                    Key = FlashDeviceStatusRowKeys.SunxiInfo,
                    Name = language.GetString("Flash_SunxiInfo_Capacity"),
                    Value = FormatByteSize(capacity)
                });
            }

            if (status.SecureWord is { } secure)
            {
                rows.Add(new FlashDeviceStatusEntry
                {
                    Key = FlashDeviceStatusRowKeys.SunxiInfo,
                    Name = language.GetString("Flash_SunxiInfo_SecureWord"),
                    Value = $"0x{secure:X}"
                });
            }
        }

        return rows;
    }

    private async Task<string> PickSunxiImageAsync(string titleKey)
    {
        if (TopLevel.GetTopLevel(Surface) is not { } topLevel)
        {
            return string.Empty;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString(titleKey),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        return files.Count == 1 && files[0].TryGetLocalPath() is { } path && File.Exists(path)
            ? path
            : string.Empty;
    }

    private bool TryReadSunxiRange(out uint address, out int length)
    {
        length = 0;
        if (!TryParseSunxiAddress(SunxiAddress, out address))
        {
            AppendLog(_localizer.GetString("Flash_SunxiAddressInvalid"));
            return false;
        }

        if (!int.TryParse(SunxiLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out length) || length <= 0)
        {
            AppendLog(_localizer.GetString("Flash_SunxiLengthInvalid"));
            return false;
        }

        if ((ulong)address + (uint)length > uint.MaxValue)
        {
            AppendLog(_localizer.GetString("Flash_SunxiAddressInvalid"));
            return false;
        }

        return true;
    }

    private static bool TryParseSunxiAddress(string text, out uint address)
    {
        var value = text.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        return uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}