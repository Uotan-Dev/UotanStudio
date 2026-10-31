using System.Globalization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Ui;
using UotanStudio.UiAbstractions;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// Rockchip page actions mirroring the vendor tool: address-based transfers, storage switching, media erase
/// and the download-list configuration file.
/// <para>对应厂商工具的瑞芯微页面动作：按地址传输、存储切换、整介质擦除与下载列表配置文件。</para>
/// </summary>
public sealed partial class FlashViewModel
{
    private string? _rkLoaderPath;

    /// <summary>
    /// Command value of a row that supplies the loader instead of a partition image; the row can be selected, which
    /// checks the file it points at, and it is never written as one of the selected partition rows.
    /// <para>提供 loader 而非分区镜像的行所用的命令值；该行可以被勾选，勾选即检查它指向的文件，而它本身从不作为选中的
    /// 分区行写入。</para>
    /// </summary>
    internal const string LoaderCommand = "loader";

    /// <summary>
    /// Registration name of the format handler that owns Rockchip loader images; a detection result is accepted as a
    /// loader only under this name, because the handler type itself is internal to the firmware library.
    /// <para>拥有瑞芯微 loader 镜像的格式处理器注册名；只有该名字下的识别结果才算 loader，因为处理器类型本身是固件库内部类型。</para>
    /// </summary>
    private const string LoaderFormatName = "Rockchip Loader Image";

    /// <summary>
    /// Command value of a row that supplies the partition table instead of a partition image; the flash path
    /// resolves its addresses from that table, so the row itself is never written as an image.
    /// <para>提供分区表而非分区镜像的行所用的命令值；刷写路径从该分区表解析地址，因此这一行本身从不作为镜像写入。</para>
    /// </summary>
    internal const string ParameterCommand = "parameter";
    private byte[]? _rkConfigHeader;

    /// <summary>
    /// The loader-row check started by the last selection or edit; a caller that needs the verdict awaits it, and the
    /// page itself never blocks on it.
    /// <para>最近一次勾选或编辑启动的 loader 行检查；需要结论的调用方等待它，页面本身从不因此阻塞。</para>
    /// </summary>
    internal Task SpecialFileRowCheck { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Loader image a custom configuration points at; it is handed to the RKDL flash path so a maskrom device
    /// can be booted into loader mode before the write.
    /// <para>自定义配置指向的 loader 镜像；它被交给 RKDL 刷写路径，使 Maskrom 设备能在写入前启动到 loader 模式。</para>
    /// </summary>
    internal string? EffectiveRkLoaderPath
        => _rkLoaderPath is { Length: > 0 } loader && File.Exists(loader) ? loader : null;

    /// <summary>
    /// Whether the page holds a legal loader row, which is what makes the Rockchip flash actions and the Rockchip
    /// section usable at all.
    /// <para>页面是否持有合法的 loader 行，这决定了瑞芯微刷写动作与瑞芯微分类是否可用。</para>
    /// </summary>
    /// <remarks>
    /// A Rockchip device in mask ROM answers only the information commands, so every storage command needs a running
    /// loader. The loader is therefore a precondition of the page, not an option of one action: this flag gates the
    /// actions through <see cref="FlashDeviceUiPolicy.IsAvailable(string, FlashDeviceMode, bool)"/> and is republished
    /// whenever the imported configuration or the loader row's file changes.
    /// <para>处于 Mask ROM 的瑞芯微设备只应答信息类命令，因此所有存储命令都需要已运行的 loader。loader 因此是页面的
    /// 前置条件而不是某个动作的选项：该标志经
    /// <see cref="FlashDeviceUiPolicy.IsAvailable(string, FlashDeviceMode, bool)"/> 门控这些动作，并在导入配置或
    /// loader 行的文件变化时重新发布。</para>
    /// </remarks>
    private bool HasRockchipLoader => EffectiveRkLoaderPath is not null;

    /// <summary>
    /// Republished whenever <see cref="HasRockchipLoader"/> can have changed, so the gate is re-applied to the page.
    /// <para><see cref="HasRockchipLoader"/> 可能变化时调用，使门控重新施加到页面。</para>
    /// </summary>
    private void RefreshRockchipLoaderGate()
    {
        OnPropertyChanged(nameof(HasRockchipLoader));
        RefreshDeviceUiGate();
    }

    /// <summary>
    /// Points the loader at one file, re-applies the gate and reports the loader the configured flash actions will use.
    /// <para>把 loader 指向某个文件，重新施加门控并报告配置后的刷写动作将使用的 loader。</para>
    /// </summary>
    /// <param name="path">Loader image the user picked, or an empty value to drop it.
    /// <para>用户选择的 loader 镜像；空值表示清除。</para></param>
    private void ApplyRkLoaderPath(string? path)
    {
        _rkLoaderPath = path is { Length: > 0 } ? path : null;
        RefreshRockchipLoaderGate();
        if (EffectiveRkLoaderPath is { } loader)
        {
            AppendLog(string.Format(CultureInfo.InvariantCulture,
                _localizer.GetString("Flash_RkLoaderDeclared"), loader));
            return;
        }

        if (_rkLoaderPath is { } missing)
        {
            AppendLog(string.Format(CultureInfo.InvariantCulture,
                _localizer.GetString("Flash_RkLoaderMissing"), missing));
        }
    }

    /// <summary>
    /// Checks whether one file is a Rockchip loader this page can hand to the RKDL flash path, and reports the outcome.
    /// <para>检查某个文件是否为本页可以交给 RKDL 刷写路径的瑞芯微 loader，并写出检查结果。</para>
    /// </summary>
    /// <param name="path">Candidate loader image; the file is opened read-only.
    /// <para>候选 loader 镜像；文件以只读方式打开。</para></param>
    /// <returns><see langword="true"/> when the loader handler reads the whole image and its trailing checksum matches;
    /// <see langword="false"/> when the file is missing, another format, structurally invalid or has a damaged checksum.
    /// <para>当 loader 处理器读取整幅镜像且末尾校验和相符时为 <see langword="true"/>；文件缺失、属于其它格式、结构非法或
    /// 校验和损坏时为 <see langword="false"/>。</para></returns>
    /// <remarks>
    /// Only the log lines are written here; the row that carries the result is updated by the caller, which owns the
    /// row's status. A file that merely starts with a loader tag is not enough: the entry tables and the trailing
    /// checksum are read as well, because that is what the flash path parses before it boots the image.
    /// <para>这里只写日志；行状态由掌握该行的调用方更新。仅以 loader 标签开头并不够：条目表与末尾校验和也会被读取，
    /// 因为刷写路径在启动镜像前解析的正是这些内容。</para>
    /// </remarks>
    private async Task<bool> ValidateRkLoaderFileAsync(string path)
    {
        var language = _localizer;
        var display = Path.GetFileName(path);
        if (!File.Exists(path))
        {
            AppendLog(string.Format(CultureInfo.InvariantCulture,
                language.GetString("Flash_RkLoaderMissing"), path));
            return false;
        }

        var outcome = await Task.Run(() => InspectRkLoader(path)).ConfigureAwait(true);
        if (outcome.ProblemKey is { } problem)
        {
            AppendLog(string.Format(CultureInfo.InvariantCulture,
                language.GetString(problem), display, outcome.Summary));
            return false;
        }

        AppendLog(string.Format(CultureInfo.InvariantCulture,
            language.GetString("Flash_RkLoaderChecked"), display, outcome.Summary));
        return true;
    }

    /// <summary>
    /// Reads one candidate loader on a worker thread and reports what the flash path would make of it.
    /// <para>在工作线程上读取一个候选 loader，并报告刷写路径会据此得出什么结论。</para>
    /// </summary>
    /// <param name="path">Candidate loader image, opened read-only.
    /// <para>候选 loader 镜像，以只读方式打开。</para></param>
    /// <returns>The problem key to log with the file name and the handler summary, or no problem key when the image is
    /// accepted. <para>需要连同文件名记录的失败键与处理器摘要；镜像被接受时没有失败键。</para></returns>
    /// <remarks>
    /// The whole image is read because the trailing checksum covers every byte before it, so this runs off the calling
    /// thread; the caller owns the log and the row so nothing is published from here.
    /// <para>由于末尾校验和覆盖它之前的每一个字节，整幅镜像都会被读取，因此该检查在调用线程之外运行；日志与行状态由调用方
    /// 负责，这里不发布任何内容。</para>
    /// </remarks>
    private static (string? ProblemKey, string Summary) InspectRkLoader(string path)
    {
        Firmware.Application.Formats.FirmwareFormatRegistry.RegisterAll();
        if (Firmware.Core.FormatDetector.Detect(path) is not { } handler ||
            !handler.FormatName.Equals(LoaderFormatName, StringComparison.Ordinal))
        {
            return ("Flash_RkLoaderNotLoader", string.Empty);
        }

        var result = handler.ReadMetadataAsync(path).GetAwaiter().GetResult();
        if (!result.Success)
        {
            return ("Flash_RkLoaderInvalid", result.Summary);
        }

        if (!result.Metadata.TryGetValue("ChecksumStatus", out var checksum) ||
            !checksum.Equals("Verified", StringComparison.Ordinal))
        {
            return ("Flash_RkLoaderChecksumMismatch", result.Summary);
        }

        return (null, result.Summary);
    }

    /// <summary>
    /// Re-checks the file an imported <c>Loader</c> row points at and publishes the verdict on that row.
    /// <para>重新检查导入的 <c>Loader</c> 行所指向的文件，并把结论发布到该行。</para>
    /// </summary>
    /// <param name="row">Loader row whose file is checked; the row keeps its text while the check runs.
    /// <para>待检查文件的 loader 行；检查期间该行保持原有文本。</para></param>
    /// <returns>A task that completes when the row status carries the verdict.
    /// <para>行状态已携带结论时完成的任务。</para></returns>
    /// <remarks>
    /// The picker only proves that a file exists, so a row whose file the user just picked is checked here as well: the
    /// loader is accepted by its declared structure and its trailing checksum, not by the file name.
    /// <para>选择器只证明文件存在，因此用户在行内刚点选文件的 loader 行同样在这里检查：loader 按声明的结构与末尾校验和
    /// 判定，而不是按文件名。</para>
    /// </remarks>
    private async Task UpdateRkLoaderRowStatusAsync(FlashPartitionEntry row)
    {
        var language = _localizer;
        if (row.FilePath.Length == 0)
        {
            row.StatusText = string.Empty;
            return;
        }

        row.StatusText = language.GetString("Flash_RkLoaderChecking");
        try
        {
            var valid = await ValidateRkLoaderFileAsync(row.FilePath).ConfigureAwait(true);
            row.StatusText = language.GetString(valid ? "Flash_RkLoaderValid" : "Flash_RkLoaderInvalidStatus");
        }
        catch (Exception ex)
        {
            AppendError(ex);
            row.StatusText = language.GetString("Flash_RkLoaderInvalidStatus");
        }

        RefreshRockchipLoaderGate();
    }

    /// <summary>
    /// Checks the loaded image of a <c>parameter</c> row, which carries the partition table instead of an image.
    /// <para>检查 <c>parameter</c> 行的已加载文件，该行携带分区表而不是镜像。</para>
    /// </summary>
    /// <param name="row">Parameter row whose file is checked.
    /// <para>待检查文件的 parameter 行。</para></param>
    /// <returns><see langword="true"/> when the file parses into at least one partition; <see langword="false"/> when the
    /// row names no file or the file is not a partition table.
    /// <para>文件能解析出至少一个分区时为 <see langword="true"/>；该行没有文件或文件不是分区表时为 <see langword="false"/>。</para></returns>
    private bool ValidateRkParameterRow(FlashPartitionEntry row)
    {
        var language = _localizer;
        if (row.FilePath.Length == 0)
        {
            row.StatusText = string.Empty;
            return false;
        }

        try
        {
            var partitions = RockchipParameterParser.Parse(row.FilePath);
            AppendLog(string.Format(CultureInfo.InvariantCulture,
                language.GetString("Flash_RkParameterChecked"), Path.GetFileName(row.FilePath), partitions.Count));
            row.StatusText = string.Format(CultureInfo.InvariantCulture,
                language.GetString("Flash_RkParameterValid"), partitions.Count);
            return partitions.Count > 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            AppendLog(string.Format(CultureInfo.InvariantCulture,
                language.GetString("Flash_RkParameterInvalid"), Path.GetFileName(row.FilePath), exception.Message));
            row.StatusText = language.GetString("Flash_RkParameterInvalidStatus");
            return false;
        }
    }

    /// <summary>
    /// Reads the chip capability word, the flash id and the current storage, then reports them in the log.
    /// <para>读取芯片能力字、Flash ID 与当前存储并写入日志。</para>
    /// </summary>
    /// <param name="device">Target RKDL endpoint.<para>目标 RKDL 端点。</para></param>
    private async Task RunRkReadInfoAsync(DetectedDevice device)
    {
        var language = _localizer;
        AppendLog($"{language.GetString("Flash_RkReadInfo")}: {device.DisplayName}");
        var storageLabel = string.Empty;
        var lines = new List<string>();
        var loader = EffectiveRkLoaderPath;

        if (loader is null)
        {
            await App.DeviceMonitor.RunRkdlAsync(device, session =>
            {
                lines.AddRange(ReadRkDeviceInformation(session, language));
                if (session.IsMaskromMode)
                {
                    lines.Add(language.GetString("Flash_RkStorageNeedsLoader"));
                }
                else
                {
                    storageLabel = DescribeRkStorage(session.ReadStorage());
                    lines.Add($"{language.GetString("Flash_RkStorage")}: {storageLabel}");
                }

                return Task.FromResult(true);
            }, CancellationToken.None).ConfigureAwait(true);
        }
        else
        {
            var loaderBytes = await File.ReadAllBytesAsync(loader).ConfigureAwait(true);
            await App.DeviceMonitor.RunRkdlLoaderTransitionAsync(device,
                async (session, token) =>
                {
                    if (!session.IsMaskromMode)
                    {
                        return null;
                    }

                    AppendLog(string.Format(CultureInfo.InvariantCulture,
                        language.GetString("Flash_RkLoaderDeclared"), loader));
                    await session.DownloadBootLoaderAsync(loaderBytes, token).ConfigureAwait(false);
                    return session.CreateLoaderTransitionOffer();
                },
                (session, _) =>
                {
                    lines.AddRange(ReadRkDeviceInformation(session, language));
                    storageLabel = DescribeRkStorage(session.ReadStorage());
                    lines.Add($"{language.GetString("Flash_RkStorage")}: {storageLabel}");
                    return Task.FromResult(true);
                },
                CancellationToken.None).ConfigureAwait(true);
        }

        foreach (var line in lines)
        {
            AppendLog(line);
        }

        ApplyReportedRkStorage(storageLabel);
    }

    /// <summary>
    /// Reads the information commands mask ROM answers as well as a loader does, so the chip can be identified
    /// before any storage command is attempted.
    /// <para>读取 Mask ROM 与 loader 都能应答的信息类命令，使芯片在尝试任何存储命令之前就能被识别。</para>
    /// </summary>
    /// <param name="session">Open RKDL session on the target device. <para>目标设备上已打开的 RKDL 会话。</para></param>
    /// <param name="language">Localized string lookup for the report labels.
    /// <para>报告标签使用的本地化字符串查询。</para></param>
    /// <returns>The report lines, without the storage line the caller adds once a loader can answer it.
    /// <para>报告行；存储行由调用方在 loader 可应答后追加。</para></returns>
    private static List<string> ReadRkDeviceInformation(RkdlSession session, ILocalizer language)
    {
        var capability = session.ReadCapability();
        var flashId = session.ReadFlashId();
        return
        [
            $"{language.GetString("Flash_RkCapability")}: 0x{Convert.ToHexString(capability.Raw)}",
            $"DirectLBA={capability.DirectLba} First4M={capability.First4MAccess}",
            $"FlashID: 0x{Convert.ToHexString(flashId)}"
        ];
    }

    /// <summary>
    /// Maps a protocol storage number to the selector label, or reports the raw number when it is unknown.
    /// <para>把协议存储编号映射为选择框文本；未知编号直接报告原值。</para>
    /// </summary>
    /// <remarks>
    /// <para><paramref name="storage"/> is the first set bit index reported by READ_STORAGE, which is a protocol
    /// identifier (eMMC is 1, SD is 2 and SPI NOR is 9), while the labels are the vendor selector's user-facing
    /// numbers; see the note on <c>RkStorageTypes</c>. Indexing the selector list with that identifier can
    /// therefore be off by one, and only real hardware can confirm the correct label.</para>
    /// <para><paramref name="storage"/> 是 READ_STORAGE 回报的首个置位下标，即协议标识（eMMC 为 1、SD 为 2、
    /// SPI NOR 为 9），而标签是厂商选择框的用户编号；见 <c>RkStorageTypes</c> 上的说明。用该标识去索引选择框
    /// 列表可能差一位，正确标签只能由真机确认。</para>
    /// </remarks>
    /// <param name="storage">Protocol storage number.<para>协议存储编号。</para></param>
    /// <returns>The label shown in the selector.<para>选择框中显示的文本。</para></returns>
    private static string DescribeRkStorage(int storage)
        => storage > 0 && storage <= RkStorageTypes.Length
            ? RkStorageTypes[storage - 1]
            : storage.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Shows a storage on the selector, or clears it when no medium is known; the vendor table allows a row to
    /// name no storage, so the selector keeps an empty state instead of assuming one.
    /// <para>把存储显示到选择框；介质未知时清空。厂商表格允许行不指定存储，因此选择框保留空状态而不是假定一个。</para>
    /// </summary>
    /// <param name="reported">Selector label such as <c>2. EMMC</c> or a plain medium name such as <c>EMMC</c>;
    /// an empty value clears the selection.<para>选择框文本（如 <c>2. EMMC</c>）或纯介质名（如 <c>EMMC</c>）；
    /// 空值清空选择。</para></param>
    private void ApplyReportedRkStorage(string reported)
    {
        if (_surface is null) return;
        var box = Field<ComboBox>("RkStorageTypeBox");
        if (reported.Length == 0)
        {
            box.SelectedIndex = -1;
            return;
        }

        var index = Array.FindIndex(RkStorageTypes, label =>
            string.Equals(label, reported, StringComparison.Ordinal) ||
            string.Equals(PlainRkStorageLabel(label), reported, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            box.SelectedIndex = index;
        }
    }

    /// <summary>
    /// The medium name without the selector's ordinal, for example <c>EMMC</c> for <c>2. EMMC</c>.
    /// <para>去掉选择框序号的介质名，例如 <c>2. EMMC</c> 得到 <c>EMMC</c>。</para>
    /// </summary>
    /// <param name="label">Selector entry.<para>选择框条目。</para></param>
    /// <returns>The text after the ordinal separator, or the entry unchanged when it has none.
    /// <para>序号分隔符之后的文本；没有分隔符时原样返回。</para></returns>
    private static string PlainRkStorageLabel(string label)
    {
        var separator = label.IndexOf(". ", StringComparison.Ordinal);
        return separator < 0 ? label : label[(separator + 2)..];
    }

    /// <summary>
    /// Mirrors the storage a configuration names onto the selector: the shared value when every image row agrees,
    /// and an empty selector when the rows leave the storage blank or disagree.
    /// <para>把配置声明的存储反映到选择框：所有镜像行一致时取该值；行内留空或不一致时清空选择框。</para>
    /// </summary>
    /// <param name="entries">Image rows parsed from the configuration.<para>从配置解析出的镜像行。</para></param>
    private void ApplyConfigRkStorage(IReadOnlyList<RockchipConfigEntry> entries)
    {
        var masks = entries.Select(entry => entry.StorageMask).Distinct().ToArray();
        ApplyReportedRkStorage(masks.Length == 1 ? RockchipConfigFile.DescribeStorage(masks[0]) : string.Empty);
    }

    /// <summary>Reads the chip capability word for the selected device. <para>读取所选设备的芯片能力字。</para></summary>
    internal async void OnRkReadInfoClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Rkdl } device)
        {
            AppendLog(_localizer.GetString("Flash_DeviceNotConnected"));
            return;
        }

        SetBusy(true);
        try
        {
            await RunRkReadInfoAsync(device);
        }
        catch (Exception ex) { AppendError(ex); }
        finally
        {
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>Runs the device ready test. <para>执行设备就绪测试。</para></summary>
    internal async void OnRkTestDeviceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Rkdl } device)
        {
            AppendLog(_localizer.GetString("Flash_DeviceNotConnected"));
            return;
        }

        var language = _localizer;
        SetBusy(true);
        try
        {
            AppendLog(language.GetString("Flash_RkTestDevice"));
            var ready = await App.DeviceMonitor.RunRkdlAsync(device,
                session => session.TestDeviceAsync(CancellationToken.None), CancellationToken.None).ConfigureAwait(true);
            AppendLog(language.GetString(ready ? "Flash_Done" : "Flash_Failed"));
        }
        catch (Exception ex) { AppendError(ex); }
        finally
        {
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>Reads the current storage number and reflects it in the selector. <para>读取当前存储编号并反映到选择框。</para></summary>
    internal async void OnRkGetStorageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Rkdl } device)
        {
            AppendLog(_localizer.GetString("Flash_DeviceNotConnected"));
            return;
        }

        SetBusy(true);
        try
        {
            var storage = await App.DeviceMonitor.RunRkdlAsync(device,
                session => Task.FromResult(session.ReadStorage()), CancellationToken.None).ConfigureAwait(true);
            var label = DescribeRkStorage(storage);
            AppendLog($"{_localizer.GetString("Flash_RkStorage")}: {label}");
            ApplyReportedRkStorage(label);
        }
        catch (Exception ex) { AppendError(ex); }
        finally
        {
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>Switches the device to the storage selected in the combo box. <para>把设备切换到选择框中选中的存储。</para></summary>
    internal async void OnRkSwitchStorageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Rkdl } device)
        {
            AppendLog(_localizer.GetString("Flash_DeviceNotConnected"));
            return;
        }

        if (_surface is null) return;
        var index = Field<ComboBox>("RkStorageTypeBox").SelectedIndex;
        if (index < 0 || index >= RkStorageTypes.Length)
        {
            AppendLog(_localizer.GetString("Flash_RkStorageNotSelected"));
            return;
        }

        var target = (byte)(index + 1);
        var language = _localizer;
        SetBusy(true);
        try
        {
            await App.DeviceMonitor.RunRkdlAsync(device, session =>
            {
                session.ChangeStorage(target);
                return Task.FromResult(true);
            }, CancellationToken.None).ConfigureAwait(true);
            AppendLog($"{language.GetString("Flash_RkSwitchStorage")}: {RkStorageTypes[index]}");
            AppendLog(language.GetString("Flash_RkStorageSwitched"));
        }
        catch (Exception ex) { AppendError(ex); }
        finally
        {
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>Erases the whole medium after an explicit confirmation. <para>经显式确认后擦除整个介质。</para></summary>
    internal async void OnRkEraseFlashClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Rkdl } device) return;
        if (TopLevel.GetTopLevel(Surface) is not Window owner) return;

        var language = _localizer;
        bool confirmed;
        _confirming = true;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner,
                language.GetString("Flash_RkEraseFlash"),
                language.GetString("Flash_RkEraseFlashConfirm")).ConfigureAwait(true);
        }
        finally
        {
            _confirming = false;
        }

        if (!confirmed) return;
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        try
        {
            var erased = await App.DeviceMonitor.RunRkdlAsync(device,
                session => Task.Run(
                    () => session.EraseAllFlash(forceBlockErase: false, progress: null, cancellation.Token),
                    cancellation.Token), cancellation.Token).ConfigureAwait(true);
            AppendLog($"{language.GetString("Flash_RkEraseFlash")}: {erased}");
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendError(ex); }
        finally
        {
            SetBusy(false);
            _flashCancellation = null;
            UpdateEdlButton();
        }
    }

    /// <summary>Reads a sector range through LBA addressing into a file. <para>经 LBA 寻址把一段扇区读入文件。</para></summary>
    internal async void OnRkLbaReadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Rkdl } device) return;
        if (!TryReadRkLbaRange(out var address, out var sectors)) return;
        if (!RkLbaRangeAllowed(address, sectors)) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = _localizer.GetString("Flash_RkLbaRead"),
            DefaultExtension = "bin",
            SuggestedFileName = $"lba_{address:X}_{sectors:X}.bin",
            FileTypeChoices = [FilePickerFileTypes.All]
        });
        if (file is null || file.TryGetLocalPath() is not { } path) return;

        var length = checked(sectors * RkdlConstants.SectorSize);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        try
        {
            var written = await App.DeviceMonitor.RunRkdlAsync(device, async session =>
            {
                await using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 128 * 1024, FileOptions.SequentialScan);
                var bytes = await Task.Run(
                    () => session.ReadLBA(address, stream, length, null, cancellation.Token),
                    cancellation.Token).ConfigureAwait(false);
                await stream.FlushAsync(cancellation.Token).ConfigureAwait(false);
                return bytes;
            }, cancellation.Token).ConfigureAwait(true);
            File.Move(temporary, path, overwrite: true);
            AppendLog($"{_localizer.GetString("Flash_RkLbaRead")}: {FormatByteSize(written)}");
        }
        catch (OperationCanceledException) { AppendLog(_localizer.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendError(ex); }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch (IOException) { }
            }
            SetBusy(false);
            _flashCancellation = null;
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Writes a file at an LBA address. The sector count must hold the whole file and the range must sit inside a
    /// declared partition unless the forced-address switch is set, which writes wherever the address points.
    /// <para>把文件写入指定 LBA 地址。除非勾选“强制按地址写”，扇区数必须容纳整个文件，且范围必须落在已声明的
    /// 分区内；勾选后按地址原样写入。</para>
    /// </summary>
    internal async void OnRkLbaWriteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Rkdl } device) return;
        if (!TryReadRkLbaRange(out var address, out var sectors)) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString("Flash_RkLbaWrite"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (files.Count != 1 || files[0].TryGetLocalPath() is not { } path || !File.Exists(path)) return;

        var info = new FileInfo(path);
        var length = info.Length;
        if (length <= 0) return;
        var fileSectors = (length + RkdlConstants.SectorSize - 1) / RkdlConstants.SectorSize;
        if (sectors < fileSectors && !RkForceAddress)
        {
            AppendLog(_localizer.GetString("Flash_RkLbaCountTooSmall"));
            return;
        }

        if (!RkLbaRangeAllowed(address, fileSectors)) return;
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        try
        {
            var written = await App.DeviceMonitor.RunRkdlAsync(device, async session =>
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.SequentialScan);
                return await Task.Run(
                    () => session.WriteLBA(address, stream, length, null, cancellation.Token),
                    cancellation.Token).ConfigureAwait(false);
            }, cancellation.Token).ConfigureAwait(true);
            AppendLog($"{_localizer.GetString("Flash_RkLbaWrite")}: {FormatByteSize(written)}");
        }
        catch (OperationCanceledException) { AppendLog(_localizer.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendError(ex); }
        finally
        {
            SetBusy(false);
            _flashCancellation = null;
            UpdateEdlButton();
        }
    }

    /// <summary>Erases a sector range through LBA addressing. <para>经 LBA 寻址擦除一段扇区。</para></summary>
    internal async void OnRkEraseSectorsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Rkdl } device) return;
        if (!TryReadRkLbaRange(out var address, out var sectors)) return;
        if (!RkLbaRangeAllowed(address, sectors)) return;
        if (TopLevel.GetTopLevel(Surface) is not Window owner) return;

        var language = _localizer;
        bool confirmed;
        _confirming = true;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner,
                language.GetString("Flash_RkEraseSectors"),
                $"LBA 0x{address:X}, {sectors} sectors").ConfigureAwait(true);
        }
        finally
        {
            _confirming = false;
        }

        if (!confirmed) return;
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        try
        {
            await App.DeviceMonitor.RunRkdlAsync(device, session =>
            {
                session.EraseLbaRange(address, sectors, null, cancellation.Token);
                return Task.FromResult(true);
            }, cancellation.Token).ConfigureAwait(true);
            AppendLog($"{language.GetString("Flash_RkEraseSectors")}: LBA 0x{address:X}, {sectors}");
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendError(ex); }
        finally
        {
            SetBusy(false);
            _flashCancellation = null;
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Parses the LBA address and sector count boxes, accepting <c>0x</c> prefixed hexadecimal.
    /// <para>解析 LBA 地址与扇区数输入框，接受 <c>0x</c> 前缀的十六进制。</para>
    /// </summary>
    /// <param name="address">Parsed start sector.<para>解析出的起始扇区。</para></param>
    /// <param name="sectors">Parsed sector count.<para>解析出的扇区数。</para></param>
    /// <returns>Whether both values are positive.<para>两个值是否都为正数。</para></returns>
    private bool TryReadRkLbaRange(out long address, out long sectors)
    {
        address = 0;
        sectors = 0;
        if (_surface is null) return false;
        var language = _localizer;
        if (!TryParseSectorNumber(Field<TextBox>("RkLbaAddressBox").Text, out address))
        {
            AppendLog(language.GetString("Flash_RkLbaAddress"));
            return false;
        }

        if (!TryParseSectorNumber(Field<TextBox>("RkLbaCountBox").Text, out sectors) || sectors <= 0)
        {
            AppendLog(language.GetString("Flash_RkLbaCount"));
            return false;
        }

        return true;
    }

    private static bool TryParseSectorNumber(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Whether the forced-address switch is set; the switch makes the LBA actions skip the partition-table check
    /// so vendor-tool workflows can write outside the declared partitions.
    /// <para>是否勾选了“强制按地址写”；勾选后 LBA 动作跳过分区表检查，以便像厂商工具那样写入未声明区域。</para>
    /// </summary>
    private bool RkForceAddress => _surface is not null && Field<CheckBox>("RkForceAddressBox").IsChecked == true;

    /// <summary>
    /// The parameter file next to the loaded images, used to validate LBA ranges; null when no image is loaded or
    /// its directory carries no partition table.
    /// <para>已加载镜像旁的 parameter 文件，用于校验 LBA 范围；没有镜像或其目录没有分区表时为 null。</para>
    /// </summary>
    private string? RkParameterTablePath()
    {
        var directory = Partitions
            .Where(entry => entry.FilePath.Length > 0)
            .Select(entry => Path.GetDirectoryName(Path.GetFullPath(entry.FilePath)))
            .FirstOrDefault(path => path is { Length: > 0 });
        if (directory is null && _loadedPath is { Length: > 0 } loaded &&
            Path.GetDirectoryName(Path.GetFullPath(loaded)) is { Length: > 0 } packageDirectory)
        {
            directory = packageDirectory;
        }

        return directory is null ? null : FlashService.FindParameterFile(directory);
    }

    /// <summary>
    /// Whether a sector range may be addressed: the forced-address switch, a missing partition table and a range
    /// covered by a declared partition all pass; anything else is refused with a hint to force the address.
    /// <para>判断某扇区范围是否可寻址：勾选强制按地址、没有分区表、或范围被已声明分区覆盖时放行；其余情况拒绝
    /// 并提示勾选强制按地址。</para>
    /// </summary>
    /// <param name="address">First sector of the range.<para>范围的起始扇区。</para></param>
    /// <param name="sectors">Sector count of the range.<para>范围的扇区数。</para></param>
    /// <returns>Whether the range is acceptable.<para>该范围是否可接受。</para></returns>
    private bool RkLbaRangeAllowed(long address, long sectors)
    {
        if (RkForceAddress) return true;
        var parameterPath = RkParameterTablePath();
        if (parameterPath is null) return true;
        if (IsRkRangeDeclared(RockchipParameterParser.Parse(parameterPath), address, sectors)) return true;
        AppendLog(_localizer.GetString("Flash_RkLbaNotDeclared"));
        return false;
    }

    /// <summary>
    /// Whether one partition of the table fully covers a sector range.
    /// <para>判断表中某个分区是否完整覆盖某扇区范围。</para>
    /// </summary>
    /// <param name="partitions">Parsed parameter entries.<para>解析出的 parameter 条目。</para></param>
    /// <param name="address">First sector of the range.<para>范围的起始扇区。</para></param>
    /// <param name="sectors">Sector count of the range.<para>范围的扇区数。</para></param>
    /// <returns><see langword="true"/> when at least one partition covers the whole range.
    /// <para>至少一个分区覆盖该完整范围时为 <see langword="true"/>。</para></returns>
    internal static bool IsRkRangeDeclared(IReadOnlyList<RockchipPartition> partitions, long address, long sectors)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        if (address < 0 || sectors <= 0) return false;
        var end = address + sectors;
        foreach (var partition in partitions)
        {
            if (address >= partition.OffsetSectors && end <= partition.OffsetSectors + partition.SizeSectors)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Imports a download-list configuration: every row name is recognized as an operation, so a <c>Loader</c> row
    /// supplies the RKDL loader image and is published as a load-loader row, a <c>Parameter</c> row is published as
    /// the partition-table row that the flash path reads its addresses from, a single existing image row goes
    /// through the package loader so whole-disk and logical-partition images keep their handling, and every other
    /// row is published as a partition row even when its file is missing or the row names no file at all.
    /// <para>导入下载列表配置：每一行的名称都被识别为操作，因此 <c>Loader</c> 行提供 RKDL loader 镜像并发布为加载
    /// loader 行，<c>Parameter</c> 行发布为刷写路径据此解析地址的分区表行；只有一条存在的镜像行时走刷机包加载
    /// 流程，保留整盘与逻辑分区镜像的处理；其余行一律发布为分区行，即使其文件缺失或该行没有指定文件。</para>
    /// </summary>
    /// <param name="path">Existing configuration file; parse failures are logged instead of thrown.
    /// <para>既有的配置文件；解析失败只记录日志，不抛出异常。</para></param>
    internal async Task ImportRkConfigAsync(string path)
    {
        try
        {
            var (header, entries) = RockchipConfigFile.Load(path);
            _rkConfigHeader = header;
            ApplyRkLoaderPath(null);
            _payloadPath = null;
            _remotePayloadUrl = null;
            _superPath = null;
            _vendorPath = null;
            _vendorEntries.Clear();
            _oppoPath = null;
            _oppoEntries.Clear();
            _mtkResolution = null;
            _mtkRows.Clear();
            _mtkPartitions.Clear();
            _edlEntries.Clear();
            _flatBinPackage = false;
            _requiredCodeName = null;
            _requiresFastbootd = false;
            var rows = new List<RockchipConfigEntry>(entries.Count);
            var images = new List<RockchipConfigEntry>();
            foreach (var entry in entries)
            {
                AppendLog($"{entry.Name}: {entry.Path}");
                if (entry.Path.Length > 0 && RockchipConfigFile.LooksLike(entry.Path))
                {
                    continue;
                }

                rows.Add(entry);
                var operation = RockchipConfigFile.ClassifyOperation(entry.Name);
                if (operation == RockchipConfigOperation.Loader)
                {
                    if (entry.Path.Length > 0)
                    {
                        ApplyRkLoaderPath(entry.Path);
                    }

                    continue;
                }

                if (operation == RockchipConfigOperation.Parameter)
                {
                    continue;
                }

                images.Add(entry);
            }

            AppendLog(string.Format(CultureInfo.InvariantCulture,
                _localizer.GetString("Flash_RkConfigImported"), $"{entries.Count}"));
            ApplyConfigRkStorage(images);
            var existing = images.Where(entry => File.Exists(entry.Path)).ToArray();
            if (images.Count == 0)
            {
                PackagePath = path;
                ApplyRkConfigRows(rows);
                AppendLog(_localizer.GetString("Flash_RkConfigNoImages"));
                return;
            }

            PackagePath = existing.Length > 0 ? existing[0].Path : path;
            if (images.Count == 1 && existing.Length == 1 && rows.Count == 1)
            {
                await LoadPackageAsync(existing[0].Path).ConfigureAwait(true);
                if (Partitions.Count > 0)
                {
                    return;
                }
            }

            ApplyRkConfigRows(rows);
        }
        catch (Exception ex) { AppendError(ex); }
        finally
        {
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Replaces the whole partition grid with one row per configuration row, so re-importing a configuration never
    /// stacks rows: the <c>Loader</c> row and the <c>parameter</c> row can be selected, which checks the file they
    /// point at, a partition row whose image is missing stays listed but cannot be selected, and a row that names no
    /// file can be selected without starting out selected. The loaded-path marker follows the configuration so the flash
    /// actions match the rows just published.
    /// <para>用配置的每一行整体替换分区列表，使重复导入配置不会叠加条目：<c>Loader</c> 行与 <c>parameter</c> 行可以勾选，
    /// 勾选即检查它们指向的文件；镜像缺失的分区行仍然列出，但不可勾选；没有指定文件的空路径行可以勾选，但导入时默认
    /// 不勾选。已加载路径标记随之更新，使刷写动作与刚发布的条目一致。</para>
    /// </summary>
    /// <param name="entries">Rows taken from the configuration, in file order.
    /// <para>按文件顺序取自配置的行。</para></param>
    private void ApplyRkConfigRows(IReadOnlyList<RockchipConfigEntry> entries)
    {
        var rows = new List<FlashPartitionEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Path.Length > 0 && RockchipConfigFile.LooksLike(entry.Path))
            {
                continue;
            }

            var exists = entry.Path.Length > 0 && File.Exists(entry.Path);
            var operation = RockchipConfigFile.ClassifyOperation(entry.Name);
            rows.Add(new FlashPartitionEntry
            {
                Name = entry.Name.Length > 0 ? entry.Name : Path.GetFileNameWithoutExtension(entry.Path),
                Command = operation switch
                {
                    RockchipConfigOperation.Loader => LoaderCommand,
                    RockchipConfigOperation.Parameter => ParameterCommand,
                    _ => "flash"
                },
                FilePath = entry.Path,
                FileName = Path.GetFileName(entry.Path),
                Size = exists ? FormatByteSize(new FileInfo(entry.Path).Length) : string.Empty,
                Offset = entry.Address == 0 ? string.Empty : $"0x{entry.Address:X8}",
                StorageMedium = RockchipConfigFile.DescribeStorage(entry.StorageMask),
                CanSelect = exists || entry.Path.Length == 0,
                StatusText = exists || entry.Path.Length == 0
                    ? string.Empty
                    : _localizer.GetString("Flash_FileMissing")
            });
        }

        Partitions.Replace(rows);
        _loadedPath = PackagePath;
        AppendLog($"{Partitions.Count} {_localizer.GetString("Flash_ImagesLoaded")}");
        foreach (var row in rows)
        {
            if (row.Command is LoaderCommand or ParameterCommand)
            {
                UpdateSpecialFileRowStatus(row);
            }
        }
    }

    /// <summary>
    /// Re-checks the file of a row that carries the loader or the partition table, and publishes the verdict on it.
    /// <para>重新检查承载 loader 或分区表的行的文件，并把结论发布到该行。</para>
    /// </summary>
    /// <param name="row">Row whose command is <see cref="LoaderCommand"/> or <see cref="ParameterCommand"/>.
    /// <para>命令为 <see cref="LoaderCommand"/> 或 <see cref="ParameterCommand"/> 的行。</para></param>
    /// <remarks>
    /// The check is synchronous for a parameter table, which is a small text file, and asynchronous for a loader, whose
    /// entry tables and trailing checksum are read; an imported row is checked right after it is published, so the grid
    /// never claims a loader is usable before its content was read.
    /// <para>分区表是小文本文件，其检查同步完成；loader 要读取条目表与末尾校验和，因此异步完成。导入的行在发布后立即
    /// 检查，因此列表不会在读取内容之前就声称某个 loader 可用。</para>
    /// </remarks>
    private void UpdateSpecialFileRowStatus(FlashPartitionEntry row)
    {
        if (row.Command == ParameterCommand)
        {
            ValidateRkParameterRow(row);
            return;
        }

        if (row.Command == LoaderCommand)
        {
            SpecialFileRowCheck = UpdateRkLoaderRowStatusAsync(row);
        }
    }

    /// <summary>
    /// Whether the row is written by a flash or erase action: the rows that carry the loader or the partition table
    /// hand a file to the flash path instead, so they are selected only to have that file checked.
    /// <para>该行是否由刷写或擦除动作写入：承载 loader 或分区表的行改为把一个文件交给刷写路径，因此勾选它们只是为了检查
    /// 该文件。</para>
    /// </summary>
    /// <param name="entry">Row to classify.<para>待判断的行。</para></param>
    /// <returns><see langword="true"/> for a row a flash or erase action writes.
    /// <para>刷写或擦除动作会写入的行返回 <see langword="true"/>。</para></returns>
    internal static bool IsWritableEntry(FlashPartitionEntry entry)
        => entry.Command is not (LoaderCommand or ParameterCommand);

    /// <summary>
    /// Writes the current rows, the loader and the loaded package as an RKDevTool configuration file.
    /// <para>把当前各行、loader 与已加载刷机包写为 RKDevTool 配置文件。</para>
    /// </summary>
    /// <param name="path">Destination file chosen by the user.<para>用户选择的目标文件。</para></param>
    private void SaveRkConfig(string path)
    {
        var entries = new List<RockchipConfigEntry>();
        if (_rkLoaderPath is { Length: > 0 } loader)
        {
            entries.Add(new RockchipConfigEntry
            {
                Name = RockchipConfigFile.LoaderRowName,
                Path = loader,
                Selected = true
            });
        }

        if (PackagePath is { Length: > 0 } package)
        {
            entries.Add(new RockchipConfigEntry { Name = "Image", Path = package, Selected = true });
        }

        foreach (var row in Partitions.Where(row => row.FilePath.Length > 0 && row.Command != LoaderCommand))
        {
            entries.Add(new RockchipConfigEntry
            {
                Name = row.Name,
                Path = row.FilePath,
                Address = TryParseSectorNumber(row.Offset, out var offset) && offset is > 0 and <= uint.MaxValue
                    ? (uint)offset
                    : 0u,
                StorageMask = RockchipConfigFile.StorageMaskFromName(row.StorageMedium),
                Selected = true
            });
        }

        RockchipConfigFile.Save(path, _rkConfigHeader ?? RockchipConfigFile.CreateHeader(), entries);
    }
}