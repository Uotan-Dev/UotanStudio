using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Radio.Qcn;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// QCN editor view model: the left pane lists NV items (filterable by id/name), the right pane shows
/// the selected item's properties, structured editing for well-known formats (IMEI/SPC/MEID/ESN/MAC)
/// and hex editing of its 128-byte data; saving patches only the changed 128-byte data regions in
/// place, leaving every other byte of the file untouched.
/// <para>QCN 编辑器视图模型：左侧为 NV 条目列表（支持按 id/名称搜索过滤），右侧为选中条目的属性、
/// 已知项结构化编辑（IMEI/SPC/MEID/ESN/MAC）与 128 字节数据的十六进制编辑；保存时仅对发生变化的
/// 128 字节数据区做原位补丁，文件其余字节保持不变。</para>
/// </summary>
public partial class QcnEditorViewModel : Document, ICommandSaveableDocument
{
    private QcnDocument? _document;
    private readonly List<QcnNvItemViewModel> _allItems = [];

    /// <summary>
    /// The localized title of the save button.
    /// <para>保存按钮的本地化标题。</para>
    /// </summary>
    public static string SaveLabel => Services.LanguageService.Instance.GetString("Dock_Qcn_Save");

    /// <summary>
    /// The localized title of the left item list.
    /// <para>左栏条目列表的本地化标题。</para>
    /// </summary>
    public static string ItemsLabel => Services.LanguageService.Instance.GetString("Dock_Qcn_Items");

    /// <summary>
    /// The localized placeholder of the search box.
    /// <para>搜索框的本地化占位提示。</para>
    /// </summary>
    public static string SearchLabel => Services.LanguageService.Instance.GetString("Dock_Qcn_Search");

    /// <summary>
    /// The localized title of the right properties pane.
    /// <para>右栏属性面板的本地化标题。</para>
    /// </summary>
    public static string PropertiesLabel => Services.LanguageService.Instance.GetString("Dock_Qcn_Properties");

    /// <summary>
    /// The localized hint shown when no item is selected.
    /// <para>未选中条目时的本地化提示。</para>
    /// </summary>
    public static string NoSelectionLabel => Services.LanguageService.Instance.GetString("Dock_Qcn_NoSelection");

    /// <summary>
    /// The localized title of the structured value input.
    /// <para>结构化值输入框的本地化标题。</para>
    /// </summary>
    public static string StructuredLabel => Services.LanguageService.Instance.GetString("Dock_Qcn_Structured");

    /// <summary>
    /// The localized title of the raw data section.
    /// <para>原始数据区域的本地化标题。</para>
    /// </summary>
    public static string DataLabel => Services.LanguageService.Instance.GetString("Dock_Qcn_Data");

    /// <summary>
    /// The localized title of the item id field.
    /// <para>“条目 Id”字段的本地化标题。</para>
    /// </summary>
    public static string ItemIdLabel => Services.LanguageService.Instance.GetString("Dock_Qcn_ItemId");

    /// <summary>
    /// The localized title of the name field.
    /// <para>“名称”字段的本地化标题。</para>
    /// </summary>
    public static string NameLabel => Services.LanguageService.Instance.GetString("Dock_Qcn_Name");

    /// <summary>
    /// The localized title of the stream field.
    /// <para>“所在流”字段的本地化标题。</para>
    /// </summary>
    public static string StreamLabel => Services.LanguageService.Instance.GetString("Dock_Qcn_Stream");

    /// <summary>
    /// The localized title of the effective-length field.
    /// <para>“有效长度”字段的本地化标题。</para>
    /// </summary>
    public static string LengthLabel => Services.LanguageService.Instance.GetString("Dock_Qcn_Length");

    /// <summary>
    /// The localized busy text.
    /// <para>加载中文案。</para>
    /// </summary>
    public static string BusyLabel => Services.LanguageService.Instance.GetString("Dock_Qcn_Busy");

    /// <summary>
    /// The file path of the document.
    /// <para>文档对应的文件路径。</para>
    /// </summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>
    /// Whether a background parse is running (drives the busy indicator).
    /// <para>是否正在后台解析（联动忙碌指示）。</para>
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// Document dirty state.
    /// <para>文档已修改状态。</para>
    /// </summary>
    [ObservableProperty] private bool _isDirty;

    partial void OnIsDirtyChanged(bool value) => IsModified = value;

    /// <summary>
    /// The last parse/save error text; empty means no error.
    /// <para>最后一次解析/保存错误文本；为空表示无错误。</para>
    /// </summary>
    [ObservableProperty] private string _compileError = string.Empty;

    /// <summary>
    /// Top summary text (file version, mobile model and item statistics).
    /// <para>顶部摘要文本（文件版本、机型与条目统计）。</para>
    /// </summary>
    [ObservableProperty] private string _sourceDescription = string.Empty;

    /// <summary>
    /// Search box text (filters by decimal/hex id or name).
    /// <para>搜索框文本（按 id 十进制/十六进制或名称过滤）。</para>
    /// </summary>
    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>
    /// The currently selected item (drives the right properties pane).
    /// <para>当前选中的条目（联动右栏属性面板）。</para>
    /// </summary>
    [ObservableProperty] private QcnNvItemViewModel? _selectedItem;

    /// <summary>
    /// Whether an item detail pane is visible.
    /// <para>右栏条目详情可见性。</para>
    /// </summary>
    public bool HasSelection => SelectedItem is not null;

    /// <summary>
    /// The current filtered item list.
    /// <para>当前过滤后的条目列表。</para>
    /// </summary>
    public ObservableCollection<QcnNvItemViewModel> Items { get; } = [];

    /// <summary>
    /// Save command: patches the edited data regions back into the file in place.
    /// <para>保存命令：将编辑后的数据区原位补丁写回文件。</para>
    /// </summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// Creates the QCN editor view model and initializes the save command.
    /// <para>创建 QCN 编辑器视图模型并初始化保存命令。</para>
    /// </summary>
    public QcnEditorViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    /// <summary>
    /// Selected item changed: refreshes the detail-pane visibility.
    /// <para>选中条目变化：刷新详情面板可见性。</para>
    /// </summary>
    partial void OnSelectedItemChanged(QcnNvItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>
    /// Search text changed: rebuilds the filtered item list.
    /// <para>搜索文本变化：重建过滤后的条目列表。</para>
    /// </summary>
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>
    /// Asynchronously loads and parses the QCN file; format failures surface a localized error.
    /// <para>异步加载并解析 QCN 文件；容器或记录不符合格式时在错误栏给出本地化提示。</para>
    /// </summary>
    /// <param name="path">QCN 文件路径。<para>The QCN file path.</para></param>
    /// <param name="cancellationToken">
    /// Cancellation token carried through the background parse.
    /// <para>取消令牌，贯穿后台解析。</para>
    /// </param>
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
            QcnDocument document;
            try
            {
                document = await Task.Run(() => QcnReader.ParseAsync(path, cancellationToken), cancellationToken).ConfigureAwait(true);
            }
            catch (QcnException)
            {
                CompileError = Services.LanguageService.Instance.GetString("UserError_QcnUnrecognized");
                return;
            }
            catch (Exception ex)
            {
                CompileError = Services.UserError.Describe(ex, "UserError_QcnLoadFailed");
                return;
            }

            _document = document;
            _allItems.Clear();
            foreach (var item in document.Items)
            {
                _allItems.Add(new QcnNvItemViewModel(item, MarkDirty));
            }

            ApplyFilter();
            SourceDescription = BuildSummary(document);
            CompileError = string.Empty;
            IsDirty = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Saves: rewrites only the 128-byte data regions that differ from disk (in-place patch through a
    /// staged temporary file, verified and atomically published).
    /// <para>保存：仅重写与磁盘内容不同的 128 字节数据区（原位补丁、临时文件、校验后原子替换）。</para>
    /// </summary>
    public async Task SaveAsync()
    {
        if (IsBusy || _document is null || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await QcnWriter.PatchAsync(FilePath!, _document.Items, default).ConfigureAwait(true);
            IsDirty = false;
            CompileError = string.Empty;
        }
        catch (Exception ex)
        {
            CompileError = Services.UserError.Describe(ex, "UserError_QcnSaveFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Follow-up after a hex edit: refreshes the decoded display and marks the document dirty.
    /// <para>十六进制数据被编辑后的联动：刷新解码显示并标记文档为脏。</para>
    /// </summary>
    public void MarkHexEdited()
    {
        SelectedItem?.RefreshDecoded();
        MarkDirty();
    }

    /// <summary>
    /// Releases the model and view data held by this document (called when the document is closed).
    /// <para>释放文档持有的模型与视图数据（文档关闭时调用）。</para>
    /// </summary>
    public void Cleanup()
    {
        SelectedItem = null;
        Items.Clear();
        _allItems.Clear();
        _document = null;
        FilePath = null;
        SourceDescription = string.Empty;
        SearchText = string.Empty;
        CompileError = string.Empty;
        IsDirty = false;
    }

    private void MarkDirty() => IsDirty = true;

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        Items.Clear();
        foreach (var item in _allItems)
        {
            if (search.Length == 0 || item.Matches(search))
            {
                Items.Add(item);
            }
        }
    }

    private static string BuildSummary(QcnDocument document)
    {
        var builder = new StringBuilder("QCN: ");
        builder.Append(document.Items.Count).Append(" NV items · streams: ");
        builder.Append(string.Join(", ", document.NvItemArrayStreamPaths));
        if (document.FileVersion.Count > 0)
        {
            builder.Append(" · file version ").Append(string.Join('.', document.FileVersion));
        }

        var info = document.MobilePropertyInfo;
        if (info is not null)
        {
            builder.Append(" · model ").Append(info.MobileModelNo);
            if (info.SwVersion.Length > 0)
            {
                builder.Append(" · SW ").Append(info.SwVersion);
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// Structured value kind: selects the codec used by the right-pane structured editor.
/// <para>结构化值类型：决定右栏结构化编辑器采用哪种编解码。</para>
/// </summary>
public enum QcnStructuredKind
{
    /// <summary>Plain hex bytes only. <para>仅原始十六进制字节。</para></summary>
    None,

    /// <summary>Nine-byte IMEI (items 550/551). <para>9 字节 IMEI（条目 550/551）。</para></summary>
    Imei,

    /// <summary>Six-digit SPC (item 85). <para>6 位 SPC（条目 85）。</para></summary>
    Spc,

    /// <summary>Byte-reversed MEID (items 1943/5598). <para>字节反序 MEID（条目 1943/5598）。</para></summary>
    Meid,

    /// <summary>Byte-reversed ESN (items 0/5597). <para>字节反序 ESN（条目 0/5597）。</para></summary>
    Esn,

    /// <summary>Six-byte MAC address (items 447/4678). <para>6 字节 MAC 地址（条目 447/4678）。</para></summary>
    Mac
}

/// <summary>
/// Binding object for one NV item: entry metadata, decoded preview and structured edit state.
/// <para>单个 NV 条目的绑定对象：条目元数据、解码预览与结构化编辑状态。</para>
/// </summary>
public partial class QcnNvItemViewModel : ObservableObject
{
    private static readonly FrozenDictionary<ushort, QcnStructuredKind> StructuredKinds =
        new Dictionary<ushort, QcnStructuredKind>
        {
            [550] = QcnStructuredKind.Imei,
            [551] = QcnStructuredKind.Imei,
            [85] = QcnStructuredKind.Spc,
            [1943] = QcnStructuredKind.Meid,
            [5598] = QcnStructuredKind.Meid,
            [0] = QcnStructuredKind.Esn,
            [5597] = QcnStructuredKind.Esn,
            [447] = QcnStructuredKind.Mac,
            [4678] = QcnStructuredKind.Mac
        }.ToFrozenDictionary();

    private readonly QcnNvItem _item;
    private readonly Action _onChanged;
    private bool _suppress;

    /// <summary>
    /// Creates an item view model.
    /// <para>创建条目视图模型。</para>
    /// </summary>
    /// <param name="item">底层条目数据。 The underlying item data.</param>
    /// <param name="onChanged">数据变更回调（标记文档为脏）。 Callback on data change (marks the document dirty).</param>
    public QcnNvItemViewModel(QcnNvItem item, Action onChanged)
    {
        _item = item;
        _onChanged = onChanged;
        QcnKnownItems.TryGet(item.Id, out var known);
        Kind = StructuredKinds.GetValueOrDefault(item.Id, QcnStructuredKind.None);
        KnownName = known?.Name;
        Category = known?.Category;
        IdHex = $"0x{item.Id:X4}";
        StreamPath = item.SourceStreamPath;
        RefreshDecoded();
    }

    /// <summary>
    /// The underlying firmware item (its data buffer feeds the save chain directly).
    /// <para>底层 Firmware 条目（保存链直接使用其数据缓冲区）。</para>
    /// </summary>
    public QcnNvItem Item => _item;

    /// <summary>
    /// The NV item id (decimal).
    /// <para>NV 条目 id（十进制）。</para>
    /// </summary>
    public ushort Id => _item.Id;

    /// <summary>
    /// The hex display of the NV item id.
    /// <para>NV 条目 id 的十六进制显示。</para>
    /// </summary>
    public string IdHex { get; }

    /// <summary>
    /// The dictionary English name; <c>null</c> for unknown items.
    /// <para>字典中的英文名称；未知条目为 <c>null</c>。</para>
    /// </summary>
    public string? KnownName { get; }

    /// <summary>
    /// The QPST category; <c>null</c> for unknown items.
    /// <para>QPST 分类；未知条目为 <c>null</c>。</para>
    /// </summary>
    public string? Category { get; }

    /// <summary>
    /// The structured value kind.
    /// <para>结构化值类型。</para>
    /// </summary>
    public QcnStructuredKind Kind { get; }

    /// <summary>
    /// The NV_ITEM_ARRAY stream path this record was parsed from.
    /// <para>记录所在的 NV_ITEM_ARRAY 流路径。</para>
    /// </summary>
    public string StreamPath { get; }

    /// <summary>
    /// The heuristic effective data length in bytes.
    /// <para>启发式有效数据长度（字节）。</para>
    /// </summary>
    public int EffectiveLength => _item.EffectiveLength;

    /// <summary>
    /// The 128-byte data buffer (hex editor binding source; edits mutate it in place).
    /// <para>128 字节数据缓冲区（十六进制编辑器绑定源，编辑就地写回）。</para>
    /// </summary>
    public byte[] Data => _item.Data;

    /// <summary>
    /// The decoded preview shown in the list and the detail pane.
    /// <para>列表与详情中的解码预览。</para>
    /// </summary>
    [ObservableProperty] private string _decodedPreview = string.Empty;

    /// <summary>
    /// The structured editor text.
    /// <para>结构化编辑框文本。</para>
    /// </summary>
    [ObservableProperty] private string _structuredValue = string.Empty;

    /// <summary>
    /// The structured edit hint (check-digit warning or invalid-value notice; empty when fine).
    /// <para>结构化编辑提示文本（校验位警告或无效值提示；为空表示正常）。</para>
    /// </summary>
    [ObservableProperty] private string _structuredHint = string.Empty;

    /// <summary>
    /// Refreshes the decoded preview and the structured editor text from the current data.
    /// <para>按当前数据刷新解码预览与结构化编辑框。</para>
    /// </summary>
    public void RefreshDecoded()
    {
        _suppress = true;
        try
        {
            var data = _item.Data.AsSpan();
            DecodedPreview = DecodePreview(data);
            if (Kind != QcnStructuredKind.None)
            {
                StructuredValue = DecodeStructured(data);
                StructuredHint = string.Empty;
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>
    /// Whether the item matches the search text (decimal/hex id, name, category or decoded preview).
    /// <para>判断条目是否命中搜索文本（id 十进制/十六进制、名称、分类或解码预览）。</para>
    /// </summary>
    /// <param name="search">
    /// The trimmed search text.
    /// <para>已去首尾空白的搜索文本。</para>
    /// </param>
    public bool Matches(string search)
    {
        if (Id.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase)
            || IdHex.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (KnownName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || (Category?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || DecodedPreview.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    partial void OnStructuredValueChanged(string value)
    {
        if (_suppress || Kind == QcnStructuredKind.None)
        {
            return;
        }

        ApplyStructured(value);
    }

    private void ApplyStructured(string value)
    {
        Span<byte> encoded = stackalloc byte[9];
        var ok = Kind switch
        {
            QcnStructuredKind.Imei => QcnImeiCodec.TryEncode(value, encoded),
            QcnStructuredKind.Spc => QcnItemCodecs.TryEncodeSpc(value, encoded),
            QcnStructuredKind.Meid => QcnItemCodecs.TryEncodeReversedHex(value, encoded),
            QcnStructuredKind.Esn => QcnItemCodecs.TryEncodeReversedHex(value, encoded),
            QcnStructuredKind.Mac => QcnItemCodecs.TryEncodeMac(value, encoded),
            _ => false
        };
        if (!ok)
        {
            StructuredHint = Services.LanguageService.Instance.GetString("Dock_Qcn_StructuredInvalid");
            return;
        }

        var length = Kind is QcnStructuredKind.Meid ? 7
            : Kind is QcnStructuredKind.Esn ? 4
            : Kind is QcnStructuredKind.Mac ? 6
            : Kind is QcnStructuredKind.Spc ? 6
            : 9;
        encoded[..length].CopyTo(_item.Data);
        StructuredHint = Kind is QcnStructuredKind.Imei && QcnImeiCodec.TryDecode(_item.Data, out _, out var valid) && !valid
            ? Services.LanguageService.Instance.GetString("Dock_Qcn_ImeiCheckInvalid")
            : string.Empty;
        _suppress = true;
        try
        {
            DecodedPreview = DecodePreview(_item.Data);
            OnPropertyChanged(nameof(EffectiveLength));
        }
        finally
        {
            _suppress = false;
        }

        _onChanged();
    }

    private string DecodePreview(ReadOnlySpan<byte> data)
    {
        if (Kind == QcnStructuredKind.Imei && QcnImeiCodec.TryDecode(data, out var imei, out _))
        {
            return imei!;
        }

        if (Kind == QcnStructuredKind.Spc)
        {
            var spc = QcnItemCodecs.DecodeSpc(data);
            if (spc is not null)
            {
                return spc;
            }
        }

        if (Kind is QcnStructuredKind.Meid or QcnStructuredKind.Esn)
        {
            var hex = QcnItemCodecs.DecodeReversedHex(data, Kind == QcnStructuredKind.Meid ? 7 : 4);
            if (hex is not null)
            {
                return hex;
            }
        }

        if (Kind == QcnStructuredKind.Mac)
        {
            var mac = QcnItemCodecs.DecodeMac(data);
            if (mac is not null)
            {
                return mac;
            }
        }

        var previewLength = Math.Min(8, Math.Max(_item.EffectiveLength, 1));
        Span<char> preview = stackalloc char[2 * previewLength];
        for (var i = 0; i < previewLength; i++)
        {
            data[i].TryFormat(preview[(2 * i)..], out _, "X2", CultureInfo.InvariantCulture);
        }

        return new string(preview);
    }

    private string DecodeStructured(ReadOnlySpan<byte> data) => Kind switch
    {
        QcnStructuredKind.Imei => QcnImeiCodec.TryDecode(data, out var imei, out _) ? imei! : string.Empty,
        QcnStructuredKind.Spc => QcnItemCodecs.DecodeSpc(data) ?? string.Empty,
        QcnStructuredKind.Meid => QcnItemCodecs.DecodeReversedHex(data, 7) ?? string.Empty,
        QcnStructuredKind.Esn => QcnItemCodecs.DecodeReversedHex(data, 4) ?? string.Empty,
        QcnStructuredKind.Mac => QcnItemCodecs.DecodeMac(data) ?? string.Empty,
        _ => string.Empty
    };
}