using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services.Flash;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// Read-only browsing view for an RKDevTool download-list configuration (<c>.cfg</c>): the header summary,
/// each stored row with its address and image path, and the trailing block kept verbatim so nothing the
/// vendor tool stored is hidden.
/// <para>RKDevTool 下载列表配置（<c>.cfg</c>）的只读浏览视图：头部摘要、每行的地址与镜像路径，以及按原样
/// 展示的尾块，厂商工具存储的内容不被隐藏。</para>
/// </summary>
public partial class RockchipConfigEditorViewModel : Document
{
    /// <summary>The localized column and field titles.<para>本地化的列标题与字段标题。</para></summary>
    public static string NameLabel => Services.LanguageService.Instance.GetString("Dock_RkConfig_Name");

    /// <summary>The localized address column title.<para>本地化的地址列标题。</para></summary>
    public static string AddressLabel => Services.LanguageService.Instance.GetString("Dock_RkConfig_Address");

    /// <summary>The localized path column title.<para>本地化的路径列标题。</para></summary>
    public static string PathLabel => Services.LanguageService.Instance.GetString("Dock_RkConfig_Path");

    /// <summary>The localized file-presence column title.<para>本地化的文件存在列标题。</para></summary>
    public static string FileLabel => Services.LanguageService.Instance.GetString("Dock_RkConfig_File");

    /// <summary>The localized text shown for an existing image file.<para>镜像文件存在时显示的本地化文本。</para></summary>
    public static string PresentLabel => Services.LanguageService.Instance.GetString("Dock_RkConfig_Present");

    /// <summary>The localized text shown for a missing image file.<para>镜像文件缺失时显示的本地化文本。</para></summary>
    public static string MissingLabel => Services.LanguageService.Instance.GetString("Dock_RkConfig_Missing");

    /// <summary>The localized header block title.<para>本地化的头部信息标题。</para></summary>
    public static string HeaderLabel => Services.LanguageService.Instance.GetString("Dock_RkConfig_Header");

    /// <summary>The localized trailing-block title.<para>本地化的尾块标题。</para></summary>
    public static string TailLabel => Services.LanguageService.Instance.GetString("Dock_RkConfig_Tail");

    /// <summary>The localized empty-list hint.<para>本地化的空列表提示。</para></summary>
    public static string EmptyLabel => Services.LanguageService.Instance.GetString("Dock_RkConfig_Empty");

    /// <summary>The path of the configuration file.<para>配置文件路径。</para></summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>Whether a parse is running.<para>是否正在解析。</para></summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>The last parse error text; empty means no error.<para>最后一次解析错误文本；为空表示无错误。</para></summary>
    [ObservableProperty] private string _compileError = string.Empty;

    /// <summary>The top summary text (header length and row count).<para>顶部摘要文本（头部长度与行数）。</para></summary>
    [ObservableProperty] private string _sourceDescription = string.Empty;

    /// <summary>The verbatim header block as hexadecimal text.<para>按原样展示的头部块十六进制文本。</para></summary>
    [ObservableProperty] private string _headerHex = string.Empty;

    /// <summary>The selected row, which drives the trailing-block pane.<para>选中的行，联动尾块面板。</para></summary>
    [ObservableProperty] private RockchipConfigRowViewModel? _selectedItem;

    /// <summary>Whether a row is selected.<para>是否选中了行。</para></summary>
    public bool HasSelection => SelectedItem is not null;

    /// <summary>Whether the configuration carries no rows.<para>配置是否没有任何行。</para></summary>
    public bool HasNoRows => Items.Count == 0;

    /// <summary>The parsed rows in file order.<para>按文件顺序解析出的行。</para></summary>
    public ObservableCollection<RockchipConfigRowViewModel> Items { get; } = [];

    partial void OnSelectedItemChanged(RockchipConfigRowViewModel? value) => OnPropertyChanged(nameof(HasSelection));

    /// <summary>
    /// Parses the configuration and publishes its rows; a format failure surfaces a localized error instead
    /// of an empty document.
    /// <para>解析配置并发布其行；格式失败时给出本地化错误而不是空文档。</para>
    /// </summary>
    /// <param name="path">Configuration file path.<para>配置文件路径。</para></param>
    /// <param name="cancellationToken">Cancellation token for the background parse.<para>后台解析的取消令牌。</para></param>
    /// <returns>Completes when the rows are published.<para>行发布完成后结束。</para></returns>
    public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
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
            (byte[] Header, IReadOnlyList<RockchipConfigEntry> Entries) parsed;
            try
            {
                parsed = await Task.Run(() => RockchipConfigFile.Load(path), cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                CompileError = Services.UserError.Describe(ex, "UserError_RkConfigLoadFailed");
                Items.Clear();
                OnPropertyChanged(nameof(HasNoRows));
                return;
            }

            Items.Clear();
            foreach (var entry in parsed.Entries)
            {
                Items.Add(new RockchipConfigRowViewModel(entry));
            }

            SelectedItem = Items.FirstOrDefault();
            HeaderHex = Convert.ToHexString(parsed.Header);
            SourceDescription = string.Format(CultureInfo.InvariantCulture,
                Services.LanguageService.Instance.GetString("Dock_RkConfig_Summary"),
                parsed.Header.Length, Items.Count);
            CompileError = string.Empty;
            OnPropertyChanged(nameof(HasNoRows));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Drops the parsed rows when the document leaves the layout.
    /// <para>文档离开布局时丢弃已解析的行。</para>
    /// </summary>
    public void Cleanup()
    {
        Items.Clear();
        SelectedItem = null;
        HeaderHex = string.Empty;
        SourceDescription = string.Empty;
        OnPropertyChanged(nameof(HasNoRows));
    }
}