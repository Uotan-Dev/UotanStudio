using UotanStudio.ViewModels.Compare;
using UotanStudio.ViewModels.Editors;
using UotanStudio.Views.Controls;

namespace UotanStudio.Services.Workspace.Documents;

/// <summary>
/// <para>文档编辑器注册表：打开派发、会话恢复、会话捕获、清理与"打开方式"菜单的共同数据源，
/// 取代此前手工维护在 WorkspaceViewModel、WorkspaceSessionCapture 与 WorkspaceDockFactory 的并行枚举。</para>
/// The document-editor registry: the shared source of truth for open dispatch, session restore,
/// session capture, cleanup and the "Open As" menu, replacing the parallel hand-maintained
/// enumerations previously spread across WorkspaceViewModel, WorkspaceSessionCapture and
/// WorkspaceDockFactory.
/// </summary>
internal static class DocumentOpenRegistry
{
    private static readonly DocumentOpenDescriptor[] _descriptors =
    [
        new()
        {
            Kind = "Text",
            Mode = EditorOpenMode.Text,
            ViewModelType = typeof(FileViewModel),
            LocaleKey = "Explorer_OpenAsText",
            MenuOrder = 5,
            IconKind = "FileDocumentOutline",
            GetPath = vm => ((FileViewModel)vm).FilePath,
            Cleanup = vm => { try { ((FileViewModel)vm).Cleanup(); } catch { } },
        },
        new()
        {
            Kind = "Elf",
            Mode = EditorOpenMode.Elf,
            ViewModelType = typeof(ElfInfoViewModel),
            LocaleKey = "Explorer_OpenAsElf",
            MenuOrder = 1,
            GetPath = vm => ((ElfInfoViewModel)vm).FilePath,
            Cleanup = vm => ((ElfInfoViewModel)vm).Cleanup(),
        },
        new()
        {
            Kind = "Qualcomm",
            Mode = EditorOpenMode.Qualcomm,
            ViewModelType = typeof(QualcommImageViewModel),
            LocaleKey = "Explorer_OpenAsQualcomm",
            MenuOrder = 2,
            IconKind = "Chip",
            GetPath = vm => ((QualcommImageViewModel)vm).FilePath,
            Cleanup = vm => ((QualcommImageViewModel)vm).Cleanup(),
        },
        new()
        {
            Kind = "Qcn",
            Mode = EditorOpenMode.Qcn,
            ViewModelType = typeof(QcnEditorViewModel),
            LocaleKey = "Explorer_OpenAsQcn",
            MenuOrder = 4,
            IconKind = "RadioTower",
            GetPath = vm => ((QcnEditorViewModel)vm).FilePath,
            IsDocument = false,
        },
        new()
        {
            Kind = "Partition",
            Mode = EditorOpenMode.Partition,
            ViewModelType = typeof(PartitionEditorViewModel),
            LocaleKey = "Explorer_OpenAsPartition",
            MenuOrder = 3,
            GetPath = vm => ((PartitionEditorViewModel)vm).FilePath,
            Cleanup = vm => { try { ((PartitionEditorViewModel)vm).Cleanup(); } catch { } },
        },
        new()
        {
            Kind = "Log",
            Mode = EditorOpenMode.Log,
            ViewModelType = typeof(LogEditorViewModel),
            LocaleKey = "Explorer_OpenAsLog",
            MenuOrder = 6,
            IconKind = "TextBoxMultipleOutline",
            GetPath = vm => ((LogEditorViewModel)vm).FilePath,
            Cleanup = vm => ((LogEditorViewModel)vm).Cleanup(),
            IsDocument = false,
        },
        new()
        {
            Kind = "Hex",
            Mode = EditorOpenMode.Hex,
            ViewModelType = typeof(HexEditorViewModel),
            LocaleKey = "Explorer_OpenAsHex",
            MenuOrder = 7,
            IconKind = "CodeBraces",
            GetPath = vm => ((HexEditorViewModel)vm).FilePath,
            Cleanup = vm => { try { ((HexEditorViewModel)vm).Cleanup(); } catch { } },
        },
        new()
        {
            Kind = "Dtb",
            Mode = EditorOpenMode.Dtb,
            ViewModelType = typeof(DtbEditorViewModel),
            LocaleKey = "Explorer_OpenAsDtb",
            MenuOrder = 8,
            IconKind = "FileTreeOutline",
            GetPath = vm => ((DtbEditorViewModel)vm).FilePath,
            Cleanup = vm => { try { ((DtbEditorViewModel)vm).Cleanup(); } catch { } },
        },
        new()
        {
            Kind = "Dex",
            Mode = EditorOpenMode.Dex,
            ViewModelType = typeof(DexEditorViewModel),
            LocaleKey = "Explorer_OpenAsDex",
            MenuOrder = 9,
            IconKind = "FileCodeOutline",
            GetPath = vm => ((DexEditorViewModel)vm).FilePath,
            Cleanup = vm => { try { ((DexEditorViewModel)vm).Cleanup(); } catch { } },
        },
        new()
        {
            Kind = "Manifest",
            Mode = EditorOpenMode.Axml,
            ViewModelType = typeof(ManifestEditorViewModel),
            LocaleKey = "Explorer_OpenAsAxml",
            MenuOrder = 10,
            IconKind = "FileXmlBox",
            GetPath = vm => ((ManifestEditorViewModel)vm).FilePath,
            Cleanup = vm => { try { ((ManifestEditorViewModel)vm).Cleanup(); } catch { } },
        },
        new()
        {
            Kind = "Arsc",
            Mode = EditorOpenMode.Arsc,
            ViewModelType = typeof(ArscEditorViewModel),
            LocaleKey = "Explorer_OpenAsArsc",
            MenuOrder = 11,
            IconKind = "DatabaseOutline",
            GetPath = vm => ((ArscEditorViewModel)vm).FilePath,
            Cleanup = vm => { try { ((ArscEditorViewModel)vm).Cleanup(); } catch { } },
        },
        new()
        {
            Kind = "Apk",
            Mode = EditorOpenMode.Apk,
            ViewModelType = typeof(ApkEditorViewModel),
            LocaleKey = "Explorer_OpenAsApk",
            MenuOrder = 12,
            IconKind = "Android",
            GetPath = vm => ((ApkEditorViewModel)vm).FilePath,
        },
        new()
        {
            Kind = "Firmware",
            Mode = EditorOpenMode.Firmware,
            ViewModelType = typeof(FirmwareInfoViewModel),
            LocaleKey = "Explorer_OpenAsFirmware",
            MenuOrder = 13,
            IconKind = "PackageOutline",
            GetPath = vm => ((FirmwareInfoViewModel)vm).FilePath,
            Cleanup = vm => { try { ((FirmwareInfoViewModel)vm).Cleanup(); } catch { } },
        },
        new()
        {
            Kind = "FileManager",
            Mode = EditorOpenMode.FileSystem,
            ViewModelType = typeof(FileManagerEditorViewModel),
            LocaleKey = "Explorer_OpenAsFileSystem",
            MenuOrder = 14,
            IconKind = "FolderSearchOutline",
            GetPath = vm => ((FileManagerEditorViewModel)vm).FilePath,
            Cleanup = vm => { try { ((FileManagerEditorViewModel)vm).Cleanup(); } catch { } },
        },
        new()
        {
            Kind = "Compare",
            ViewModelType = typeof(CompareViewModel),
            GetPath = _ => null,
            Cleanup = vm => ((CompareViewModel)vm).Cleanup(),
        },
        new()
        {
            Kind = "ScreenMirror",
            ViewModelType = typeof(ScreenMirrorViewModel),
            GetPath = _ => null,
            Cleanup = vm => { try { ((ScreenMirrorViewModel)vm).Cleanup(); } catch { } },
            IsDocument = false,
            Singleton = false,
        },
    ];

    /// <summary><para>全部描述符，按登记顺序。</para>All descriptors in registration order.</summary>
    public static IReadOnlyList<DocumentOpenDescriptor> All => _descriptors;

    /// <summary><para>需要出现在"打开方式"菜单中的描述符，按 MenuOrder 升序（Auto 伪项由菜单侧固定置顶）。</para>
    /// Descriptors listed in the "Open As" menu in ascending MenuOrder (the Auto pseudo entry is
    /// pinned first by the menu itself).</summary>
    public static IReadOnlyList<DocumentOpenDescriptor> MenuEntries { get; } =
        [.. _descriptors.Where(d => d.MenuOrder >= 0).OrderBy(d => d.MenuOrder)];

    /// <summary><para>按会话 Kind 字符串查找；未登记返回 null。</para>Looks up by session Kind string; null when unregistered.</summary>
    public static DocumentOpenDescriptor? ByKind(string kind) =>
        _descriptors.FirstOrDefault(d => string.Equals(d.Kind, kind, StringComparison.Ordinal));

    /// <summary><para>按显式打开模式查找；Auto 与未登记模式返回 null。</para>Looks up by explicit open mode; null for Auto and unregistered modes.</summary>
    public static DocumentOpenDescriptor? ByMode(EditorOpenMode mode) =>
        _descriptors.FirstOrDefault(d => d.Mode == mode);

    /// <summary><para>按 ViewModel 类型查找，用于会话捕获与清理。</para>Looks up by ViewModel type, used for session capture and cleanup.</summary>
    public static DocumentOpenDescriptor? ByViewModelType(Type viewModelType) =>
        _descriptors.FirstOrDefault(d => d.ViewModelType == viewModelType);
}
