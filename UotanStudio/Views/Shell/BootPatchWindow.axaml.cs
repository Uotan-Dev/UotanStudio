using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Firmware.BootSecurity.Patcher;
using UotanStudio.Services;
using UotanStudio.Services.PatchPackages;
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
    private BootPatchDetection? _detection;
    private string? _packagePath;
    private string? _bootArchitecture;
    private string? _bootKmi;
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _probeCts;
    private int _sourceEpoch;
    private int _prepareRequest;
    private bool _isFetchingReleases;
    private bool _isPatching;

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
        Opened += async (_, _) =>
        {
            await DetectBootArchitectureAsync();
            await EnsureReleasesLoadedAsync();
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

    private void UpdateFileSourceState()
    {
        var custom = FileSourceBox.SelectedIndex == CustomFileIndex;
        FilePathBox.IsVisible = custom;
        BrowseFileButton.IsVisible = custom;
        FileVersionBox.IsVisible = !custom;

        _sourceEpoch++;
        _prepareRequest++;
        CancelOperations();
        _probeCts?.Cancel();
        _releases = [];
        _packagePath = null;
        _versionLabels.Clear();
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

    /// <summary>Enables start only when a usable patch package and a boot image are both selected.
    /// <para>仅当补丁包与 boot 镜像都可用时才允许开始，否则禁用「开始」按钮。</para>
    /// </summary>
    private void UpdateStartEnabled() => StartButton.IsEnabled =
        !_isPatching
        && !string.IsNullOrWhiteSpace(BootPathBox.Text)
        && IsPackageSelected();

    private bool IsPackageSelected()
    {
        if (SelectedSource is not null)
        {
            return SelectedRelease() is not null && _packagePath is not null && _detection is { IsSupported: true };
        }

        return !string.IsNullOrWhiteSpace(FilePathBox.Text) && _detection is { IsSupported: true };
    }

    private void SetStatus(string text)
    {
        ProgressText.Text = text;
        if (!_isPatching) ProgressPanel.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private string DescribeInvalidPackage(string? problem)
        => string.IsNullOrWhiteSpace(problem)
            ? LanguageService.Instance.GetString("BootPatch_PackageInvalid", string.Empty)
            : LanguageService.Instance.GetString("BootPatch_PackageInvalid", problem);

    /// <summary>Loads the release list of the selected upstream project into the version list.
    /// <para>把所选上游项目的版本列表加载到版本下拉框。</para>
    /// </summary>
    private async Task RefreshReleasesAsync()
    {
        if (_isFetchingReleases || SelectedSource is not { } source)
        {
            if (SelectedSource is null) SetStatus(string.Empty);
            return;
        }

        var epoch = _sourceEpoch;
        var token = BeginOperation();
        _isFetchingReleases = true;
        FileVersionBox.IsEnabled = false;
        if (!_packageService.CatalogLoaded(source))
        {
            SetStatus(LanguageService.Instance.GetString("BootPatch_VersionsLoading"));
        }

        try
        {
            var releases = await _packageService.ListPackagesAsync(source, token);
            if (epoch != _sourceEpoch) return;
            _releases = releases;
            _versionLabels.Clear();
            foreach (var release in releases)
            {
                _versionLabels.Add(DescribeVersion(release));
            }

            if (releases.Count > 0)
            {
                if (_packageService.PreferredSourceAuthority is null)
                {
                    SetStatus(LanguageService.Instance.GetString("BootPatch_ProbingSources"));
                }

                await ProbeSourcesAsync(releases[0].DownloadUrl);
            }

            if (epoch != _sourceEpoch) return;
            FileVersionBox.SelectedIndex = releases.Count > 0 ? 0 : -1;
            SetStatus(releases.Count > 0 ? string.Empty : LanguageService.Instance.GetString("BootPatch_VersionsEmpty"));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (epoch == _sourceEpoch) SetStatus(UserError.Describe(ex));
        }
        finally
        {
            _isFetchingReleases = false;
            if (epoch == _sourceEpoch)
            {
                FileVersionBox.IsEnabled = true;
                UpdateStartEnabled();
            }
        }
    }

    /// <summary>Speed-tests every candidate source of the newest asset and pins the fastest one for downloads.
    /// <para>对最新资产的全部候选源测速，并把最快的一个固定给后续下载使用。</para>
    /// </summary>
    /// <param name="downloadUrl">Newest release asset URL used as the probe target.
    /// <para>作为测速目标的最新发布资产地址。</para></param>
    private async Task ProbeSourcesAsync(string downloadUrl)
    {
        _probeCts?.Cancel();
        var source = new CancellationTokenSource();
        _probeCts = source;
        try
        {
            await _packageService.ProbeSourcesAsync(new Uri(downloadUrl), source.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException)
        {
            LogService.Instance.Write(LogLevel.Warn, "BootPatch", $"Source probe skipped: {ex.Message}");
        }
    }

    /// <summary>Loads the release list when the window opens with a source selected but nothing listed yet.
    /// <para>窗口打开时若已选中来源但列表为空，则加载版本列表。</para>
    /// </summary>
    private async Task EnsureReleasesLoadedAsync()
    {
        if (_isFetchingReleases || _releases.Count > 0 || SelectedSource is null)
        {
            return;
        }

        await RefreshReleasesAsync();
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
        if (_versionLabels.Count != _releases.Count)
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

    private async void OnVersionChanged(object? sender, SelectionChangedEventArgs e)
        => await PrepareSelectedReleaseAsync();

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
        var token = BeginOperation();
        try
        {
            var artifact = await EnsureReleasePackageAsync(release, token);
            if (epoch != _sourceEpoch || request != _prepareRequest) return;
            var validation = await _packageService.ValidateAsync(artifact.Path, source, token);
            if (epoch != _sourceEpoch || request != _prepareRequest) return;
            _packagePath = validation.IsSupported ? artifact.Path : null;
            SetDetection(validation.Detection);
            SetStatus(validation.IsSupported
                ? LanguageService.Instance.GetString("BootPatch_PackageReady", release.Version)
                : DescribeInvalidPackage(validation.Problem));
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
                SetStatus(UserError.Describe(ex));
            }
        }
        finally
        {
            if (epoch == _sourceEpoch && request == _prepareRequest)
            {
                RefreshVersionLabels();
                UpdateStartEnabled();
            }
        }
    }

    private async Task<RootPatchPackageArtifact> EnsureReleasePackageAsync(RootPatchPackage release,
        CancellationToken token)
    {
        var progress = new Progress<double>(value =>
        {
            PatchProgressBar.Value = Math.Clamp(value * 100d, 0d, 100d);
            ProgressPanel.IsVisible = true;
            ProgressText.Text = LanguageService.Instance.GetString("BootPatch_PackageDownloading", release.Version);
        });
        return await _packageService.EnsurePackageAsync(release, progress, token);
    }

    private async void OnBrowseFileClick(object? sender, RoutedEventArgs e)
    {
        await BrowseAsync(FilePathBox, "Patch package", ["*.apk", "*.zip", "*.ko"]);
        UpdateStartEnabled();
        await DetectPackageAsync();
    }

    private async void OnBrowseBootClick(object? sender, RoutedEventArgs e)
    {
        await BrowseAsync(BootPathBox, "Boot image", ["*.img", "*.bin"]);
        UpdateStartEnabled();
        await DetectBootArchitectureAsync();
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
                SetStatus(DescribeDetection(validation.Detection));
            }
            else
            {
                SetStatus(DescribeInvalidPackage(validation.Problem));
            }
        }
        catch (Exception ex)
        {
            SetDetection(null);
            SetStatus(UserError.Describe(ex));
        }

        UpdateStartEnabled();
    }

    private string DescribeDetection(BootPatchDetection? detection)
    {
        if (detection is null) return string.Empty;
        var kmi = detection.Kmis is { Count: > 0 }
            ? $"; KMI: {string.Join(", ", detection.Kmis)}"
            : string.Empty;
        var boot = string.IsNullOrWhiteSpace(_bootKmi) ? string.Empty : $"; boot KMI: {_bootKmi}";
        return $"{detection.Kind}: {string.Join(", ", detection.Architectures)}{kmi}{boot}";
    }

    private void SetDetection(BootPatchDetection? detection, bool preserveInputs = false)
    {
        _detection = detection;
        var isMagisk = detection?.Kind == BootPatchKind.Magisk;
        var isApatch = detection?.Kind == BootPatchKind.APatch;
        var isKernelSu = detection?.Kind == BootPatchKind.KernelSu;
        var showPatchInputs = isMagisk || isApatch || isKernelSu;

        PatchOptionsPanel.IsVisible = showPatchInputs;
        PatchInputRow.IsVisible = showPatchInputs;
        MagiskOptionsRow.IsVisible = isMagisk;
        PrebootDeviceBox.IsVisible = isMagisk || isApatch;
        ArchitectureBox.IsVisible = isMagisk || isApatch;
        KmiBox.IsVisible = isKernelSu;
        var supported = detection?.SupportedOptions;
        var supports = (BootPatchOption option) => isMagisk && (supported is null || supported.Contains(option));
        ForceRootfsBox.IsVisible = supports(BootPatchOption.LegacySar);
        KeepEncryptionBox.IsVisible = supports(BootPatchOption.KeepForceEncryption);
        InstallRecoveryBox.IsVisible = supports(BootPatchOption.RecoveryMode);
        KeepAvbBox.IsVisible = supports(BootPatchOption.KeepVerity);
        PatchVbmetaBox.IsVisible = supports(BootPatchOption.PatchVbmetaFlag);
        if (!ForceRootfsBox.IsVisible) ForceRootfsBox.IsChecked = false;
        if (!KeepEncryptionBox.IsVisible) KeepEncryptionBox.IsChecked = false;
        if (!InstallRecoveryBox.IsVisible) InstallRecoveryBox.IsChecked = false;
        if (!KeepAvbBox.IsVisible) KeepAvbBox.IsChecked = false;
        if (!PatchVbmetaBox.IsVisible) PatchVbmetaBox.IsChecked = false;
        RefreshArchitectureOptions();
        RefreshKmiOptions();

        if (!preserveInputs) PrebootDeviceBox.Text = string.Empty;
        PrebootDeviceBox.PlaceholderText = LanguageService.Instance.GetString(
            isApatch ? "BootPatch_ApatchPassword" : "BootPatch_DevicePlaceholder");
        PrebootDeviceBox.PasswordChar = isApatch ? '*' : '\0';
    }

    private async Task DetectBootArchitectureAsync()
    {
        _bootArchitecture = null;
        _bootKmi = null;
        if (!string.IsNullOrWhiteSpace(BootPathBox.Text) && File.Exists(BootPathBox.Text))
        {
            try
            {
                _bootArchitecture = await BootPatchAnalyzer.DetectBootArchitectureAsync(BootPathBox.Text);
                _bootKmi = await BootPatchAnalyzer.DetectBootKmiAsync(BootPathBox.Text);
            }
            catch { _bootArchitecture = null; }
        }
        if (_detection?.Kind == BootPatchKind.KernelSu)
            SetStatus(string.IsNullOrWhiteSpace(_bootKmi)
                ? LanguageService.Instance.GetString("BootPatch_KmiDetectionFailed")
                : LanguageService.Instance.GetString("BootPatch_KmiDetected", _bootKmi));
        RefreshArchitectureOptions();
        RefreshKmiOptions();
    }

    private void RefreshArchitectureOptions()
    {
        var detection = _detection;
        if (detection is null || (detection.Kind is not BootPatchKind.Magisk and not BootPatchKind.APatch))
        {
            ArchitectureBox.ItemsSource = Array.Empty<string>();
            ArchitectureBox.SelectedIndex = -1;
            return;
        }

        var detectedLabel = string.IsNullOrWhiteSpace(_bootArchitecture)
            ? LanguageService.Instance.GetString("BootPatch_ArchitectureUnknown")
            : _bootArchitecture;
        var autoText = LanguageService.Instance.GetString("BootPatch_AutoDetectArchitecture", detectedLabel);
        List<string> items = [autoText];

        items.AddRange(detection.Architectures);
        ArchitectureBox.ItemsSource = items;
        ArchitectureBox.SelectedIndex = 0;
    }

    private void RefreshKmiOptions()
    {
        var detection = _detection;
        if (detection?.Kind != BootPatchKind.KernelSu)
        {
            KmiBox.ItemsSource = Array.Empty<string>();
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
        if (detection.Kmis is { Count: > 0 })
            items.AddRange(detection.Kmis);
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
        => _detection?.Kind == BootPatchKind.KernelSu && KmiBox.SelectedIndex > 0
            ? KmiBox.SelectedItem as string
            : null;

    private async void OnStartPatchClick(object? sender, RoutedEventArgs e)
    {
        var bootPath = BootPathBox.Text;
        if (_isPatching || string.IsNullOrWhiteSpace(bootPath)) return;
        try
        {
            _isPatching = true;
            UpdateStartEnabled();
            MainBusyContainer.IsLoading = true; ProgressPanel.IsVisible = true; PatchProgressBar.Value = 0;
            var packagePath = await ResolvePackagePathAsync();
            if (string.IsNullOrWhiteSpace(packagePath)) return;
            var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(bootPath))!, $"patched_{DateTime.Now:yyyyMMdd_HHmmss}.img");
            var result = await _patchService.PatchAsync(packagePath, bootPath, output, new BootPatchOptions
            {
                Architecture = SelectedArchitecture(),
                KernelSuKmi = SelectedKmi(),
                SuperPassword = _detection?.Kind == BootPatchKind.APatch ? PrebootDeviceBox.Text : null,
                ForceRootfs = ForceRootfsBox.IsChecked == true,
                KeepForceEncryption = KeepEncryptionBox.IsChecked == true,
                KeepVerity = KeepAvbBox.IsChecked == true,
                RecoveryMode = InstallRecoveryBox.IsChecked == true,
                PatchVbmetaFlag = PatchVbmetaBox.IsChecked == true,
                PreInitDevice = _detection?.Kind == BootPatchKind.APatch ? null : PrebootDeviceBox.Text,
                Progress = new Progress<double>(p => { PatchProgressBar.Value = Math.Clamp(p * 100d, 0d, 100d); ProgressText.Text = $"{p:P0}"; })
            });
            ProgressText.Text = $"{result.OutputPath} ({result.Changes.Count} changes)";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) { SetStatus(UserError.Describe(ex)); }
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

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        CancelOperations();
        Close();
    }
}