using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Executable.Elf;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Logging;
using UotanStudio.Views.Controls;

namespace UotanStudio.ViewModels.Editors;

/// <summary>Read-only ELF document, with cancellable metadata loading and localized presentation.</summary>
public partial class ElfInfoViewModel : Document
{
    private CancellationTokenSource? _loadCancellation;
    private ElfInfo? _info;
    private bool _closed;

    /// <summary>Creates a document that refreshes its labels when the application language changes.</summary>
    public ElfInfoViewModel()
    {
        LanguageService.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Source path used by opening, navigation and session persistence.</summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>Whether metadata is loading.</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Whether validated metadata is available.</summary>
    [ObservableProperty] private bool _isRecognized;

    /// <summary>Localized load failure; detailed diagnostics are written to the application log.</summary>
    [ObservableProperty] private string _errorMessage = string.Empty;

    /// <summary>Localized file-header fields.</summary>
    [ObservableProperty] private IReadOnlyList<ElfFieldRow> _fields = [];

    /// <summary>Program-header rows, formatted without loading segment contents.</summary>
    [ObservableProperty] private IReadOnlyList<ElfSegmentRow> _segments = [];

    /// <summary>Section-header rows, including named NOBITS sections.</summary>
    [ObservableProperty] private IReadOnlyList<ElfSectionRow> _sections = [];

    /// <summary>Selected program header, synchronized with the properties pane.</summary>
    [ObservableProperty] private ElfSegmentRow? _selectedSegment;

    /// <summary>Selected section header, synchronized with the properties pane.</summary>
    [ObservableProperty] private ElfSectionRow? _selectedSection;

    /// <summary>Header, segment or section page currently visible.</summary>
    [ObservableProperty] private int _selectedPageIndex;

    /// <summary>Loads only ELF metadata; replaced or closed loads cannot publish stale results.</summary>
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
            Segments = info.Segments.Select(static segment => new ElfSegmentRow(segment.Index,
                SegmentType(segment.Type), SegmentFlags(segment.Flags), Hex(segment.Offset), Hex(segment.VirtualAddress),
                Hex(segment.PhysicalAddress), Hex(segment.FileSize), Hex(segment.MemorySize), Hex(segment.Alignment))).ToArray();
            Sections = info.Sections.Select(static section => new ElfSectionRow(section.Index, section.Name,
                SectionType(section.Type), Hex(section.Flags), Hex(section.Address), Hex(section.Offset), Hex(section.Size),
                section.Link, section.Info, Hex(section.Alignment), Hex(section.EntrySize))).ToArray();
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

    /// <summary>Cancels pending reads and releases the language subscription on document close.</summary>
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

/// <summary>A localized header field and its selectable value.</summary>
public sealed record ElfFieldRow(string Name, string Value);

/// <summary>Formatted segment metadata for the virtualized table.</summary>
public sealed record ElfSegmentRow(int Index, string Type, string Flags, string Offset, string VirtualAddress,
    string PhysicalAddress, string FileSize, string MemorySize, string Alignment);

/// <summary>Formatted section metadata for the virtualized table.</summary>
public sealed record ElfSectionRow(int Index, string Name, string Type, string Flags, string Address, string Offset,
    string Size, uint Link, uint Info, string Alignment, string EntrySize);