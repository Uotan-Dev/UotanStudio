using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>Hex 编辑器视图模型：承载二进制文件的十六进制查看/编辑（基于自绘制
/// <c>HexViewer</c> 控件），维护原始字节数组（编辑就地写回）、大小统计与脏状态，
/// 保存时把字节数组写回原文件。</para>
/// Hex editor view model: hosts binary-file hex viewing/editing (on the custom-rendered
/// <c>HexViewer</c> control), holding the raw byte array (edits mutate it in place), size
/// statistics and dirty state; saving writes the byte array back to the file.
/// </summary>
public partial class HexEditorViewModel : Document, ICommandSaveableDocument
{
    /// <summary>
    /// <para>保存按钮的本地化标题。</para>
    /// The localized title of the save button.
    /// </summary>
    public static string SaveLabel => LanguageService.Instance.GetString("Dock_Hex_Save");

    /// <summary>
    /// <para>加载中提示文案的本地化标题。</para>
    /// The localized title of the busy/loading label.
    /// </summary>
    public static string BusyLabel => LanguageService.Instance.GetString("Dock_Hex_Busy");

    /// <summary>
    /// <para>是否正在后台加载（联动忙碌指示动画）。</para>
    /// Whether the background load is running (drives the busy indicator animation).
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// <para>文档对应的文件路径。</para>
    /// The file path of the document.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePathDisplay))]
    private string? _filePath;

    /// <summary>
    /// <para>本地化后的文件路径显示文本（顶部路径栏）。</para>
    /// Localized file path display text (top path bar).
    /// </summary>
    public string FilePathDisplay =>
        string.IsNullOrEmpty(FilePath)
            ? string.Empty
            : string.Format(LanguageService.Instance.GetString("Dock_File_PathFormat"), FilePath);

    /// <summary>
    /// <para>加载/保存错误文本；为空表示无错误。</para>
    /// The load/save error text; empty means no error.
    /// </summary>
    [ObservableProperty] private string _compileError = string.Empty;

    /// <summary>
    /// <para>文档已修改状态（HexViewer 编辑事件置位，保存后清除）。</para>
    /// Document dirty state (set by the HexViewer edit event, cleared on save).
    /// </summary>
    [ObservableProperty] private bool _isDirty;

    partial void OnIsDirtyChanged(bool value) => IsModified = value;

    /// <summary>
    /// <para>文件大小显示文本（状态栏）。</para>
    /// The localized file-size display text (status bar).
    /// </summary>
    [ObservableProperty] private string _sizeDisplay = string.Empty;

    /// <summary>
    /// <para>原始字节数组（HexViewer 绑定源；编辑就地写回，保存时整体写盘）。</para>
    /// The raw byte array (HexViewer binding source; edits mutate it in place; save writes it out).
    /// </summary>
    [ObservableProperty] private byte[]? _rawBytes;

    /// <summary>
    /// <para>文本列解码/输入使用的编码名称（默认 ASCII，与属性面板下拉框共用集合）。</para>
    /// Encoding name used for text-column decoding/input (default ASCII; shares the options list
    /// with the properties-panel dropdown).
    /// </summary>
    [ObservableProperty] private string _encodingName = "ASCII";

    /// <summary>
    /// <para>应用用户选择的文本列编码（仅影响显示与输入，不改变保存的字节内容）。</para>
    /// Applies a user-chosen text-column encoding (display/input only; never changes saved bytes).
    /// </summary>
    public void ApplyEncoding(string encodingName)
    {
        if (string.IsNullOrEmpty(encodingName)) return;
        EncodingName = encodingName;
    }

    /// <summary>
    /// <para>保存命令：把字节数组写回原文件。</para>
    /// Save command: writes the byte array back to the file.
    /// </summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// <para>创建 Hex 编辑器视图模型并初始化保存命令。</para>
    /// Creates the Hex editor view model and initializes the save command.
    /// </summary>
    public HexEditorViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    /// <summary>
    /// <para>异步加载二进制文件：读取字节数组并记录大小统计。</para>
    /// Asynchronously loads a binary file: reads the byte array and records size statistics.
    /// </summary>
    /// <param name="path">二进制文件路径。<para>Binary file path.</para></param>
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
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                CompileError = $"File not found: {path}";
                return;
            }

            RawBytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            SizeDisplay = string.Format(
                LanguageService.Instance.GetString("Dock_File_SizeFormat"), info.Length);
            CompileError = string.Empty;
            IsDirty = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>保存：把当前字节数组（含 HexViewer 就地编辑）写回原文件。</para>
    /// Saves: writes the current byte array (including HexViewer in-place edits) back to the file.
    /// </summary>
    public async Task SaveAsync()
    {
        if (RawBytes is null || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        if (IsBusy) return;
        var path = FilePath;
        var temporary = path + ".save-" + Guid.NewGuid().ToString("N");
        IsBusy = true;
        try
        {
            await File.WriteAllBytesAsync(temporary, RawBytes).ConfigureAwait(true);
            File.Move(temporary, path, overwrite: true);
            IsDirty = false;
            CompileError = string.Empty;
            LogService.Instance.Write(LogLevel.Info, "HexEditor", $"Saved {FilePath}");
        }
        catch (Exception ex)
        {
            CompileError = $"Failed to save: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// <para>释放文档持有的状态（文档关闭时调用）。</para>
    /// Releases the state held by this document (called when the document is closed).
    /// </summary>
    public void Cleanup()
    {
        RawBytes = null;
        EncodingName = "ASCII";
        SizeDisplay = string.Empty;
        CompileError = string.Empty;
        IsDirty = false;
        FilePath = null;
    }
}