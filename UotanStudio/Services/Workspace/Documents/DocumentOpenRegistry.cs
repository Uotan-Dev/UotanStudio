using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Magic;
using UotanStudio.Firmware.Executable.Elf;
using UotanStudio.ViewModels.Compare;
using UotanStudio.ViewModels.Editors;
using UotanStudio.Views.Controls;

namespace UotanStudio.Services.Workspace.Documents;

/// <summary>
/// Registry of document-open descriptors: the single source of truth for open dispatch, session
/// restore, cleanup and the "Open As" menu, and the owner of the automatic-mode rule ordering.
/// <para>文档打开描述符注册表：打开派发、会话恢复、清理与"打开方式"菜单的唯一数据源，并持有自动模式的规则次序。</para>
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
            Open = static (w, r) => r.Mode == EditorOpenMode.Auto
                ? w.OpenDocumentAsync(r.Path!, EditorOpenMode.Auto)
                : w.OpenTextAsync(r.Path!, r.Mode),
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
            IconKind = "FileDocumentOutline",
            Probe = static r => IsElfHeader(ProbeHeader(r)),
            Open = static (w, r) => w.OpenElfAsync(r.Path!),
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
            Open = static (w, r) => w.OpenQualcommAsync(r.Path!),
            GetPath = vm => ((QualcommImageViewModel)vm).FilePath,
            Cleanup = vm => ((QualcommImageViewModel)vm).Cleanup(),
        },
        new()
        {
            Kind = "Qcn",
            Mode = EditorOpenMode.Qcn,
            ViewModelType = typeof(QcnEditorViewModel),
            LocaleKey = "Explorer_OpenAsQcn",
            MenuOrder = 3,
            IconKind = "RadioTower",
            Probe = static r => FileOpenMode.IsQcnExtension(Path.GetExtension(r.Path ?? string.Empty)),
            Open = static (w, r) => w.OpenQcnAsync(r.Path!),
            GetPath = vm => ((QcnEditorViewModel)vm).FilePath,
            IsDocument = false,
        },
        new()
        {
            Kind = "Partition",
            Mode = EditorOpenMode.Partition,
            ViewModelType = typeof(PartitionEditorViewModel),
            LocaleKey = "Explorer_OpenAsPartition",
            MenuOrder = 4,
            Probe = static r => IsPartitionPath(r.Path),
            Open = static (w, r) => w.OpenPartitionAsync(r.Path!),
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
            Probe = static r => IsLogExtension(Path.GetExtension(r.Path ?? string.Empty)),
            Open = static (w, r) => w.OpenLogAsync(r.Path!),
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
            Probe = static r => FileOpenMode.IsKnownBinaryExtension(Path.GetExtension(r.Path ?? string.Empty)),
            Open = static (w, r) => w.OpenHexAsync(r.Path!),
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
            Probe = static r =>
            {
                var ext = Path.GetExtension(r.Path ?? string.Empty);
                return FileOpenMode.IsDtbExtension(ext) || FileOpenMode.IsDtbMagic(ProbeHeader(r));
            },
            Open = static (w, r) => w.OpenDtbAsync(r.Path!),
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
            Probe = static r =>
            {
                var ext = Path.GetExtension(r.Path ?? string.Empty);
                return FileOpenMode.IsDexExtension(ext) || FileOpenMode.IsJarExtension(ext)
                    || FileOpenMode.IsDexMagic(ProbeHeader(r));
            },
            Open = static (w, r) => w.OpenDexAsync(r.Path!),
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
            Probe = static r => UotanStudio.Firmware.AndroidMeta.AXml.AXmlParser.IsAxml(ProbeHeader(r)),
            Open = static (w, r) => w.OpenManifestAsync(r.Path!),
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
            Probe = static r => FileOpenMode.IsArscExtension(Path.GetExtension(r.Path ?? string.Empty)),
            Open = static (w, r) => w.OpenArscAsync(r.Path!),
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
            Probe = static r => FileOpenMode.IsApkExtension(Path.GetExtension(r.Path ?? string.Empty)),
            Open = static (w, r) => w.OpenApkAsync(r.Path!),
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
            Probe = static r =>
            {
                var ext = Path.GetExtension(r.Path ?? string.Empty);
                return FileOpenMode.IsFirmwareExtension(ext) || FileOpenMode.IsFirmwareMagic(ProbeHeader(r));
            },
            Open = static (w, r) => w.OpenFirmwareAsync(r.Path!, r.Detection),
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
            Probe = static r =>
            {
                var ext = Path.GetExtension(r.Path ?? string.Empty);
                return FileOpenMode.IsFilesystemImageExtension(ext) || FileOpenMode.IsCpioArchiveExtension(ext)
                    || UotanStudio.Firmware.Archive.Browsing.ArchiveBrowser.SupportsPath(r.Path ?? string.Empty);
            },
            Open = static (w, r) => w.OpenFileSystemAsync(r.Path!),
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

    /// <summary>All descriptors in registration order. <para>全部描述符，按登记顺序。</para></summary>
    /// <value>The registered descriptor array. <para>已登记的描述符数组。</para></value>
    public static IReadOnlyList<DocumentOpenDescriptor> All => _descriptors;

    /// <summary>
    /// Descriptors listed in the "Open As" menu in ascending MenuOrder (the Auto pseudo entry is
    /// pinned first by the menu itself).
    /// <para>需要出现在"打开方式"菜单中的描述符，按 MenuOrder 升序（Auto 伪项由菜单侧固定置顶）。</para>
    /// </summary>
    /// <value>The menu-listed descriptors in ascending order. <para>进入菜单的描述符，按升序排列。</para></value>
    public static IReadOnlyList<DocumentOpenDescriptor> MenuEntries { get; } =
        [.. _descriptors.Where(d => d.MenuOrder >= 0).OrderBy(d => d.MenuOrder)];

    /// <summary>Looks up by session Kind string; null when unregistered. <para>按会话 Kind 字符串查找；未登记返回 null。</para></summary>
    /// <param name="kind">The session Kind string to match verbatim. <para>需逐字匹配的会话 Kind 字符串。</para></param>
    /// <returns>The matching descriptor, or null. <para>匹配的描述符，或为 null。</para></returns>
    public static DocumentOpenDescriptor? ByKind(string kind) =>
        _descriptors.FirstOrDefault(d => string.Equals(d.Kind, kind, StringComparison.Ordinal));

    /// <summary>Looks up by explicit open mode; null for Auto and unregistered modes. <para>按显式打开模式查找；Auto 与未登记模式返回 null。</para></summary>
    /// <param name="mode">The explicit open mode to match. <para>要匹配的显式打开方式。</para></param>
    /// <returns>The matching descriptor, or null. <para>匹配的描述符，或为 null。</para></returns>
    public static DocumentOpenDescriptor? ByMode(EditorOpenMode mode) =>
        _descriptors.FirstOrDefault(d => d.Mode == mode);

    /// <summary>Looks up by the document type registered for a kind. <para>按各种类登记的文档类型查找。</para></summary>
    /// <param name="viewModelType">The document type to match. <para>要匹配的文档类型。</para></param>
    /// <returns>The matching descriptor, or null. <para>匹配的描述符，或为 null。</para></returns>
    public static DocumentOpenDescriptor? ByViewModelType(Type viewModelType) =>
        _descriptors.FirstOrDefault(d => d.ViewModelType == viewModelType);

    /// <summary>
    /// Looks up by instance type, matching the semantics of a type pattern including derived types.
    /// <para>按实例类型查找（与类型模式匹配语义一致，含派生类型）。</para>
    /// </summary>
    /// <param name="dockable">The document instance to classify. <para>待判定的文档实例。</para></param>
    /// <returns>The matching descriptor, or null. <para>匹配的描述符，或为 null。</para></returns>
    public static DocumentOpenDescriptor? ByInstance(object dockable) =>
        _descriptors.FirstOrDefault(d => d.ViewModelType.IsInstanceOfType(dockable));

    /// <summary>
    /// Automatic-mode format routing: evaluates the detection predicates in priority order and returns
    /// the first matching mode, degrading to extension-only routing without a header sample.
    /// <para>自动模式格式路由：按优先级顺序判定检测谓词，取首个命中规则的打开方式；无头部样本时退化为扩展名路由。</para>
    /// </summary>
    /// <param name="path">
    /// A file name or full path.
    /// <para>文件名或完整路径。</para>
    /// </param>
    /// <param name="header">
    /// Optional loaded file-header bytes; header-dependent rules are skipped when <c>null</c>.
    /// <para>可选的已加载文件头部字节；为 <c>null</c> 时跳过依赖头部的规则。</para>
    /// </param>
    /// <param name="detection">
    /// Optional format-detection result; a non-<c>ExtensionOnly</c> confidence routes by format name.
    /// <para>可选的格式检测结果；非 <c>ExtensionOnly</c> 置信度时按格式名路由。</para>
    /// </param>
    /// <returns>
    /// The mode of the first matching rule, or Auto when none match.
    /// <para>首个命中规则的打开方式；无命中时为 Auto。</para>
    /// </returns>
    public static EditorOpenMode ResolveMode(string path, byte[]? header, FormatDetectionResult? detection)
    {
        var extension = new Lazy<EditorOpenMode>(() => ResolveExtensionOnly(path));
        foreach (var rule in _autoRules)
        {
            if (rule.IsMatch(path, header, detection, extension))
            {
                return rule.Mode;
            }
        }

        return EditorOpenMode.Auto;
    }

    /// <summary>
    /// Automatic routing by file name or extension only, with no header sample.
    /// <para>仅按文件名或扩展名的自动路由，无头部样本。</para>
    /// </summary>
    /// <param name="path">
    /// A file name or full path.
    /// <para>文件名或完整路径。</para>
    /// </param>
    /// <returns>
    /// The mode chosen by the first matching rule, or Auto.
    /// <para>首个命中规则选择的打开方式，或为 Auto。</para>
    /// </returns>
    public static EditorOpenMode ResolveMode(string path) => ResolveMode(path, null, null);

    private delegate bool AutoRuleMatch(string path, byte[]? header, FormatDetectionResult? detection, Lazy<EditorOpenMode> extension);

    private readonly record struct AutoRule(EditorOpenMode Mode, AutoRuleMatch IsMatch);

    private static readonly AutoRule[] _autoRules =
    [
        new(EditorOpenMode.Elf, static (_, header, _, _) =>
            header is not null && header.AsSpan().StartsWith("\u007fELF"u8) && ElfReader.TryReadHeader(header, out _)),
        new(EditorOpenMode.Hex, static (_, header, _, _) =>
            header is not null && header.AsSpan().StartsWith("\u007fELF"u8) && !ElfReader.TryReadHeader(header, out _)),

        new(EditorOpenMode.Axml, static (_, header, _, _) =>
            header is not null && UotanStudio.Firmware.AndroidMeta.AXml.AXmlParser.IsAxml(header)),

        new(EditorOpenMode.Dtb, static (_, header, _, _) =>
            header is not null && FileOpenMode.IsDtbMagic(header)),

        new(EditorOpenMode.Dex, static (_, header, _, _) =>
            header is not null && FileOpenMode.IsDexMagic(header)),

        new(EditorOpenMode.Firmware, static (_, header, _, _) =>
            header is not null && FileOpenMode.IsFirmwareMagic(header)),

        new(EditorOpenMode.FileSystem, static (path, header, _, _) =>
            header is not null && FileOpenMode.IsZipMagic(header)
            && !FileOpenMode.IsApkExtension(Path.GetExtension(path))
            && !FileOpenMode.IsJarExtension(Path.GetExtension(path))),

        new(EditorOpenMode.FileSystem, static (_, header, _, _) =>
            header is not null && FileOpenMode.IsFilesystemMagic(header)),

        new(EditorOpenMode.Partition, static (_, header, _, _) =>
            header is not null && FileOpenMode.IsLpMetadataMagic(header)),

        new(EditorOpenMode.Partition, static (_, header, _, _) =>
            header is not null && (FileOpenMode.IsGptMagic(header) || FileOpenMode.IsMbrMagic(header))),

        new(EditorOpenMode.Partition, static (_, header, _, _) =>
            header is not null && FileOpenMode.IsRockchipParameterText(header)),

        new(EditorOpenMode.Arsc, static (_, _, detection, _) =>
            detection is { Confidence: not DetectionConfidence.ExtensionOnly }
            && detection.Handler.FormatName.Contains("Resource Table", StringComparison.OrdinalIgnoreCase)),
        new(EditorOpenMode.FileSystem, static (_, _, detection, _) =>
            detection is { Confidence: not DetectionConfidence.ExtensionOnly }
            && (detection.Handler.FormatName.Contains("Compressed", StringComparison.OrdinalIgnoreCase)
                || detection.Handler.FormatName.Contains("Archive", StringComparison.OrdinalIgnoreCase))),
        new(EditorOpenMode.Firmware, static (_, _, detection, _) =>
            detection is { Confidence: not DetectionConfidence.ExtensionOnly }),

        new(EditorOpenMode.Hex, static (_, header, _, extension) =>
            header is not null && extension.Value is EditorOpenMode.Text or EditorOpenMode.Log
            && FileOpenMode.IsBinaryContent(header)),

        new(EditorOpenMode.Log, static (path, header, _, extension) =>
            header is not null && extension.Value is EditorOpenMode.Auto or EditorOpenMode.Text
            && (string.IsNullOrEmpty(Path.GetExtension(path))
                || Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            && UotanStudio.Services.Logging.LogFileService.LooksLikeLog(header)),

        new(EditorOpenMode.Text, static (_, _, _, extension) => extension.Value == EditorOpenMode.Text),
        new(EditorOpenMode.Log, static (_, _, _, extension) => extension.Value == EditorOpenMode.Log),
        new(EditorOpenMode.Dtb, static (_, _, _, extension) => extension.Value == EditorOpenMode.Dtb),
        new(EditorOpenMode.Dex, static (_, _, _, extension) => extension.Value == EditorOpenMode.Dex),
        new(EditorOpenMode.Arsc, static (_, _, _, extension) => extension.Value == EditorOpenMode.Arsc),
        new(EditorOpenMode.Apk, static (_, _, _, extension) => extension.Value == EditorOpenMode.Apk),
        new(EditorOpenMode.Qcn, static (_, _, _, extension) => extension.Value == EditorOpenMode.Qcn),
        new(EditorOpenMode.FileSystem, static (_, _, _, extension) => extension.Value == EditorOpenMode.FileSystem),
        new(EditorOpenMode.Partition, static (_, _, _, extension) => extension.Value == EditorOpenMode.Partition),
        new(EditorOpenMode.Firmware, static (_, _, _, extension) => extension.Value == EditorOpenMode.Firmware),
        new(EditorOpenMode.Hex, static (_, _, _, extension) => extension.Value == EditorOpenMode.Hex),

        new(EditorOpenMode.Arsc, static (_, header, _, _) =>
            header is not null && UotanStudio.Firmware.AndroidMeta.Arsc.ArscParser.IsArsc(header)),

        new(EditorOpenMode.Firmware, static (path, header, detection, _) =>
            header is not null && header.Length > 0 && string.IsNullOrEmpty(Path.GetExtension(path))
            && (detection ?? DetectDetailedWithRegistry(path)) is not null),

        new(EditorOpenMode.Hex, static (_, header, _, _) =>
            header is not null && FileMagic.Describe(header) is not null),
        new(EditorOpenMode.Hex, static (_, header, _, _) =>
            header is not null && FileOpenMode.IsBinaryContent(header)),
    ];

    private static FormatDetectionResult? DetectDetailedWithRegistry(string path)
    {
        FirmwareFormatRegistry.RegisterAll();
        return FormatDetector.DetectDetailed(path);
    }

    /// <summary>
    /// Extension and file-name routing table, evaluated entry by entry in list order.
    /// <para>扩展名与文件名路由表，按表内顺序逐条判定。</para>
    /// </summary>
    /// <param name="path">
    /// A file name or full path.
    /// <para>文件名或完整路径。</para>
    /// </param>
    /// <returns>
    /// The mode chosen by the first matching entry, or Auto.
    /// <para>首个命中项选择的打开方式，或为 Auto。</para>
    /// </returns>
    private static EditorOpenMode ResolveExtensionOnly(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return EditorOpenMode.Auto;
        }

        var ext = Path.GetExtension(path);

        if (IsLogExtension(ext))
        {
            return EditorOpenMode.Log;
        }

        if (FileOpenMode.IsDtbExtension(ext))
        {
            return EditorOpenMode.Dtb;
        }

        if (FileOpenMode.IsDexExtension(ext) || FileOpenMode.IsJarExtension(ext))
        {
            return EditorOpenMode.Dex;
        }

        if (FileOpenMode.IsArscExtension(ext))
        {
            return EditorOpenMode.Arsc;
        }

        if (FileOpenMode.IsApkExtension(ext))
        {
            return EditorOpenMode.Apk;
        }

        if (FileOpenMode.IsQcnExtension(ext))
        {
            return EditorOpenMode.Qcn;
        }

        if (FileOpenMode.IsDtsExtension(ext))
        {
            return EditorOpenMode.Text;
        }

        if (FileOpenMode.IsFilesystemImageExtension(ext) && FileOpenMode.IsFilesystemImage(path))
        {
            return EditorOpenMode.FileSystem;
        }

        if (UotanStudio.Firmware.Archive.Browsing.ArchiveBrowser.SupportsPath(path))
        {
            return EditorOpenMode.FileSystem;
        }

        if (IsLpCandidatePath(path) && FileOpenMode.IsLpSuperImage(path))
        {
            return EditorOpenMode.Partition;
        }

        if (IsPartitionTableCandidatePath(path) && (FileOpenMode.IsGptImage(path) || FileOpenMode.IsMbrImage(path)))
        {
            return EditorOpenMode.Partition;
        }

        if (FileOpenMode.IsFirmwareExtension(ext))
        {
            return EditorOpenMode.Firmware;
        }

        if (FileOpenMode.IsKnownBinaryExtension(ext))
        {
            return EditorOpenMode.Hex;
        }

        if (FileOpenMode.IsTextExtension(ext))
        {
            return EditorOpenMode.Text;
        }

        if (FileOpenMode.IsRockchipParameter(path))
        {
            return EditorOpenMode.Partition;
        }

        return EditorOpenMode.Auto;
    }

    private static bool IsLogExtension(string? ext) =>
        ext is not null
        && (ext.Equals(".log", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".logcat", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".adblog", StringComparison.OrdinalIgnoreCase));

    private static bool IsPartitionPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return FileOpenMode.IsLpSuperImage(path) || FileOpenMode.IsGptImage(path)
            || FileOpenMode.IsMbrImage(path) || FileOpenMode.IsRockchipParameter(path);
    }

    private static bool IsElfHeader(byte[]? header) =>
        header is not null && header.AsSpan().StartsWith("\u007fELF"u8) && ElfReader.TryReadHeader(header, out _);

    private static bool IsLpCandidatePath(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith("super", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ext = Path.GetExtension(path);
        return ext.Equals(".img", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".super", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPartitionTableCandidatePath(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length == 0)
        {
            return true;
        }

        return ext.Equals(".img", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gpt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mbr", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads a bounded file header for descriptor routing predicates.
    /// <para>读取有界文件头，供描述符路由谓词判定。</para>
    /// </summary>
    /// <param name="request">
    /// The open request carrying the target path.
    /// <para>持有目标路径的打开请求。</para>
    /// </param>
    /// <returns>
    /// The header bytes, or an empty array on a read failure or empty path.
    /// <para>头部字节；读取失败或路径为空时为空数组。</para>
    /// </returns>
    private static byte[] ProbeHeader(DocumentOpenRequest request)
    {
        var path = request.Path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return [];
        }

        try
        {
            return FormatDetector.ReadHeader(path, FileOpenMode.ContentProbeLength);
        }
        catch
        {
            return [];
        }
    }
}