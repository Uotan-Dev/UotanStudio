using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Apk;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>APK 元数据编辑器视图模型：承载 <see cref="ApkParser"/> 提取的应用信息
/// （包名/版本/SDK/权限/ABI/图标），主视图展示图标与全部详细信息。</para>
/// APK metadata editor view model: hosts the application info extracted by <see cref="ApkParser"/>
/// (package name/version/SDK/permissions/ABIs/icon), with the main view showing the icon and all
/// details.
/// </summary>
public partial class ApkEditorViewModel : Document
{
    /// <summary>
    /// <para>加载中提示文案的本地化标题。</para>
    /// The localized title of the busy/loading label.
    /// </summary>
    public static string BusyLabel => LanguageService.Instance.GetString("Dock_Apk_Busy");

    /// <summary>
    /// <para>是否正在后台分析（联动忙碌指示动画）。</para>
    /// Whether the background analysis is running (drives the busy indicator animation).
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// <para>文档对应的文件路径。</para>
    /// The file path of the document.
    /// </summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>
    /// <para>分析错误文本；为空表示无错误。</para>
    /// The analysis error text; empty means no error.
    /// </summary>
    [ObservableProperty] private string _compileError = string.Empty;

    /// <summary>
    /// <para>文件大小显示文本（状态栏/概览）。</para>
    /// The localized file-size display text (status bar / overview).
    /// </summary>
    [ObservableProperty] private string _sizeDisplay = string.Empty;

    /// <summary>
    /// <para>应用包名。</para>
    /// The application package name.
    /// </summary>
    [ObservableProperty] private string _packageName = string.Empty;

    /// <summary>
    /// <para>版本名（versionName）。</para>
    /// The version name.
    /// </summary>
    [ObservableProperty] private string _versionName = string.Empty;

    /// <summary>
    /// <para>版本号文本（versionCode）。</para>
    /// The version-code text.
    /// </summary>
    [ObservableProperty] private string _versionCodeDisplay = string.Empty;

    /// <summary>
    /// <para>SDK 信息文本（min/target/compile）。</para>
    /// The SDK info text (min/target/compile).
    /// </summary>
    [ObservableProperty] private string _sdkDisplay = string.Empty;

    /// <summary>
    /// <para>ABI 列表文本。</para>
    /// The ABI list text.
    /// </summary>
    [ObservableProperty] private string _abiDisplay = string.Empty;

    /// <summary>
    /// <para>权限列表（主视图逐行展示）。</para>
    /// The permission list (shown line by line in the main view).
    /// </summary>
    [ObservableProperty] private string _permissionsDisplay = string.Empty;

    /// <summary>
    /// <para>应用图标（PNG/WebP/JPG 可渲染；矢量/缺失为 <c>null</c> 时显示占位）。</para>
    /// The application icon (rendered for PNG/WebP/JPG; placeholder when <c>null</c>).
    /// </summary>
    [ObservableProperty] private Bitmap? _iconBitmap;

    /// <summary>
    /// <para>图标是否可渲染（否则主视图显示占位）。</para>
    /// Whether the icon is renderable (otherwise the main view shows a placeholder).
    /// </summary>
    [ObservableProperty] private bool _hasIcon;

    /// <summary>
    /// <para>解析后的 APK 元数据（属性面板取值用）。</para>
    /// The parsed APK metadata (for the properties panel).
    /// </summary>
    private ApkInfo? _info;

    /// <summary>
    /// <para>解析后的 APK 元数据；未加载时为 <c>null</c>。</para>
    /// The parsed APK metadata; <c>null</c> when not loaded.
    /// </summary>
    public ApkInfo? Info => _info;

    /// <summary>
    /// <para>创建 APK 元数据编辑器视图模型。</para>
    /// Creates the APK metadata editor view model.
    /// </summary>
    public ApkEditorViewModel()
    {
    }

    /// <summary>
    /// <para>异步分析 APK 文件并填充元数据展示属性。</para>
    /// Asynchronously analyzes an APK file and populates the metadata display properties.
    /// </summary>
    /// <param name="path">APK 文件路径。<para>APK file path.</para></param>
    public async Task LoadAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        FilePath = path;
        Title = Path.GetFileName(path);

        IsBusy = true;
        try
        {
            var info = await Task.Run(() => ApkParser.Analyze(path), default).ConfigureAwait(true);

            _info = info;
            PackageName = info.PackageName;
            VersionName = info.VersionName;
            VersionCodeDisplay = info.VersionCode.ToString("N0");
            SizeDisplay = string.Format(
                LanguageService.Instance.GetString("Dock_File_SizeFormat"), info.FileSize);
            SdkDisplay = $"min {info.MinSdkVersion} / target {info.TargetSdkVersion} / compile {info.CompileSdkVersion}";
            AbiDisplay = info.Abis.Count > 0 ? string.Join(", ", info.Abis) : "-";
            PermissionsDisplay = info.Permissions.Count > 0 ? string.Join(Environment.NewLine, info.Permissions) : "-";

            LoadIcon(info);
            CompileError = string.Empty;
        }
        catch (Exception ex)
        {
            CompileError = $"Failed to analyze APK: {ex.Message}";
            PackageName = string.Empty;
            VersionName = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>从图标字节生成位图：仅 PNG/WebP/JPG 可渲染，其余（矢量 AXML 等）显示占位。</para>
    /// Creates a bitmap from the icon bytes: only PNG/WebP/JPG are renderable; others (vector AXML
    /// etc.) show a placeholder.
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

        // 魔数：PNG 89 50 4E 47；JPEG FF D8 FF；WebP RIFF..WEBP；其余（AXML 03 00 08 00 等）不渲染。
        // Magic: PNG 89 50 4E 47; JPEG FF D8 FF; WebP RIFF..WEBP; others (AXML 03 00 08 00 etc.) skipped.
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
            // 解码失败时保持占位。
            // Keep the placeholder when decoding fails.
        }
    }

    /// <summary>
    /// <para>释放文档持有的模型与视图数据（文档关闭时调用）。</para>
    /// Releases the model and view data held by this document (called when the document is closed).
    /// </summary>
    public void Cleanup()
    {
        _info = null;
        IconBitmap = null;
        HasIcon = false;
        PackageName = string.Empty;
        VersionName = string.Empty;
        VersionCodeDisplay = string.Empty;
        SdkDisplay = string.Empty;
        AbiDisplay = string.Empty;
        PermissionsDisplay = string.Empty;
        SizeDisplay = string.Empty;
        CompileError = string.Empty;
        FilePath = null;
    }
}
