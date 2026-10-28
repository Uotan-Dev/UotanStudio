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
    private byte[]? _rkConfigHeader;

    /// <summary>
    /// Loader image a custom configuration points at; it is handed to the RKDL flash path so a maskrom device
    /// can be booted into loader mode before the write.
    /// <para>自定义配置指向的 loader 镜像；它被交给 RKDL 刷写路径，使 Maskrom 设备能在写入前启动到 loader 模式。</para>
    /// </summary>
    internal string? EffectiveRkLoaderPath
        => _rkLoaderPath is { Length: > 0 } loader && File.Exists(loader) ? loader : null;

    /// <summary>
    /// Enables the Rockchip-only actions when the selected device speaks RKDL and no operation is running.
    /// <para>仅当所选设备使用 RKDL 且没有操作在运行时启用瑞芯微专属动作。</para>
    /// </summary>
    /// <param name="isRockchip">Whether the selected device uses RKDL.<para>所选设备是否使用 RKDL。</para></param>
    private void UpdateRockchipActionState(bool isRockchip)
    {
        if (Surface is null) return;
        var enabled = !_busy && isRockchip;
        foreach (var name in new[]
                 {
                     "RkReadInfoButton", "RkTestDeviceButton", "RkGetStorageButton", "RkSwitchStorageButton",
                     "RkEraseFlashButton", "RkLbaReadButton", "RkLbaWriteButton", "RkEraseSectorsButton"
                 })
        {
            Field<Button>(name).IsEnabled = enabled;
        }

        Field<TextBox>("RkLbaAddressBox").IsEnabled = enabled;
        Field<TextBox>("RkLbaCountBox").IsEnabled = enabled;
        Field<CheckBox>("RkForceAddressBox").IsEnabled = enabled;
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
        var report = await App.DeviceMonitor.RunRkdlAsync(device, session =>
        {
            var capability = session.ReadCapability();
            var flashId = session.ReadFlashId();
            var storage = session.ReadStorage();
            var lines = new List<string>
            {
                $"{language.GetString("Flash_RkCapability")}: 0x{Convert.ToHexString(capability.Raw)}",
                $"DirectLBA={capability.DirectLba} First4M={capability.First4MAccess}",
                $"FlashID: 0x{Convert.ToHexString(flashId)}",
                $"{language.GetString("Flash_RkStorage")}: {DescribeRkStorage(storage)}"
            };
            return Task.FromResult(lines);
        }, CancellationToken.None).ConfigureAwait(true);
        foreach (var line in report)
        {
            AppendLog(line);
        }

        ApplyReportedRkStorage(report[^1]);
    }

    /// <summary>
    /// Maps a protocol storage number to the selector label, or reports the raw number when it is unknown.
    /// <para>把协议存储编号映射为选择框文本；未知编号直接报告原值。</para>
    /// </summary>
    /// <param name="storage">Protocol storage number.<para>协议存储编号。</para></param>
    /// <returns>The label shown in the selector.<para>选择框中显示的文本。</para></returns>
    private static string DescribeRkStorage(int storage)
        => storage > 0 && storage <= RkStorageTypes.Length
            ? RkStorageTypes[storage - 1]
            : storage.ToString(CultureInfo.InvariantCulture);

    private void ApplyReportedRkStorage(string reported)
    {
        if (Surface is null) return;
        var index = Array.IndexOf(RkStorageTypes, reported);
        if (index >= 0)
        {
            Field<ComboBox>("RkStorageTypeBox").SelectedIndex = index;
        }
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
            AppendLog(_localizer.GetString("Flash_RkSwitchStorage"));
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

    /// <summary>Writes a file at an LBA address; the forced-address switch only documents intent.
    /// <para>把文件写入指定 LBA 地址；“强制按地址写”开关只表达意图。</para>
    /// </summary>
    internal async void OnRkLbaWriteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Rkdl } device) return;
        if (!TryReadRkLbaRange(out var address, out _)) return;

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
    /// Asks for a download-list configuration and imports it.
    /// <para>选择下载列表配置并导入。</para>
    /// </summary>
    internal async void OnRkImportConfigClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString("Flash_RkImportConfig"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(_localizer.GetString("Flash_RkConfigFileType")) { Patterns = ["*.cfg"] },
                FilePickerFileTypes.All
            ]
        });
        if (files.Count != 1 || files[0].TryGetLocalPath() is not { } path || !File.Exists(path)) return;
        await ImportRkConfigAsync(path);
    }

    /// <summary>
    /// Imports a download-list configuration: the loader row supplies the RKDL loader image and the first
    /// existing image row becomes the loaded package so its rows can be flashed directly.
    /// <para>导入下载列表配置：Loader 行提供 RKDL loader 镜像，第一条存在的镜像行作为已加载刷机包，
    /// 使其中的行可直接刷写。</para>
    /// </summary>
    /// <param name="path">Existing configuration file; parse failures are logged instead of thrown.
    /// <para>既有的配置文件；解析失败只记录日志，不抛出异常。</para></param>
    internal async Task ImportRkConfigAsync(string path)
    {
        try
        {
            var (header, entries) = RockchipConfigFile.Load(path);
            _rkConfigHeader = header;
            foreach (var entry in entries)
            {
                AppendLog($"{entry.Name}: {entry.Path}");
                if (string.Equals(entry.Name, "Loader", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(entry.Path))
                {
                    _rkLoaderPath = entry.Path;
                }
            }

            AppendLog(string.Format(CultureInfo.InvariantCulture,
                _localizer.GetString("Flash_RkConfigImported"), $"{entries.Count}"));
            var image = entries.FirstOrDefault(entry =>
                !string.Equals(entry.Name, "Loader", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(entry.Path) &&
                !RockchipConfigFile.LooksLike(entry.Path));
            if (image is not null)
            {
                PackagePath = image.Path;
                await LoadPackageAsync(image.Path).ConfigureAwait(true);
            }
            else
            {
                PackagePath = path;
            }
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            UpdateEdlButton();
        }
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
            entries.Add(new RockchipConfigEntry { Name = "Loader", Path = loader });
        }

        if (PackagePath is { Length: > 0 } package)
        {
            entries.Add(new RockchipConfigEntry { Name = "Image", Path = package });
        }

        foreach (var row in Partitions.Where(row => row.FilePath.Length > 0))
        {
            entries.Add(new RockchipConfigEntry
            {
                Name = row.Name,
                Path = row.FilePath,
                Address = TryParseSectorNumber(row.Offset, out var offset) ? (ulong)offset : 0UL
            });
        }

        RockchipConfigFile.Save(path, _rkConfigHeader ?? RockchipConfigFile.CreateHeader(), entries);
    }
}