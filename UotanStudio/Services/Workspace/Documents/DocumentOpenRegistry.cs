using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Magic;
using UotanStudio.Firmware.Executable.Elf;
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
/// <para>自动模式（<see cref="EditorOpenMode.Auto"/>）的格式路由决策序列集中在此，
/// 与 <see cref="FileOpenMode"/> 的判定谓词组合有序命中；魔数实现与扩展名集合本身保留在 FileOpenMode。</para>
/// The automatic-mode format-routing decision sequence lives here, composed in priority order from
/// <see cref="FileOpenMode"/>'s detection predicates; the magic-number implementations and extension
/// sets themselves remain in FileOpenMode.
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

    /// <summary><para>按实例类型查找（与类型模式匹配语义一致，含派生类型）。</para>
    /// Looks up by instance type, matching the semantics of a type pattern including derived types.</summary>
    public static DocumentOpenDescriptor? ByInstance(object dockable) =>
        _descriptors.FirstOrDefault(d => d.ViewModelType.IsInstanceOfType(dockable));

    /// <summary>
    /// <para>自动模式格式路由：按优先级顺序组合 <see cref="FileOpenMode"/> 的判定谓词，
    /// 命中的首个规则决定打开方式。魔数与扩展名集合的实现保留在 FileOpenMode，此处只编排顺序。
    /// 当无头部样本时等价于扩展名路由，与旧 <c>FileOpenMode.ResolveMode(path, header, detection)</c> 行为一致。</para>
    /// Automatic-mode format routing: composes <see cref="FileOpenMode"/>'s detection predicates in priority
    /// order and returns the mode of the first matching rule. The magic-number and extension-set implementations
    /// stay in FileOpenMode; only the ordering lives here. With no header sample it degrades to the
    /// extension-only route, matching the former <c>FileOpenMode.ResolveMode(path, header, detection)</c>.
    /// </summary>
    /// <param name="path">
    /// <para>文件名或完整路径。</para>
    /// A file name or full path.
    /// </param>
    /// <param name="header">
    /// <para>可选的已加载文件头部字节；为 <c>null</c> 时跳过依赖头部的规则。</para>
    /// Optional loaded file-header bytes; header-dependent rules are skipped when <c>null</c>.
    /// </param>
    /// <param name="detection">
    /// <para>可选的格式检测结果；非 <c>ExtensionOnly</c> 置信度时按格式名路由。</para>
    /// Optional format-detection result; a non-<c>ExtensionOnly</c> confidence routes by format name.
    /// </param>
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
    /// <para>仅按文件名/扩展名的自动路由（无头部样本）。</para>
    /// Automatic routing by file name/extension only (no header sample).
    /// </summary>
    /// <param name="path">
    /// <para>文件名或完整路径。</para>
    /// A file name or full path.
    /// </para>
    public static EditorOpenMode ResolveMode(string path) => ResolveMode(path, null, null);

    private delegate bool AutoRuleMatch(string path, byte[]? header, FormatDetectionResult? detection, Lazy<EditorOpenMode> extension);

    private readonly record struct AutoRule(EditorOpenMode Mode, AutoRuleMatch IsMatch);

    private static readonly AutoRule[] _autoRules =
    [
        // 1. ELF header: a malformed ELF falls back to Hex, a valid one opens the information panel.
        new(EditorOpenMode.Elf, static (_, header, _, _) =>
            header is not null && header.AsSpan().StartsWith("\u007fELF"u8) && ElfReader.TryReadHeader(header, out _)),
        new(EditorOpenMode.Hex, static (_, header, _, _) =>
            header is not null && header.AsSpan().StartsWith("\u007fELF"u8) && !ElfReader.TryReadHeader(header, out _)),

        // 2. Binary-XML manifest.
        new(EditorOpenMode.Axml, static (_, header, _, _) =>
            header is not null && UotanStudio.Firmware.AndroidMeta.AXml.AXmlParser.IsAxml(header)),

        // 3. Device tree (FDT/DTBO magic).
        new(EditorOpenMode.Dtb, static (_, header, _, _) =>
            header is not null && FileOpenMode.IsDtbMagic(header)),

        // 4. DEX container.
        new(EditorOpenMode.Dex, static (_, header, _, _) =>
            header is not null && FileOpenMode.IsDexMagic(header)),

        // 5. Strong firmware magic independent of extension.
        new(EditorOpenMode.Firmware, static (_, header, _, _) =>
            header is not null && FileOpenMode.IsFirmwareMagic(header)),

        // 6. ZIP container that is not an APK/JAR (those keep their dedicated editors).
        new(EditorOpenMode.FileSystem, static (path, header, _, _) =>
            header is not null && FileOpenMode.IsZipMagic(header)
            && !FileOpenMode.IsApkExtension(Path.GetExtension(path))
            && !FileOpenMode.IsJarExtension(Path.GetExtension(path))),

        // 7. Filesystem image magic (ext4/EROFS/F2FS/FAT/exFAT).
        new(EditorOpenMode.FileSystem, static (_, header, _, _) =>
            header is not null && FileOpenMode.IsFilesystemMagic(header)),

        // 8. LP super metadata magic.
        new(EditorOpenMode.Partition, static (_, header, _, _) =>
            header is not null && FileOpenMode.IsLpMetadataMagic(header)),

        // 9. GPT/MBR partition-table magic.
        new(EditorOpenMode.Partition, static (_, header, _, _) =>
            header is not null && (FileOpenMode.IsGptMagic(header) || FileOpenMode.IsMbrMagic(header))),

        // 10. Rockchip parameter text (CMDLINE:/mtdparts= markers).
        new(EditorOpenMode.Partition, static (_, header, _, _) =>
            header is not null && FileOpenMode.IsRockchipParameterText(header)),

        // 11. Confirmed format detection (non-extension-only confidence) routes by format name.
        new(EditorOpenMode.Arsc, static (_, _, detection, _) =>
            detection is { Confidence: not DetectionConfidence.ExtensionOnly }
            && detection.Handler.FormatName.Contains("Resource Table", StringComparison.OrdinalIgnoreCase)),
        new(EditorOpenMode.FileSystem, static (_, _, detection, _) =>
            detection is { Confidence: not DetectionConfidence.ExtensionOnly }
            && (detection.Handler.FormatName.Contains("Compressed", StringComparison.OrdinalIgnoreCase)
                || detection.Handler.FormatName.Contains("Archive", StringComparison.OrdinalIgnoreCase))),
        new(EditorOpenMode.Firmware, static (_, _, detection, _) =>
            detection is { Confidence: not DetectionConfidence.ExtensionOnly }),

        // 12. Text/Log extension route upgraded to Hex when the sample is binary.
        new(EditorOpenMode.Hex, static (_, header, _, extension) =>
            header is not null && extension.Value is EditorOpenMode.Text or EditorOpenMode.Log
            && FileOpenMode.IsBinaryContent(header)),

        // 13. Log-shaped content for a .txt/extension-less file.
        new(EditorOpenMode.Log, static (path, header, _, extension) =>
            header is not null && extension.Value is EditorOpenMode.Auto or EditorOpenMode.Text
            && (string.IsNullOrEmpty(Path.GetExtension(path))
                || Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            && UotanStudio.Services.Logging.LogFileService.LooksLikeLog(header)),

        // 14. Extension-only route result when it is decisive.
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

        // 15. ARSC magic on an otherwise-unknown (Auto) file.
        new(EditorOpenMode.Arsc, static (_, header, _, _) =>
            header is not null && UotanStudio.Firmware.AndroidMeta.Arsc.ArscParser.IsArsc(header)),

        // 16. Extension-less file with a detectable firmware format.
        new(EditorOpenMode.Firmware, static (path, header, detection, _) =>
            header is not null && header.Length > 0 && string.IsNullOrEmpty(Path.GetExtension(path))
            && (detection ?? DetectDetailedWithRegistry(path)) is not null),

        // 17. Generic file magic or binary content falls back to Hex.
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
    /// <para>扩展名/文件名优先路由表，逐条沿用旧 <c>FileOpenMode.ResolveMode(path)</c> 的顺序与谓词。</para>
    /// Extension/file-name first routing table, preserving the order and predicates of the former
    /// <c>FileOpenMode.ResolveMode(path)</c>.
    /// </summary>
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
    /// <para>读取有界文件头用于描述符路由谓词；读取失败或路径为空返回空数组。</para>
    /// Reads a bounded file header for descriptor routing predicates; returns an empty array on a read
    /// failure or empty path.
    /// </summary>
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