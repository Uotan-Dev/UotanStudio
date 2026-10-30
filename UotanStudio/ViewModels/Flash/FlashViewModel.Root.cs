using System.Collections.ObjectModel;
using Avalonia.Controls;
using UotanStudio.Services;
using UotanStudio.Services.Flash;
using UotanStudio.Services.PatchPackages;

namespace UotanStudio.ViewModels.Flash;

public sealed partial class FlashViewModel
{
    /// <summary>Index of the custom-file source in <see cref="RootPatchSources"/>'s presentation list.
    /// <para>来源列表中「自选文件」的索引。</para>
    /// </summary>
    private const int RootCustomFileIndex = 5;

    private readonly RootPatchPackageService _rootPackageService = RootPatchPackageService.Shared;
    private readonly ObservableCollection<string> _rootVersionLabels = [];
    private IReadOnlyList<string> _rootPartitionNames = [];
    private RootPatchPackage? _rootPreparedRelease;
    private IReadOnlyList<RootPatchPackage> _rootReleases = [];
    private RootPatchPackageSource? _rootLoadedSource;
    private CancellationTokenSource? _rootListCts;
    private CancellationTokenSource? _rootPrepareCts;
    private int _rootEpoch;
    private int _rootPrepareRequest;

    /// <summary>
    /// Returns the distinct partition names of the given rows that contain <c>boot</c>.
    /// <para>返回给定条目中名称含 <c>boot</c> 的去重分区名。</para>
    /// </summary>
    /// <param name="entries">Rows to inspect. <para>要检查的条目。</para></param>
    /// <returns>Names in row order, compared without case. <para>按条目顺序去重后的分区名，忽略大小写比较。</para></returns>
    internal static IReadOnlyList<string> RootBootPartitionNames(IEnumerable<FlashPartitionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        List<string> names = [];
        foreach (var entry in entries)
        {
            if (!entry.Name.Contains("boot", StringComparison.OrdinalIgnoreCase) ||
                names.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            names.Add(entry.Name);
        }

        return names;
    }

    /// <summary>
    /// Finds the selected flashable row whose partition matches the root patch target.
    /// <para>在已勾选条目中查找与 Root 修补目标分区匹配的可刷写条目。</para>
    /// </summary>
    /// <param name="selected">Rows chosen for the run. <para>本次运行勾选的条目。</para></param>
    /// <param name="partitionName">Partition chosen in the root patch list; null or blank matches nothing.
    /// <para>Root 修补列表选中的分区；为空时不匹配任何条目。</para></param>
    /// <returns>The matching row, or <see langword="null"/> when the target is not being flashed.
    /// <para>匹配的条目；目标分区未参与刷写时为 <see langword="null"/>。</para></returns>
    internal static FlashPartitionEntry? FindRootPartitionEntry(IEnumerable<FlashPartitionEntry> selected,
        string? partitionName)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (string.IsNullOrWhiteSpace(partitionName)) return null;
        foreach (var entry in selected)
        {
            if (entry.EffectiveCommand == "flash" &&
                string.Equals(entry.Name, partitionName, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Rebuilds the root patch partition list from the rows currently loaded on the page.
    /// <para>根据页面当前加载的条目重建 Root 修补分区列表。</para>
    /// </summary>
    internal void RefreshRootPartitionOptions()
    {
        var names = RootBootPartitionNames(Partitions);
        if (names.SequenceEqual(_rootPartitionNames, StringComparer.OrdinalIgnoreCase)) return;
        _rootPartitionNames = names;
        var box = Field<ComboBox>("RootPartitionBox");
        var previous = box.SelectedItem as string;
        box.ItemsSource = names;
        var restored = previous is null
            ? -1
            : names.ToList().FindIndex(name => string.Equals(name, previous, StringComparison.OrdinalIgnoreCase));
        box.SelectedIndex = names.Count == 0 ? -1 : restored >= 0 ? restored : 0;
    }

    /// <summary>
    /// Drops the cached release list and version selection of the root patch panel.
    /// <para>清空 Root 修补面板缓存的版本列表与已选版本。</para>
    /// </summary>
    internal void ResetRootReleases()
    {
        _rootEpoch++;
        _rootReleases = [];
        _rootLoadedSource = null;
        _rootPackagePath = null;
        _rootPreparedRelease = null;
        _rootVersionLabels.Clear();
        CancelRootOperations();
    }

    /// <summary>
    /// Formats one version entry, marking versions this installation already stores.
    /// <para>格式化单个版本条目，把本机已下载的版本标记出来。</para>
    /// </summary>
    /// <param name="package">Release entry to describe. <para>要描述的发布条目。</para></param>
    /// <returns>The version, with the downloaded marker appended when it is stored locally.
    /// <para>版本号；本机已存放时追加已下载标记。</para></returns>
    private string DescribeRootVersion(RootPatchPackage package)
        => _rootPackageService.HasStoredPackage(package)
            ? _localizer.GetString("BootPatch_VersionDownloaded", package.Version)
            : package.Version;

    /// <summary>
    /// Refreshes the downloaded marker of every listed version without changing the selection.
    /// <para>在不改变当前选择的前提下刷新列表中每个版本的已下载标记。</para>
    /// </summary>
    private void RefreshRootVersionLabels()
    {
        if (_rootVersionLabels.Count != _rootReleases.Count) return;
        var box = Field<ComboBox>("RootVersionBox");
        var selected = box.SelectedIndex;
        var changed = false;
        for (var index = 0; index < _rootReleases.Count; index++)
        {
            var label = DescribeRootVersion(_rootReleases[index]);
            if (string.Equals(_rootVersionLabels[index], label, StringComparison.Ordinal)) continue;
            _rootVersionLabels[index] = label;
            changed = true;
        }

        if (changed && box.SelectedIndex != selected) box.SelectedIndex = selected;
    }

    /// <summary>Starts the one-time catalog warm-up so version switching never re-queries the upstreams.
    /// <para>启动一次性的清单预热，使切换版本不再重复查询上游。</para>
    /// </summary>
    internal void WarmRootCatalogs() => _ = WarmRootCatalogsAsync();

    /// <summary>
    /// Loads the release list of the selected source once, when the root patch panel needs it.
    /// <para>在 Root 修补面板需要时加载一次所选来源的版本列表。</para>
    /// </summary>
    private void EnsureRootReleasesLoaded()
    {
        if (RootPatchSources.FromIndex(Field<ComboBox>("RootFileSourceBox").SelectedIndex) is not { } source)
        {
            return;
        }

        if (_rootListCts is not null || _rootLoadedSource == source) return;
        _ = LoadRootReleasesAsync(source);
    }

    /// <summary>
    /// Reads the release list of one source, speed-tests its newest asset and fills the version list.
    /// <para>读取一个来源的版本列表，对最新资产测速，并回填版本下拉框。</para>
    /// </summary>
    /// <param name="source">Source whose releases are listed. <para>要列出发布版的来源。</para></param>
    private async Task LoadRootReleasesAsync(RootPatchPackageSource source)
    {
        var epoch = ++_rootEpoch;
        _rootLoadedSource = source;
        var cts = BeginRootList();
        var box = Field<ComboBox>("RootVersionBox");
        box.IsEnabled = false;
        if (!_rootPackageService.CatalogLoaded(source))
        {
            AppendLog(_localizer.GetString("BootPatch_VersionsLoading"));
        }

        try
        {
            var releases = await _rootPackageService.ListPackagesAsync(source, cts.Token);
            if (epoch != _rootEpoch) return;
            _rootReleases = releases;
            _rootVersionLabels.Clear();
            foreach (var release in releases) _rootVersionLabels.Add(DescribeRootVersion(release));
            if (releases.Count == 0)
            {
                box.SelectedIndex = -1;
                AppendLog(_localizer.GetString("BootPatch_VersionsEmpty"));
                return;
            }

            if (_rootPackageService.PreferredSourceAuthority is null)
            {
                AppendLog(_localizer.GetString("BootPatch_ProbingSources"));
            }

            await ProbeRootSourcesAsync(releases[0].DownloadUrl);
            if (epoch != _rootEpoch) return;
            box.SelectedIndex = 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (epoch == _rootEpoch) AppendLog(ex.Message);
        }
        finally
        {
            if (_rootListCts == cts) _rootListCts = null;
            if (epoch == _rootEpoch)
            {
                box.IsEnabled = true;
                UpdateRootControlsState();
            }
        }
    }

    /// <summary>
    /// Speed-tests every candidate source of one asset and pins the fastest one for later downloads.
    /// <para>对单个资产的全部候选源测速，并把最快的一个固定给后续下载使用。</para>
    /// </summary>
    /// <param name="downloadUrl">Asset URL used as the probe target. <para>作为测速目标的资产地址。</para></param>
    private async Task ProbeRootSourcesAsync(string downloadUrl)
    {
        using var probe = new CancellationTokenSource();
        try
        {
            await _rootPackageService.ProbeSourcesAsync(new Uri(downloadUrl), probe.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException)
        {
            LogService.Instance.Write(LogLevel.Warn, "FlashRoot",
                $"Source probe skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves and validates the currently selected root patch package, downloading it when needed.
    /// <para>解析并校验当前选中的 Root 修补包，需要时先下载。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the download and the validation. <para>取消下载与校验。</para></param>
    /// <returns>The absolute path of a verified package usable by <see cref="FlashRootImage"/>.
    /// <para><see cref="FlashRootImage"/> 可用的已校验包绝对路径。</para></returns>
    /// <exception cref="InvalidDataException">Nothing is selected, or the file is not a patchable package.
    /// <para>未选择任何包，或该文件不是可修补的包。</para></exception>
    internal async Task<string> ResolveRootPackageAsync(CancellationToken cancellationToken)
    {
        if (RootPatchSources.FromIndex(Field<ComboBox>("RootFileSourceBox").SelectedIndex) is not { } source)
        {
            var custom = Field<TextBox>("RootPackagePathBox").Text;
            if (string.IsNullOrWhiteSpace(custom) || !File.Exists(custom))
            {
                throw new InvalidDataException(_localizer.GetString("Flash_RootPackageMissing"));
            }

            var chosen = RequirePatchable(await _rootPackageService.ValidateAsync(custom, null, cancellationToken),
                cancellationToken);
            _rootPackagePath = chosen;
            return chosen;
        }

        var index = Field<ComboBox>("RootVersionBox").SelectedIndex;
        if (index < 0 || index >= _rootReleases.Count || _rootReleases[index].Source != source)
        {
            throw new InvalidDataException(_localizer.GetString("Flash_RootPackageMissing"));
        }

        var release = _rootReleases[index];
        var artifact = await _rootPackageService.EnsurePackageAsync(release,
            cancellationToken: cancellationToken);
        var path = RequirePatchable(
            await _rootPackageService.ValidateAsync(artifact.Path, source, cancellationToken), cancellationToken);
        _rootPackagePath = path;
        RefreshRootVersionLabels();
        return path;
    }

    /// <summary>
    /// Verifies a validation result can feed the flash patch flow and returns its path.
    /// <para>确认校验结果可用于烧录修补流程，并返回其路径。</para>
    /// </summary>
    /// <param name="validation">Result of validating one package file. <para>单个包文件的校验结果。</para></param>
    /// <param name="cancellationToken">Token cancelling the wait. <para>用于取消等待的令牌。</para></param>
    /// <returns>The validated absolute path. <para>已校验的绝对路径。</para></returns>
    /// <exception cref="InvalidDataException">The package is unreadable or its family cannot be patched here.
    /// <para>包不可读，或其类型族无法在此处修补。</para></exception>
    private string RequirePatchable(RootPatchPackageValidation validation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!validation.IsSupported || validation.Detection is null ||
            !FlashRootImage.IsSupportedPackage(validation.Detection))
        {
            throw new InvalidDataException(string.IsNullOrWhiteSpace(validation.Problem)
                ? _localizer.GetString("Flash_RootPackageUnsupported")
                : _localizer.GetString("BootPatch_PackageInvalid", validation.Problem));
        }

        return validation.Path;
    }

    /// <summary>
    /// Downloads and validates the version the user just picked so the run can patch without waiting.
    /// <para>下载并校验用户刚选中的版本，使刷写开始时无需再等待。</para>
    /// </summary>
    private async Task PrepareRootSelectionAsync()
    {
        if (Field<CheckBox>("AddRootBox").IsChecked != true ||
            RootPatchSources.FromIndex(Field<ComboBox>("RootFileSourceBox").SelectedIndex) is not { } source)
        {
            return;
        }

        var index = Field<ComboBox>("RootVersionBox").SelectedIndex;
        if (index < 0 || index >= _rootReleases.Count || _rootReleases[index].Source != source) return;
        var release = _rootReleases[index];
        if (_rootPackagePath is not null && _rootPreparedRelease == release) return;
        var request = ++_rootPrepareRequest;
        var cts = BeginRootPrepare();
        _rootPackagePath = null;
        _rootPreparedRelease = null;
        var stored = _rootPackageService.HasStoredPackage(release);
        if (!stored) AppendLog(_localizer.GetString("BootPatch_PackageDownloading", release.Version));
        try
        {
            await ResolveRootPackageAsync(cts.Token);
            _rootPreparedRelease = release;
            if (!stored) AppendLog(_localizer.GetString("BootPatch_PackageReady", release.Version));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (request == _rootPrepareRequest) AppendLog(ex.Message);
        }
        finally
        {
            if (_rootPrepareCts == cts) _rootPrepareCts = null;
            if (request == _rootPrepareRequest) UpdateRootControlsState();
        }
    }

    /// <summary>Handles a new version choice in the root patch panel. <para>处理 Root 修补面板中的版本切换。</para></summary>
    /// <param name="sender">Event source. <para>事件源。</para></param>
    /// <param name="e">Selection details. <para>选择详情。</para></param>
    internal async void OnRootVersionChanged(object? sender, SelectionChangedEventArgs e)
        => await PrepareRootSelectionAsync();

    private async Task WarmRootCatalogsAsync()
    {
        try
        {
            await _rootPackageService.WarmUpAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "FlashRoot", $"Root patch warm-up failed: {ex.Message}");
        }
    }

    /// <summary>Starts one cancellable list read and cancels the previous one.
    /// <para>开始一次可取消的列表读取，并取消上一次读取。</para>
    /// </summary>
    /// <returns>Token of the new read. <para>新的读取令牌。</para></returns>
    private CancellationTokenSource BeginRootList()
    {
        _rootListCts?.Cancel();
        var cts = new CancellationTokenSource();
        _rootListCts = cts;
        return cts;
    }

    /// <summary>Starts one cancellable package preparation and cancels the previous one.
    /// <para>开始一次可取消的包准备，并取消上一次准备。</para>
    /// </summary>
    /// <returns>Token of the new preparation. <para>新的准备令牌。</para></returns>
    private CancellationTokenSource BeginRootPrepare()
    {
        _rootPrepareCts?.Cancel();
        var cts = new CancellationTokenSource();
        _rootPrepareCts = cts;
        return cts;
    }

    /// <summary>Cancels the in-flight root package list read and preparation.
    /// <para>取消正在进行的 Root 包列表读取与包准备。</para>
    /// </summary>
    private void CancelRootOperations()
    {
        _rootListCts?.Cancel();
        _rootListCts = null;
        _rootPrepareCts?.Cancel();
        _rootPrepareCts = null;
    }
}
