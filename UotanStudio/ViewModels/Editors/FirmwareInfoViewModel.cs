using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware;
using UotanStudio.Firmware.Common;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;
using FirmwareOpResult = UotanStudio.Services.Firmware.FirmwareOpResult;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>固件元数据面板视图模型：以只读 Dock 文档展示固件文件的格式名、摘要、
/// 元数据键值对与条目列表，数据来自 <see cref="FormatDetector"/> + <see cref="IFormatHandler.ReadMetadataAsync"/>。</para>
/// Firmware metadata panel view model: a read-only Dock document showing a firmware file's format name,
/// summary, metadata key-value pairs and entry list, populated from <see cref="FormatDetector"/> plus
/// <see cref="IFormatHandler.ReadMetadataAsync"/>.
/// </summary>
public partial class FirmwareInfoViewModel : Document
{
    /// <summary>
    /// <para>面板标题的本地化文本。</para>
    /// The localized panel title.
    /// </summary>
    public static string TitleLabel => LanguageService.Instance.GetString("FirmwareInfo_Title");

    /// <summary>
    /// <para>「格式名」字段标签的本地化文本。</para>
    /// The localized "format name" field label.
    /// </summary>
    public static string FormatNameLabel => LanguageService.Instance.GetString("FirmwareInfo_FormatName");

    /// <summary>
    /// <para>「摘要」字段标签的本地化文本。</para>
    /// The localized "summary" field label.
    /// </summary>
    public static string SummaryLabel => LanguageService.Instance.GetString("FirmwareInfo_Summary");

    /// <summary>
    /// <para>「元数据」分区标题的本地化文本。</para>
    /// The localized "metadata" section title.
    /// </summary>
    public static string MetadataLabel => LanguageService.Instance.GetString("FirmwareInfo_Metadata");

    /// <summary>
    /// <para>「条目」分区标题的本地化文本。</para>
    /// The localized "entries" section title.
    /// </summary>
    public static string EntriesLabel => LanguageService.Instance.GetString("FirmwareInfo_Entries");

    /// <summary>
    /// <para>「组件」分区标题的本地化文本。</para>
    /// The localized "components" section title.
    /// </summary>
    public static string ComponentsLabel => LanguageService.Instance.GetString("FirmwareInfo_Components");

    /// <summary>
    /// <para>加载中提示的本地化文本。</para>
    /// The localized loading hint.
    /// </summary>
    public static string LoadingLabel => LanguageService.Instance.GetString("FirmwareInfo_Loading");

    /// <summary>
    /// <para>失败错误区标题的本地化文本。</para>
    /// The localized failure error header.
    /// </summary>
    public static string ErrorLabel => LanguageService.Instance.GetString("FirmwareInfo_Error");

    /// <summary>
    /// <para>无法识别格式时的本地化错误文本。</para>
    /// The localized error text for an unrecognized format.
    /// </summary>
    public static string UnrecognizedLabel => LanguageService.Instance.GetString("FirmwareInfo_Unrecognized");

    /// <summary>
    /// <para>「校验」动作按钮标题的本地化文本。</para>
    /// The localized title of the "verify" action button.
    /// </summary>
    public static string VerifyLabel => LanguageService.Instance.GetString("FirmwareInfo_Verify");

    /// <summary>
    /// <para>「解包到工程目录」动作按钮标题的本地化文本。</para>
    /// The localized title of the "unpack to project directory" action button.
    /// </summary>
    public static string UnpackLabel => LanguageService.Instance.GetString("FirmwareInfo_UnpackToProject");

    /// <summary>
    /// <para>「操作结果」分区标题的本地化文本。</para>
    /// The localized "operation result" section title.
    /// </summary>
    public static string ResultLabel => LanguageService.Instance.GetString("FirmwareInfo_Result");

    /// <summary>
    /// <para>校验进行中的本地化提示文本。</para>
    /// The localized verifying-in-progress hint.
    /// </summary>
    public static string VerifyingLabel => LanguageService.Instance.GetString("FirmwareInfo_Verifying");

    /// <summary>
    /// <para>解包进行中的本地化提示文本。</para>
    /// The localized unpacking-in-progress hint.
    /// </summary>
    public static string UnpackingLabel => LanguageService.Instance.GetString("FirmwareInfo_Unpacking");

    /// <summary>
    /// <para>尚未打开工程时的本地化错误文本。</para>
    /// The localized error text for when no project is open.
    /// </summary>
    public static string NoProjectLabel => LanguageService.Instance.GetString("FirmwareInfo_NoProject");

    /// <summary>
    /// <para>当前格式不支持解包时的本地化错误文本。</para>
    /// The localized error text for an unsupported unpack format.
    /// </summary>
    public static string UnpackUnsupportedLabel => LanguageService.Instance.GetString("FirmwareInfo_UnpackUnsupported");

    /// <summary>
    /// <para>文档对应的文件路径。</para>
    /// The file path of the document.
    /// </summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>
    /// <para>是否正在后台读取元数据（驱动加载中提示）。</para>
    /// Whether metadata is being read in the background (drives the loading hint).
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// <para>检测到的固件格式名称；未识别时为空字符串。</para>
    /// The detected firmware format name; empty when unrecognized.
    /// </summary>
    [ObservableProperty] private string _formatName = string.Empty;

    /// <summary>
    /// <para>读取摘要文本。</para>
    /// The read summary text.
    /// </summary>
    [ObservableProperty] private string _summary = string.Empty;

    /// <summary>
    /// <para>错误信息文本；无错误时为空字符串。</para>
    /// The error message text; empty when there is no error.
    /// </summary>
    [ObservableProperty] private string _errorMessage = string.Empty;

    /// <summary>
    /// <para>是否存在错误（驱动错误区显示）。</para>
    /// Whether an error is present (drives the error-section visibility).
    /// </summary>
    [ObservableProperty] private bool _hasError;

    /// <summary>
    /// <para>是否已识别出格式处理器（驱动格式名显示）。</para>
    /// Whether a format handler was detected (drives the format-name visibility).
    /// </summary>
    [ObservableProperty] private bool _isRecognized;

    /// <summary>
    /// <para>元数据分区是否非空（驱动分区显示）。</para>
    /// Whether the metadata section is non-empty (drives its visibility).
    /// </summary>
    [ObservableProperty] private bool _hasMetadata;

    /// <summary>
    /// <para>条目分区是否非空（驱动分区显示）。</para>
    /// Whether the entries section is non-empty (drives its visibility).
    /// </summary>
    [ObservableProperty] private bool _hasEntries;

    /// <summary>
    /// <para>组件分区是否非空（驱动分区显示）。</para>
    /// Whether the components section is non-empty (drives its visibility).
    /// </summary>
    [ObservableProperty] private bool _hasComponents;

    /// <summary>
    /// <para>是否正在执行校验动作（驱动校验按钮忙碌状态）。</para>
    /// Whether the verify action is running (drives the verify button busy state).
    /// </summary>
    [ObservableProperty] private bool _isVerifying;

    /// <summary>
    /// <para>是否正在执行解包动作（驱动解包按钮忙碌状态）。</para>
    /// Whether the unpack action is running (drives the unpack button busy state).
    /// </summary>
    [ObservableProperty] private bool _isUnpacking;

    /// <summary>
    /// <para>是否有动作（校验/解包）正在执行（驱动工具栏状态提示显示）。</para>
    /// Whether any action (verify/unpack) is running (drives the toolbar status hint).
    /// </summary>
    [ObservableProperty] private bool _isOperating;

    /// <summary>
    /// <para>当前动作的状态提示文本（如「正在校验…」）。</para>
    /// The status hint text of the current action (e.g. "Verifying…").
    /// </summary>
    [ObservableProperty] private string _operationStatus = string.Empty;

    /// <summary>
    /// <para>最近一次动作（校验/解包）的结果文本；尚无结果时为空字符串。</para>
    /// The result text of the last action (verify/unpack); empty when no result yet.
    /// </summary>
    [ObservableProperty] private string _resultText = string.Empty;

    /// <summary>
    /// <para>动作结果区是否非空（驱动结果区显示）。</para>
    /// Whether the action result section is non-empty (drives its visibility).
    /// </summary>
    [ObservableProperty] private bool _hasResult;

    /// <summary>
    /// <para>元数据键值对列表（UI 绑定）。</para>
    /// The metadata key-value list (bound by the UI).
    /// </summary>
    public ObservableCollection<PropertyRow> Metadata { get; } = [];

    /// <summary>
    /// <para>条目名称列表（UI 绑定）。</para>
    /// The entry-name list (bound by the UI).
    /// </summary>
    public ObservableCollection<string> Entries { get; } = [];

    /// <summary>
    /// <para>组件名称-大小行列表（UI 绑定）。</para>
    /// The component name-size row list (bound by the UI).
    /// </summary>
    public ObservableCollection<PropertyRow> Components { get; } = [];

    /// <summary>
    /// <para>创建固件元数据面板视图模型。</para>
    /// Creates the firmware metadata panel view model.
    /// </summary>
    public FirmwareInfoViewModel()
    {
    }

    /// <summary>
    /// <para>校验命令是否可执行（元数据加载或任一动作执行中禁用）。</para>
    /// Whether the verify command can execute (disabled while metadata loads or any action runs).
    /// </summary>
    private bool CanVerify() => !IsBusy && !IsVerifying && !IsUnpacking;

    /// <summary>
    /// <para>解包命令是否可执行（元数据加载或任一动作执行中禁用）。</para>
    /// Whether the unpack command can execute (disabled while metadata loads or any action runs).
    /// </summary>
    private bool CanUnpack() => !IsBusy && !IsVerifying && !IsUnpacking;

    partial void OnIsBusyChanged(bool value) => NotifyOperationCommands();

    partial void OnIsVerifyingChanged(bool value) => NotifyOperationCommands();

    partial void OnIsUnpackingChanged(bool value) => NotifyOperationCommands();

    /// <summary>
    /// <para>重新评估两个动作命令的 CanExecute（忙碌状态变化时刷新按钮可用性）。</para>
    /// Re-evaluates the CanExecute of both action commands (refreshes button availability on busy-state changes).
    /// </summary>
    private void NotifyOperationCommands()
    {
        VerifyCommand.NotifyCanExecuteChanged();
        UnpackCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// <para>异步读取固件元数据：幂等注册全部格式处理器 → <see cref="FormatDetector.Detect"/> 识别格式 →
    /// <see cref="IFormatHandler.ReadMetadataAsync"/> 读取元数据，随后填充展示属性。</para>
    /// Asynchronously reads firmware metadata: idempotently registers all format handlers, detects the
    /// format via <see cref="FormatDetector.Detect"/>, reads metadata via
    /// <see cref="IFormatHandler.ReadMetadataAsync"/>, then populates the display properties.
    /// </summary>
    /// <param name="path">
    /// <para>固件文件路径。</para>
    /// The firmware file path.
    /// </param>
    public async Task LoadAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        FilePath = path;
        Title = Path.GetFileName(path);

        IsBusy = true;
        HasError = false;
        ErrorMessage = string.Empty;
        IsRecognized = false;
        FormatName = string.Empty;
        Summary = string.Empty;
        Metadata.Clear();
        Entries.Clear();
        Components.Clear();
        HasMetadata = false;
        HasEntries = false;
        HasComponents = false;

        try
        {
            // 幂等注册全部格式处理器（应用启动时通常已注册），确保识别集合完整。
            // Idempotently register every format handler (the app usually does at startup) so detection is complete.
            FirmwareFormatRegistry.RegisterAll();

            // Detect 内部为同步文件读取（流式读头部），放后台线程避免阻塞 UI。
            // Detect performs synchronous file reads (streamed headers); run it on a background thread.
            var handler = await Task.Run(() => FormatDetector.Detect(path)).ConfigureAwait(true);
            if (handler is null)
            {
                ErrorMessage = UnrecognizedLabel;
                HasError = true;
                return;
            }

            FormatName = handler.FormatName;
            IsRecognized = true;

            // 部分 handler 的解析是同步实现（Apk/Arsc/Dt/Nb0/Ntpi 等直接读文件），
            // 必须在后台线程执行，否则整文件读取会阻塞 UI。
            // Some handlers parse synchronously (Apk/Arsc/Dt/Nb0/Ntpi etc. read files directly);
            // run them on a background thread so the file reads never block the UI.
            var result = await Task.Run(() => handler.ReadMetadataAsync(path)).ConfigureAwait(true);
            if (!result.Success)
            {
                ErrorMessage = result.Summary;
                HasError = true;
                return;
            }

            Summary = result.Summary;

            if (result.Metadata is not null)
            {
                foreach (var kv in result.Metadata)
                {
                    Metadata.Add(new PropertyRow(kv.Key, kv.Value));
                }
            }
            HasMetadata = Metadata.Count > 0;

            // 结构化组件列表优先（名称 + 大小），未提供时回退到纯名称条目列表。
            // Prefer the structured component list (name + size); fall back to the plain name list.
            if (result.Components is { Count: > 0 })
            {
                foreach (var component in result.Components)
                {
                    Components.Add(new PropertyRow(component.Name, FormatSize(component.SizeBytes)));
                }
                HasComponents = Components.Count > 0;
            }
            else if (result.Entries is not null)
            {
                foreach (var entry in result.Entries)
                {
                    Entries.Add(entry);
                }
                HasEntries = Entries.Count > 0;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
            LogService.Instance.Write(LogLevel.Error, "FirmwareInfo",
                $"Failed to read firmware metadata for '{path}': {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>释放文档持有的数据（文档关闭时调用），让 GC 回收不再引用的大对象。</para>
    /// Releases the data held by this document (called when the document is closed) so the GC can
    /// reclaim the no-longer-referenced large objects.
    /// </summary>
    public void Cleanup()
    {
        Metadata.Clear();
        Entries.Clear();
        Components.Clear();
        HasMetadata = false;
        HasEntries = false;
        HasComponents = false;
        FormatName = string.Empty;
        Summary = string.Empty;
        ErrorMessage = string.Empty;
        HasError = false;
        IsRecognized = false;
        IsBusy = false;
        IsVerifying = false;
        IsUnpacking = false;
        IsOperating = false;
        OperationStatus = string.Empty;
        ResultText = string.Empty;
        HasResult = false;
        FilePath = null;
    }

    /// <summary>
    /// <para>校验命令：对当前固件文件执行完整性校验。AVB/vbmeta 镜像调用
    /// <see cref="FirmwareService.VerifyAvbAsync"/> 校验签名与内嵌摘要；其余格式流式计算
    /// SHA-256 摘要。结果经 <see cref="LogService"/> 记录并显示在结果区。</para>
    /// Verify command: performs an integrity check on the current firmware file. AVB/vbmeta images
    /// go through <see cref="FirmwareService.VerifyAvbAsync"/> (signature and embedded digest); all
    /// other formats get a streaming SHA-256 digest. The result is logged via <see cref="LogService"/>
    /// and shown in the result section.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task VerifyAsync()
    {
        var path = FilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        IsVerifying = true;
        IsOperating = true;
        OperationStatus = VerifyingLabel;
        HasError = false;
        ErrorMessage = string.Empty;
        try
        {
            // 按格式名判断是否为 AVB/vbmeta 镜像：是则做 AVB 签名/摘要校验，否则流式计算 SHA-256。
            // Dispatch by format name: AVB/vbmeta images get AVB signature/digest verification,
            // everything else gets a streaming SHA-256 digest.
            FirmwareOpResult result;
            if (IsAvbFormat())
            {
                // VerifyAvbAsync 内部含 CPU 密集的 RSA 验签，放到线程池执行避免阻塞 UI。
                // VerifyAvbAsync performs CPU-bound RSA verification; run it on the thread pool.
                result = await Task.Run(() => FirmwareService.Instance.VerifyAvbAsync(path))
                    .ConfigureAwait(true);
            }
            else
            {
                // 流式 SHA-256：FirmwareService.VerifyChecksumAsync 内部经
                // System.Security.Cryptography.SHA256.HashDataAsync 流式计算，避免整文件载入内存
                // （与 UotanStudio.Firmware 中 Payload 的流式哈希用法一致）。
                // Streaming SHA-256: FirmwareService.VerifyChecksumAsync streams the file via
                // System.Security.Cryptography.SHA256.HashDataAsync instead of loading it fully
                // (matching the streaming-hash usage found in UotanStudio.Firmware's Payload code).
                result = await FirmwareService.Instance.VerifyChecksumAsync(path).ConfigureAwait(true);
            }

            ResultText = result.Summary;
            HasResult = true;
            if (!result.Success)
            {
                ErrorMessage = result.Summary;
                HasError = true;
            }

            LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Error, "FirmwareInfo",
                result.Success
                    ? $"Verification passed for '{path}': {result.Summary}"
                    : $"Verification failed for '{path}': {result.Summary}");
        }
        catch (Exception ex)
        {
            ResultText = ex.Message;
            HasResult = true;
            ErrorMessage = ex.Message;
            HasError = true;
            LogService.Instance.Write(LogLevel.Error, "FirmwareInfo", $"Failed to verify '{path}': {ex.Message}");
        }
        finally
        {
            IsVerifying = false;
            IsOperating = false;
            OperationStatus = string.Empty;
        }
    }

    /// <summary>
    /// <para>解包命令：按当前格式名分派 <see cref="FirmwareService"/> 的解包方法（CPIO/Payload/Boot/Oppo/Super），
    /// 将当前固件文件解包到工程 <c>intermediate/&lt;文件名&gt;/</c> 子目录（路径经
    /// <see cref="ProjectContextService.Instance"/> 获取）；失败时给出清晰错误信息。</para>
    /// Unpack command: dispatches a <see cref="FirmwareService"/> unpack method (CPIO/Payload/Boot/Oppo/Super)
    /// by the current format name, unpacking the firmware file into the project
    /// <c>intermediate/&lt;file-name&gt;/</c> sub-directory (path obtained via
    /// <see cref="ProjectContextService.Instance"/>); failures produce a clear error message.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUnpack))]
    private async Task UnpackAsync()
    {
        var path = FilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            ResultText = NoProjectLabel;
            HasResult = true;
            ErrorMessage = NoProjectLabel;
            HasError = true;
            LogService.Instance.Write(LogLevel.Warn, "FirmwareInfo",
                $"Unpack aborted: no project is open  {NoProjectLabel}");
            return;
        }

        IsUnpacking = true;
        IsOperating = true;
        OperationStatus = UnpackingLabel;
        HasError = false;
        ErrorMessage = string.Empty;
        try
        {
            // 目标目录 = 子项目根目录/<文件名>/ 子目录（与 images/ 同级）。
            // Target directory = sub-project root/<file-name>/ sub-directory (same level as images/).
            var subProjectRoot = ProjectContextService.Instance.ActiveSubProject?.RootPath;
            if (subProjectRoot is null)
            {
                OperationStatus = "No active sub-project  无活动子项目";
                HasError = true;
                ErrorMessage = OperationStatus;
                return;
            }

            var outputDir = Path.Combine(subProjectRoot, Path.GetFileNameWithoutExtension(path));
            if (Directory.Exists(outputDir))
            {
                OperationStatus = $"Directory \"{Path.GetFileNameWithoutExtension(path)}\" already exists  目录 \"{Path.GetFileNameWithoutExtension(path)}\" 已存在";
                HasError = true;
                ErrorMessage = OperationStatus;
                return;
            }

            Directory.CreateDirectory(outputDir);

            var result = await DispatchUnpackAsync(path, outputDir).ConfigureAwait(true);

            ResultText = result.Summary;
            HasResult = true;
            if (!result.Success)
            {
                ErrorMessage = result.Summary;
                HasError = true;
            }

            LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Error, "FirmwareInfo",
                result.Success
                    ? $"Unpacked '{path}' -> {outputDir}: {result.Summary}"
                    : $"Unpack failed for '{path}': {result.Summary}");
        }
        catch (Exception ex)
        {
            ResultText = ex.Message;
            HasResult = true;
            ErrorMessage = ex.Message;
            HasError = true;
            LogService.Instance.Write(LogLevel.Error, "FirmwareInfo", $"Failed to unpack '{path}': {ex.Message}");
        }
        finally
        {
            IsUnpacking = false;
            IsOperating = false;
            OperationStatus = string.Empty;
        }
    }

    /// <summary>
    /// <para>按当前格式名将解包操作分派到 <see cref="FirmwareService"/> 的解包方法；
    /// 不支持解包的格式返回带清晰提示的失败结果。CPU 密集解包放到线程池执行。</para>
    /// Dispatches the unpack operation to a <see cref="FirmwareService"/> unpack method by the current
    /// format name; formats without unpack support return a failure with a clear hint. CPU-bound
    /// unpacking is offloaded to the thread pool.
    /// </summary>
    /// <param name="path">固件文件路径。<para>The firmware file path.</para></param>
    /// <param name="outputDirectory">解包输出目录。<para>The unpack output directory.</para></param>
    /// <returns>解包操作结果。<para>The unpack operation result.</para></returns>
    private Task<FirmwareOpResult> DispatchUnpackAsync(string path, string outputDirectory)
    {
        var service = FirmwareService.Instance;
        return FormatName switch
        {
            "Android Boot / Vendor Boot Image" => Task.Run(() => service.UnpackBootAsync(path, outputDirectory)),
            "CPIO/TAR Archive" => Task.Run(() => service.UnpackCpioAsync(path, outputDirectory)),
            "Android OTA Payload (payload.bin)" => Task.Run(() => service.ExtractPayloadAsync(path, outputDirectory)),
            "Oppo Firmware (OFP/OPS/OZIP)" => Task.Run(() => service.ExtractOppoAsync(path, outputDirectory)),
            "Android Logical Partition (super)" => Task.Run(() => service.UnpackSuperAsync(path, outputDirectory)),
            // Android sparse 镜像（如 product.img）：级联解出内部文件系统内容（非文件系统内容时服务内回退 .raw）。
            // Android sparse images (e.g. product.img): cascade into the internal filesystem contents
            // (falls back to .raw inside the service when the content is not a filesystem).
            "Android Sparse Image (simg)" => Task.Run(() => service.UnpackSparseImageAsync(path, outputDirectory)),
            // 文件系统镜像（ext4/erofs/f2fs）：通过对应流式解析器级联解包到目录。
            // Filesystem images (ext4/erofs/f2fs): cascade-unpack into the directory via the matching streaming parser.
            "EXT4" or "EROFS" or "F2FS" => Task.Run(() => service.UnpackFileSystemImageAsync(path, outputDirectory)),
            _ => Task.FromResult(FirmwareOpResult.Fail(UnpackUnsupportedLabel))
        };
    }

    /// <summary>
    /// <para>根据现有元数据（格式名、文件名/扩展名）判断当前文件是否为 AVB/vbmeta 镜像。</para>
    /// Determines whether the current file is an AVB/vbmeta image from the existing metadata
    /// (format name and file name/extension).
    /// </summary>
    private bool IsAvbFormat()
    {
        if (FormatName.Contains("AVB", StringComparison.OrdinalIgnoreCase) ||
            FormatName.Contains("vbmeta", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (FilePath is null)
        {
            return false;
        }

        return Path.GetExtension(FilePath).Equals(".vbmeta", StringComparison.OrdinalIgnoreCase) ||
               Path.GetFileName(FilePath).StartsWith("vbmeta", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <para>将字节数格式化为可读大小文本（如 "1.5 MB"）；0 字节显示 "0 B"。</para>
    /// Formats a byte count into a human-readable size string (e.g. "1.5 MB"); 0 bytes shows "0 B".
    /// </summary>
    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var abs = Math.Abs(bytes);
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        var value = (double)abs;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[0]}"
            : $"{value:0.##} {units[unit]}";
    }
}