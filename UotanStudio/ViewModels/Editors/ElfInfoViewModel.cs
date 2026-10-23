using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Executable.Elf;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.UiAbstractions;

namespace UotanStudio.ViewModels.Editors;

/// <summary>Read-only ELF document, with cancellable metadata loading and localized presentation.<para>只读 ELF 文档，支持可取消的元数据加载与本地化呈现。</para></summary>
public partial class ElfInfoViewModel : Document
{
    private CancellationTokenSource? _loadCancellation;
    private ElfInfo? _info;
    private bool _closed;

    /// <summary>Creates a document that refreshes its labels when the application language changes.<para>创建应用语言变更时刷新标签的文档。</para></summary>
    public ElfInfoViewModel()
    {
        LanguageService.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Source path of the opened document.<para>已打开文档的源文件路径。</para></summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>Whether metadata is loading.<para>元数据是否正在加载。</para></summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Whether validated metadata is available.<para>是否已有经过校验的元数据。</para></summary>
    [ObservableProperty] private bool _isRecognized;

    /// <summary>Localized load failure message.<para>本地化的加载失败信息。</para></summary>
    [ObservableProperty] private string _errorMessage = string.Empty;

    /// <summary>Localized file-header fields.<para>本地化的文件头字段。</para></summary>
    [ObservableProperty] private IReadOnlyList<ElfFieldRow> _fields = [];

    /// <summary>Program-header rows, formatted without loading segment contents.<para>程序头行，格式化时不加载段内容。</para></summary>
    [ObservableProperty] private IReadOnlyList<ElfSegmentRow> _segments = [];

    /// <summary>Section-header rows, including named NOBITS sections.<para>节头行，包含具名 NOBITS 节。</para></summary>
    [ObservableProperty] private IReadOnlyList<ElfSectionRow> _sections = [];

    /// <summary>Selected program-header row.<para>当前选中的程序头行。</para></summary>
    [ObservableProperty] private ElfSegmentRow? _selectedSegment;

    /// <summary>Selected section-header row.<para>当前选中的节头行。</para></summary>
    [ObservableProperty] private ElfSectionRow? _selectedSection;

    /// <summary>Header, segment or section page currently visible.<para>当前显示的文件头、段或节页面。</para></summary>
    [ObservableProperty] private int _selectedPageIndex;

    /// <summary>Loads only ELF metadata; replaced or closed loads cannot publish stale results.<para>仅加载 ELF 元数据；被替换或已关闭的加载不会发布过期结果。</para></summary>
    /// <param name="path">Path of the ELF file to read.<para>要读取的 ELF 文件路径。</para></param>
    /// <param name="cancellationToken">Token cancelling the pending load.<para>用于取消待完成加载的令牌。</para></param>
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
        _info = null;
        Fields = [];
        SelectedSegment = null;
        SelectedSection = null;
        Segments = [];
        Sections = [];
        try
        {
            var info = await ElfReader.ReadAsync(path, source.Token);
            source.Token.ThrowIfCancellationRequested();
            _info = info;
            UpdateFields();
            Segments = [.. info.Segments.Select(static segment => new ElfSegmentRow(segment.Index,
                SegmentType(segment.Type), SegmentFlags(segment.Flags), Hex(segment.Offset), Hex(segment.VirtualAddress),
                Hex(segment.PhysicalAddress), Hex(segment.FileSize), Hex(segment.MemorySize), Hex(segment.Alignment)))];
            Sections = [.. info.Sections.Select(static section => new ElfSectionRow(section.Index, section.Name,
                SectionType(section.Type), Hex(section.Flags), Hex(section.Address), Hex(section.Offset), Hex(section.Size),
                section.Link, section.Info, Hex(section.Alignment), Hex(section.EntrySize)))];
            IsRecognized = true;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_loadCancellation, source) && !_closed)
            {
                ErrorMessage = Label("LoadError");
                LogService.Instance.Write(LogLevel.Error, "ELF", $"Failed to read '{path}': {ex.Message}");
            }
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

    [RelayCommand]
    private void OpenHex()
    {
        if (FilePath is { Length: > 0 } path)
        {
            WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(path, EditorOpenMode.Hex));
        }
    }

    /// <summary>Cancels pending reads and releases the language subscription on document close.<para>在文档关闭时取消待完成的读取并释放语言订阅。</para></summary>
    public void Cleanup()
    {
        _closed = true;
        _loadCancellation?.Cancel();
        LanguageService.Instance.LanguageChanged -= OnLanguageChanged;
        _info = null;
        Fields = [];
        SelectedSegment = null;
        SelectedSection = null;
        Segments = [];
        Sections = [];
        IsRecognized = false;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateFields();
        if (ErrorMessage.Length > 0)
        {
            ErrorMessage = Label("LoadError");
        }
    }

    private void UpdateFields()
    {
        if (_info is not { } info)
        {
            return;
        }

        var header = info.Header;
        Fields =
        [
            new(Label("Class"), header.Is64Bit ? "ELF64" : "ELF32"),
            new(Label("ByteOrder"), Label(header.IsLittleEndian ? "LittleEndian" : "BigEndian")),
            new(Label("Type"), $"{Label(header.Type switch { 0 => "None", 1 => "Relocatable", 2 => "Executable", 3 => "SharedObject", 4 => "Core", _ => "Unknown" })} ({Hex(header.Type)})"),
            new(Label("Machine"), $"{MachineName(header.Machine)} ({Hex(header.Machine)})"),
            new(Label("OsAbi"), $"{AbiName(header.OsAbi)} ({Hex(header.OsAbi)})"),
            new(Label("AbiVersion"), Number(header.AbiVersion)),
            new(Label("Version"), Number(header.Version)),
            new(Label("EntryPoint"), Hex(header.EntryPoint)),
            new(Label("Flags"), Hex(header.Flags)),
            new(Label("FileSize"), Number((ulong)info.FileSize)),
            new(Label("HeaderSize"), Number(header.HeaderSize)),
            new(Label("ProgramOffset"), Hex(header.ProgramHeaderOffset)),
            new(Label("ProgramCount"), Number(header.ProgramHeaderCount)),
            new(Label("ProgramEntrySize"), Number(header.ProgramHeaderEntrySize)),
            new(Label("SectionOffset"), Hex(header.SectionHeaderOffset)),
            new(Label("SectionCount"), Number(header.SectionHeaderCount)),
            new(Label("SectionEntrySize"), Number(header.SectionHeaderEntrySize)),
            new(Label("NameTableIndex"), Number(header.SectionNameTableIndex))
        ];
    }

    private static string Label(string key) => LanguageService.Instance.GetString($"Dock_Elf_{key}");
    private static string Hex(ulong value) => $"0x{value:X}";
    private static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static string MachineName(ushort value) => value switch
    {
        0 => "EM_NONE",
        2 => "SPARC",
        3 => "x86",
        8 => "MIPS",
        20 => "PowerPC",
        21 => "PowerPC64",
        22 => "S390",
        40 => "ARM",
        62 => "x86-64",
        183 => "AArch64",
        243 => "RISC-V",
        247 => "BPF",
        258 => "LoongArch",
        _ => $"e_machine={value}"
    };

    private static string AbiName(byte value) => value switch
    {
        0 => "System V",
        1 => "HP-UX",
        2 => "NetBSD",
        3 => "GNU/Linux",
        6 => "Solaris",
        7 => "AIX",
        9 => "FreeBSD",
        12 => "OpenBSD",
        64 => "ARM EABI",
        97 => "ARM",
        255 => "Standalone",
        _ => $"EI_OSABI={value}"
    };

    private static string SegmentType(uint value) => value switch
    {
        0 => "NULL",
        1 => "LOAD",
        2 => "DYNAMIC",
        3 => "INTERP",
        4 => "NOTE",
        5 => "SHLIB",
        6 => "PHDR",
        7 => "TLS",
        0x6474e550 => "GNU_EH_FRAME",
        0x6474e551 => "GNU_STACK",
        0x6474e552 => "GNU_RELRO",
        0x6474e553 => "GNU_PROPERTY",
        _ => Hex(value)
    };

    private static string SectionType(uint value) => value switch
    {
        0 => "NULL",
        1 => "PROGBITS",
        2 => "SYMTAB",
        3 => "STRTAB",
        4 => "RELA",
        5 => "HASH",
        6 => "DYNAMIC",
        7 => "NOTE",
        8 => "NOBITS",
        9 => "REL",
        10 => "SHLIB",
        11 => "DYNSYM",
        14 => "INIT_ARRAY",
        15 => "FINI_ARRAY",
        16 => "PREINIT_ARRAY",
        17 => "GROUP",
        18 => "SYMTAB_SHNDX",
        19 => "RELR",
        0x6ffffff6 => "GNU_HASH",
        0x6ffffffd => "GNU_VERDEF",
        0x6ffffffe => "GNU_VERNEED",
        0x6fffffff => "GNU_VERSYM",
        _ => Hex(value)
    };

    private static string SegmentFlags(uint flags) =>
        $"{((flags & 4) != 0 ? 'R' : '-')}{((flags & 2) != 0 ? 'W' : '-')}{((flags & 1) != 0 ? 'X' : '-')} ({Hex(flags)})";
}

/// <summary>A localized header field and its selectable value.<para>本地化的头部字段及其可选取的值。</para></summary>
/// <param name="Name">Localized field label.<para>本地化的字段名称。</para></param>
/// <param name="Value">Selectable field value.<para>可选取的字段值。</para></param>
public sealed record ElfFieldRow(string Name, string Value);

/// <summary>Formatted segment metadata for the virtualized table.<para>供虚拟化表格展示的格式化段元数据。</para></summary>
/// <param name="Index">Zero-based segment index.<para>从零开始的段序号。</para></param>
/// <param name="Type">Formatted segment type name.<para>格式化后的段类型名称。</para></param>
/// <param name="Flags">Formatted segment flags.<para>格式化后的段标志。</para></param>
/// <param name="Offset">Formatted file offset.<para>格式化后的文件偏移。</para></param>
/// <param name="VirtualAddress">Formatted virtual address.<para>格式化后的虚拟地址。</para></param>
/// <param name="PhysicalAddress">Formatted physical address.<para>格式化后的物理地址。</para></param>
/// <param name="FileSize">Formatted file size of the segment.<para>格式化后的段文件大小。</para></param>
/// <param name="MemorySize">Formatted memory size of the segment.<para>格式化后的段内存大小。</para></param>
/// <param name="Alignment">Formatted segment alignment.<para>格式化后的段对齐值。</para></param>
public sealed record ElfSegmentRow(int Index, string Type, string Flags, string Offset, string VirtualAddress,
    string PhysicalAddress, string FileSize, string MemorySize, string Alignment);

/// <summary>Formatted section metadata for the virtualized table.<para>供虚拟化表格展示的格式化节元数据。</para></summary>
/// <param name="Index">Zero-based section index.<para>从零开始的节序号。</para></param>
/// <param name="Name">Section name from the string table.<para>来自字符串表的节名称。</para></param>
/// <param name="Type">Formatted section type name.<para>格式化后的节类型名称。</para></param>
/// <param name="Flags">Formatted section flags.<para>格式化后的节标志。</para></param>
/// <param name="Address">Formatted section address.<para>格式化后的节地址。</para></param>
/// <param name="Offset">Formatted file offset.<para>格式化后的文件偏移。</para></param>
/// <param name="Size">Formatted section size.<para>格式化后的节大小。</para></param>
/// <param name="Link">Raw section link field.<para>原始的节关联字段。</para></param>
/// <param name="Info">Raw section info field.<para>原始的节信息字段。</para></param>
/// <param name="Alignment">Formatted section alignment.<para>格式化后的节对齐值。</para></param>
/// <param name="EntrySize">Formatted section entry size.<para>格式化后的节条目大小。</para></param>
public sealed record ElfSectionRow(int Index, string Name, string Type, string Flags, string Address, string Offset,
    string Size, uint Link, uint Info, string Alignment, string EntrySize);