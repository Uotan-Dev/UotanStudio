using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Views.Controls;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>文档视图模型，承载文档编辑器的文本内容、语法语言、文件路径与脏状态，
/// 并支持二进制文件的 16 进制查看与固件结构分析。</para>
/// Document view model, holding the document editor's text content, syntax language, file path and
/// dirty state, with support for hex viewing and firmware structure analysis of binary files.
/// </summary>
public partial class FileViewModel : Document, IEditable
{
    private bool _isLoading;
    private System.Text.Encoding _textEncoding = new System.Text.UTF8Encoding(false);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private DateTime _loadedWriteTime;

    /// <summary>
    /// <para>魔数嗅探缓冲区（libmagic 风格兜底，避免每文件分配）。</para>
    /// Reusable magic-sniff buffer (libmagic-style fallback, avoids per-file allocation).
    /// </summary>
    private readonly byte[] _magicProbeBuffer = new byte[4096];

    /// <summary>
    /// <para>文件原始字节数据（用于 16 进制查看）。</para>
    /// The raw file bytes used for hex viewing.
    /// </summary>
    [ObservableProperty]
    private byte[]? _rawBytes;

    /// <summary>
    /// <para>编辑器文本内容。</para>
    /// The text content of the editor.
    /// </summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>
    /// <para>TextMate 语法语言标识；空值表示不安装语法高亮。</para>
    /// The TextMate syntax language id; empty means no syntax highlighting is installed.
    /// </summary>
    [ObservableProperty]
    private string _language = string.Empty;

    /// <summary>
    /// <para>文档对应的文件路径；内存文档或尚未保存的文档为 <c>null</c>。</para>
    /// The file path of the document; <c>null</c> for in-memory or unsaved documents.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePathDisplay))]
    private string? _filePath;

    /// <summary>
    /// <para>本地化后的文件路径显示文本（工具栏标题）。</para>
    /// Localized file path display text (toolbar title).
    /// </summary>
    public string FilePathDisplay =>
        string.IsNullOrEmpty(FilePath)
            ? string.Empty
            : string.Format(LanguageService.Instance.GetString("Dock_File_PathFormat"), FilePath);

    /// <summary>
    /// <para>本地化后的文件大小显示文本（状态栏）。</para>
    /// Localized file size display text (status bar).
    /// </summary>
    public string SizeDisplay =>
        RawBytes is null
            ? string.Empty
            : string.Format(LanguageService.Instance.GetString("Dock_File_SizeFormat"), RawBytes.Length);

    /// <summary>
    /// <para>文档是否已被修改（与磁盘内容不一致）。</para>
    /// Whether the document has been modified (diverged from the disk content).
    /// </summary>
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>
    /// <para>是否为二进制文件（无法以文本方式显示）。</para>
    /// Whether the file is binary (cannot be displayed as text).
    /// </summary>
    [ObservableProperty]
    private bool _isBinary;

    /// <summary>
    /// <para>文档的打开方式；由资源管理器等请求传入，控制视图模式选择。</para>
    /// The open mode of the document; supplied by open-mode requests, controlling the chosen view.
    /// </summary>
    [ObservableProperty]
    private EditorOpenMode _openMode = EditorOpenMode.Auto;

    /// <summary>
    /// <para>固件格式分析结果（键值对列表），UI 绑定此项。</para>
    /// Firmware format analysis results (key-value pairs), bound by the UI.
    /// </summary>
    public ObservableCollection<PropertyRow> Analysis { get; } = [];

    /// <summary>
    /// <para>检测到的固件格式名称；未识别时为 <c>null</c>。</para>
    /// The detected firmware format name; <c>null</c> when unrecognized.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatDisplay))]
    private string? _detectedFormat;

    /// <summary>
    /// <para>本地化后的固件格式显示文本（状态栏与分析面板）。</para>
    /// Localized firmware format display text (status bar and analysis panel).
    /// </summary>
    public string FormatDisplay =>
        string.IsNullOrEmpty(DetectedFormat)
            ? string.Empty
            : string.Format(LanguageService.Instance.GetString("Dock_File_FormatFormat"), DetectedFormat);

    /// <summary>
    /// <para>异步读取指定文件内容到 <see cref="Text"/>，并初始化 <see cref="FilePath"/>；
    /// 加载期间与加载完成后均不标记为已修改。按扩展名与内容综合判定是否启用 16 进制查看。</para>
    /// Asynchronously loads the file content at the given path into <see cref="Text"/> and initializes
    /// <see cref="FilePath"/>; the document is not marked dirty during or after loading.
    /// Whether hex viewing is enabled is decided by extension and content.
    /// </summary>
    /// <param name="path">
    /// <para>要加载的文件路径。</para>
    /// The file path to load.
    /// </param>
    /// <param name="mode">
    /// <para>请求的打开方式；默认 <c>Auto</c>，按类型+后缀+内容综合判定。</para>
    /// The requested open mode; default <c>Auto</c>, decided by type+extension+content.
    /// </param>
    public async Task LoadContentAsync(string path, EditorOpenMode mode = EditorOpenMode.Auto)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        OpenMode = mode;
        FilePath = path;
        _isLoading = true;
        try
        {
            RawBytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            IsBinary = ResolveBinary(mode, path, RawBytes);

            if (IsBinary)
            {
                Text = string.Empty;
                await AnalyzeFirmwareAsync(path).ConfigureAwait(true);
            }
            else
            {
                using var reader = new StreamReader(path, new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                Text = await reader.ReadToEndAsync().ConfigureAwait(true);
                _textEncoding = reader.CurrentEncoding;
                DetectedEncoding = DetectEncoding(RawBytes);
                ClearAnalysis();
            }

            _loadedWriteTime = File.GetLastWriteTimeUtc(path);
            IsDirty = false;
        }
        finally
        {
            _isLoading = false;
            IsDirty = false;
        }
    }

    /// <summary>Saves modified text atomically, retaining edits made during saving.<para>原子保存文本，保留保存期间发生的新修改。</para></summary>
    public async Task SaveAsync()
    {
        await _saveGate.WaitAsync();
        string? temporary = null;
        try
        {
            if (_isLoading || !IsDirty || IsBinary || string.IsNullOrWhiteSpace(FilePath)) return;
            var path = FilePath;
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) != _loadedWriteTime)
                throw new IOException($"The file changed outside the editor: {path}");
            var text = Text;
            temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(temporary, text, _textEncoding);
            if (File.GetLastWriteTimeUtc(path) != _loadedWriteTime)
                throw new IOException($"The file changed outside the editor: {path}");
            File.Move(temporary, path, overwrite: true);
            _loadedWriteTime = File.GetLastWriteTimeUtc(path);
            if (FilePath == path && Text == text) IsDirty = false;
        }
        finally
        {
            try { if (temporary is not null) File.Delete(temporary); }
            finally { _saveGate.Release(); }
        }
    }

    /// <summary>
    /// <para>检测到的文本编码名称；非文本文档时为 <c>null</c>。</para>
    /// Detected text encoding name; <c>null</c> for non-text documents.
    /// </summary>
    [ObservableProperty] private string? _detectedEncoding;

    /// <summary>
    /// <para>文本总行数（按 <c>\n</c> 计）；空文本为 0。</para>
    /// Total line count of the text (counted by <c>\n</c>); 0 for empty text.
    /// </summary>
    public int LineCount
    {
        get
        {
            if (string.IsNullOrEmpty(Text))
            {
                return 0;
            }

            var count = 0;
            foreach (var c in Text)
            {
                if (c == '\n')
                {
                    count++;
                }
            }

            // 末尾无换行符时，最后一行仍需计数。
            // Count the final line when it does not end with a newline.
            if (Text.Length > 0 && Text[^1] != '\n')
            {
                count++;
            }

            return count;
        }
    }

    /// <summary>
    /// <para>文本字符数（<see cref="Text"/> 的长度）。</para>
    /// Character count of the text (length of <see cref="Text"/>).
    /// </summary>
    public int CharCount => Text?.Length ?? 0;

    /// <summary>
    /// <para>通过 BOM 与字节模式推测文本编码名称；无法确定时回落为 <c>UTF-8</c>。</para>
    /// Sniffs the text encoding name from BOM and byte patterns, falling back to <c>UTF-8</c> when
    /// undeterminable.
    /// </summary>
    /// <param name="data">
    /// <para>文件的原始字节。</para>
    /// The raw file bytes.
    /// </param>
    /// <returns>编码名称（如 <c>UTF-8</c>、<c>UTF-16 LE</c>、<c>ASCII</c>）。</returns>
    private static string DetectEncoding(byte[] data)
    {
        if (data is null || data.Length == 0)
        {
            return "UTF-8";
        }

        // BOM 探测：UTF-8 (EF BB BF)、UTF-16 LE (FF FE)、UTF-16 BE (FE FF)、UTF-32 LE (FF FE 00 00)。
        // BOM detection for UTF-8/UTF-16 LE/BE/UTF-32 LE.
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return "UTF-8 (BOM)";
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return data.Length >= 4 && data[2] == 0x00 && data[3] == 0x00 ? "UTF-32 LE" : "UTF-16 LE";
        }

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return "UTF-16 BE";
        }

        // 无 BOM 时按字节分布推断：所有字节均 < 0x80 视为 ASCII；出现成对的 0x00 视为 UTF-16。
        // Without BOM, infer by byte distribution: all < 0x80 → ASCII; paired 0x00 → UTF-16.
        var checkLength = Math.Min(data.Length, 4096);
        var hasHighByte = false;
        var looksUtf16Le = false;
        var looksUtf16Be = false;
        var zeroCount = 0;
        for (var i = 0; i < checkLength; i++)
        {
            var b = data[i];
            if (b == 0)
            {
                zeroCount++;
            }

            if (b >= 0x80)
            {
                hasHighByte = true;
            }
        }

        if (checkLength >= 2 && zeroCount > checkLength / 4)
        {
            // 大量空字节，倾向于 UTF-16；按奇偶位置判定字节序。
            // Many null bytes → likely UTF-16; endianness decided by null-byte position.
            looksUtf16Le = data[1] == 0;
            looksUtf16Be = data[0] == 0;
        }

        if (looksUtf16Le)
        {
            return "UTF-16 LE";
        }

        if (looksUtf16Be)
        {
            return "UTF-16 BE";
        }

        if (!hasHighByte)
        {
            return "ASCII";
        }

        return "UTF-8";
    }

    /// <summary>
    /// <para>按打开方式、扩展名与内容综合判定是否为二进制（应走十六进制编辑器）。
    /// <c>Text</c> 强制为文本；<c>Hex</c> 强制为二进制；<c>Auto</c> 依据扩展名白名单与内容探测。
    /// 即使请求为 <c>Text</c>，内容明显为二进制（含 NUL 字节，如 AXML / UTF-16 无 BOM）时仍按二进制
    /// 处理，避免在代码编辑器中显示大量 NUL 字符图标。</para>
    /// Decides whether the file is binary (opens in the hex editor) from open mode, extension and content.
    /// <c>Text</c> forces text; <c>Hex</c> forces binary; <c>Auto</c> uses the extension whitelist and content
    /// sniffing. Even for <c>Text</c>, content that is clearly binary (NUL bytes present, e.g. AXML or
    /// UTF-16 without BOM) is still treated as binary, so the code editor does not show NUL glyphs.
    /// </summary>
    private static bool ResolveBinary(EditorOpenMode mode, string path, byte[] data)
    {
        switch (mode)
        {
            case EditorOpenMode.Text:
                return FileOpenMode.IsBinaryContent(data);
            case EditorOpenMode.Hex:
                return true;
            case EditorOpenMode.Dtb:
                return false;
            case EditorOpenMode.Dex:
                return false;
            default:
                var ext = Path.GetExtension(path);
                if (FileOpenMode.IsKnownBinaryExtension(ext))
                {
                    return true;
                }

                return FileOpenMode.IsBinaryContent(data);
        }
    }

    /// <summary>
    /// <para>对文件进行固件格式自动识别与结构分析，结果写入 <see cref="Analysis"/>。</para>
    /// Performs automatic firmware format detection and structure analysis, writing results to <see cref="Analysis"/>.
    /// </summary>
    private async Task AnalyzeFirmwareAsync(string path)
    {
        ClearAnalysis();
        DetectedFormat = null;

        try
        {
            var handler = FormatDetector.Detect(path);
            if (handler is null)
            {
                // FormatDetector 未识别时，尝试纯托管魔数嗅探（libmagic 风格）兜底
                // Fall back to pure-managed magic sniffing (libmagic style) when FormatDetector misses
                string? magic = null;
                try
                {
                    const int probeSize = 4096;
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, probeSize, FileOptions.SequentialScan);
                    int read = fs.Read(_magicProbeBuffer, 0, probeSize);
                    magic = ManagedMagic.Detect(_magicProbeBuffer.AsSpan(0, read));
                }
                catch
                {
                    // 嗅探失败不影响主流程
                    // Sniff failures must not break the main flow
                }

                string format = magic ?? "Unknown / 未知格式";
                AddAnalysis("Format", format);
                AddAnalysis("Size", $"{new FileInfo(path).Length:N0} bytes");
                NotifyAnalysisComplete(path);
                return;
            }

            DetectedFormat = handler.FormatName;
            AddAnalysis("Format", handler.FormatName);
            AddAnalysis("Size", $"{new FileInfo(path).Length:N0} bytes");

            var result = await handler.ReadMetadataAsync(path).ConfigureAwait(true);
            if (result.Success)
            {
                if (result.Metadata != null)
                {
                    foreach (var kv in result.Metadata)
                    {
                        AddAnalysis(kv.Key, kv.Value);
                    }
                }

                // 把组件/分区条目列表上屏，使属性面板在仅有 Metadata 之外也能看到封装内容。
                // Surface the entry/partition list so the properties panel shows the encapsulated
                // components even when the handler exposes no metadata.
                if (result.Entries is { Count: > 0 })
                {
                    AddAnalysis("Entries", result.Entries.Count.ToString("N0"));
                    foreach (var entry in result.Entries)
                    {
                        AddAnalysis("Entry", entry);
                    }
                }

                if (!string.IsNullOrEmpty(result.Summary))
                {
                    AddAnalysis("Summary", result.Summary);
                }
            }
            else
            {
                AddAnalysis("Error", result.Summary);
            }

            NotifyAnalysisComplete(path);
        }
        catch (Exception ex)
        {
            AddAnalysis("Error", ex.Message);
            NotifyAnalysisComplete(path);
        }
    }

    private void NotifyAnalysisComplete(string path)
    {
        // 通知属性面板等订阅方展示分析结果
        // Notify subscribers such as the properties panel to display analysis results
        WeakReferenceMessenger.Default.Send(new FileAnalysisCompletedMessage(Analysis.ToList(), DetectedFormat, path));
    }

    private void AddAnalysis(string name, string value)
    {
        Analysis.Add(new PropertyRow(name, value));
    }

    private void ClearAnalysis()
    {
        Analysis.Clear();
    }

    /// <summary>
    /// <para>标记文档为已修改；由 16 进制查看器的字节编辑事件调用。</para>
    /// Marks the document as modified; invoked by the hex viewer's byte-edit event.
    /// </summary>
    public void MarkModifiedByHexEdit() => IsDirty = true;

    /// <summary>
    /// <para>由 FileView 在加载时设置的 TextEditor 引用，用于支持剪贴板操作。</para>
    /// TextEditor reference set by FileView on load, used to support clipboard operations.
    /// </summary>
    internal AvaloniaEdit.TextEditor? Editor { get; set; }

    /// <inheritdoc/>
    public void Cut() => Editor?.Cut();

    /// <inheritdoc/>
    public void Copy() => Editor?.Copy();

    /// <inheritdoc/>
    public void Paste() => Editor?.Paste();

    /// <inheritdoc/>
    public void Delete()
    {
        if (Editor is { } ed && ed.SelectionLength > 0)
            ed.SelectedText = "";
    }

    /// <inheritdoc/>
    public void SelectAll() => Editor?.SelectAll();

    partial void OnTextChanged(string value)
    {
        // 非加载路径的文本变更（编辑器回写、用户编辑等）视为内容已修改。
        // Text changes outside loading (editor write-backs, user edits) mark the content dirty.
        if (!_isLoading)
        {
            IsDirty = true;
        }
    }
}

/// <summary>
/// <para>属性行视图模型，承载名称-值对。</para>
/// Property row view model, carrying a name-value pair.
/// </summary>
public sealed partial class PropertyRow : ObservableObject
{
    /// <summary>
    /// <para>属性名称。</para>
    /// Property name.
    /// </summary>
    [ObservableProperty] private string _name = string.Empty;

    /// <summary>
    /// <para>属性值文本。</para>
    /// Property value text.
    /// </summary>
    [ObservableProperty] private string _value = string.Empty;

    /// <summary>
    /// <para>创建属性行。</para>
    /// Creates a property row.
    /// </summary>
    public PropertyRow(string name, string value)
    {
        _name = name;
        _value = value;
    }
}
