using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Ui;
using UotanStudio.UiAbstractions;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// Allwinner page actions: status readout, the FEL memory tools, and the FEL-to-FES service switch driven by a
/// first-stage loader and a service payload. FES flashing waits for an image plan and is not offered yet.
/// <para>全志页面动作：状态读取、FEL 内存工具，以及由一级加载器与服务负载驱动的 FEL 到 FES 切换。FES 烧录等待
/// 镜像计划，暂不提供。</para>
/// </summary>
public sealed partial class FlashViewModel
{
    /// <summary>Cooperative bound for one Sunxi status query, matching the monitor's own bound.
    /// <para>单次全志状态查询的协作式时限，与监视服务自身的时限一致。</para></summary>
    private const int SunxiStatusTimeoutMs = 5000;

    /// <summary>Largest payload the page hands to one FEL memory operation; larger files stay out of the page.
    /// <para>页面交给单次 FEL 内存操作的最大负载；更大的文件不允许进入页面。</para></summary>
    private const long SunxiMemoryLimitBytes = 64 * 1024 * 1024;

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

    /// <summary>Memory address used by the read and write actions. <para>读取与写入动作使用的内存地址。</para></summary>
    [ObservableProperty]
    private string _sunxiAddress = "0x40000000";

    /// <summary>Byte count used by the read action. <para>读取动作使用的字节数。</para></summary>
    [ObservableProperty]
    private string _sunxiLength = "256";

    /// <summary>Entry address used by the execute action. <para>执行动作使用的入口地址。</para></summary>
    [ObservableProperty]
    private string _sunxiExecuteAddress = "0x40000000";

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
    /// Queries the selected endpoint once and publishes the identity, mode and — in FES mode — the storage answers.
    /// The mode also updates the device badge, because discovery cannot tell FEL from FES.
    /// <para>查询一次所选端点并发布身份、模式，以及 FES 模式下的存储应答。模式同时更新设备徽章，因为发现阶段无法区分
    /// FEL 与 FES。</para>
    /// </summary>
    internal async void RefreshSunxiDeviceStatus()
    {
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Sunxi } device)
        {
            SunxiStatusText = string.Empty;
            return;
        }

        var language = _localizer;
        try
        {
            using var timeout = new CancellationTokenSource(SunxiStatusTimeoutMs);
            var status = await Task.Run(() => App.DeviceMonitor.QuerySunxiStatusAsync(device, timeout.Token)
                .GetAwaiter().GetResult(), timeout.Token).ConfigureAwait(true);
            PublishSunxiStatus(status, language);
        }
        catch (OperationCanceledException)
        {
            SunxiStatusText = language.GetString("Flash_SunxiStatusTimeout");
        }
        catch (Exception exception)
        {
            SunxiStatusText = language.GetString("Flash_SunxiStatusFailed") + " " + exception.Message;
        }
    }

    /// <summary>Re-reads the selected endpoint and refreshes its device badge.
    /// <para>重新读取所选端点并刷新其设备徽章。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件来源。</para></param>
    /// <param name="e">Event arguments. <para>事件参数。</para></param>
    internal void OnSunxiRefreshClick(object? sender, RoutedEventArgs e) => RefreshSunxiDeviceStatus();

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
                DeviceIdentifier = _deviceViewModel?.SelectedDevice?.DisplayName
            }).ConfigureAwait(true);
        }
        finally
        {
            _confirming = false;
        }

        if (!confirmed) return;

        byte[] fes1;
        byte[] uboot;
        try
        {
            fes1 = await File.ReadAllBytesAsync(SunxiFes1Path).ConfigureAwait(true);
            uboot = await File.ReadAllBytesAsync(SunxiUbootPath).ConfigureAwait(true);
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
            var status = await App.DeviceMonitor.RunSunxiBootServiceAsync(device, fes1, uboot, progress,
                cancellation.Token).ConfigureAwait(true);
            PublishSunxiStatus(status, language);
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
                DeviceIdentifier = _deviceViewModel?.SelectedDevice?.DisplayName
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

    private void PublishSunxiStatus(SunxiDeviceStatus status, ILocalizer language)
    {
        var mode = status.IsFes
            ? language.GetString("Flash_SunxiModeFes")
            : language.GetString("Flash_SunxiModeFel");
        var parts = new List<string>
        {
            status.ChipName,
            mode,
            $"FW 0x{status.Identity.Firmware:X}"
        };
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

        SunxiStatusText = string.Join(" · ", parts);
        if (_deviceViewModel?.SelectedDevice is { } info)
        {
            info.SunxiMode = status.IsFes ? "FES" : "FEL";
        }
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