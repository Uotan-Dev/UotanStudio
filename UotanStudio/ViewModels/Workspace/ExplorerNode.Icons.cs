using Avalonia.Media;
using Material.Icons;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// <para>资源管理器节点的图标：目录按「项目文件夹 / 普通文件夹」区分，文件按扩展名映射到图标与家族配色。
/// 凡 Material 图标集提供空心（Outline）版本的类型一律使用空心版，只有硬盘、芯片、Android、XML、JSON、PDF
/// 等少数家族没有空心版时才保留实心图标。该映射只决定树中显示的图形与颜色，不参与文件格式判定、
/// 打开方式路由或分区归属，那些规则仍由 <c>FileOpenMode</c> 与格式处理器注册表维护。</para>
/// Explorer node icons: directories are split into project folders and plain folders, files are mapped by
/// extension to an icon and a family colour. Types that have an outline variant in the Material icon set
/// always use it; only the few families without one (hard disk, chip, Android, XML, JSON, PDF) stay filled.
/// The mapping only picks the glyph and colour shown in the tree; file format detection, open-mode routing
/// and partition ownership stay with <c>FileOpenMode</c> and the format-handler registry.
/// </summary>
public sealed partial class ExplorerNode
{
    /// <summary>
    /// <para>节点在资源管理器中显示的图标（空心版优先，与 VS 解决方案管理器的扁平风格一致）。</para>
    /// The icon shown for this node in the explorer (outline variants preferred, matching the flat look
    /// of the VS Solution Explorer).
    /// </summary>
    public MaterialIconKind IconKind => Icon.Kind;

    /// <summary>
    /// <para>节点图标颜色，按文件家族取自 <see cref="ExplorerIconPalette"/>。</para>
    /// The node icon colour, taken from <see cref="ExplorerIconPalette"/> by file family.
    /// </summary>
    public IBrush IconBrush => Icon.Brush;

    /// <summary>
    /// <para>镜像类文件的扩展名（分区镜像与裸镜像）。不含 OTA 产物 <c>.dat</c> 与压缩流 <c>.br</c>：
    /// 它们与镜像共用磁盘图标，但不是「格式转换」的输入。</para>
    /// Extensions of image files (partition and raw images). OTA artifacts (<c>.dat</c>) and compressed
    /// streams (<c>.br</c>) are excluded: they share the disk icon but are not conversion inputs.
    /// </summary>
    private static readonly HashSet<string> ImageFileExtensions = new(StringComparer.Ordinal)
    {
        ".img", ".raw", ".bin", ".simg", ".sparse", ".super", ".image", ".iso"
    };

    /// <summary>
    /// <para>是否为镜像类文件，用作资源管理器右键「格式转换」入口的显示条件。</para>
    /// Whether this node is an image file; gates the explorer's "convert format" context-menu entry.
    /// </summary>
    public bool IsImageFile => !IsDirectory && !IsPlaceholder &&
        ImageFileExtensions.Contains(System.IO.Path.GetExtension(Path).ToLowerInvariant());

    private ExplorerIcon? _icon;
    private ExplorerIcon Icon => _icon ??= ResolveIcon();

    private ExplorerIcon ResolveIcon()
    {
        if (IsPlaceholder)
        {
            return new(MaterialIconKind.FileOutline, ExplorerIconPalette.Document);
        }

        if (IsDirectory)
        {
            return IsProjectFolder
                ? new(MaterialIconKind.FolderStarOutline, ExplorerIconPalette.Folder)
                : new(MaterialIconKind.FolderOutline, ExplorerIconPalette.Folder);
        }

        // 卡刷配对清单按文件名后缀识别，不能只凭 .list 扩展名判定。
        // The flashable companion list is recognized by file-name suffix, not by the bare .list extension.
        if (System.IO.Path.GetFileName(Path).EndsWith(".transfer.list", StringComparison.OrdinalIgnoreCase))
        {
            return new(MaterialIconKind.FileArrowUpDownOutline, ExplorerIconPalette.Image);
        }

        var extension = System.IO.Path.GetExtension(Path).ToLowerInvariant();

        // 镜像类文件共用磁盘图标（与「格式转换」入口共用同一份扩展名表）。
        // Image files share the disk icon (same extension table as the "convert format" entry).
        if (ImageFileExtensions.Contains(extension))
        {
            return new(MaterialIconKind.Harddisk, ExplorerIconPalette.Image);
        }

        return extension switch
        {
            // OTA 块数据（.new.dat/.patch.dat）与镜像共用磁盘图标，但不能作为转换输入。
            // OTA block data (.new.dat/.patch.dat) shares the disk icon but is not a conversion input.
            ".dat" =>
                new(MaterialIconKind.Harddisk, ExplorerIconPalette.Image),

            // 引导链与原生二进制：Kernel/ELF、内核模块、厂商固件（同样无空心芯片图形）。
            // Boot chain and native binaries: kernel/ELF, kernel modules, vendor blobs (no outline chip glyph).
            ".elf" or ".el" or ".so" or ".ko" or ".mbn" or ".melf" =>
                new(MaterialIconKind.Chip, ExplorerIconPalette.Firmware),

            // 设备树源文件与编译产物。
            // Device-tree sources and their compiled artifacts.
            ".dtb" or ".dtbo" or ".dts" or ".dtsi" =>
                new(MaterialIconKind.FileTreeOutline, ExplorerIconPalette.DeviceTree),

            // Android 安装包（Material 的 Android 图形只有实心版）。
            // Android packages (the Material Android glyph exists only in its filled form).
            ".apk" or ".apks" or ".apkm" or ".xapk" or ".aab" =>
                new(MaterialIconKind.Android, ExplorerIconPalette.Android),

            // 源码与字节码：DEX/JAR 等容器与普通源文件同属「代码文件」家族。
            // Source and bytecode: DEX/JAR containers share the "code file" family with plain sources.
            ".dex" or ".odex" or ".vdex" or ".oat" or ".smali" or ".jar"
                or ".c" or ".h" or ".cc" or ".cpp" or ".hpp" or ".java" or ".kt" or ".kts" or ".aidl" =>
                new(MaterialIconKind.FileCodeOutline, ExplorerIconPalette.Code),

            // 资源表：resources.arsc。
            // Resource tables: resources.arsc.
            ".arsc" => new(MaterialIconKind.DatabaseOutline, ExplorerIconPalette.Resource),

            // 清单与普通 XML：Material 的 XML 图形只有实心版，用字形区分二进制清单与文本 XML。
            // Manifests and plain XML: the Material XML glyph exists only filled, so the subtypes differ by glyph.
            ".axml" => new(MaterialIconKind.FileXmlBox, ExplorerIconPalette.Markup),
            ".xml" => new(MaterialIconKind.Xml, ExplorerIconPalette.Markup),

            // JSON 数据（Material 的 JSON 图形只有实心版）。
            // JSON data (the Material JSON glyph exists only filled).
            ".json" or ".jsonc" or ".jsonl" => new(MaterialIconKind.CodeJson, ExplorerIconPalette.Json),

            // 属性与配置（build.prop、fstab、*.rc 等）。
            // Properties and configuration (build.prop, fstab, *.rc and friends).
            ".prop" or ".rc" or ".te" or ".fsconfig" or ".fstab" or ".ini" or ".cfg" or ".conf" or ".toml" =>
                new(MaterialIconKind.FileCogOutline, ExplorerIconPalette.Document),

            // 脚本。
            // Scripts.
            ".sh" or ".bash" or ".bat" or ".cmd" or ".ps1" or ".psm1" or ".py" or ".pl" or ".rb" or ".lua" =>
                new(MaterialIconKind.ScriptOutline, ExplorerIconPalette.Script),

            // 归档容器与单流压缩：ZIP 系容器与 gz/xz/zst/br 等压缩流分开取图标。
            // Archive containers and single-stream compression: ZIP-style containers stay distinct from gz/xz/zst/br streams.
            ".zip" or ".7z" or ".rar" => new(MaterialIconKind.ZipBoxOutline, ExplorerIconPalette.Archive),
            ".tar" or ".tgz" or ".gz" or ".xz" or ".bz2" or ".zst" or ".lz4" or ".lzma" or ".lz" or ".br" or ".cpio" =>
                new(MaterialIconKind.ArchiveOutline, ExplorerIconPalette.Archive),

            // 厂商整包固件（线刷/卡刷工具包）。
            // Vendor firmware bundles (flash-tool packages).
            ".pac" or ".ops" or ".nb0" or ".ofp" or ".ozip" or ".kdz" or ".ftf" =>
                new(MaterialIconKind.PackageOutline, ExplorerIconPalette.Firmware),

            // 签名证书与密钥库。
            // Signing certificates and keystores.
            ".pem" or ".der" or ".crt" or ".cer" or ".cert" or ".x509" =>
                new(MaterialIconKind.FileCertificateOutline, ExplorerIconPalette.Security),
            ".key" or ".pk8" or ".jks" or ".keystore" or ".p12" or ".pfx" or ".avbpubkey" =>
                new(MaterialIconKind.FileKeyOutline, ExplorerIconPalette.Security),

            // 图片、音视频资源。
            // Image, video and audio assets.
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".ico" or ".svg" =>
                new(MaterialIconKind.FileImageOutline, ExplorerIconPalette.Media),
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" or ".3gp" or ".flv" =>
                new(MaterialIconKind.MovieOutline, ExplorerIconPalette.Media),
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac" or ".opus" =>
                new(MaterialIconKind.FileMusicOutline, ExplorerIconPalette.Media),

            // 文档、日志与表格。
            // Documents, logs and tables.
            ".md" or ".markdown" => new(MaterialIconKind.LanguageMarkdownOutline, ExplorerIconPalette.Document),
            ".csv" or ".tsv" => new(MaterialIconKind.FileDelimitedOutline, ExplorerIconPalette.Document),
            ".pdf" => new(MaterialIconKind.FilePdfBox, ExplorerIconPalette.Pdf),
            ".doc" or ".docx" => new(MaterialIconKind.FileWordOutline, ExplorerIconPalette.Document),
            ".xls" or ".xlsx" => new(MaterialIconKind.FileExcelOutline, ExplorerIconPalette.Document),
            ".ppt" or ".pptx" => new(MaterialIconKind.FilePowerpointOutline, ExplorerIconPalette.Document),
            ".log" or ".logcat" or ".adblog" =>
                new(MaterialIconKind.TextBoxMultipleOutline, ExplorerIconPalette.Document),
            ".txt" or ".text" or ".md5" or ".sha1" or ".sha256" =>
                new(MaterialIconKind.FileDocumentOutline, ExplorerIconPalette.Document),

            // 未知或没有扩展名的文件保持通用文件图标。
            // Unknown files and extension-less files keep the generic file icon.
            _ => new(MaterialIconKind.FileOutline, ExplorerIconPalette.Document)
        };
    }

    /// <summary>图标与家族配色的组合，避免每次访问属性时重复解析扩展名。</summary>
    /// <param name="Kind"><para>Material 图标。</para>The Material icon.</param>
    /// <param name="Brush"><para>图标颜色。</para>The icon colour.</param>
    private readonly record struct ExplorerIcon(MaterialIconKind Kind, IBrush Brush);
}