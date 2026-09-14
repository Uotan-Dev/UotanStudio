using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher;
using UotanStudio.Services;

namespace UotanStudio.Views.Shell;

/// <summary>Native boot patch window.</summary>
public partial class BootPatchWindow : Window
{
    private readonly BootPatchService _patchService = new();
    private BootPatchDetection? _detection;
    private string? _bootArchitecture;
    private string? _bootKmi;
    private bool _isPatching;

    public BootPatchWindow() : this(null) { }
    public BootPatchWindow(string? imagePath)
    {
        InitializeComponent();
        FileSourceBox.SelectionChanged += OnFileSourceChanged;
        Opened += async (_, _) => await DetectBootArchitectureAsync();
        BootPathBox.Text = ToAbsolutePath(imagePath);
        UpdateFileSourceState();
    }

    private static string? ToAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        try { return Path.GetFullPath(path); } catch { return path; }
    }
    private void OnFileSourceChanged(object? sender, SelectionChangedEventArgs e) => UpdateFileSourceState();
    private void UpdateFileSourceState()
    {
        var custom = FileSourceBox.SelectedIndex == 0;
        FilePathBox.IsEnabled = custom; BrowseFileButton.IsEnabled = custom;
        if (!custom)
        {
            FilePathBox.Text = string.Empty;
            SetDetection(null);
        }
    }
    private async void OnBrowseFileClick(object? sender, RoutedEventArgs e)
    { await BrowseAsync(FilePathBox, "Patch package", ["*.apk", "*.zip", "*.ko"]); await DetectPackageAsync(); }
    private async void OnBrowseBootClick(object? sender, RoutedEventArgs e)
    {
        await BrowseAsync(BootPathBox, "Boot image", ["*.img", "*.bin"]);
        await DetectBootArchitectureAsync();
    }
    private async Task BrowseAsync(TextBox target, string description, IReadOnlyList<string> patterns)
    {
        if (StorageProvider is not { CanOpen: true } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, Title = description, FileTypeFilter = [new FilePickerFileType(description) { Patterns = patterns }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) target.Text = path;
    }
    private async Task DetectPackageAsync()
    {
        SetDetection(null);
        if (string.IsNullOrWhiteSpace(FilePathBox.Text) || !File.Exists(FilePathBox.Text)) return;
        try
        {
            SetDetection(await _patchService.DetectAsync(FilePathBox.Text));
            if (_detection is not null)
            {
                var kmi = _detection.Kmis is { Count: > 0 }
                    ? $"; KMI: {string.Join(", ", _detection.Kmis)}"
                    : string.Empty;
                var boot = string.IsNullOrWhiteSpace(_bootKmi) ? string.Empty : $"; boot KMI: {_bootKmi}";
                ProgressText.Text = $"{_detection.Kind}: {string.Join(", ", _detection.Architectures)}{kmi}{boot}";
            }
        }
        catch (Exception ex) { SetDetection(null); ProgressText.Text = ex.Message; }
    }

    private void SetDetection(BootPatchDetection? detection)
    {
        _detection = detection;
        var isMagisk = detection?.Kind == BootPatchKind.Magisk;
        var isApatch = detection?.Kind == BootPatchKind.APatch;
        var showPatchInputs = isMagisk || isApatch;

        PatchOptionsPanel.IsVisible = showPatchInputs;
        MagiskOptionsRow.IsVisible = isMagisk;
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

        // The left field is the SuperKey field for APatch and the pre-init
        // device field for Magisk. KernelSU and unknown packages hide both.
        PrebootDeviceBox.Text = string.Empty;
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
            ProgressText.Text = string.IsNullOrWhiteSpace(_bootKmi)
                ? "Unable to determine the boot kernel KMI."
                : $"KernelSU KMI: {_bootKmi} (supported: {string.Join(", ", _detection.Kmis ?? [])})";
        RefreshArchitectureOptions();
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

        var autoLabel = LanguageService.Instance.GetString("BootPatch_AutoDetect");
        var detectedLabel = string.IsNullOrWhiteSpace(_bootArchitecture)
            ? LanguageService.Instance.GetString("BootPatch_ArchitectureUnknown")
            : _bootArchitecture;
        var autoText = $"{autoLabel}（{detectedLabel}）";
        var items = new List<string> { autoText };
        // Preserve the package's architecture entries verbatim, including an
        // entry equal to the architecture detected from boot.
        items.AddRange(detection.Architectures);
        ArchitectureBox.ItemsSource = items;
        ArchitectureBox.SelectedIndex = 0;
    }

    private string? SelectedArchitecture()
    {
        if (ArchitectureBox.SelectedIndex <= 0)
            return _bootArchitecture ?? _detection?.Architectures.FirstOrDefault();
        return ArchitectureBox.SelectedItem as string;
    }
    private async void OnStartPatchClick(object? sender, RoutedEventArgs e)
    {
        if (_isPatching) return;
        if (string.IsNullOrWhiteSpace(FilePathBox.Text) || string.IsNullOrWhiteSpace(BootPathBox.Text)) { ProgressText.Text = "Select a patch package and boot image first."; return; }
        try
        {
            _isPatching = true;
            MainBusyContainer.IsLoading = true; ProgressPanel.IsVisible = true; PatchProgressBar.Value = 0;
            var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(BootPathBox.Text))!, $"patched_{DateTime.Now:yyyyMMdd_HHmmss}.img");
            var result = await _patchService.PatchAsync(FilePathBox.Text, BootPathBox.Text, output, new BootPatchOptions
            {
                Architecture = SelectedArchitecture(),
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
        catch (Exception ex) { ProgressText.Text = ex.Message; }
        finally { MainBusyContainer.IsLoading = false; _isPatching = false; }
    }
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}