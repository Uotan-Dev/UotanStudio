using System.Globalization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Ui;

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
    /// Command value of a row that supplies the loader instead of a partition image; such a row is listed but can
    /// neither be selected nor written.
    /// <para>提供 loader 而非分区镜像的行所用的命令值；这样的行列出来，但既不能勾选也不能写入。</para>
    /// </summary>
    internal const string LoaderCommand = "loader";

    /// <summary>
    /// Command value of a row that supplies the partition table instead of a partition image; the flash path
    /// resolves its addresses from that table, so the row itself is never written as an image.
    /// <para>提供分区表而非分区镜像的行所用的命令值；刷写路径从该分区表解析地址，因此这一行本身从不作为镜像写入。</para>
    /// </summary>
    internal const string ParameterCommand = "parameter";
    private byte[]? _rkConfigHeader;

    /// <summary>
    /// Loader image a custom configuration points at; it is handed to the RKDL flash path so a maskrom device
    /// can be booted into loader mode before the write.
    /// <para>自定义配置指向的 loader 镜像；它被交给 RKDL 刷写路径，使 Maskrom 设备能在写入前启动到 loader 模式。</para>
    /// </summary>
    internal string? EffectiveRkLoaderPath
        => _rkLoaderPath is { Length: > 0 } loader && File.Exists(loader) ? loader : null;

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
        var report = await App.DeviceMonitor.RunRkdlAsync(device, session =>
        {
            var capability = session.ReadCapability();
            var flashId = session.ReadFlashId();
            storageLabel = DescribeRkStorage(session.ReadStorage());
            var lines = new List<string>
            {
                $"{language.GetString("Flash_RkCapability")}: 0x{Convert.ToHexString(capability.Raw)}",
                $"DirectLBA={capability.DirectLba} First4M={capability.First4MAccess}",
                $"FlashID: 0x{Convert.ToHexString(flashId)}",
                $"{language.GetString("Flash_RkStorage")}: {storageLabel}"
            };
            return Task.FromResult(lines);
        }, CancellationToken.None).ConfigureAwait(true);
        foreach (var line in report)
        {
            AppendLog(line);
        }

        ApplyReportedRkStorage(storageLabel);
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
        catch (Exception ex) { AppendLog(ex.Message); }
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
        catch (Exception ex) { AppendLog(ex.Message); }
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
        catch (Exception ex) { AppendLog(ex.Message); }
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

        if (Surface is null) return;
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
        catch (Exception ex) { AppendLog(ex.Message); }
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
        catch (Exception ex) { AppendLog(ex.Message); }
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
        catch (Exception ex) { AppendLog(ex.Message); }
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
        catch (Exception ex) { AppendLog(ex.Message); }
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
        catch (Exception ex) { AppendLog(ex.Message); }
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
        if (Surface is null) return false;
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
    private bool RkForceAddress => Surface is not null && Field<CheckBox>("RkForceAddressBox").IsChecked == true;

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
            _rkLoaderPath = null;
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
                        _rkLoaderPath = entry.Path;
                        AppendLog(string.Format(CultureInfo.InvariantCulture,
                            _localizer.GetString(File.Exists(entry.Path)
                                ? "Flash_RkLoaderDeclared"
                                : "Flash_RkLoaderMissing"), entry.Path));
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
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Replaces the whole partition grid with one row per configuration row, so re-importing a configuration never
    /// stacks rows: a recognized <c>Loader</c> row carries the load-loader command and can never be selected or
    /// written, a partition row whose image is missing stays listed but cannot be selected, and a row that names no
    /// file can be selected without starting out selected. The loaded-path
    /// marker follows the configuration so the flash actions match the rows just published.
    /// <para>用配置的每一行整体替换分区列表，使重复导入配置不会叠加条目：识别出的 <c>Loader</c> 行携带加载
    /// loader 命令，永远不能勾选或写入；镜像缺失的分区行仍然列出，但不可勾选；没有指定文件的空路径行可以勾选，
    /// 但导入时默认不勾选。已加载路径标记随之更新，使刷写动作与刚发布的条目一致。</para>
    /// </summary>
    /// <param name="entries">Rows taken from the configuration, in file order.
    /// <para>按文件顺序取自配置的行。</para></param>
    private void ApplyRkConfigRows(IReadOnlyList<RockchipConfigEntry> entries)
    {
        var rows = new List<FlashPartitionEntry>(entries.Count);
        foreach (var entry in entries)
        {
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
                CanSelect = operation == RockchipConfigOperation.Flash && (exists || entry.Path.Length == 0),
                StatusText = exists || entry.Path.Length == 0
                    ? string.Empty
                    : _localizer.GetString("Flash_FileMissing")
            });
        }

        Partitions.Apply(Partitions.Count, rows);
        _loadedPath = PackagePath;
        AppendLog($"{Partitions.Count} {_localizer.GetString("Flash_ImagesLoaded")}");
    }

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