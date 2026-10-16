using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.AndroidMeta.Apk;
using UotanStudio.Firmware.Archive.Browsing;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// APK metadata editor view model: hosts the application info extracted by <see cref="ApkParser"/>
/// (package name/version/SDK/permissions/ABIs/icon), with the main view showing the icon and all
/// details.
/// <para>APK 元数据编辑器视图模型：承载 <see cref="ApkParser"/> 提取的应用信息
/// （包名/版本/SDK/权限/ABI/图标），主视图展示图标与全部详细信息。</para>
/// </summary>
public partial class ApkEditorViewModel : Document
{
    /// <summary>
    /// The localized title of the busy/loading label.
    /// <para>加载中提示文案的本地化标题。</para>
    /// </summary>
    public static string BusyLabel => LanguageService.Instance.GetString("Dock_Apk_Busy");

    /// <summary>
    /// Whether the background analysis is running (drives the busy indicator animation).
    /// <para>是否正在后台分析（联动忙碌指示动画）。</para>
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// The file path of the document.
    /// <para>文档对应的文件路径。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignApkCommand))]
    private string? _filePath;

    /// <summary>
    /// The analysis error text; empty means no error.
    /// <para>分析错误文本；为空表示无错误。</para>
    /// </summary>
    [ObservableProperty] private string _compileError = string.Empty;

    /// <summary>
    /// The localized file-size display text (status bar / overview).
    /// <para>文件大小显示文本（状态栏/概览）。</para>
    /// </summary>
    [ObservableProperty] private string _sizeDisplay = string.Empty;

    /// <summary>
    /// The application package name.
    /// <para>应用包名。</para>
    /// </summary>
    [ObservableProperty] private string _packageName = string.Empty;

    [ObservableProperty] private string _applicationName = string.Empty;

    /// <summary>
    /// The version name.
    /// <para>版本名（versionName）。</para>
    /// </summary>
    [ObservableProperty] private string _versionName = string.Empty;

    /// <summary>
    /// The version-code text.
    /// <para>版本号文本（versionCode）。</para>
    /// </summary>
    [ObservableProperty] private string _versionCodeDisplay = string.Empty;

    [ObservableProperty] private string _versionDisplay = string.Empty;

    /// <summary>
    /// The SDK info text (min/target/compile).
    /// <para>SDK 信息文本（min/target/compile）。</para>
    /// </summary>
    [ObservableProperty] private string _sdkDisplay = string.Empty;

    [ObservableProperty] private string _targetSystemDisplay = "-";

    [ObservableProperty] private string _minimumSystemDisplay = "-";

    [ObservableProperty] private string _signingStateDisplay = "-";

    [ObservableProperty] private string _hardeningStateDisplay = "-";

    /// <summary>
    /// The ABI list text.
    /// <para>ABI 列表文本。</para>
    /// </summary>
    [ObservableProperty] private string _abiDisplay = string.Empty;

    /// <summary>
    /// The permission list (shown line by line in the main view).
    /// <para>权限列表（主视图逐行展示）。</para>
    /// </summary>
    [ObservableProperty] private string _permissionsDisplay = string.Empty;

    /// <summary>
    /// The application icon (rendered for PNG/WebP/JPG; placeholder when <c>null</c>).
    /// <para>应用图标（PNG/WebP/JPG 可渲染；矢量/缺失为 <c>null</c> 时显示占位）。</para>
    /// </summary>
    [ObservableProperty] private Bitmap? _iconBitmap;

    /// <summary>
    /// Whether the icon is renderable (otherwise the main view shows a placeholder).
    /// <para>图标是否可渲染（否则主视图显示占位）。</para>
    /// </summary>
    [ObservableProperty] private bool _hasIcon;

    /// <summary>Whether the APK can currently be installed to the selected online ADB device.</summary>
    [ObservableProperty] private bool _isInstalling;

    /// <summary>
    /// The parsed APK metadata (for the properties panel).
    /// <para>解析后的 APK 元数据（属性面板取值用）。</para>
    /// </summary>
    private ApkInfo? _info;

    /// <summary>
    /// The parsed APK metadata; <c>null</c> when not loaded.
    /// <para>解析后的 APK 元数据；未加载时为 <c>null</c>。</para>
    /// </summary>
    public ApkInfo? Info => _info;

    /// <summary>
    /// Creates the APK metadata editor view model.
    /// <para>创建 APK 元数据编辑器视图模型。</para>
    /// </summary>
    public ApkEditorViewModel()
    {
    }

    /// <summary>
    /// Gets whether the install action has a valid APK and an online selected ADB device.
    /// Fastboot, recovery and bootrom devices cannot receive an APK install command.
    /// </summary>
    public bool CanInstall => !IsInstalling
        && !string.IsNullOrWhiteSpace(FilePath)
        && File.Exists(FilePath)
        && DeviceViewModel.Instance?.SelectedDevice is { Protocol: DeviceProtocolKind.Adb, State: var state }
        && string.Equals(state, "device", StringComparison.OrdinalIgnoreCase);

    /// <summary>Refreshes the install button state after device selection or connection changes.</summary>
    public void RefreshInstallAvailability() => OnPropertyChanged(nameof(CanInstall));

    partial void OnIsInstallingChanged(bool value) => OnPropertyChanged(nameof(CanInstall));

    /// <summary>
    /// Extracts every APK entry into the directory containing the APK.
    /// </summary>
    public async Task ExtractToContainingDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var path = FilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("APK file was not found.", path);
        }

        var destination = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new InvalidOperationException("The APK has no containing directory.");
        }

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var browser = ArchiveBrowser.Open(path, cancellationToken);
            browser.Extract(".", destination, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Installs this APK on the currently selected online ADB device.</summary>
    public async Task<FlashResult> InstallToSelectedDeviceAsync(CancellationToken cancellationToken = default)
    {
        var device = DeviceViewModel.Instance?.SelectedDevice;
        var path = FilePath;
        if (!CanInstall || device is null || string.IsNullOrWhiteSpace(path))
        {
            return new FlashResult { Success = false, Summary = "Select an online ADB device before installing the APK." };
        }

        IsInstalling = true;
        try
        {
            return await FlashService.Default.FlashImageAsync(
                device, "apk", path, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            IsInstalling = false;
            RefreshInstallAvailability();
        }
    }

    /// <summary>
    /// Asynchronously analyzes an APK file and populates the metadata display properties.
    /// <para>异步分析 APK 文件并填充元数据展示属性。</para>
    /// </summary>
    /// <param name="path">APK 文件路径。<para>APK file path.</para></param>
    public async Task LoadAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        FilePath = path;
        RefreshInstallAvailability();
        SigningOutputPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "-signed.apk");
        Title = Path.GetFileName(path);

        IsBusy = true;
        try
        {
            var info = await Task.Run(() => ApkParser.Analyze(path, LanguageService.Instance.CurrentLanguage), default).ConfigureAwait(true);

            _info = info;
            PackageName = info.PackageName;
            ApplicationName = string.IsNullOrWhiteSpace(info.ApplicationName) || info.ApplicationName.StartsWith("@", StringComparison.Ordinal)
                ? info.PackageName : info.ApplicationName;
            VersionName = info.VersionName;
            VersionCodeDisplay = info.VersionCode.ToString("N0");
            VersionDisplay = string.IsNullOrWhiteSpace(VersionName)
                ? VersionCodeDisplay
                : string.IsNullOrWhiteSpace(VersionCodeDisplay) ? VersionName : $"{VersionName} ({VersionCodeDisplay})";
            SizeDisplay = FormatFileSize(info.FileSize);
            TargetSystemDisplay = info.TargetSdkVersion > 0 ? info.TargetSdkVersion.ToString() : "-";
            MinimumSystemDisplay = info.MinSdkVersion > 0 ? info.MinSdkVersion.ToString() : "-";
            SdkDisplay = $"min {info.MinSdkVersion} / target {info.TargetSdkVersion} / compile {info.CompileSdkVersion}";
            SigningStateDisplay = info.SigningSchemes.Count == 0
                ? LanguageService.Instance.GetString("Apk_SigningUnsigned")
                : string.Format(LanguageService.Instance.GetString("Apk_SigningSchemes"), string.Join(", ", info.SigningSchemes));
            HardeningStateDisplay = info.HardeningProvider is { Length: > 0 } provider
                ? string.Format(LanguageService.Instance.GetString("Apk_HardeningDetected"), provider)
                : LanguageService.Instance.GetString("Apk_HardeningNotDetected");
            AbiDisplay = info.Abis.Count > 0 ? string.Join(", ", info.Abis) : "-";
            PermissionsDisplay = info.Permissions.Count > 0 ? string.Join(Environment.NewLine, info.Permissions) : "-";

            LoadIcon(info);
            CompileError = string.Empty;
            RefreshInstallAvailability();
        }
        catch (Exception ex)
        {
            CompileError = UserError.Describe(ex, "UserError_AnalyzeFailed", "APK");
            PackageName = string.Empty;
            ApplicationName = string.Empty;
            VersionName = string.Empty;
            VersionDisplay = string.Empty;
            RefreshInstallAvailability();
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes:N0} B";
        var value = (double)bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value:0.##} {units[index]}";
    }

    /// <summary>
    /// Creates a bitmap from the icon bytes: PNG/WebP/JPG decode directly; AXML vector
    /// drawables are parsed and rendered; other formats show a placeholder.
    /// <para>从图标字节生成位图：PNG/WebP/JPG 直接解码；AXML vector drawable 解析并渲染；
    /// 其余格式显示占位。</para>
    /// </summary>
    private void LoadIcon(ApkInfo info)
    {
        IconBitmap = null;
        HasIcon = false;

        var bytes = info.IconBytes;
        if (bytes is null || bytes.Length < 8)
        {
            return;
        }

        var isAxml = bytes[0] == 0x03 && bytes[1] == 0x00 && bytes[2] == 0x08 && bytes[3] == 0x00;
        if (isAxml)
        {
            IconBitmap = RenderVectorIcon(bytes);
            HasIcon = IconBitmap is not null;
            return;
        }

        var isPng = bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
        var isJpeg = bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        var isWebp = bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F';
        if (!isPng && !isJpeg && !isWebp)
        {
            return;
        }

        try
        {
            using var ms = new MemoryStream(bytes);
            IconBitmap = new Bitmap(ms);
            HasIcon = true;
        }
        catch
        {
        }
    }

    /// <summary>
    /// Parses an AXML vector drawable and renders it to a bitmap; returns <c>null</c> when
    /// parsing or rendering fails.
    /// <para>解析 AXML vector drawable 并渲染为位图；解析或渲染失败时返回 <c>null</c>。</para>
    /// </summary>
    private static Bitmap? RenderVectorIcon(byte[] bytes)
    {
        var vd = VectorDrawableParser.Parse(bytes);
        if (vd is null || vd.Paths.Count == 0)
        {
            return null;
        }

        try
        {
            var width = Math.Max(1, (int)Math.Round(vd.Width));
            var height = Math.Max(1, (int)Math.Round(vd.Height));
            var scaleX = vd.ViewportWidth > 0 ? width / vd.ViewportWidth : 1.0;
            var scaleY = vd.ViewportHeight > 0 ? height / vd.ViewportHeight : 1.0;

            var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            using (var ctx = bitmap.CreateDrawingContext(true))
            {
                using (ctx.PushTransform(Matrix.CreateScale(scaleX, scaleY)))
                {
                    foreach (var path in vd.Paths)
                    {
                        Geometry geometry;
                        try
                        {
                            geometry = Geometry.Parse(path.PathData);
                        }
                        catch
                        {
                            continue;
                        }

                        if (path.FillColor is { } fill)
                        {
                            ctx.DrawGeometry(
                                new SolidColorBrush(Color.FromUInt32(fill), path.FillAlpha),
                                null,
                                geometry);
                        }

                        if (path.StrokeColor is { } stroke && path.StrokeWidth > 0)
                        {
                            ctx.DrawGeometry(
                                null,
                                new Pen(new SolidColorBrush(Color.FromUInt32(stroke), path.StrokeAlpha), path.StrokeWidth),
                                geometry);
                        }
                    }
                }
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Releases the model and view data held by this document (called when the document is closed).
    /// <para>释放文档持有的模型与视图数据（文档关闭时调用）。</para>
    /// </summary>
    public void Cleanup()
    {
        _info = null;
        IconBitmap = null;
        HasIcon = false;
        PackageName = string.Empty;
        ApplicationName = string.Empty;
        VersionName = string.Empty;
        VersionCodeDisplay = string.Empty;
        VersionDisplay = string.Empty;
        SdkDisplay = string.Empty;
        TargetSystemDisplay = "-";
        MinimumSystemDisplay = "-";
        SigningStateDisplay = "-";
        HardeningStateDisplay = "-";
        AbiDisplay = string.Empty;
        PermissionsDisplay = string.Empty;
        SizeDisplay = string.Empty;
        CompileError = string.Empty;
        FilePath = null;
        RefreshInstallAvailability();
    }
}