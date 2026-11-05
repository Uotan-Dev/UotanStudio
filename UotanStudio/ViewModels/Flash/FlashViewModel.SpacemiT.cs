using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Comm.Protocols.SpacemiT;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Ui;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// SpacemiT page actions: choosing a flash package directory (the unpacked titan flasher layout with
/// <c>fastboot.yaml</c> at its root) and flashing it against the selected SpacemiT K1/K3 fastboot endpoint
/// behind the dangerous confirmation gate.
/// <para>进迭时空页面动作：选择烧录包目录（根目录含 <c>fastboot.yaml</c> 的 titan 烧录器解包布局），并在
/// 危险确认门之后对所选进迭时空 K1/K3 fastboot 端点执行整包烧录。</para>
/// </summary>
public sealed partial class FlashViewModel
{
    /// <summary>The loaded flash package; <see langword="null"/> until a package directory validates.
    /// <para>已加载的烧录包；包目录校验通过前为 <see langword="null"/>。</para></summary>
    private SpacemiTFlashPackage? _spacemiTPackage;

    /// <summary>Display path of the chosen flash package directory; empty while none is loaded.
    /// <para>所选烧录包目录的展示路径；未加载时为空。</para></summary>
    [ObservableProperty]
    private string _spacemiTPackagePath = string.Empty;

    /// <summary>Whether the device reboots into the system after the package script finished.
    /// <para>包脚本完成后设备是否重启进入系统。</para></summary>
    [ObservableProperty]
    private bool _spacemiTRebootAfter;

    /// <summary>One-line transfer status, refreshed by byte-level progress reports.
    /// <para>单行传输状态，由字节级进度报告刷新。</para></summary>
    [ObservableProperty]
    private string _spacemiTStatusText = string.Empty;

    /// <summary>
    /// SpacemiT section: the pane and both buttons answer only on the SpacemiT fastboot gadget itself, so each of them
    /// carries that condition instead of a page-wide lookup; the header stays present for every endpoint.
    /// <para>进迭时空分类：面板与两个按钮只在进迭时空 fastboot gadget 本身应答，因此各自持有该条件，而不是依赖页面级查表；
    /// 表头对任何端点都保持呈现。</para>
    /// </summary>
    private void RegisterSpacemiTControlGates()
    {
        RegisterGate("SpacemiTExpander", FlashControlGate.SectionHeader("SpacemiTExpander"));
        RegisterGate("SpacemiTPane", FlashControlGate.SpacemiT("SpacemiTPane"));
        RegisterGate("SpacemiTChoosePackageButton", FlashControlGate.SpacemiT("SpacemiTChoosePackageButton"));
        RegisterGate("SpacemiTFlashButton", FlashControlGate.SpacemiT("SpacemiTFlashButton"));
    }

    /// <summary>
    /// Opens the folder picker, validates the directory as a SpacemiT flash package and records it for the
    /// flash action.
    /// <para>打开文件夹选择器，把目录校验为进迭时空烧录包，并为烧录动作记录它。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件来源。</para></param>
    /// <param name="e">Event arguments. <para>事件参数。</para></param>
    internal async void OnSpacemiTChoosePackageClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;

        var picked = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _localizer.GetString("Flash_SpacemiTChoosePackage"),
            AllowMultiple = false
        }).ConfigureAwait(true);
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;

        try
        {
            _spacemiTPackage = SpacemiTFlashPackage.Load(path);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidDataException or IOException)
        {
            _spacemiTPackage = null;
            SpacemiTPackagePath = string.Empty;
            SpacemiTStatusText = string.Empty;
            AppendError(_localizer.GetString("Flash_SpacemiTPackageMissing") + Environment.NewLine + ex.Message);
            SetControlEnabled("SpacemiTFlashButton", false);
            return;
        }

        SpacemiTPackagePath = _spacemiTPackage.RootPath;
        SpacemiTStatusText = string.Empty;
        AppendLog($"[SpacemiT] {Path.GetFileName(path)}: v{_spacemiTPackage.Script.Version}, " +
            $"{_spacemiTPackage.Script.Actions.Count} actions");
        SetControlEnabled("SpacemiTFlashButton", true);
    }

    /// <summary>
    /// Flashes the loaded package to the selected SpacemiT fastboot endpoint after the typed "FLASH"
    /// confirmation, streaming the flasher's progress into the page log and the section status line.
    /// <para>在输入 "FLASH" 的危险确认之后，把已加载的烧录包烧写到所选进迭时空 fastboot 端点，并把烧录器的
    /// 进度流入页面日志与分类状态行。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件来源。</para></param>
    /// <param name="e">Event arguments. <para>事件参数。</para></param>
    internal async void OnSpacemiTFlashClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (_spacemiTPackage is null) return;
        if (_deviceViewModel?.SelectedDevice is not { IsSpacemiTFastboot: true }) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Fastboot } device) return;
        if (TopLevel.GetTopLevel(Surface) is not Window owner) return;

        var language = _localizer;
        bool confirmed;
        _confirming = true;
        try
        {
            confirmed = await DialogService.ConfirmDangerousAsync(owner, new DangerousConfirmRequest
            {
                TabTitle = language.GetString("Flash_SpacemiT"),
                Title = language.GetString("Flash_SpacemiTFlash"),
                Detail = language.GetString("Flash_SpacemiTConfirmDetail"),
                ConfirmWord = SpacemiTFlashOptions.RequiredConfirmation,
                DeviceIdentifier = _deviceViewModel?.SelectedDevice?.DisplayName
            }).ConfigureAwait(true);
        }
        finally
        {
            _confirming = false;
        }

        if (!confirmed) return;
        var package = _spacemiTPackage;
        var options = new SpacemiTFlashOptions
        {
            FlashConfirmation = SpacemiTFlashOptions.RequiredConfirmation,
            RebootAfterFlash = SpacemiTRebootAfter,
        };
        // 页面勾选的分区收窄本次烧录（OnlyPartitions）；未勾选任何行时保持整包行为。
        var partitionCount = Partitions.Count(entry => entry.Name.Length > 0);
        var chosen = Partitions.Where(entry => entry.IsSelected && entry.Name.Length > 0)
            .Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (chosen.Length > 0 && chosen.Length < partitionCount)
        {
            options.OnlyPartitions = chosen;
        }
        var progress = new Progress<SpacemiTFlashProgress>(update =>
        {
            if (update.BytesCompleted is { } done && update.BytesTotal is { } total && total > 0)
            {
                SpacemiTStatusText = $"{update.Partition}: {done * 100.0 / total:F1}%";
                return;
            }

            AppendLog($"[SpacemiT] {update.Message}");
        });

        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        SetBusy(true);
        _deviceViewModel?.BeginSelectionHold();
        try
        {
            await App.DeviceMonitor.RunSpacemiTFlashAsync(device, package, options, progress,
                cancellation.Token).ConfigureAwait(true);
            SpacemiTStatusText = string.Empty;
            AppendLog($"{language.GetString("Flash_SpacemiTFlash")}: {language.GetString("Flash_Done")}");
        }
        catch (OperationCanceledException)
        {
            AppendLog(language.GetString("Flash_Canceled"));
        }
        catch (Exception ex)
        {
            AppendError(ex);
        }
        finally
        {
            SetBusy(false);
            _flashCancellation = null;
            UpdateEdlButton();
        }
    }
}