using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.BootSecurity.LogoPartition;
using UotanStudio.Firmware.Core.Magic;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.UiAbstractions;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// One embedded image row in the logo partition tool.
/// <para>Logo 分区工具中的一条内嵌图片行。</para>
/// </summary>
public sealed class LogoPartitionEntryItem
{
    /// <summary>Creates a display row from a validated container entry.
    /// <para>由经过校验的容器条目创建展示行。</para></summary>
    /// <param name="entry">Parsed entry. <para>已解析的条目。</para></param>
    public LogoPartitionEntryItem(LogoPartitionEntry entry) => Entry = entry;

    /// <summary>Parsed entry backing this row. <para>本行对应的已解析条目。</para></summary>
    public LogoPartitionEntry Entry { get; }

    /// <summary>Display name. <para>显示名称。</para></summary>
    public string Name => Entry.Name;

    /// <summary>Stored dimensions text. <para>存储尺寸的展示文本。</para></summary>
    public string Dimensions => Entry.Width > 0 && Entry.Height > 0
        ? $"{Entry.Width} x {Entry.Height}"
        : "—";

    /// <summary>Pixel encoding text. <para>像素编码文本。</para></summary>
    public string Encoding => Entry.Encoding.ToString();

    /// <summary>Stored payload size text. <para>负载大小的展示文本。</para></summary>
    public string SizeText => $"{Entry.DataSize:N0} bytes";
}

/// <summary>
/// One related-partition row in the logo partition tool.
/// <para>Logo 分区工具中的一条相关分区行。</para>
/// </summary>
public sealed class RelatedPartitionRow
{
    /// <summary>Creates a display row from a discovered sibling partition.
    /// <para>由探测到的同目录分区创建展示行。</para></summary>
    /// <param name="item">Discovery result. <para>探测结果。</para></param>
    public RelatedPartitionRow(RelatedPartitionItem item) => Item = item;

    /// <summary>Discovery result backing this row. <para>本行对应的探测结果。</para></summary>
    public RelatedPartitionItem Item { get; }

    /// <summary>Partition name. <para>分区名。</para></summary>
    public string Name => Item.Name;

    /// <summary>Localized size text, or the missing marker. <para>本地化大小文本；缺失时显示缺失标记。</para></summary>
    public string SizeText => Item.Exists
        ? $"{Item.Length:N0} bytes"
        : LanguageService.Instance.GetString("Dock_Logo_RelatedMissing");

    /// <summary>Localized capability marker. <para>本地化能力标记。</para></summary>
    public string Capability => !Item.Exists
        ? LanguageService.Instance.GetString("Dock_Logo_RelatedMissing")
        : LanguageService.Instance.GetString(Item.IsLogoContainer
            ? "Dock_Logo_CapabilityExtract"
            : "Dock_Logo_CapabilityOpen");

    /// <summary>Whether the row can be opened. <para>该行是否可打开。</para></summary>
    public bool CanOpen => Item.Exists;
}

/// <summary>
/// Read-only logo partition workbench: opens a logo/splash container, lists its embedded images with a
/// preview, extracts entries to a chosen directory and offers one-click opening of the related boot
/// partitions found beside it.
/// <para>只读 Logo 分区工作台：打开 Logo/splash 容器、列出内嵌图片并预览、把条目提取到指定目录，
/// 并支持一键打开同目录下发现的相关启动分区。</para>
/// </summary>
public partial class LogoPartitionViewModel : Document
{
    private readonly List<string> _temporaryFiles = [];
    private LogoPartitionImage? _image;

    /// <summary>The localized loading status. <para>本地化的加载状态文案。</para></summary>
    public static string LoadingLabel => LanguageService.Instance.GetString("Dock_Logo_Loading");

    /// <summary>The localized empty-list hint. <para>本地化的空列表提示。</para></summary>
    public static string EmptyLabel => LanguageService.Instance.GetString("Dock_Logo_Empty");

    /// <summary>The localized failure hint. <para>本地化的失败提示。</para></summary>
    public static string FailedLabel => LanguageService.Instance.GetString("Dock_Logo_ExtractFailed");

    /// <summary>The localized folder-picker title. <para>本地化的目录选择器标题。</para></summary>
    public static string PickFolderLabel => LanguageService.Instance.GetString("Dock_Logo_PickFolder");

    /// <summary>Absolute path of the opened partition. <para>已打开分区的绝对路径。</para></summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>Human-readable container format name. <para>可读的容器格式名。</para></summary>
    [ObservableProperty] private string _formatName = string.Empty;

    /// <summary>Description produced by the compiled magic engine. <para>编译型魔数引擎给出的类型描述。</para></summary>
    [ObservableProperty] private string _magicDescription = string.Empty;

    /// <summary>Status text shown in place of the preview. <para>替代预览显示的状态文案。</para></summary>
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>Whether a load or extraction is running. <para>是否正在加载或提取。</para></summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Decoded preview of the selected entry. <para>当前选中条目的解码预览。</para></summary>
    [ObservableProperty] private Bitmap? _preview;

    /// <summary>Selected embedded image row. <para>当前选中的内嵌图片行。</para></summary>
    [ObservableProperty] private LogoPartitionEntryItem? _selectedEntry;

    /// <summary>Selected related-partition row. <para>当前选中的相关分区行。</para></summary>
    [ObservableProperty] private RelatedPartitionRow? _selectedRelated;

    /// <summary>Embedded image rows in container order. <para>按容器顺序排列的内嵌图片行。</para></summary>
    public ObservableCollection<LogoPartitionEntryItem> Entries { get; } = [];

    /// <summary>Related boot/display partitions found beside the opened file.
    /// <para>在打开文件旁发现的相关启动/显示分区。</para></summary>
    public ObservableCollection<RelatedPartitionRow> RelatedPartitions { get; } = [];

    /// <summary>Whether a decoded preview is available. <para>是否已有解码预览。</para></summary>
    public bool HasPreview => Preview is not null;

    /// <summary>Parsed container metadata, or null before the first successful load.
    /// <para>已解析的容器元数据；首次成功加载前为 null。</para></summary>
    public LogoPartitionImage? Image => _image;

    /// <summary>
    /// Loads the container at <paramref name="path"/>, reads its entry table and magic description, and
    /// populates the related-partition list. Failures are reported as status text instead of throwing.
    /// <para>载入 <paramref name="path"/> 指向的容器，读取条目表与魔数描述并填充相关分区列表；
    /// 失败以状态文案上报而不抛异常。</para>
    /// </summary>
    /// <param name="path">Absolute or relative path of the partition. <para>分区的绝对或相对路径。</para></param>
    /// <param name="cancellationToken">Cancels the load. <para>取消加载。</para></param>
    public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        FilePath = Path.GetFullPath(path);
        IsBusy = true;
        StatusText = LoadingLabel;
        ReplacePreview(null);
        Entries.Clear();
        RelatedPartitions.Clear();

        try
        {
            var image = await Task.Run(() => LogoPartitionFormat.Read(FilePath, cancellationToken))
                .ConfigureAwait(true);
            _image = image;
            FormatName = image.FormatName;
            MagicDescription = IdentifyMagic(FilePath);

            foreach (var entry in image.Entries)
            {
                Entries.Add(new LogoPartitionEntryItem(entry));
            }

            RefreshRelated();
            StatusText = Entries.Count == 0 ? EmptyLabel : string.Empty;
            if (Entries.Count > 0)
            {
                SelectedEntry = Entries[0];
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = LoadingLabel;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            StatusText = exception.Message;
            LogService.Instance.Write(LogLevel.Debug, "LogoPartition",
                $"Failed to open logo partition '{FilePath}': {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Extracts the selected entry or all entries to a user-chosen directory.
    /// <para>把选中条目或全部条目提取到用户选择的目录。</para>
    /// </summary>
    /// <summary>Extracts only the selected embedded image. <para>只提取选中的内嵌图片。</para></summary>
    [RelayCommand]
    private Task ExtractSelectedAsync() => ExtractCoreAsync(all: false);

    /// <summary>Extracts every embedded image. <para>提取全部内嵌图片。</para></summary>
    [RelayCommand]
    private Task ExtractAllAsync() => ExtractCoreAsync(all: true);

    private async Task ExtractCoreAsync(bool all)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(FilePath) || _image is null)
        {
            return;
        }

        var selected = all ? null : SelectedEntry;
        if (!all && selected is null)
        {
            return;
        }

        var topLevel = TopLevelExtensions.GetTopLevel();
        if (topLevel is not { StorageProvider.CanPickFolder: true })
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = PickFolderLabel,
            AllowMultiple = false
        }).ConfigureAwait(true);
        if (folders.Count == 0)
        {
            return;
        }

        var target = folders[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var names = await Task.Run(() => LogoPartitionFormat.Extract(FilePath, target,
                    selected is null ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selected.Entry.FileName },
                    overwrite: true))
                .ConfigureAwait(true);
            StatusText = string.Format(
                LanguageService.Instance.GetString("Dock_Logo_ExtractDone"), names.Count);
            LogService.Instance.Write(LogLevel.Info, "LogoPartition",
                $"Extracted {names.Count} logo image(s) from '{FilePath}' to '{target}'.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            StatusText = FailedLabel;
            LogService.Instance.Write(LogLevel.Debug, "LogoPartition",
                $"Logo extraction failed: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Rescans the sibling directory for related partitions.
    /// <para>重新扫描同目录的相关分区。</para></summary>
    [RelayCommand]
    private void RefreshRelated()
    {
        RelatedPartitions.Clear();
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        foreach (var item in LogoPartitionCatalog.Scan(FilePath))
        {
            RelatedPartitions.Add(new RelatedPartitionRow(item));
        }
    }

    /// <summary>Opens a related partition with the existing document dispatch chain.
    /// <para>用现有文档派发链路打开相关分区。</para></summary>
    /// <param name="row">Row to open. <para>要打开的行。</para></param>
    [RelayCommand]
    private void OpenRelated(RelatedPartitionRow? row)
    {
        if (row is not { CanOpen: true })
        {
            return;
        }

        var mode = row.Item.IsLogoContainer ? EditorOpenMode.LogoPartition : EditorOpenMode.Auto;
        WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(row.Item.Path, mode));
    }

    /// <summary>Opens the selected embedded image in the image viewer through a temporary file.
    /// <para>经临时文件在图片查看器中打开选中的内嵌图片。</para></summary>
    [RelayCommand]
    private void OpenSelectedImage()
    {
        if (SelectedEntry is null || string.IsNullOrWhiteSpace(FilePath) || _image is null)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "UotanStudio-Logo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, SelectedEntry.Entry.FileName);
            using (var input = File.OpenRead(FilePath))
            {
                var bytes = LogoPartitionFormat.ReadEntryImage(input, _image, SelectedEntry.Entry);
                File.WriteAllBytes(target, bytes);
            }

            _temporaryFiles.Add(target);
            WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(target, EditorOpenMode.Image));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            LogService.Instance.Write(LogLevel.Debug, "LogoPartition",
                $"Failed to open logo entry: {exception.Message}");
        }
    }

    /// <summary>Releases the decoded bitmap and any temporary preview files.
    /// <para>释放解码位图与所有临时预览文件。</para></summary>
    public void Cleanup()
    {
        ReplacePreview(null);
        foreach (var file in _temporaryFiles)
        {
            try
            {
                File.Delete(file);
                var directory = Path.GetDirectoryName(file);
                if (directory is not null && Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        _temporaryFiles.Clear();
    }

    partial void OnPreviewChanged(Bitmap? value) => OnPropertyChanged(nameof(HasPreview));

    partial void OnSelectedEntryChanged(LogoPartitionEntryItem? value)
    {
        if (value is not null)
        {
            _ = RefreshPreviewAsync(value, CancellationToken.None);
        }
    }

    private async Task RefreshPreviewAsync(LogoPartitionEntryItem item, CancellationToken cancellationToken)
    {
        if (_image is null || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        StatusText = LoadingLabel;
        try
        {
            var bytes = await Task.Run(() =>
            {
                using var input = File.OpenRead(FilePath);
                return LogoPartitionFormat.ReadEntryImage(input, _image, item.Entry, cancellationToken);
            }).ConfigureAwait(true);
            ReplacePreview(bytes);
            StatusText = string.Empty;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            ReplacePreview(null);
            StatusText = exception.Message;
        }
    }

    private void ReplacePreview(byte[]? bytes)
    {
        var previous = Preview;
        Bitmap? next = null;
        if (bytes is { Length: > 0 })
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                next = new Bitmap(stream);
            }
            catch (Exception)
            {
                next = null;
            }
        }

        Preview = next;
        previous?.Dispose();
    }

    private static string IdentifyMagic(string path)
    {
        try
        {
            return FileMagic.IdentifyFile(path)?.Description
                ?? LanguageService.Instance.GetString("Dock_Logo_MagicUnknown");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return LanguageService.Instance.GetString("Dock_Logo_MagicUnknown");
        }
    }
}