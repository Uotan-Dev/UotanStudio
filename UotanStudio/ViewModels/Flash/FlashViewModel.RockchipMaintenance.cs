using Avalonia.Controls;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Ui;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// Rockchip maintenance actions that hand the erase to the device firmware through the TEST_UNIT_READY
/// sub codes, mirroring the vendor tool's system-disk erase, lower format and user-data erase entries.
/// <para>对应厂商工具「擦除系统盘」「低格」「擦除用户数据」的瑞芯微维护动作，通过 TEST_UNIT_READY 子码把擦除交给设备固件执行。</para>
/// </summary>
public sealed partial class FlashViewModel
{
    /// <summary>
    /// Confirmation token the dangerous dialog requires before a firmware-side erase is allowed to start.
    /// <para>危险确认窗口要求在固件侧擦除开始前输入的确认口令。</para>
    /// </summary>
    internal const string RkMaintenanceConfirmWord = "WIPE";

    /// <summary>Erases the system disk through the device firmware after the dangerous confirmation.
    /// <para>经危险操作确认后，通过设备固件擦除系统盘。</para></summary>
    internal async void OnRkEraseSystemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await RunRkFirmwareEraseAsync("Flash_RkEraseSystem", "Flash_RkEraseSystemConfirm",
            static (session, token) => session.EraseSystemDiskBySubCode(RkMaintenanceConfirmWord,
                cancellationToken: token)).ConfigureAwait(true);

    /// <summary>Lower-formats the storage through the device firmware after the dangerous confirmation.
    /// <para>经危险操作确认后，通过设备固件对存储执行低级格式化。</para></summary>
    internal async void OnRkLowerFormatClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await RunRkFirmwareEraseAsync("Flash_RkLowerFormat", "Flash_RkLowerFormatConfirm",
            static (session, token) => session.LowerFormatBySubCode(RkMaintenanceConfirmWord,
                cancellationToken: token)).ConfigureAwait(true);

    /// <summary>Erases the user-data area through the device firmware after the dangerous confirmation.
    /// <para>经危险操作确认后，通过设备固件擦除用户数据区。</para></summary>
    internal async void OnRkEraseUserDataClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await RunRkFirmwareEraseAsync("Flash_RkEraseUserData", "Flash_RkEraseUserDataConfirm",
            static (session, token) => session.EraseUserDataBySubCode(RkMaintenanceConfirmWord,
                cancellationToken: token)).ConfigureAwait(true);

    /// <summary>
    /// Asks the firmware whether the user-data area is addressable and reports the answer.
    /// <para>询问固件用户数据区是否可寻址并报告结果。</para>
    /// </summary>
    internal async void OnRkUserSectorMapClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Rkdl } device)
        {
            AppendLog(_localizer.GetString("Flash_DeviceNotConnected"));
            return;
        }

        var language = _localizer;
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        SetBusy(true);
        try
        {
            var present = await App.DeviceMonitor.RunRkdlAsync(device,
                session => Task.FromResult(session.GetUserSectorMap()), cancellation.Token).ConfigureAwait(true);
            AppendLog($"{language.GetString("Flash_RkUserSectorMap")}: " +
                language.GetString(present ? "Flash_RkUserSectorMapPresent" : "Flash_RkUserSectorMapAbsent"));
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
    /// Runs one firmware-side erase behind the dangerous confirmation gate and reports the outcome.
    /// <para>在危险操作确认门之后执行一次固件侧擦除并报告结果。</para>
    /// </summary>
    /// <param name="titleKey">Resource key of the action name. <para>动作名对应的资源键。</para></param>
    /// <param name="detailKey">Resource key of the warning detail. <para>警示说明对应的资源键。</para></param>
    /// <param name="operation">The session operation to run once confirmed.
    /// <para>确认后执行的会话操作。</para></param>
    private async Task RunRkFirmwareEraseAsync(string titleKey, string detailKey,
        Action<RkdlSession, CancellationToken> operation)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Rkdl } device ||
            TopLevel.GetTopLevel(Surface) is not Window owner) return;

        var language = _localizer;
        var title = language.GetString(titleKey);
        bool confirmed;
        _confirming = true;
        try
        {
            confirmed = await DialogService.ConfirmDangerousAsync(owner, new DangerousConfirmRequest
            {
                TabTitle = language.GetString("Flash_Rockchip"),
                Title = title,
                Detail = language.GetString(detailKey),
                ConfirmWord = RkMaintenanceConfirmWord,
                DeviceIdentifier = device.DisplayName
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
            await App.DeviceMonitor.RunRkdlAsync(device, session =>
            {
                operation(session, cancellation.Token);
                return Task.FromResult(true);
            }, cancellation.Token).ConfigureAwait(true);
            AppendLog($"{title}: {language.GetString("Flash_Done")}");
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
}