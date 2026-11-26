using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.VendorFirmware.MediaTek.Preloader;
using UotanStudio.Services;
using UotanStudio.UiAbstractions;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// Preloader document: reports the declared boot storage, the code offset and the <c>AND_ROMINFO_v</c> lock
/// state, and exposes the fastboot unlock rearrangement as an explicit opt-in that always writes a new file.
/// <para>预加载器文档：报告声明的引导存储、代码偏移与 <c>AND_ROMINFO_v</c> 锁状态，并把 fastboot 解锁重排
/// 作为显式选择的操作，且始终写出新文件。</para>
/// </summary>
public partial class MtkPreloaderViewModel : Document
{
    private CancellationTokenSource? _loadCancellation;
    private MtkPreloaderImage? _image;
    private bool _closed;

    /// <summary>Creates a document that refreshes its labels when the application language changes.
    /// <para>创建应用语言变更时刷新标签的文档。</para></summary>
    public MtkPreloaderViewModel()
    {
        LanguageService.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Gets or sets the source region path.<para>获取或设置源区域路径。</para></summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>Gets or sets whether the region is loading.<para>获取或设置区域是否正在加载。</para></summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Gets or sets whether a supported preloader boot region was parsed.<para>获取或设置是否解析出受支持的预加载器引导区。</para></summary>
    [ObservableProperty] private bool _isRecognized;

    /// <summary>Gets or sets the localized failure message.<para>获取或设置本地化失败信息。</para></summary>
    [ObservableProperty] private string _errorMessage = string.Empty;

    /// <summary>Gets or sets the localized storage description.<para>获取或设置本地化存储描述。</para></summary>
    [ObservableProperty] private string _storageText = string.Empty;

    /// <summary>Gets or sets the localized code offset.<para>获取或设置本地化代码偏移。</para></summary>
    [ObservableProperty] private string _codeOffsetText = string.Empty;

    /// <summary>Gets or sets the localized lock state.<para>获取或设置本地化锁状态。</para></summary>
    [ObservableProperty] private string _lockStateText = string.Empty;

    /// <summary>Gets or sets the localized flag-block description.<para>获取或设置本地化标志块描述。</para></summary>
    [ObservableProperty] private string _flagBlockText = string.Empty;

    /// <summary>Gets or sets whether the parsed region satisfies every patch prerequisite.<para>获取或设置已解析区域是否满足全部修补前提。</para></summary>
    [ObservableProperty] private bool _canPatch;

    /// <summary>Gets or sets the field rows shown by the document.<para>获取或设置文档显示的字段行。</para></summary>
    [ObservableProperty] private IReadOnlyList<PreloaderFieldRow> _fields = [];

    /// <summary>Gets or sets whether the unlock rearrangement is requested.<para>获取或设置是否请求解锁重排。</para></summary>
    [ObservableProperty] private bool _unlockFastboot = true;

    /// <summary>Gets or sets whether a backup of the source region is kept.<para>获取或设置是否保留源区域备份。</para></summary>
    [ObservableProperty] private bool _createBackup = true;

    /// <summary>Gets or sets the output path for a patch run.<para>获取或设置修补运行的输出路径。</para></summary>
    [ObservableProperty] private string _outputPath = string.Empty;

    /// <summary>Gets or sets the localized result text shown after a patch run.<para>获取或设置修补运行后显示的本地化结果文本。</para></summary>
    [ObservableProperty] private string _resultText = string.Empty;

    /// <summary>Gets or sets whether a patch run is in progress.<para>获取或设置修补运行是否进行中。</para></summary>
    [ObservableProperty] private bool _isPatching;

    /// <summary>Loads the region description without reading the whole region.
    /// <para>载入区域描述，不读取整个区域。</para></summary>
    /// <param name="path">Region path.<para>区域路径。</para></param>
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
        CanPatch = false;
        ErrorMessage = string.Empty;
        ResultText = string.Empty;
        StorageText = string.Empty;
        CodeOffsetText = string.Empty;
        LockStateText = string.Empty;
        FlagBlockText = string.Empty;
        Fields = [];
        _image = null;
        if (string.IsNullOrWhiteSpace(OutputPath) && !string.IsNullOrWhiteSpace(path))
        {
            OutputPath = BuildDefaultOutput(path);
        }

        try
        {
            var image = await MtkPreloaderReader.ReadAsync(path, source.Token).ConfigureAwait(true);
            if (!source.IsCancellationRequested)
            {
                _image = image;
                IsRecognized = true;
                CanPatch = image.CanPatch;
                Present(image);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = UserError.Describe(ex);
            LogService.Instance.Write(LogLevel.Warn, "Preloader", ex.Message);
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
    /// <para>运行补丁服务并报告结果。</para>
    /// </summary>
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
            var options = new MtkPreloaderPatchOptions
            {
                UnlockFastboot = UnlockFastboot,
                CreateBackup = CreateBackup,
            };
            var result = await MtkPreloaderPatchService.PatchAsync(_image, OutputPath, options).ConfigureAwait(true);
            ResultText = string.Format(CultureInfo.CurrentCulture,
                LanguageService.Instance.GetString("Preloader_PatchDone"), result.OutputPath, result.Notes.Count,
                result.BackupPath ?? LanguageService.Instance.GetString("Lk_NoBackup"));
        }
        catch (Exception ex)
        {
            ResultText = UserError.Describe(ex);
            LogService.Instance.Write(LogLevel.Error, "Preloader", ex.Message);
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

    private void Present(MtkPreloaderImage image)
    {
        var language = LanguageService.Instance;
        StorageText = image.Storage switch
        {
            MtkPreloaderStorage.EmmcBoot => "EMMC_BOOT",
            MtkPreloaderStorage.UfsBoot => "UFS_BOOT",
            MtkPreloaderStorage.ComboBoot => "COMBO_BOOT",
            MtkPreloaderStorage.Raw => language.GetString("Preloader_RawUnsupported"),
            _ => language.GetString("Preloader_UnknownStorage"),
        };
        CodeOffsetText = "0x" + image.CodeOffset.ToString("X", CultureInfo.InvariantCulture);
        var block = image.FlagBlock;
        FlagBlockText = block is null
            ? language.GetString("Preloader_NoFlagBlock")
            : "0x" + block.Offset.ToString("X", CultureInfo.InvariantCulture);
        LockStateText = block is null
            ? "-"
            : block.IsLocked ? language.GetString("Preloader_Locked")
            : block.IsHardLocked ? language.GetString("Preloader_HardLocked")
            : language.GetString("Preloader_Unlocked");
        Fields =
        [
            new PreloaderFieldRow(language.GetString("Dock_Preloader_Storage"), StorageText),
            new PreloaderFieldRow(language.GetString("Dock_Preloader_CodeOffset"), CodeOffsetText),
            new PreloaderFieldRow(language.GetString("Dock_Preloader_FlagBlock"), FlagBlockText),
            new PreloaderFieldRow(language.GetString("Dock_Preloader_LockState"), LockStateText),
        ];
    }

    private static string BuildDefaultOutput(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return Path.Combine(directory, stem + ".unlocked" + extension);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_image is not null)
        {
            Present(_image);
        }
    }
}

/// <summary>
/// One labelled row of the preloader summary table.
/// <para>预加载器摘要表的一个带标签行。</para>
/// </summary>
/// <param name="Name">Localized label.<para>本地化标签。</para></param>
/// <param name="Value">Formatted value shown in monospace.<para>以等宽字体显示的值。</para></param>
public sealed record PreloaderFieldRow(string Name, string Value);