using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.VendorFirmware.MediaTek.Lk;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// Read-only LK image document that lists the entry chain and exposes the documented binary patches as an
/// explicit, opt-in action that always writes a new file.
/// <para>只读 LK 镜像文档：列出条目链，并把有记录的二进制补丁作为显式选择的操作，且始终写出新文件。</para>
/// </summary>
public partial class LkImageViewModel : Document
{
    private CancellationTokenSource? _loadCancellation;
    private LkImage? _image;
    private bool _closed;

    /// <summary>Creates a document that refreshes its labels when the application language changes.
    /// <para>创建应用语言变更时刷新标签的文档。</para></summary>
    public LkImageViewModel()
    {
        LanguageService.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Gets or sets the source image path.<para>获取或设置源镜像路径。</para></summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>Gets or sets whether the image is loading.<para>获取或设置镜像是否正在加载。</para></summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Gets or sets whether a valid LK chain was parsed.<para>获取或设置是否解析出有效的 LK 链。</para></summary>
    [ObservableProperty] private bool _isRecognized;

    /// <summary>Gets or sets the localized failure message.<para>获取或设置本地化失败信息。</para></summary>
    [ObservableProperty] private string _errorMessage = string.Empty;

    /// <summary>Gets or sets the localized summary line.<para>获取或设置本地化摘要行。</para></summary>
    [ObservableProperty] private string _summary = string.Empty;

    /// <summary>Gets or sets the entry rows.<para>获取或设置条目行。</para></summary>
    [ObservableProperty] private IReadOnlyList<LkEntryRow> _entries = [];

    /// <summary>Gets or sets the selected entry row.<para>获取或设置选中的条目行。</para></summary>
    [ObservableProperty] private LkEntryRow? _selectedEntry;

    /// <summary>Gets or sets the patch categories offered by the service.<para>获取或设置服务提供的补丁类别。</para></summary>
    [ObservableProperty] private IReadOnlyList<string> _categories = [.. LkPatchService.Categories];

    /// <summary>Gets or sets the selected categories to apply.<para>获取或设置要应用的已选类别。</para></summary>
    [ObservableProperty] private IReadOnlyList<string> _selectedCategories = [.. LkPatchService.Categories];

    /// <summary>Gets or sets whether the opt-in certificate digest override is requested.<para>获取或设置是否请求显式的证书摘要覆盖。</para></summary>
    [ObservableProperty] private bool _applyCertBypass;

    /// <summary>Gets or sets whether a backup of the source image is kept.<para>获取或设置是否保留源镜像备份。</para></summary>
    [ObservableProperty] private bool _createBackup = true;

    /// <summary>Gets or sets the output path for a patch run.<para>获取或设置修补运行的输出路径。</para></summary>
    [ObservableProperty] private string _outputPath = string.Empty;

    /// <summary>Gets or sets the localized result text shown after a patch run.<para>获取或设置修补运行后显示的本地化结果文本。</para></summary>
    [ObservableProperty] private string _resultText = string.Empty;

    /// <summary>Gets or sets whether a patch run is in progress.<para>获取或设置修补运行是否进行中。</para></summary>
    [ObservableProperty] private bool _isPatching;

    /// <summary>Loads the LK chain without reading payloads into memory.
    /// <para>载入 LK 链，不把负载读入内存。</para></summary>
    /// <param name="path">Image path.<para>镜像路径。</para></param>
    /// <param name="cancellationToken">Cancels the pending load.<para>取消待完成的载入。</para></param>
    /// <returns>Completion of the load.<para>载入完成的完成任务。</para></returns>
    public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_closed)
        {
            return;
        }

        _loadCancellation?.Cancel();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loadCancellation = source;
        FilePath = path;
        IsBusy = true;
        IsRecognized = false;
        ErrorMessage = string.Empty;
        Summary = string.Empty;
        Entries = [];
        SelectedEntry = null;
        ResultText = string.Empty;
        _image = null;
        if (string.IsNullOrWhiteSpace(OutputPath) && !string.IsNullOrWhiteSpace(path))
        {
            OutputPath = BuildDefaultOutput(path);
        }

        try
        {
            var image = await LkImageReader.ReadAsync(path, source.Token).ConfigureAwait(true);
            if (!source.IsCancellationRequested)
            {
                _image = image;
                Entries = [.. image.Entries.Select(static entry => new LkEntryRow(entry))];
                IsRecognized = true;
                Summary = string.Format(CultureInfo.CurrentCulture,
                    LanguageService.Instance.GetString("Lk_SourceSummary"), image.Entries.Count, image.FileSize);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = UserError.Describe(ex);
            LogService.Instance.Write(LogLevel.Warn, "Lk", ex.Message);
        }
        finally
        {
            if (!source.IsCancellationRequested)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>Runs the patch service and reports the outcome.
    /// <para>运行补丁服务并报告结果。</para></summary>
    /// <returns>Completion of the run.<para>运行完成的完成任务。</para></returns>
    [RelayCommand]
    private async Task PatchAsync()
    {
        if (_image is null || IsPatching)
        {
            return;
        }

        IsPatching = true;
        try
        {
            var options = new LkPatchOptions
            {
                Categories = SelectedCategories,
                ApplyCertBypass = ApplyCertBypass,
                CreateBackup = CreateBackup,
            };
            var result = await LkPatchService.PatchAsync(_image, OutputPath, options).ConfigureAwait(true);
            var changeCount = result.Changes.Count(static change => change.Offset >= 0);
            ResultText = string.Format(CultureInfo.CurrentCulture,
                LanguageService.Instance.GetString("Lk_PatchDone"), result.OutputPath, changeCount,
                result.BackupPath ?? LanguageService.Instance.GetString("Lk_NoBackup"));
        }
        catch (Exception ex)
        {
            ResultText = UserError.Describe(ex);
            LogService.Instance.Write(LogLevel.Error, "Lk", ex.Message);
        }
        finally
        {
            IsPatching = false;
        }
    }

    /// <summary>Releases the load cancellation source when the document closes.
    /// <para>文档关闭时释放载入取消源。</para>
    /// </summary>
    public void Cleanup()
    {
        _closed = true;
        LanguageService.Instance.LanguageChanged -= OnLanguageChanged;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    private static string BuildDefaultOutput(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return Path.Combine(directory, stem + ".patched" + extension);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_image is not null)
        {
            Summary = string.Format(CultureInfo.CurrentCulture,
                LanguageService.Instance.GetString("Lk_SourceSummary"), _image.Entries.Count, _image.FileSize);
        }
    }
}

/// <summary>
/// One row of the LK entry table.
/// <para>LK 条目表的一行。</para>
/// </summary>
/// <param name="Entry">The parsed entry.<para>已解析条目。</para></param>
public sealed class LkEntryRow(LkImageEntry Entry)
{
    /// <summary>Gets the partition name.<para>获取分区名。</para></summary>
    public string Name => Entry.Name;

    /// <summary>Gets the localized entry kind.<para>获取本地化条目种类。</para></summary>
    public string Kind => Entry.Kind switch
    {
        LkImageEntryKind.Certificate => LanguageService.Instance.GetString("Lk_KindCert"),
        LkImageEntryKind.Modem => LanguageService.Instance.GetString("Lk_KindModem"),
        _ => LanguageService.Instance.GetString("Lk_KindApp"),
    };

    /// <summary>Gets the payload size in bytes.<para>获取负载字节数。</para></summary>
    public string Size => Entry.DataSize.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Gets the load address in hexadecimal.<para>获取十六进制加载地址。</para></summary>
    public string Address => "0x" + Entry.MemoryAddress.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Gets the header offset in hexadecimal.<para>获取十六进制头部偏移。</para></summary>
    public string Offset => "0x" + Entry.HeaderOffset.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>Gets the attached certificate name, or a localized dash.<para>获取附加证书名，或本地化短横线。</para></summary>
    public string Certificate => Entry.CertificateName ?? "-";

    /// <summary>Gets whether the header uses the extended layout.<para>获取头部是否使用扩展布局。</para></summary>
    public bool IsExtended => Entry.IsExtended;
}