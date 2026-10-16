using System.Collections.Frozen;
using Avalonia.Media;
using Material.Icons;
using UotanStudio.Firmware.OtaDelta.Dat;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// File/directory icon catalog: maps a path to a Material glyph and a family colour (outline variants
/// are always preferred when the Material set provides one). The explorer tree and the compare view's
/// file columns share this single mapping instead of duplicating extension tables; it only picks the
/// glyph and colour shown, and never feeds format detection, open-mode routing or partition ownership.
/// <para>文件/目录图标目录：把路径映射到 Material 图标与家族配色（凡 Material 图标集提供空心版的
/// 类型一律使用空心版）。资源管理器树与对比视图的文件列共用同一份映射，避免复制扩展名判断；
/// 该映射只决定显示的图形与颜色，不参与文件格式判定、打开方式路由或分区归属。</para>
/// </summary>
public static class ExplorerIconCatalog
{
    /// <summary>
    /// Extensions of image files (partition and raw images). OTA artifacts (<c>.dat</c>) and compressed
    /// streams (<c>.br</c>) are excluded: they share the disk icon but are not conversion inputs. Optical-disc
    /// images stay in this glyph family even though they are not conversion inputs, because the two questions
    /// are answered by different owners: this table picks the glyph, <see cref="Services.Firmware.ImageInputService"/>
    /// decides conversion scope.
    /// <para>镜像类文件的扩展名（分区镜像与裸镜像）。不含 OTA 产物 <c>.dat</c> 与压缩流 <c>.br</c>：
    /// 它们与镜像共用磁盘图标，但不是「格式转换」的输入。光盘镜像保留在本字形家族内，即使它不是转换输入，
    /// 因为这两个问题由不同所有者回答：本表只决定字形，转换范围由
    /// <see cref="Services.Firmware.ImageInputService"/> 判定。</para>
    /// </summary>
    private static readonly FrozenSet<string> ImageFileExtensions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ".img", ".raw", ".bin", ".simg", ".sparse", ".super", ".image", ".iso"
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Resolves a node icon: placeholder nodes and unknown extensions use the generic file glyph,
    /// directories split into project folders and plain folders, and files map by extension to a glyph
    /// and family colour.
    /// <para>解析节点图标：占位节点与未知扩展名取通用文件图标，目录按「项目文件夹 / 普通文件夹」区分，
    /// 文件按扩展名映射到图标与家族配色。</para>
    /// </summary>
    /// <param name="path">The node path. <para>节点路径。</para></param>
    /// <param name="isDirectory">Whether the node is a directory. <para>是否为目录。</para></param>
    /// <param name="isProjectFolder">Whether the node is a project folder. <para>是否为项目文件夹。</para></param>
    /// <param name="isPlaceholder">Whether the node is a placeholder. <para>是否为占位节点。</para></param>
    /// <returns>The glyph and colour to show for the node. <para>该节点应显示的图标与配色。</para></returns>
    public static ExplorerIcon Resolve(string path, bool isDirectory, bool isProjectFolder = false, bool isPlaceholder = false)
    {
        if (isPlaceholder)
        {
            return new(MaterialIconKind.FileOutline, ExplorerIconPalette.Document);
        }

        if (isDirectory)
        {
            return isProjectFolder
                ? new(MaterialIconKind.FolderStarOutline, ExplorerIconPalette.Folder)
                : new(MaterialIconKind.FolderOutline, ExplorerIconPalette.Folder);
        }

        if (DatFileSet.IsTransferList(path))
        {
            return new(MaterialIconKind.FileArrowUpDownOutline, ExplorerIconPalette.Image);
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();

        if (ImageFileExtensions.Contains(extension))
        {
            return new(MaterialIconKind.Harddisk, ExplorerIconPalette.Image);
        }

        return extension switch
        {

            ".dat" =>
                new(MaterialIconKind.Harddisk, ExplorerIconPalette.Image),

            ".elf" or ".el" or ".so" or ".ko" or ".mbn" or ".melf" =>
                new(MaterialIconKind.Chip, ExplorerIconPalette.Firmware),

            ".dtb" or ".dtbo" or ".dts" or ".dtsi" =>
                new(MaterialIconKind.FileTreeOutline, ExplorerIconPalette.DeviceTree),

            ".apk" or ".apks" or ".apkm" or ".xapk" or ".aab" =>
                new(MaterialIconKind.Android, ExplorerIconPalette.Android),

            ".dex" or ".odex" or ".vdex" or ".oat" or ".smali" or ".jar"
                or ".c" or ".h" or ".cc" or ".cpp" or ".hpp" or ".java" or ".kt" or ".kts" or ".aidl" =>
                new(MaterialIconKind.FileCodeOutline, ExplorerIconPalette.Code),

            ".arsc" => new(MaterialIconKind.DatabaseOutline, ExplorerIconPalette.Resource),

            ".axml" => new(MaterialIconKind.FileXmlBox, ExplorerIconPalette.Markup),
            ".xml" => new(MaterialIconKind.Xml, ExplorerIconPalette.Markup),

            ".json" or ".jsonc" or ".jsonl" => new(MaterialIconKind.CodeJson, ExplorerIconPalette.Json),

            ".prop" or ".rc" or ".te" or ".fsconfig" or ".fstab" or ".ini" or ".cfg" or ".conf" or ".toml" =>
                new(MaterialIconKind.FileCogOutline, ExplorerIconPalette.Document),

            ".sh" or ".bash" or ".bat" or ".cmd" or ".ps1" or ".psm1" or ".py" or ".pl" or ".rb" or ".lua" =>
                new(MaterialIconKind.ScriptOutline, ExplorerIconPalette.Script),

            ".zip" or ".7z" or ".rar" => new(MaterialIconKind.ZipBoxOutline, ExplorerIconPalette.Archive),
            ".tar" or ".tgz" or ".gz" or ".xz" or ".bz2" or ".zst" or ".lz4" or ".lzma" or ".lz" or ".br" or ".cpio" =>
                new(MaterialIconKind.ArchiveOutline, ExplorerIconPalette.Archive),

            ".pac" or ".ops" or ".nb0" or ".ofp" or ".ozip" or ".kdz" or ".ftf" =>
                new(MaterialIconKind.PackageOutline, ExplorerIconPalette.Firmware),

            ".pem" or ".der" or ".crt" or ".cer" or ".cert" or ".x509" =>
                new(MaterialIconKind.FileCertificateOutline, ExplorerIconPalette.Security),
            ".key" or ".pk8" or ".jks" or ".keystore" or ".p12" or ".pfx" or ".avbpubkey" =>
                new(MaterialIconKind.FileKeyOutline, ExplorerIconPalette.Security),

            ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".ico" or ".svg" =>
                new(MaterialIconKind.FileImageOutline, ExplorerIconPalette.Media),
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" or ".3gp" or ".flv" =>
                new(MaterialIconKind.MovieOutline, ExplorerIconPalette.Media),
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac" or ".opus" =>
                new(MaterialIconKind.FileMusicOutline, ExplorerIconPalette.Media),

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

            _ => new(MaterialIconKind.FileOutline, ExplorerIconPalette.Document)
        };
    }
}

/// <summary>
/// A glyph paired with its family colour, so the extension is not re-resolved on every property access.
/// <para>图标与家族配色的组合，避免每次访问属性时重复解析扩展名。</para>
/// </summary>
/// <param name="Kind">The Material icon. <para>Material 图标。</para></param>
/// <param name="Brush">The icon colour. <para>图标颜色。</para></param>
public readonly record struct ExplorerIcon(MaterialIconKind Kind, IBrush Brush);