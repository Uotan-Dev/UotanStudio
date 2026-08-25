using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware;
using UotanStudio.Firmware.Common;
using UotanStudio.Messages;
using UotanStudio.Services;

namespace UotanStudio.ViewModels;

/// <summary>
/// <para>文档视图模型，承载文档编辑器的文本内容、语法语言、文件路径与脏状态，
/// 并支持二进制文件的 16 进制查看与固件结构分析。</para>
/// Document view model, holding the document editor's text content, syntax language, file path and
/// dirty state, with support for hex viewing and firmware structure analysis of binary files.
/// </summary>
public partial class FileViewModel : Document
{
    private bool _isLoading;
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
        _rawBytes is null
            ? string.Empty
            : string.Format(LanguageService.Instance.GetString("Dock_File_SizeFormat"), _rawBytes.Length);

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
    /// <para>是否显示 16 进制视图（二进制文件时可用）。</para>
    /// Whether to show the hex view (available for binary files).
    /// </summary>
    [ObservableProperty]
    private bool _showHexView = true;

    /// <summary>
    /// <para>是否显示结构分析面板（二进制文件时可用）。</para>
    /// Whether to show the structure analysis panel (available for binary files).
    /// </summary>
    [ObservableProperty]
    private bool _showAnalysis = true;

    /// <summary>
    /// <para>文件原始字节数据（用于 16 进制查看）。</para>
    /// The raw file bytes used for hex viewing.
    /// </summary>
    public byte[]? RawBytes => _rawBytes;

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
    /// 加载期间与加载完成后均不标记为已修改。对二进制文件自动启用 16 进制查看与结构分析。</para>
    /// Asynchronously loads the file content at the given path into <see cref="Text"/> and initializes
    /// <see cref="FilePath"/>; the document is not marked dirty during or after loading.
    /// For binary files, hex viewing and structure analysis are enabled automatically.
    /// </summary>
    /// <param name="path">
    /// <para>要加载的文件路径。</para>
    /// The file path to load.
    /// </param>
    public async Task LoadContentAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        FilePath = path;
        _isLoading = true;
        try
        {
            _rawBytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            IsBinary = IsBinaryFile(_rawBytes);

            if (IsBinary)
            {
                Text = string.Empty;
                await AnalyzeFirmwareAsync(path).ConfigureAwait(true);
            }
            else
            {
                Text = await File.ReadAllTextAsync(path).ConfigureAwait(true);
                ClearAnalysis();
            }

            IsDirty = false;
        }
        finally
        {
            _isLoading = false;
            IsDirty = false;
        }
    }

    /// <summary>
    /// <para>检测字节数组是否为二进制文件（包含空字节或大量非打印字符）。</para>
    /// Detects whether a byte array is a binary file (contains null bytes or many non-printable chars).
    /// </summary>
    private static bool IsBinaryFile(byte[] data)
    {
        if (data.Length == 0)
        {
            return false;
        }

        // 检查前 8000 字节：若包含空字节或超过 30% 非打印字符则视为二进制。
        // Check first 8000 bytes: binary if null bytes present or >30% non-printable.
        var checkLength = Math.Min(data.Length, 8000);
        var nonPrintable = 0;
        for (var i = 0; i < checkLength; i++)
        {
            var b = data[i];
            if (b == 0)
            {
                return true;
            }

            if (b < 0x20 && b != '\r' && b != '\n' && b != '\t')
            {
                nonPrintable++;
            }
        }

        return (double)nonPrintable / checkLength > 0.3;
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
                AddAnalysis("Format", "Unknown / 未知格式");
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

    partial void OnTextChanged(string value)
    {
        // 非加载路径的文本变更（编辑器回写、用户编辑等）视为内容已修改。
        // Text changes outside loading (editor write-backs, user edits) mark the content dirty.
        if (!_isLoading)
        {
            IsDirty = true;
        }
    }

    partial void OnShowHexViewChanged(bool value)
    {
        // 16 进制视图与分析面板互斥：打开一方时关闭另一方。
        // Hex view and the analysis panel are mutually exclusive: opening one closes the other.
        if (value)
        {
            ShowAnalysis = false;
        }
    }

    partial void OnShowAnalysisChanged(bool value)
    {
        // 16 进制视图与分析面板互斥：打开一方时关闭另一方。
        // Hex view and the analysis panel are mutually exclusive: opening one closes the other.
        if (value)
        {
            ShowHexView = false;
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