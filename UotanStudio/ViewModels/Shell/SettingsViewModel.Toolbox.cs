using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Comm.Transports.Usb.Drivers;
using UotanStudio.Hash.Simd;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.HardwareAcceleration;
using UotanStudio.Services.Toolbox;
using UotanStudio.Services.Ui;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Toolbox, hardware acceleration and driver installation sections of the settings page.
/// <para>设置页的工具箱、硬件加速与驱动安装区域。</para>
/// </summary>
/// <remarks>
/// The toolbox rows are rebuilt on demand rather than observed, because every size is a directory walk: the card
/// exposes one refresh command and re-reads after each purge. Embedded bundles are never deleted; only their derived
/// materialization cache is, and the frames stay in the assembly so the next use rebuilds them.
/// <para>工具箱行按需重建而不是持续观察，因为每个体积都是一次目录遍历：卡片提供一个刷新命令，并在每次清除后重新读取。
/// 内嵌资源包绝不删除，只删除其派生出的物化缓存；帧仍在程序集中，下次使用时会重建。</para>
/// </remarks>
public partial class SettingsViewModel
{
    /// <summary>
    /// Gets the toolbox rows: embedded bundles with their driver packages, rebuildable download caches, and the
    /// root patch packages already on disk.
    /// <para>获取工具箱行：内嵌资源包及其驱动包、可重建下载缓存，以及磁盘上已有的 root 补丁包。</para>
    /// </summary>
    public ObservableCollection<ToolboxContentItem> ToolboxItems { get; } = [];

    /// <summary>
    /// Gets the embedded toolbox rows (embedded asset bundles and vendor driver packages).
    /// <para>获取内嵌工具箱行（内嵌资源包与厂商驱动包）。</para>
    /// </summary>
    public ObservableCollection<ToolboxContentItem> EmbeddedToolboxItems { get; } = [];

    /// <summary>
    /// Gets the downloaded toolbox rows (rebuildable download caches and downloaded root patch packages).
    /// <para>获取已下载的工具箱行（可重建下载缓存与已下载的 root 补丁包）。</para>
    /// </summary>
    public ObservableCollection<ToolboxContentItem> DownloadedToolboxItems { get; } = [];

    /// <summary>The selectable dispatch targets in dropdown order.<para>下拉框顺序中的可选派发目标。</para></summary>
    private HardwareAccelerationTarget[] _hardwareAccelerationTargets = [];

    /// <summary>The index of the persisted choice inside <see cref="_hardwareAccelerationTargets"/>.
    /// <para>持久化选择在 <see cref="_hardwareAccelerationTargets"/> 中的索引。</para></summary>
    private int _hardwareAccelerationIndex;

    /// <summary>The dropdown entries; rebuilt whenever the target list changes.
    /// <para>下拉条目；目标列表变化时重建。</para></summary>
    private LocalizedOptionSet _hardwareAccelerationOptions = LocalizedOptionSet.FromOptions([]);


    /// <summary>Gets or sets the inline result of the latest toolbox action.
    /// <para>获取或设置最近一次工具箱操作的行内结果。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolboxStatus))]
    private string _toolboxStatus = string.Empty;

    /// <summary>Gets or sets whether the inline result reports a failure.
    /// <para>获取或设置行内结果是否表示失败。</para>
    /// </summary>
    [ObservableProperty]
    private bool _toolboxStatusIsError;

    /// <summary>Gets or sets whether a toolbox action is running.
    /// <para>获取或设置工具箱操作是否正在运行。</para>
    /// </summary>
    [ObservableProperty]
    private bool _isToolboxBusy;

    /// <summary>Gets whether the toolbox card shows an inline status line.
    /// <para>获取工具箱卡片是否显示行内状态行。</para>
    /// </summary>
    public bool HasToolboxStatus => ToolboxStatus.Length > 0;

    /// <summary>Gets the aggregated footprint summary of the toolbox card.
    /// <para>获取工具箱卡片占用合计摘要。</para>
    /// </summary>
    public string ToolboxSummary => string.Format(
        LanguageService.Instance.GetString("Toolbox_Summary"),
        ToolboxItems.Count,
        FormatByteSize(ToolboxItems.Sum(item => item.Row.PresentBytes)));

    private void RefreshToolboxLocalizedState()
    {
        foreach (var item in ToolboxItems)
        {
            item.RefreshLocalizedText();
        }

        OnPropertyChanged(nameof(HardwareAccelerationSummary));
    }

    /// <summary>
    /// Rebuilds the toolbox and driver rows from their sources.
    /// <para>从各自的来源重建工具箱行与驱动行。</para>
    /// </summary>
    [RelayCommand]
    private async Task RefreshToolboxAsync()
    {
        if (IsToolboxBusy)
        {
            return;
        }

        IsToolboxBusy = true;
        try
        {
            SetToolboxStatus(string.Empty, isError: false);
            await RebuildToolboxRowsAsync();
        }
        catch (Exception ex)
        {
            SetToolboxStatus(string.Format(
                LanguageService.Instance.GetString("Toolbox_RefreshFailed"), UserError.Describe(ex)), isError: true);
        }
        finally
        {
            IsToolboxBusy = false;
        }
    }

    /// <summary>
    /// Rebuilds the toolbox rows in place without touching the inline result or the busy latch, so an action can
    /// refresh the card after it finishes and still leave its own message on screen.
    /// <para>原地重建工具箱行，不改动行内结果与忙碌标记，使动作结束后既能刷新卡片又能保留自己的提示。</para>
    /// </summary>
    private async Task RebuildToolboxRowsAsync()
    {
        var rows = await Task.Run(ToolboxContentService.Enumerate);
        ToolboxItems.Clear();
        EmbeddedToolboxItems.Clear();
        DownloadedToolboxItems.Clear();
        foreach (var row in rows)
        {
            var item = row.Kind == ToolboxContentKind.DriverPackage
                ? new ToolboxContentItem(row, IgnoreToolboxAction,
                    target => RunToolboxAction(ExportToolboxBundle, target),
                    target => RunToolboxAction(ToggleDriverPackage, target))
                : new ToolboxContentItem(row,
                    target => RunToolboxAction(PurgeToolboxCache, target),
                    target => RunToolboxAction(ExportToolboxBundle, target),
                    null,
                    (target, entry) => RunToolboxAction(
                        _ => DeleteToolboxEntryAsync(target, entry), target));
            ToolboxItems.Add(item);
            if (row.Kind is ToolboxContentKind.DownloadCache or ToolboxContentKind.RootPatchPackage)
            {
                DownloadedToolboxItems.Add(item);
            }
            else
            {
                EmbeddedToolboxItems.Add(item);
            }
        }

        OnPropertyChanged(nameof(ToolboxSummary));
    }

    /// <summary>
    /// Deletes the derived cache behind one toolbox row and reports the freed space.
    /// <para>删除某工具箱行的派生缓存并报告释放的空间。</para>
    /// </summary>
    /// <param name="item">The row whose cache is deleted.<para>要删除其缓存的行。</para></param>
    private async Task PurgeToolboxCache(ToolboxContentItem item)
    {
        if (!item.CanPurgeCache)
        {
            return;
        }

        var language = LanguageService.Instance;
        var owner = DialogService.GetOwnerWindow();
        if (owner is null || !await DialogService.ConfirmAsync(
                owner,
                language.GetString("Toolbox_PurgeTitle"),
                string.Format(language.GetString("Toolbox_PurgeMessage"), item.DisplayName),
                language.GetString("Toolbox_PurgeConfirm")))
        {
            return;
        }

        var freed = item.Row.PresentBytes;
        try
        {
            await Task.Run(() => ToolboxContentService.Purge(item.Row));
            SetToolboxStatus(string.Format(
                language.GetString("Toolbox_PurgeSucceeded"), item.DisplayName, FormatByteSize(freed)), isError: false);
        }
        catch (Exception ex)
        {
            SetToolboxStatus(string.Format(
                language.GetString("Toolbox_PurgeFailed"), item.DisplayName, UserError.Describe(ex)), isError: true);
        }

        await RebuildToolboxRowsAsync();
    }

    /// <summary>
    /// Deletes one downloaded artifact inside a cache or patch row while leaving that row's other versions in place.
    /// <para>删除缓存行或补丁行中的一个已下载产物，同时保留该行的其它版本。</para>
    /// </summary>
    /// <param name="item">The row that owns the artifact.<para>持有该产物的行。</para></param>
    /// <param name="entry">The single artifact to delete.<para>要删除的单个产物。</para></param>
    private async Task DeleteToolboxEntryAsync(ToolboxContentItem item, ToolboxContentEntryItem entry)
    {
        if (!item.CanPurgeCache)
        {
            return;
        }

        var language = LanguageService.Instance;
        var owner = DialogService.GetOwnerWindow();
        if (owner is null || !await DialogService.ConfirmAsync(
                owner,
                language.GetString("Toolbox_EntryDeleteTitle"),
                string.Format(language.GetString("Toolbox_EntryDeleteMessage"), entry.Name),
                language.GetString("Toolbox_EntryDeleteConfirm")))
        {
            return;
        }

        try
        {
            await Task.Run(() => ToolboxContentService.PurgeEntry(item.Row, entry.Entry));
            SetToolboxStatus(string.Format(
                language.GetString("Toolbox_EntryDeleteSucceeded"), entry.Name), isError: false);
        }
        catch (Exception ex)
        {
            SetToolboxStatus(string.Format(
                language.GetString("Toolbox_EntryDeleteFailed"), entry.Name, UserError.Describe(ex)), isError: true);
        }

        await RebuildToolboxRowsAsync();
    }

    /// <summary>
    /// Exports one embedded bundle into a directory the user picks.
    /// <para>把一个内嵌资源包导出到用户选择的目录。</para>
    /// </summary>
    /// <param name="item">The bundle row to export.<para>要导出的资源包行。</para></param>
    private async Task ExportToolboxBundle(ToolboxContentItem item)
    {
        if (!item.CanExport)
        {
            return;
        }

        var language = LanguageService.Instance;
        if (TopLevelExtensions.GetTopLevel() is not { StorageProvider.CanPickFolder: true } topLevel)
        {
            SetToolboxStatus(language.GetString("Toolbox_ExportNoPicker"), isError: true);
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = string.Format(language.GetString("Toolbox_ExportPick"), item.DisplayName),
            AllowMultiple = false
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { Length: > 0 } destination)
        {
            return;
        }

        try
        {
            var bundleKey = ToolboxContentService.BundleKeyFor(item.Row);
            var files = await Task.Run(() => ToolboxContentService.ExportBundle(bundleKey, destination));
            SetToolboxStatus(string.Format(
                language.GetString("Toolbox_ExportSucceeded"), item.DisplayName, files.Count, destination),
                isError: false);
        }
        catch (Exception ex)
        {
            SetToolboxStatus(string.Format(
                language.GetString("Toolbox_ExportFailed"), item.DisplayName, UserError.Describe(ex)), isError: true);
        }
    }

    /// <summary>
    /// Installs or removes one vendor driver package, following the state the row currently reports.
    /// <para>按该行当前报告的状态安装或移除一个厂商驱动包。</para>
    /// </summary>
    /// <param name="item">The driver package row to toggle.<para>要切换的驱动包行。</para></param>
    private Task ToggleDriverPackage(ToolboxContentItem item)
        => item.IsInstalled ? UninstallDriverPackage(item) : InstallDriverPackage(item);

    /// <summary>
    /// Stages one vendor driver package into the Windows driver store, elevating through the shared driver service.
    /// <para>通过共用的驱动服务（必要时提权）把一个厂商驱动包暂存到 Windows 驱动仓库。</para>
    /// </summary>
    /// <param name="item">The driver package row to install.<para>要安装的驱动包行。</para></param>
    private async Task InstallDriverPackage(ToolboxContentItem item)
    {
        if (!item.IsDriver || !item.IsShipped)
        {
            return;
        }

        var language = LanguageService.Instance;
        var owner = DialogService.GetOwnerWindow();
        if (owner is null || !await DialogService.ConfirmAsync(
                owner,
                language.GetString("Settings_DriverInstallTitle"),
                string.Format(language.GetString("Settings_DriverInstallMessage"), item.DisplayName),
                language.GetString("Settings_DriverInstallConfirm")))
        {
            return;
        }

        try
        {
            var result = await Task.Run(() => StageDriverPackage(item.Id));
            var (key, isError) = result.Status switch
            {
                WinUsbDriverOperationStatus.Succeeded => ("Settings_DriverInstallSucceeded", false),
                WinUsbDriverOperationStatus.ElevationRequired => ("Settings_DriverInstallElevation", true),
                _ => ("Settings_DriverInstallFailed", true),
            };
            var detail = result.Status == WinUsbDriverOperationStatus.Succeeded
                ? string.Empty
                : " " + (result.Message ?? string.Empty);
            SetToolboxStatus(string.Format(language.GetString(key), item.DisplayName) + detail, isError);
        }
        catch (Exception ex)
        {
            SetToolboxStatus(string.Format(
                    language.GetString("Settings_DriverInstallFailed"), item.DisplayName) + " " + UserError.Describe(ex),
                isError: true);
        }
    }

    /// <summary>
    /// Removes one vendor driver package from the Windows driver store, elevating through the shared driver service.
    /// The embedded payload stays in the build, so the row turns back into an install action afterwards.
    /// <para>通过共用的驱动服务（必要时提权）从 Windows 驱动仓库中移除一个厂商驱动包。内嵌负载仍留在构建中，
    /// 因此该行随后会变回安装动作。</para>
    /// </summary>
    /// <param name="item">The driver package row to remove.<para>要移除的驱动包行。</para></param>
    private async Task UninstallDriverPackage(ToolboxContentItem item)
    {
        if (!item.IsDriver || !item.IsInstalled)
        {
            return;
        }

        var language = LanguageService.Instance;
        var owner = DialogService.GetOwnerWindow();
        if (owner is null || !await DialogService.ConfirmAsync(
                owner,
                language.GetString("Settings_DriverUninstallTitle"),
                string.Format(language.GetString("Settings_DriverUninstallMessage"), item.DisplayName),
                language.GetString("Settings_DriverUninstallConfirm")))
        {
            return;
        }

        try
        {
            var result = await Task.Run(() => RemoveDriverPackage(item.Id));
            var (key, isError) = result.Status switch
            {
                WinUsbDriverOperationStatus.Succeeded => ("Settings_DriverUninstallSucceeded", false),
                WinUsbDriverOperationStatus.ElevationRequired => ("Settings_DriverInstallElevation", true),
                _ => ("Settings_DriverUninstallFailed", true),
            };
            var detail = result.Status == WinUsbDriverOperationStatus.Succeeded
                ? string.Empty
                : " " + (result.Message ?? string.Empty);
            SetToolboxStatus(string.Format(language.GetString(key), item.DisplayName) + detail, isError);
        }
        catch (Exception ex)
        {
            SetToolboxStatus(string.Format(
                    language.GetString("Settings_DriverUninstallFailed"), item.DisplayName) + " " +
                UserError.Describe(ex),
                isError: true);
        }

        await RebuildToolboxRowsAsync();
    }

    private void IgnoreToolboxAction(ToolboxContentItem item)
    {
    }

    /// <summary>Runs one row action without blocking the UI thread and reports failures through the card status.
    /// <para>在不阻塞 UI 线程的前提下执行某一行的动作，并把失败交给卡片状态行。</para>
    /// </summary>
    /// <param name="action">The row action to execute.<para>要执行的行动作。</para></param>
    /// <param name="item">The row the action targets.<para>动作目标行。</para></param>
    private async void RunToolboxAction(Func<ToolboxContentItem, Task> action, ToolboxContentItem item)
    {
        if (IsToolboxBusy)
        {
            return;
        }

        IsToolboxBusy = true;
        try
        {
            SetToolboxStatus(string.Empty, isError: false);
            await action(item);
        }
        catch (Exception ex)
        {
            SetToolboxStatus(UserError.Describe(ex), isError: true);
        }
        finally
        {
            IsToolboxBusy = false;
        }
    }

    /// <summary>Stages one vendor driver package through the shared driver service.
    /// <para>通过共用的驱动服务暂存一个厂商驱动包。</para>
    /// </summary>
    /// <param name="packageId">Driver package id from the catalog.<para>目录中的驱动包标识。</para></param>
    /// <returns>The outcome category and the optional service message.
    /// <para>结果类别与可选的服务消息。</para></returns>
    /// <remarks>Returns <see cref="WinUsbDriverOperationStatus.ElevationRequired"/> off Windows, where no driver store exists.
    /// <para>非 Windows 平台没有驱动仓库，直接返回 <see cref="WinUsbDriverOperationStatus.ElevationRequired"/>。</para></remarks>
    private static (WinUsbDriverOperationStatus Status, string? Message) StageDriverPackage(string packageId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (WinUsbDriverOperationStatus.ElevationRequired, null);
        }

        var result = UsbDriverConfigService.Default.InstallPackage(packageId);
        return (result.Status, result.Message);
    }

    /// <summary>Removes one vendor driver package through the shared driver service.
    /// <para>通过共用的驱动服务移除一个厂商驱动包。</para>
    /// </summary>
    /// <param name="packageId">Driver package id from the catalog.<para>目录中的驱动包标识。</para></param>
    /// <returns>The outcome category and the optional service message.
    /// <para>结果类别与可选的服务消息。</para></returns>
    /// <remarks>Returns <see cref="WinUsbDriverOperationStatus.ElevationRequired"/> off Windows, where no driver store exists.
    /// <para>非 Windows 平台没有驱动仓库，直接返回 <see cref="WinUsbDriverOperationStatus.ElevationRequired"/>。</para></remarks>
    private static (WinUsbDriverOperationStatus Status, string? Message) RemoveDriverPackage(string packageId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (WinUsbDriverOperationStatus.ElevationRequired, null);
        }

        var result = UsbDriverConfigService.Default.UninstallPackage(packageId);
        return (result.Status, result.Message);
    }
    private void SetToolboxStatus(string text, bool isError)
    {
        ToolboxStatus = text;
        ToolboxStatusIsError = isError;
    }

    /// <summary>
    /// Gets the selectable hardware acceleration targets: the automatic entry, every family this processor reports,
    /// then the scalar software loop.
    /// <para>获取可选的硬件加速目标：自动项、当前处理器报告的全部指令族，然后是标量软件循环。</para>
    /// </summary>
    /// <value>The dropdown entries backed by localized technical labels.
    /// <para>由本地化技术标签构成的下拉条目。</para></value>
    public IReadOnlyList<LocalizedOption> HardwareAccelerationOptions => _hardwareAccelerationOptions.Options;

    /// <summary>
    /// Gets or sets the selected hardware acceleration target; the change is stored and installed on the running
    /// process right away.
    /// <para>获取或设置选中的硬件加速目标；改动会被保存并立即安装到当前进程。</para>
    /// </summary>
    /// <value>The index in <see cref="HardwareAccelerationOptions"/>; <c>-1</c> when the list is empty.
    /// <para><see cref="HardwareAccelerationOptions"/> 中的索引；列表为空时为 <c>-1</c>。</para></value>
    /// <remarks>
    /// Byte-block dispatch re-resolves the process ceiling on every call, so publishing the choice through
    /// <see cref="SimdOps.ProcessIsaCeiling"/> switches the running session without a restart. The automatic entry
    /// stores <see cref="SimdIsa.None"/>, which restores the kernel the process would have picked on its own; every
    /// other entry is stored as the ceiling.
    /// <para>字节块分派在每次调用时重新解析进程上限，因此通过 <see cref="SimdOps.ProcessIsaCeiling"/> 发布选择即可
    /// 切换当前会话，无需重启。自动项保存 <see cref="SimdIsa.None"/>，恢复进程本会自行选择的内核；其余条目按上限
    /// 保存。</para>
    /// </remarks>
    public int HardwareAccelerationIndex
    {
        get => _hardwareAccelerationIndex;
        set
        {
            if (_loading || value < 0 || value >= _hardwareAccelerationTargets.Length || _hardwareAccelerationIndex == value)
            {
                return;
            }

            _hardwareAccelerationIndex = value;
            var target = _hardwareAccelerationTargets[value].Isa;
            _appSettings.IsaCeiling = target;
            _appSettings.UseSoftwareFallback = target == SimdIsa.Scalar;
            SimdOps.ProcessSoftwareFallback = _appSettings.UseSoftwareFallback;
            SimdOps.ProcessIsaCeiling = target;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HardwareAccelerationSummary));
            OnPropertyChanged(nameof(HardwareAccelerationAutoLabel));
            DebouncedSaveSettings();
        }
    }

    /// <summary>
    /// Gets the text of the automatic entry, combining the localized automatic label with the family the processor
    /// would pick on its own.
    /// <para>获取自动项的文本，把本地化“自动”标签与处理器自行选择的指令族组合起来。</para>
    /// </summary>
    /// <value>For example <c>Automatic (AVX-512)</c>.<para>例如 <c>自动（AVX-512）</c>。</para></value>
    public static string HardwareAccelerationAutoLabel => string.Format(
        LanguageService.Instance.GetString(HardwareAccelerationService.AutoTitleKey),
        LanguageService.Instance.GetString(HardwareAccelerationService.TitleKeyOf(SimdCapabilities.ActiveIsa)));

    /// <summary>Gets the one-line summary of the processor and the kernel dispatch actually runs.
    /// <para>获取处理器与分派实际运行的内核的单行摘要。</para>
    /// </summary>
    public string HardwareAccelerationSummary => string.Format(
        LanguageService.Instance.GetString("Settings_HardwareAccelerationSummary"),
        HardwareAccelerationService.Architecture,
        LanguageService.Instance.GetString(HardwareAccelerationService.TitleKeyOf(HardwareAccelerationService.ActiveIsa)),
        SimdCapabilities.EffectiveVectorWidthBytes);

    /// <summary>Gets whether the card must explain that the machine has no vector implementation.
    /// <para>获取卡片是否要说明本机没有任何向量实现。</para>
    /// </summary>
    public bool LacksHardwareAcceleration => HardwareAccelerationService.LacksVectorSupport;

    /// <summary>
    /// Rebuilds the hardware acceleration dropdown from the running processor and the persisted selection.
    /// <para>依据当前处理器与持久化选择重建硬件加速下拉框。</para>
    /// </summary>
    private void RefreshHardwareAccelerationOptions()
    {
        _hardwareAccelerationTargets = [.. HardwareAccelerationService.Targets()];
        _hardwareAccelerationOptions = LocalizedOptionSet.FromOptions(
            [.. _hardwareAccelerationTargets.Select(target => target.Isa == SimdIsa.None
                ? LocalizedOption.FromFactory(() => HardwareAccelerationAutoLabel)
                : LocalizedOption.FromResource(target.TitleKey))]);
        var persisted = _appSettings.UseSoftwareFallback ? SimdIsa.Scalar : _appSettings.IsaCeiling;
        _hardwareAccelerationIndex = FindHardwareAccelerationIndex(persisted);
        OnPropertyChanged(nameof(HardwareAccelerationOptions));
        OnPropertyChanged(nameof(HardwareAccelerationIndex));
        OnPropertyChanged(nameof(HardwareAccelerationSummary));
        OnPropertyChanged(nameof(LacksHardwareAcceleration));
    }

    /// <summary>
    /// Resolves the dropdown position of a stored ceiling.
    /// <para>解析持久化上限在下拉框中的位置。</para>
    /// </summary>
    /// <param name="ceiling">The persisted ceiling; <see cref="SimdIsa.None"/> selects the automatic entry.
    /// <para>持久化的上限；<see cref="SimdIsa.None"/> 选中自动项。</para></param>
    /// <returns>The matching index, or the automatic entry when the stored family is not offered.
    /// <para>匹配的索引；持久化的指令族不在列表中时为自动项。</para></returns>
    private int FindHardwareAccelerationIndex(SimdIsa ceiling)
    {
        var index = Array.FindIndex(_hardwareAccelerationTargets, target => target.Isa == ceiling);
        return index >= 0 ? index : 0;
    }

    /// <summary>
    /// Formats a byte count with binary units in the active language.
    /// <para>按当前语言用二进制单位格式化字节数。</para>
    /// </summary>
    /// <param name="bytes">The byte count.<para>字节数。</para></param>
    /// <returns>The formatted size, for example <c>1.5 MiB</c>.<para>格式化后的体积，例如 <c>1.5 MiB</c>。</para></returns>
    internal static string FormatByteSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Format(LanguageService.Instance.GetString("Toolbox_SizeBytes"), bytes)
            : string.Format(LanguageService.Instance.GetString("Toolbox_SizeUnit"), value, units[unit]);
    }
}