using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Controls;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// Firmware metadata panel view model: a read-only Dock document showing a firmware file's format name,
/// summary, metadata key-value pairs and entry list, populated from <see cref="FormatDetector"/> plus
/// <see cref="IFormatHandler.ReadMetadataAsync"/>.
/// <para>固件元数据面板视图模型：以只读 Dock 文档展示固件文件的格式名、摘要、
/// 元数据键值对与条目列表，数据来自 <see cref="FormatDetector"/> + <see cref="IFormatHandler.ReadMetadataAsync"/>。</para>
/// </summary>
public partial class FirmwareInfoViewModel : Document, ITool
{
    /// <summary>
    /// The localized panel title.
    /// <para>面板标题的本地化文本。</para>
    /// </summary>
    public static string TitleLabel => LanguageService.Instance.GetString("FirmwareInfo_Title");

    /// <summary>
    /// The localized "format name" field label.
    /// <para>「格式名」字段标签的本地化文本。</para>
    /// </summary>
    public static string FormatNameLabel => LanguageService.Instance.GetString("FirmwareInfo_FormatName");

    /// <summary>
    /// The localized "summary" field label.
    /// <para>「摘要」字段标签的本地化文本。</para>
    /// </summary>
    public static string SummaryLabel => LanguageService.Instance.GetString("FirmwareInfo_Summary");

    /// <summary>
    /// The localized "metadata" section title.
    /// <para>「元数据」分区标题的本地化文本。</para>
    /// </summary>
    public static string MetadataLabel => LanguageService.Instance.GetString("FirmwareInfo_Metadata");

    /// <summary>
    /// The localized "entries" section title.
    /// <para>「条目」分区标题的本地化文本。</para>
    /// </summary>
    public static string EntriesLabel => LanguageService.Instance.GetString("FirmwareInfo_Entries");

    /// <summary>
    /// The localized "components" section title.
    /// <para>「组件」分区标题的本地化文本。</para>
    /// </summary>
    public static string ComponentsLabel => LanguageService.Instance.GetString("FirmwareInfo_Components");

    /// <summary>
    /// The localized loading hint.
    /// <para>加载中提示的本地化文本。</para>
    /// </summary>
    public static string LoadingLabel => LanguageService.Instance.GetString("FirmwareInfo_Loading");

    /// <summary>
    /// The localized failure error header.
    /// <para>失败错误区标题的本地化文本。</para>
    /// </summary>
    public static string ErrorLabel => LanguageService.Instance.GetString("FirmwareInfo_Error");

    /// <summary>
    /// The localized error text for an unrecognized format.
    /// <para>无法识别格式时的本地化错误文本。</para>
    /// </summary>
    public static string UnrecognizedLabel => LanguageService.Instance.GetString("FirmwareInfo_Unrecognized");

    /// <summary>
    /// The file path of the document.
    /// <para>文档对应的文件路径。</para>
    /// </summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>
    /// Whether metadata is being read in the background (drives the loading hint).
    /// <para>是否正在后台读取元数据（驱动加载中提示）。</para>
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// The detected firmware format name; empty when unrecognized.
    /// <para>检测到的固件格式名称；未识别时为空字符串。</para>
    /// </summary>
    [ObservableProperty] private string _formatName = string.Empty;

    /// <summary>
    /// The read summary text.
    /// <para>读取摘要文本。</para>
    /// </summary>
    [ObservableProperty] private string _summary = string.Empty;

    /// <summary>
    /// Whether the summary section has text (drives its visibility); AVB images never show one.
    /// <para>摘要分区是否有文本（驱动分区显示）；AVB 镜像不显示摘要。</para>
    /// </summary>
    [ObservableProperty] private bool _hasSummary;

    /// <summary>
    /// The error message text; empty when there is no error.
    /// <para>错误信息文本；无错误时为空字符串。</para>
    /// </summary>
    [ObservableProperty] private string _errorMessage = string.Empty;

    /// <summary>
    /// Whether an error is present (drives the error-section visibility).
    /// <para>是否存在错误（驱动错误区显示）。</para>
    /// </summary>
    [ObservableProperty] private bool _hasError;

    /// <summary>
    /// Whether a format handler was detected (drives the format-name visibility).
    /// <para>是否已识别出格式处理器（驱动格式名显示）。</para>
    /// </summary>
    [ObservableProperty] private bool _isRecognized;

    /// <summary>
    /// Whether the metadata section is non-empty (drives its visibility).
    /// <para>元数据分区是否非空（驱动分区显示）。</para>
    /// </summary>
    [ObservableProperty] private bool _hasMetadata;

    /// <summary>
    /// Whether the entries section is non-empty (drives its visibility).
    /// <para>条目分区是否非空（驱动分区显示）。</para>
    /// </summary>
    [ObservableProperty] private bool _hasEntries;

    /// <summary>
    /// Whether the components section is non-empty (drives its visibility).
    /// <para>组件分区是否非空（驱动分区显示）。</para>
    /// </summary>
    [ObservableProperty] private bool _hasComponents;

    /// <summary>
    /// The metadata key-value list (bound by the UI).
    /// <para>元数据键值对列表（UI 绑定）。</para>
    /// </summary>
    public ObservableCollection<PropertyRow> Metadata { get; } = [];

    /// <summary>
    /// The entry-name list (bound by the UI).
    /// <para>条目名称列表（UI 绑定）。</para>
    /// </summary>
    public ObservableCollection<string> Entries { get; } = [];

    /// <summary>
    /// The component name-size row list (bound by the UI).
    /// <para>组件名称-大小行列表（UI 绑定）。</para>
    /// </summary>
    public ObservableCollection<PropertyRow> Components { get; } = [];

    /// <summary>
    /// Creates the firmware metadata panel view model.
    /// <para>创建固件元数据面板视图模型。</para>
    /// </summary>
    public FirmwareInfoViewModel()
    {
        LanguageService.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Asynchronously reads firmware metadata: idempotently registers all format handlers, detects the
    /// format via <see cref="FormatDetector.Detect"/>, reads metadata via
    /// <see cref="IFormatHandler.ReadMetadataAsync"/>, then populates the display properties.
    /// <para>异步读取固件元数据：幂等注册全部格式处理器 → <see cref="FormatDetector.Detect"/> 识别格式 →
    /// <see cref="IFormatHandler.ReadMetadataAsync"/> 读取元数据，随后填充展示属性。</para>
    /// </summary>
    /// <param name="path">
    /// The firmware file path.
    /// <para>固件文件路径。</para>
    /// </param>
    public async Task LoadAsync(string path, FormatDetectionResult? detection = null, CancellationToken cancellationToken = default)
    {
        if (_closed || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _loadCancellation?.Cancel();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loadCancellation = source;
        _rawMetadata = null;
        MetadataGroups.Clear();
        FilePath = path;
        Title = Path.GetFileName(path);

        IsBusy = true;
        HasError = false;
        ErrorMessage = string.Empty;
        IsRecognized = false;
        FormatName = string.Empty;
        Summary = string.Empty;
        HasSummary = false;
        Metadata.Clear();
        Entries.Clear();
        Components.Clear();
        HasMetadata = false;
        HasEntries = false;
        HasComponents = false;

        try
        {
            FirmwareFormatRegistry.RegisterAll();

            var detectionResult = detection ?? await Task.Run(() => FormatDetector.DetectDetailed(path), source.Token).ConfigureAwait(true);
            source.Token.ThrowIfCancellationRequested();
            var handler = detectionResult?.Handler;
            if (handler is null)
            {
                ErrorMessage = UnrecognizedLabel;
                HasError = true;
                return;
            }

            FormatName = handler.FormatName;
            IsRecognized = true;

            var result = await Task.Run(() => handler.ReadMetadataAsync(path, source.Token), source.Token).ConfigureAwait(true);
            source.Token.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                ErrorMessage = UserError.WithDetails(result.Summary, "UserError_LoadFailed");
                HasError = true;
                return;
            }

            _rawSummary = result.Summary;
            var fields = new Dictionary<string, string>
            {
                ["FileName"] = Path.GetFileName(path),
                ["FileSize"] = new FileInfo(path).Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            if (result.Metadata is not null)
            {
                foreach (var (key, value) in result.Metadata)
                {
                    fields[key] = value;
                }
            }
            _rawMetadata = fields;
            RefreshMetadata();

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
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_closed || !ReferenceEquals(_loadCancellation, source))
            {
                return;
            }
            ErrorMessage = UserError.Describe(ex);
            HasError = true;
            LogService.Instance.Write(LogLevel.Error, "FirmwareInfo",
                $"Failed to read firmware metadata for '{path}': {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, source))
            {
                _loadCancellation = null;
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// Releases the data held by this document (called when the document is closed) so the GC can
    /// reclaim the no-longer-referenced large objects.
    /// <para>释放文档持有的数据（文档关闭时调用），让 GC 回收不再引用的大对象。</para>
    /// </summary>
    public void Cleanup()
    {
        _closed = true;
        _loadCancellation?.Cancel();
        LanguageService.Instance.LanguageChanged -= OnLanguageChanged;
        _rawMetadata = null;
        _rawSummary = string.Empty;
        MetadataGroups.Clear();
        Metadata.Clear();
        Entries.Clear();
        Components.Clear();
        HasMetadata = false;
        HasEntries = false;
        HasComponents = false;
        FormatName = string.Empty;
        Summary = string.Empty;
        HasSummary = false;
        ErrorMessage = string.Empty;
        HasError = false;
        IsRecognized = false;
        IsBusy = false;
        FilePath = null;
    }

    /// <summary>
    /// Formats a byte count into a human-readable size string (e.g. "1.5 MB"); 0 bytes shows "0 B".
    /// <para>将字节数格式化为可读大小文本（如 "1.5 MB"）；0 字节显示 "0 B"。</para>
    /// </summary>
    private static string FormatSize(long bytes) => SizeFormatter.Format(bytes);
}