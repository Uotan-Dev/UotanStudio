using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Services;

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
        FilePath = null;
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