using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Firmware.BootSecurity.Patcher;
using UotanStudio.Services;
using UotanStudio.Services.PatchPackages;
using UotanStudio.Services.Ui;
using UotanStudio.ViewModels;

namespace UotanStudio.Views.Shell;

/// <summary>Native boot patch window.
/// <para>原生 boot 补丁窗口。</para>
/// </summary>
public partial class BootPatchWindow : Window
{
    private const int CustomFileIndex = 5;

    private readonly LocalizedOptionSet _fileSourceOptions = LocalizedOptionSet.FromOptions(
        LocalizedOption.FromText("Magisk"),
        LocalizedOption.FromText("Alpha"),
        LocalizedOption.FromText("KernelSU"),
        LocalizedOption.FromText("SukiSU"),
        LocalizedOption.FromText("APatch"),
        LocalizedOption.FromResource("BootPatch_CustomFile"));
    private readonly BootPatchService _patchService = new();
    private readonly RootPatchPackageService _packageService = RootPatchPackageService.Shared;
    private readonly ObservableCollection<string> _versionLabels = [];
    private IReadOnlyList<RootPatchPackage> _releases = [];
    private bool _hasMoreVersions;
    private bool _loadingMoreVersions;
    private bool _suppressVersionChanged;
    private int _lastVersionIndex = -1;
    private BootPatchDetection? _detection;
    private string? _packagePath;
    private BootImageInspection? _bootInspection;
    private string? _bootInspectedPath;
    private string? _warnedBootPath;
    private string? _warnedBootArchitecturePath;
    private string? _warnedGkiVersionPath;
    private string? _validationMessage;
    private string? _bootArchitecture;
    private string? _bootKmi;
    private CancellationTokenSource? _operationCts;
    private int _sourceEpoch;
    private int _prepareRequest;
    private bool _probeStarted;
    private bool _isPatching;
    private string _statusText = string.Empty;
    private string _downloadStats = string.Empty;

    public BootPatchWindow() : this(null) { }
    public BootPatchWindow(string? imagePath)
    {
        InitializeComponent();
        FileSourceBox.ItemsSource = _fileSourceOptions.Options;
        FileSourceBox.SelectedIndex = 0;
        FileSourceBox.SelectionChanged += OnFileSourceChanged;
        FileVersionBox.ItemsSource = _versionLabels;
        FileVersionBox.SelectionChanged += OnVersionChanged;

        FilePathBox.TextChanging += (_, _) => UpdateStartEnabled();
        BootPathBox.TextChanging += (_, _) => UpdateStartEnabled();
        PrebootDeviceBox.TextChanging += (_, _) => UpdateStartEnabled();
        ArchitectureBox.SelectionChanged += (_, _) => UpdateStartEnabled();
        KmiBox.SelectionChanged += (_, _) => UpdateStartEnabled();
        Opened += async (_, _) =>
        {
            _ = WarmCatalogsAsync();
            await InspectBootAsync();
            await RefreshReleasesAsync();
        };
        BootPathBox.Text = ToAbsolutePath(imagePath);
        UpdateFileSourceState();
        UpdateStartEnabled();
    }

    private static string? ToAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    /// <summary>Source selected in the file list; <see langword="null"/> for the custom-file entry.
    /// <para>文件列表中已选择的来源；自选文件条目为 <see langword="null"/>。</para></summary>
    private RootPatchPackageSource? SelectedSource => RootPatchSources.FromIndex(FileSourceBox.SelectedIndex);

    private RootPatchPackage? SelectedRelease()
    {
        var index = FileVersionBox.SelectedIndex;
        return index >= 0 && index < _releases.Count ? _releases[index] : null;
    }

    private async void OnFileSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateFileSourceState();
        await RefreshReleasesAsync();
    }

    /// <summary>Resolves the patch mode from the detected package content: KernelSU packages carrying a
    /// KernelPatch image take the KPM path, kernel modules take the LKM path, and kernel images or
    /// AnyKernel3 packages take the GKI path.
    /// <para>按检测到的包内容解析修补模式：携带 KernelPatch 镜像的 KernelSU 包走 KPM，内核模块走 LKM，
    /// 内核镜像或 AnyKernel3 压缩包走 GKI。</para></summary>
    private BootPatchMode ResolvedPatchMode() => _detection?.Kind switch
    {
        BootPatchKind.KernelSu => _detection.Kmis is { Count: > 0 } || !_detection.HasKernelPatchImage
            ? BootPatchMode.Lkm
            : BootPatchMode.Kpm,
        BootPatchKind.KernelSuLkm => BootPatchMode.Lkm,
        BootPatchKind.KernelPatch => BootPatchMode.Kpm,
        BootPatchKind.GkiKernel => BootPatchMode.Gki,
        _ => BootPatchMode.Auto,
    };

    /// <summary>Applies the resolved patch mode to the option rows: GKI drops the device field and shows the GKI
    /// version list, KPM turns the device field into the superkey field, KernelSU LKM shows the module KMI list,
    /// and a Magisk package without PREINITDEVICE hides the device field.
    /// <para>把解析出的修补模式应用到选项行：GKI 隐藏设备框并显示 GKI 版本列表，KPM 把设备框变为超级密钥框，
    /// KernelSU LKM 显示模块 KMI 列表，不含 PREINITDEVICE 的 Magisk 包隐藏设备框。</para></summary>
    private void UpdatePatchModeState()
    {
        var detection = _detection;
        var kind = detection?.Kind;
        var mode = ResolvedPatchMode();
        var supportsPreInitDevice = kind != BootPatchKind.Magisk
            || detection?.SupportedOptions?.Contains(BootPatchOption.PreInitDevice) == true;
        PrebootDeviceBox.IsVisible = mode != BootPatchMode.Gki && supportsPreInitDevice;
        KmiBox.IsVisible = RequiresKmiChoice();
        var usesSuperKey = kind is BootPatchKind.APatch or BootPatchKind.KernelPatch || mode == BootPatchMode.Kpm;
        PrebootDeviceBox.PlaceholderText = LanguageService.Instance.GetString(
            kind == BootPatchKind.APatch ? "BootPatch_ApatchPassword"
            : usesSuperKey ? "BootPatch_SuperKey"
            : "BootPatch_DevicePlaceholder");
        PrebootDeviceBox.PasswordChar = usesSuperKey ? '*' : '\0';
        KmiBox.PlaceholderText = mode == BootPatchMode.Gki
            ? LanguageService.Instance.GetString("BootPatch_GkiVersion")
            : string.Empty;
    }

    /// <summary>Reports whether the option row must show the KMI list: KernelSU LKM chooses the module KMI, and a
    /// GKI package asks the user to confirm the GKI version it carries. Pending KernelSU sources show automatic KMI selection.
    /// <para>报告选项行是否显示 KMI 列表：KernelSU 来源加载期间显示自动识别，LKM 选择模块 KMI，GKI 包要求确认它携带的版本。</para>
    /// </summary>
    private bool RequiresKmiChoice()
    {
        if (_detection is null)
        {
            return SelectedSource is RootPatchPackageSource.KernelSu or RootPatchPackageSource.SukiSu;
        }

        var mode = ResolvedPatchMode();
        return mode == BootPatchMode.Gki
            ? HasGkiVersionChoices()
            : _detection?.Kind == BootPatchKind.KernelSu && mode == BootPatchMode.Lkm;
    }

    /// <summary>Reports whether the detected GKI package publishes GKI versions the user can pick.
    /// <para>报告检测到的 GKI 包是否发布了可供选择的 GKI 版本。</para>
    /// </summary>
    private bool HasGkiVersionChoices()
        => ResolvedPatchMode() == BootPatchMode.Gki && _detection?.Kmis is { Count: > 0 };

    private void UpdateFileSourceState()
    {
        var custom = FileSourceBox.SelectedIndex == CustomFileIndex;
        FilePathBox.IsVisible = custom;
        BrowseFileButton.IsVisible = custom;
        FileVersionBox.IsVisible = !custom;

        _sourceEpoch++;
        _prepareRequest++;
        CancelOperations();
        _releases = [];
        _hasMoreVersions = false;
        _lastVersionIndex = -1;
        _packagePath = null;
        _suppressVersionChanged = true;
        try
        {
            _versionLabels.Clear();
        }
        finally
        {
            _suppressVersionChanged = false;
        }

        FileVersionBox.IsEnabled = true;
        if (custom)
        {
            FilePathBox.Text = string.Empty;
            SetDetection(null);
            SetStatus(string.Empty);
        }
        else
        {
            FilePathBox.Text = string.Empty;
            SetDetection(null);
        }

        UpdateStartEnabled();
    }

    private CancellationToken BeginOperation()
    {
        CancelOperations();
        var source = new CancellationTokenSource();
        _operationCts = source;
        return source.Token;
    }

    private void CancelOperations()
    {
        var source = _operationCts;
        _operationCts = null;
        source?.Cancel();
    }

    /// <summary>Enables start only when the package, the boot image and every required field satisfy the checks
    /// for the resolved patch mode. <para>仅当补丁包、boot 镜像与修补模式的必填项全部通过检查时才允许开始。</para>
    /// </summary>
    private void UpdateStartEnabled() => StartButton.IsEnabled =
        !_isPatching
        && IsPackageSelected()
        && IsBootInspected()
        && HasRequiredSuperKey()
        && HasRequiredGkiVersion()
        && EvaluateSelection();

    private bool IsBootInspected()
    {
        if (string.IsNullOrWhiteSpace(BootPathBox.Text) || !File.Exists(BootPathBox.Text)) return false;
        try
        {
            return _bootInspection is not null
                && string.Equals(_bootInspectedPath, Path.GetFullPath(BootPathBox.Text), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private bool HasRequiredSuperKey()
        => BootPatchCompatibility.IsSuperKeyAcceptable(_detection?.Kind ?? BootPatchKind.Unknown,
            ResolvedPatchMode(), PrebootDeviceBox.Text);

    /// <summary>Reports whether the GKI version picker holds a choice when the package publishes any.
    /// <para>报告包发布 GKI 版本时，版本选择框是否已有选择。</para>
    /// </summary>
    private bool HasRequiredGkiVersion() => !HasGkiVersionChoices() || KmiBox.SelectedIndex >= 0;

    private bool IsPackageSelected()
    {
        if (SelectedSource is not null)
        {
            return SelectedRelease() is not null && _packagePath is not null && _detection is { IsSupported: true };
        }

        return !string.IsNullOrWhiteSpace(FilePathBox.Text) && _detection is { IsSupported: true };
    }

    /// <summary>Checks the boot requirements, architecture and KMI consistency of the resolved patch mode and
    /// reports the first blocking problem in the status line.
    /// <para>检查修补模式对 boot 的组件要求、架构与 KMI 一致性，并在状态行报告第一个阻塞问题。</para>
    /// </summary>
    private bool EvaluateSelection()
    {
        var detection = _detection;
        if (detection is not { IsSupported: true } || !IsBootInspected() || _bootInspection is not { } boot)
        {
            SetValidationStatus(null);
            return false;
        }

        var mode = ResolvedPatchMode();
        var explicitArchitecture = ArchitectureBox.SelectedIndex > 0 ? ArchitectureBox.SelectedItem as string : null;
        var problem = BootPatchCompatibility.Check(detection, mode, boot, explicitArchitecture, SelectedKmi());
        var language = LanguageService.Instance;
        switch (problem)
        {
            case BootPatchCompatibilityProblem.KernelRequired:
                SetValidationStatus(language.GetString("BootPatch_BootNeedsKernel"));
                return false;
            case BootPatchCompatibilityProblem.RamdiskRequired:
                SetValidationStatus(language.GetString("BootPatch_BootNeedsRamdisk"));
                return false;
            case BootPatchCompatibilityProblem.ArchitectureMismatch:
                SetValidationStatus(string.Format(language.GetString("BootPatch_ArchitectureMismatch"),
                    string.Join(", ", detection.Architectures), boot.Architecture));
                return false;
            case BootPatchCompatibilityProblem.SelectedArchitectureMismatch:
                SetValidationStatus(string.Format(language.GetString("BootPatch_SelectedArchitectureMismatch"),
                    explicitArchitecture, boot.Architecture));
                return false;
            case BootPatchCompatibilityProblem.KmiUnknown:
                SetValidationStatus(language.GetString("BootPatch_KmiDetectionFailed"));
                return false;
            case BootPatchCompatibilityProblem.KernelSuKmiMismatch:
                SetValidationStatus(string.Format(language.GetString("BootPatch_KmiMismatch"),
                    SelectedKmi() ?? boot.Kmi));
                return false;
            case BootPatchCompatibilityProblem.GkiKmiMismatch:
                SetValidationStatus(string.Format(language.GetString("BootPatch_GkiKmiMismatch"),
                    SelectedKmi() ?? string.Join(", ", detection.Kmis ?? []), boot.Kmi));
                return false;
        }

        if (!HasRequiredGkiVersion())
        {
            SetValidationStatus(language.GetString("BootPatch_GkiVersionRequired"));
            return false;
        }

        SetValidationStatus(null);
        return true;
    }

    private void SetValidationStatus(string? message)
    {
        if (string.Equals(_validationMessage, message, StringComparison.Ordinal))
        {
            if (message is not null && !string.Equals(_statusText, message, StringComparison.Ordinal))
                SetStatus(message);
            return;
        }

        var previous = _validationMessage;
        _validationMessage = message;
        if (message is not null)
        {
            SetStatus(message);
        }
        else if (previous is not null && string.Equals(_statusText, previous, StringComparison.Ordinal))
        {
            SetStatus(string.Empty);
        }
    }

    private void SetStatus(string text)
    {
        _statusText = text;
        UpdateProgressText();
        if (!_isPatching) ProgressPanel.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private void UpdateProgressText()
    {
        ProgressText.Text = string.IsNullOrEmpty(_downloadStats) ? _statusText
            : string.IsNullOrEmpty(_statusText) ? _downloadStats : $"{_statusText} · {_downloadStats}";
    }

    private string DescribeInvalidPackage(string? problem)
    {
        if (!string.IsNullOrWhiteSpace(problem))
        {
            LogService.Instance.Write(LogLevel.Warn, "BootPatch", problem);
        }

        return LanguageService.Instance.GetString("BootPatch_PackageInvalidShort");
    }

    private static string DescribeStatusError(Exception exception)
    {
        LogService.Instance.Write(LogLevel.Warn, "BootPatch", exception.ToString());
        var description = UserError.Describe(exception);
        var detailsStart = description.IndexOf(Environment.NewLine, StringComparison.Ordinal);
        return detailsStart < 0 ? description : description[..detailsStart];
    }

    /// <summary>Fills the version list of the selected source from the shared catalog and starts the patch preparation.
    /// <para>从共享清单填充所选来源的版本列表，并开始准备补丁包。</para>
    /// </summary>
    private async Task RefreshReleasesAsync()
    {
        if (SelectedSource is not { } source)
        {
            SetStatus(string.Empty);
            return;
        }

        var epoch = _sourceEpoch;
        var token = BeginOperation();
        FileVersionBox.IsEnabled = false;
        if (!_packageService.CatalogLoaded(source))
        {
            SetStatus(LanguageService.Instance.GetString("BootPatch_VersionsLoadingShort"));
        }

        try
        {
            var releases = await _packageService.ListPackagesAsync(source, token);
            if (epoch != _sourceEpoch) return;
            _releases = releases;
            _hasMoreVersions = _packageService.HasMore(source);
            RebuildVersionLabels(0, prepare: releases.Count > 0);

            if (releases.Count == 0)
            {
                SetStatus(LanguageService.Instance.GetString("BootPatch_VersionsEmptyShort"));
                return;
            }

            SetStatus(string.Empty);
            StartSourceProbe(releases[0].DownloadUrl);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (epoch == _sourceEpoch) SetStatus(DescribeStatusError(ex));
        }
        finally
        {
            if (epoch == _sourceEpoch)
            {
                FileVersionBox.IsEnabled = true;
                UpdateStartEnabled();
            }
        }
    }

    /// <summary>Loads every provisionable catalog and ranks mirror sources once, without blocking the window.
    /// <para>在后台读取全部可供给清单并完成一次镜像源测速，不阻塞窗口。</para>
    /// </summary>
    private async Task WarmCatalogsAsync()
    {
        try
        {
            await _packageService.WarmUpAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "BootPatch", $"Root patch warm-up failed: {ex.Message}");
        }
    }

    /// <summary>Speed-tests the candidate sources of the newest asset in the background, once per window.
    /// <para>在后台对最新资产的全部候选源测速，每个窗口只执行一次。</para>
    /// </summary>
    /// <param name="downloadUrl">Newest release asset URL used as the probe target.
    /// <para>作为测速目标的最新发布资产地址。</para></param>
    private void StartSourceProbe(string downloadUrl)
    {
        if (_probeStarted) return;
        _probeStarted = true;
        _ = ProbeSourcesAsync(downloadUrl);
    }

    private async Task ProbeSourcesAsync(string downloadUrl)
    {
        try
        {
            await _packageService.ProbeSourcesAsync(new Uri(downloadUrl));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException)
        {
            LogService.Instance.Write(LogLevel.Warn, "BootPatch", $"Source probe skipped: {ex.Message}");
        }
    }

    /// <summary>Formats one version entry, marking versions this installation already stores.
    /// <para>格式化单个版本条目，把本机已下载的版本标记出来。</para></summary>
    private string DescribeVersion(RootPatchPackage release)
        => _packageService.HasStoredPackage(release)
            ? LanguageService.Instance.GetString("BootPatch_VersionDownloaded", release.Version)
            : release.Version;

    /// <summary>Refreshes the downloaded marker of every listed version without changing the selection.
    /// <para>在不改变当前选择的前提下刷新列表中每个版本的已下载标记。</para>
    /// </summary>
    private void RefreshVersionLabels()
    {
        if (_versionLabels.Count < _releases.Count)
        {
            return;
        }

        var selected = FileVersionBox.SelectedIndex;
        var changed = false;
        for (var index = 0; index < _releases.Count; index++)
        {
            var label = DescribeVersion(_releases[index]);
            if (string.Equals(_versionLabels[index], label, StringComparison.Ordinal)) continue;
            _versionLabels[index] = label;
            changed = true;
        }

        if (changed && FileVersionBox.SelectedIndex != selected)
        {
            FileVersionBox.SelectedIndex = selected;
        }
    }

    /// <summary>Rebuilds the version list, appending the load-more row when older releases remain.
    /// <para>重建版本列表；仍有更早版本时在末尾追加「获取更多」条目。</para>
    /// </summary>
    /// <param name="selected">Version index to keep selected; negative clears the selection.
    /// <para>要保留选中的版本索引；负值表示清空选择。</para></param>
    /// <param name="prepare">Whether to start preparing the selected release.
    /// <para>是否开始准备所选版本。</para></param>
    private void RebuildVersionLabels(int selected, bool prepare)
    {
        _suppressVersionChanged = true;
        try
        {
            _versionLabels.Clear();
            foreach (var release in _releases)
            {
                _versionLabels.Add(DescribeVersion(release));
            }

            if (_hasMoreVersions)
            {
                _versionLabels.Add(LanguageService.Instance.GetString("BootPatch_LoadMore"));
            }

            var restore = selected >= 0 && selected < _releases.Count ? selected
                : _releases.Count > 0 ? 0
                : -1;
            FileVersionBox.SelectedIndex = restore;
            _lastVersionIndex = restore;
        }
        finally
        {
            _suppressVersionChanged = false;
        }

        if (prepare && _lastVersionIndex >= 0)
        {
            _ = PrepareSelectedReleaseAsync();
        }
    }

    private async void OnVersionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressVersionChanged) return;
        var index = FileVersionBox.SelectedIndex;
        if (_hasMoreVersions && index == _releases.Count)
        {
            await LoadMoreVersionsAsync();
            return;
        }

        if (index >= 0 && index < _releases.Count)
        {
            _lastVersionIndex = index;
        }

        await PrepareSelectedReleaseAsync();
    }

    /// <summary>Fetches the next version page without keeping the load-more row selected.
    /// <para>获取下一页版本，且不把「获取更多」条目保留为下拉框选中项。</para>
    /// </summary>
    private async Task LoadMoreVersionsAsync()
    {
        if (_loadingMoreVersions || SelectedSource is not { } source)
        {
            return;
        }

        _loadingMoreVersions = true;
        var epoch = _sourceEpoch;
        var restore = _lastVersionIndex >= 0 && _lastVersionIndex < _releases.Count
            ? _lastVersionIndex
            : _releases.Count > 0 ? _releases.Count - 1 : -1;
        _suppressVersionChanged = true;
        try
        {
            FileVersionBox.SelectedIndex = restore;
        }
        finally
        {
            _suppressVersionChanged = false;
        }

        FileVersionBox.IsEnabled = false;
        FileSourceBox.IsEnabled = false;
        try
        {
            await _packageService.LoadMoreAsync(source);
            if (epoch != _sourceEpoch) return;
            _releases = _packageService.TryGetCatalog(source) ?? _releases;
            _hasMoreVersions = _packageService.HasMore(source);
            var hadSelection = restore >= 0;
            RebuildVersionLabels(restore, prepare: false);
            if (!hadSelection && _releases.Count > 0)
            {
                _ = PrepareSelectedReleaseAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (epoch == _sourceEpoch) SetStatus(DescribeStatusError(ex));
        }
        finally
        {
            _loadingMoreVersions = false;
            if (epoch == _sourceEpoch)
            {
                FileVersionBox.IsEnabled = true;
                UpdateStartEnabled();
            }

            FileSourceBox.IsEnabled = true;
        }
    }

    /// <summary>Locks the package source and version selectors while a download is in flight.
    /// <para>下载进行中时锁定补丁包类别与版本选择。</para></summary>
    private void SetProvisionLock(bool locked)
    {
        FileSourceBox.IsEnabled = !locked;
        FileVersionBox.IsEnabled = !locked;
    }

    /// <summary>Downloads and validates the selected release before the patch can start.
    /// <para>在允许开始修补前下载并校验已选择的版本。</para>
    /// </summary>
    private async Task PrepareSelectedReleaseAsync()
    {
        if (_isPatching || SelectedSource is not { } source || SelectedRelease() is not { } release)
        {
            UpdateStartEnabled();
            return;
        }

        var epoch = _sourceEpoch;
        var request = ++_prepareRequest;
        _packagePath = null;
        SetDetection(null);
        UpdateStartEnabled();
        SetProvisionLock(true);
        var token = BeginOperation();
        try
        {
            var artifact = await EnsureReleasePackageAsync(release, token);
            if (epoch != _sourceEpoch || request != _prepareRequest) return;
            var validation = await _packageService.ValidateAsync(artifact.Path, source, token);
            if (epoch != _sourceEpoch || request != _prepareRequest) return;
            _packagePath = validation.IsSupported ? artifact.Path : null;
            SetDetection(validation.Detection);
            SetStatus(validation.IsSupported ? string.Empty : DescribeInvalidPackage(validation.Problem));
            if (!validation.IsSupported && validation.Detection is null)
            {
                await WarnAsync("BootPatch_PackageReadFailed", validation.Problem ?? release.AssetName);
            }

            await WarnReadFailuresAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (epoch == _sourceEpoch && request == _prepareRequest)
            {
                _packagePath = null;
                SetDetection(null);
                SetStatus(DescribeStatusError(ex));
            }
        }
        finally
        {
            if (epoch == _sourceEpoch && request == _prepareRequest)
            {
                RefreshVersionLabels();
                UpdateStartEnabled();
            }

            SetProvisionLock(false);
        }
    }

    /// <summary>Downloads one release, reporting status, transfer speed and remaining time above the bar.
    /// <para>下载某个发布版，并在进度条上方合并显示状态、传输速度与预计剩余时间。</para></summary>
    private async Task<RootPatchPackageArtifact> EnsureReleasePackageAsync(RootPatchPackage release,
        CancellationToken token)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var lastSample = stopwatch.Elapsed;
        var lastBytes = 0d;
        var smoothedRate = 0d;
        var acceptingProgress = true;
        var progress = new Progress<double>(value =>
        {
            if (!acceptingProgress)
            {
                return;
            }
            PatchProgressBar.Value = Math.Clamp(value * 100d, 0d, 100d);
            ProgressPanel.IsVisible = true;
            _statusText = LanguageService.Instance.GetString("BootPatch_PackageDownloadingShort");
            var processed = Math.Clamp(value, 0d, 1d) * release.Length;
            var elapsed = stopwatch.Elapsed - lastSample;
            if (elapsed.TotalSeconds >= 0.25 && processed >= lastBytes)
            {
                var instant = (processed - lastBytes) / elapsed.TotalSeconds;
                smoothedRate = smoothedRate <= 0 ? instant : smoothedRate * 0.6 + instant * 0.4;
                lastBytes = processed;
                lastSample = stopwatch.Elapsed;
            }

            if (smoothedRate > 0)
            {
                _downloadStats = FormatDownloadStats(release.Length - processed, smoothedRate);
            }
            UpdateProgressText();
        });
        try
        {
            return await _packageService.EnsurePackageAsync(release, progress, token);
        }
        finally
        {
            acceptingProgress = false;
            _downloadStats = string.Empty;
            UpdateProgressText();
        }
    }

    /// <summary>Formats the transfer rate and, when the total size is known, the estimated remaining time.
    /// <para>格式化传输速度；总大小已知时一并给出预计剩余时间。</para></summary>
    private string FormatDownloadStats(double remainingBytes, double rate)
    {
        var stats = $"{FormatBytes(rate)}/s";
        if (remainingBytes <= 0) return stats;
        var eta = TimeSpan.FromSeconds(Math.Ceiling(remainingBytes / rate));
        var remaining = LanguageService.Instance.GetString("BootPatch_TimeRemaining",
            eta.TotalHours >= 1 ? eta.ToString(@"h\:mm") : eta.ToString(@"mm\:ss"));
        return $"{stats} · {remaining}";
    }

    private static string FormatBytes(double bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (1L << 10):F1} KB",
        _ => $"{bytes:F0} B",
    };

    private async void OnBrowseFileClick(object? sender, RoutedEventArgs e)
    {
        await BrowseAsync(FilePathBox, "Patch package", ["*.apk", "*.zip", "*.ko", "*.img", "*.gz", "*"]);
        UpdateStartEnabled();
        await DetectPackageAsync();
    }

    private async void OnBrowseBootClick(object? sender, RoutedEventArgs e)
    {
        await BrowseAsync(BootPathBox, "Boot image", ["*.img", "*.bin"]);
        UpdateStartEnabled();
        await InspectBootAsync();
    }

    private async Task BrowseAsync(TextBox target, string description, IReadOnlyList<string> patterns)
    {
        if (StorageProvider is not { CanOpen: true } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, Title = description, FileTypeFilter = [new FilePickerFileType(description) { Patterns = patterns }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) target.Text = path;
    }

    /// <summary>Detects the family, ABIs and options of the user-chosen package and validates it for patching.
    /// <para>检测自选补丁包的类型族、ABI 与选项，并校验它是否可用于修补。</para>
    /// </summary>
    private async Task DetectPackageAsync()
    {
        SetDetection(null);
        _packagePath = null;
        if (string.IsNullOrWhiteSpace(FilePathBox.Text) || !File.Exists(FilePathBox.Text))
        {
            UpdateStartEnabled();
            return;
        }

        try
        {
            var validation = await _packageService.ValidateAsync(FilePathBox.Text);
            SetDetection(validation.Detection);
            if (validation.IsSupported)
            {
                _packagePath = validation.Path;
                SetStatus(string.Empty);
            }
            else
            {
                SetStatus(DescribeInvalidPackage(validation.Problem));
                if (validation.Detection is null)
                {
                    await WarnAsync("BootPatch_PackageReadFailed",
                        validation.Problem ?? Path.GetFileName(FilePathBox.Text));
                }
            }
        }
        catch (Exception ex)
        {
            SetDetection(null);
            SetStatus(DescribeStatusError(ex));
            await WarnAsync("BootPatch_PackageReadFailed", ex.Message);
        }

        await WarnReadFailuresAsync();
        UpdateStartEnabled();
    }

    private void SetDetection(BootPatchDetection? detection, bool preserveInputs = false)
    {
        _detection = detection;
        var isMagisk = detection?.Kind == BootPatchKind.Magisk;

        PatchOptionsPanel.IsVisible = true;
        PatchInputRow.IsVisible = true;
        MagiskOptionsRow.IsVisible = isMagisk;
        if (!preserveInputs)
        {
            ForceRootfsBox.IsChecked = false;
            KeepEncryptionBox.IsChecked = true;
            InstallRecoveryBox.IsChecked = false;
            KeepAvbBox.IsChecked = true;
            PatchVbmetaBox.IsChecked = false;
        }

        var supported = detection?.SupportedOptions;
        var supports = (BootPatchOption option) => isMagisk &&
            (supported is null || supported.Contains(option));
        ForceRootfsBox.IsVisible = supports(BootPatchOption.LegacySar);
        KeepEncryptionBox.IsVisible = supports(BootPatchOption.KeepForceEncryption);
        InstallRecoveryBox.IsVisible = supports(BootPatchOption.RecoveryMode);
        KeepAvbBox.IsVisible = supports(BootPatchOption.KeepVerity);
        PatchVbmetaBox.IsVisible = supports(BootPatchOption.PatchVbmetaFlag);
        if (!ForceRootfsBox.IsVisible) ForceRootfsBox.IsChecked = false;
        if (!KeepEncryptionBox.IsVisible) KeepEncryptionBox.IsChecked = true;
        if (!InstallRecoveryBox.IsVisible) InstallRecoveryBox.IsChecked = false;
        if (!KeepAvbBox.IsVisible) KeepAvbBox.IsChecked = true;
        if (!PatchVbmetaBox.IsVisible) PatchVbmetaBox.IsChecked = false;
        RefreshArchitectureOptions();
        RefreshKmiOptions();
        UpdatePatchModeState();
        UpdateStartEnabled();

        if (!preserveInputs)
        {
            PrebootDeviceBox.Text = string.Empty;
        }
    }

    /// <summary>Reads the boot architecture, KMI and component presence once and reports a read failure in a
    /// warning dialog. <para>一次性读取 boot 架构、KMI 与组件存在性；读取失败时弹出警告对话框。</para>
    /// </summary>
    private async Task InspectBootAsync()
    {
        _bootInspection = null;
        _bootInspectedPath = null;
        _bootArchitecture = null;
        _bootKmi = null;
        var path = BootPathBox.Text;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var inspection = await BootPatchAnalyzer.InspectBootAsync(path);
                _bootInspection = inspection;
                _bootInspectedPath = Path.GetFullPath(path);
                _bootArchitecture = inspection.Architecture;
                _bootKmi = inspection.Kmi;
                _warnedBootPath = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Instance.Write(LogLevel.Warn, "BootPatch", ex.ToString());
                if (!string.Equals(_warnedBootPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _warnedBootPath = path;
                    await WarnAsync("BootPatch_BootReadFailed", ex.Message);
                }
            }
        }

        await WarnReadFailuresAsync();
        RefreshArchitectureOptions();
        RefreshKmiOptions();
        UpdateStartEnabled();
    }

    private async Task WarnAsync(string messageKey, string detail)
    {
        try
        {
            await DialogService.ShowAsync(this, LanguageService.Instance.GetString("BootPatch_WarningTitle"),
                string.Format(LanguageService.Instance.GetString(messageKey), detail),
                Ursa.Controls.MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "BootPatch", ex.ToString());
        }
    }

    /// <summary>Raises one warning per input when the boot architecture or the package GKI version cannot be read,
    /// so a consistency check that had to be skipped is never silent.
    /// <para>当 boot 架构或补丁包 GKI 版本无法读取时，对每个输入只弹一次警告，使被迫跳过的校验不会静默通过。</para>
    /// </summary>
    private async Task WarnReadFailuresAsync()
    {
        if (_bootInspection is { HasKernel: true } boot && string.IsNullOrWhiteSpace(boot.Architecture)
            && _bootInspectedPath is { } bootPath
            && !string.Equals(_warnedBootArchitecturePath, bootPath, StringComparison.OrdinalIgnoreCase))
        {
            _warnedBootArchitecturePath = bootPath;
            await WarnAsync("BootPatch_BootArchitectureUnreadable", Path.GetFileName(bootPath));
        }

        if (_detection is { Kind: BootPatchKind.GkiKernel, Kmis: not { Count: > 0 } })
        {
            var packagePath = _packagePath ?? (File.Exists(FilePathBox.Text) ? FilePathBox.Text : null);
            if (packagePath is not null
                && !string.Equals(_warnedGkiVersionPath, packagePath, StringComparison.OrdinalIgnoreCase))
            {
                _warnedGkiVersionPath = packagePath;
                await WarnAsync("BootPatch_GkiVersionUnreadable", Path.GetFileName(packagePath));
            }
        }
    }

    private void RefreshArchitectureOptions()
    {
        var detection = _detection;
        var detectedLabel = string.IsNullOrWhiteSpace(_bootArchitecture)
            ? LanguageService.Instance.GetString("BootPatch_ArchitectureUnknown")
            : _bootArchitecture;
        var autoText = LanguageService.Instance.GetString("BootPatch_AutoDetectArchitecture", detectedLabel);
        List<string> items = [autoText];

        if (detection?.Architectures is { Count: > 0 })
        {
            items.AddRange(detection.Architectures);
        }

        ArchitectureBox.ItemsSource = items;
        ArchitectureBox.SelectedIndex = 0;
    }

    private void RefreshKmiOptions()
    {
        var detection = _detection;
        if (!RequiresKmiChoice())
        {
            KmiBox.ItemsSource = Array.Empty<string>();
            KmiBox.SelectedIndex = -1;
            return;
        }

        if (HasGkiVersionChoices())
        {
            KmiBox.ItemsSource = detection!.Kmis!.ToList();
            KmiBox.SelectedIndex = -1;
            return;
        }

        var detectedLabel = string.IsNullOrWhiteSpace(_bootKmi)
            ? LanguageService.Instance.GetString("BootPatch_KmiUnknown")
            : _bootKmi;
        var items = new List<string>
        {
            LanguageService.Instance.GetString("BootPatch_AutoDetectKmi", detectedLabel)
        };
        if (detection?.Kmis is { Count: > 0 })
        {
            items.AddRange(detection.Kmis);
        }
        KmiBox.ItemsSource = items;
        KmiBox.SelectedIndex = 0;
    }

    private string? SelectedArchitecture()
    {
        if (ArchitectureBox.SelectedIndex <= 0)
            return _bootArchitecture ?? _detection?.Architectures.FirstOrDefault();
        return ArchitectureBox.SelectedItem as string;
    }

    private string? SelectedKmi()
    {
        if (KmiBox.SelectedIndex < 0) return null;
        if (ResolvedPatchMode() == BootPatchMode.Gki) return KmiBox.SelectedItem as string;
        return _detection?.Kind == BootPatchKind.KernelSu && KmiBox.SelectedIndex > 0
            ? KmiBox.SelectedItem as string
            : null;
    }

    private async void OnStartPatchClick(object? sender, RoutedEventArgs e)
    {
        var bootPath = BootPathBox.Text;
        if (_isPatching || string.IsNullOrWhiteSpace(bootPath)) return;
        if (!IsPackageSelected() || !EvaluateSelection() || !HasRequiredSuperKey()) return;
        try
        {
            _isPatching = true;
            UpdateStartEnabled();
            MainBusyContainer.IsLoading = true; ProgressPanel.IsVisible = true; PatchProgressBar.Value = 0;
            SetStatus(string.Empty);
            var packagePath = await ResolvePackagePathAsync();
            if (string.IsNullOrWhiteSpace(packagePath)) return;
            var patchMode = ResolvedPatchMode();
            var usesSuperKey = _detection?.Kind is BootPatchKind.APatch or BootPatchKind.KernelPatch
                || patchMode == BootPatchMode.Kpm;
            var output = BuildOutputPath(bootPath, packagePath, patchMode);
            var result = await _patchService.PatchAsync(packagePath, bootPath, output, new BootPatchOptions
            {
                Architecture = SelectedArchitecture(),
                KernelSuKmi = SelectedKmi(),
                SuperPassword = usesSuperKey ? PrebootDeviceBox.Text : null,
                ForceRootfs = ForceRootfsBox.IsChecked == true,
                KeepForceEncryption = KeepEncryptionBox.IsChecked == true,
                KeepVerity = KeepAvbBox.IsChecked == true,
                RecoveryMode = InstallRecoveryBox.IsChecked == true,
                PatchVbmetaFlag = PatchVbmetaBox.IsChecked == true,
                PreInitDevice = patchMode is BootPatchMode.Gki or BootPatchMode.Kpm || usesSuperKey
                    ? null
                    : PrebootDeviceBox.Text,
                Progress = new Progress<double>(p => PatchProgressBar.Value = Math.Clamp(p * 100d, 0d, 100d))
            });
            SetStatus(Path.GetFileName(result.OutputPath));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) { SetStatus(DescribeStatusError(ex)); }
        finally { MainBusyContainer.IsLoading = false; _isPatching = false; UpdateStartEnabled(); }
    }

    /// <summary>Returns the local package file to patch with, downloading the selected release when needed.
    /// <para>返回用于修补的本地补丁包；按需下载已选择的版本。</para>
    /// </summary>
    private async Task<string?> ResolvePackagePathAsync()
    {
        if (SelectedSource is not { } source)
        {
            return FilePathBox.Text;
        }

        if (SelectedRelease() is not { } release)
        {
            return null;
        }

        if (_packagePath is not null && _detection is { IsSupported: true })
        {
            return _packagePath;
        }

        SetProvisionLock(true);
        try
        {
            var token = _operationCts?.Token ?? BeginOperation();
            var artifact = await EnsureReleasePackageAsync(release, token);
            var validation = await _packageService.ValidateAsync(artifact.Path, source, token);
            if (!validation.IsSupported)
            {
                SetDetection(validation.Detection, preserveInputs: true);
                SetStatus(DescribeInvalidPackage(validation.Problem));
                return null;
            }

            _packagePath = artifact.Path;
            SetDetection(validation.Detection, preserveInputs: true);
            RefreshVersionLabels();
            return artifact.Path;
        }
        finally
        {
            SetProvisionLock(false);
        }
    }

    /// <summary>Builds the output path beside the boot image: <c>boot-&lt;package&gt;-&lt;mode&gt;-&lt;8 random&gt;.img</c>.
    /// <para>在 boot 镜像同目录构造输出路径：<c>boot-&lt;补丁包&gt;-&lt;模式&gt;-&lt;8 位随机&gt;.img</c>。</para>
    /// </summary>
    private string BuildOutputPath(string bootPath, string packagePath, BootPatchMode mode)
    {
        var packageToken = SanitizeToken(Path.GetFileNameWithoutExtension(packagePath));
        var modeToken = mode switch
        {
            BootPatchMode.Lkm => "lkm",
            BootPatchMode.Kpm => "kpm",
            BootPatchMode.Gki => "gki",
            _ => _detection?.Kind switch
            {
                BootPatchKind.Magisk => "magisk",
                BootPatchKind.APatch => "apatch",
                BootPatchKind.KernelPatch => "kpm",
                BootPatchKind.KernelSu => "lkm",
                BootPatchKind.KernelSuLkm => "lkm",
                _ => "auto",
            },
        };
        var directory = Path.GetDirectoryName(Path.GetFullPath(bootPath))!;
        string path;
        do
        {
            var random = RandomNumberGenerator.GetString(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 8);
            path = Path.Combine(directory, $"boot-{packageToken}-{modeToken}-{random}.img");
        }
        while (File.Exists(path));

        return path;
    }

    private static string SanitizeToken(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 || char.IsWhiteSpace(character) ? '-' : character);
        }

        var token = builder.ToString().Trim('-', '.');
        return token.Length == 0 ? "package" : token;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        CancelOperations();
        Close();
    }
}