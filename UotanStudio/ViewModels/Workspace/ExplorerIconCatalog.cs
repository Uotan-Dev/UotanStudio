using System.Collections.Frozen;
using Avalonia.Media;
using UotanStudio.Firmware.OtaDelta.Dat;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// File/directory icon catalog: maps a path to an Iconica TDesign glyph and a family colour. The explorer tree
/// and the compare view's file columns share this single mapping instead of duplicating extension tables; it
/// only picks the glyph and colour shown, and never feeds format detection, open-mode routing or partition
/// ownership.
/// <para>文件/目录图标目录：把路径映射到 Iconica TDesign 图标与家族配色。资源管理器树与对比视图的文件列
/// 共用同一份映射，避免复制扩展名判断；该映射只决定显示的图形与颜色，不参与文件格式判定、
/// 打开方式路由或分区归属。</para>
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
            return new(AppIconKind.File, ExplorerIconPalette.Document);
        }

        if (isDirectory)
        {
            return isProjectFolder
                ? new(AppIconKind.ProjectFolder, ExplorerIconPalette.Folder)
                : new(AppIconKind.Folder, ExplorerIconPalette.Folder);
        }

        if (DatFileSet.IsTransferList(path))
        {
            return new(AppIconKind.FileTransfer, ExplorerIconPalette.Image);
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();

        if (ImageFileExtensions.Contains(extension))
        {
            return new(AppIconKind.HardDisk, ExplorerIconPalette.Image);
        }

        return extension switch
        {

            ".dat" =>
                new(AppIconKind.HardDisk, ExplorerIconPalette.Image),

            ".elf" or ".el" or ".so" or ".ko" or ".mbn" or ".melf" =>
                new(AppIconKind.Chip, ExplorerIconPalette.Firmware),

            ".dtb" or ".dtbo" or ".dts" or ".dtsi" =>
                new(AppIconKind.DeviceTree, ExplorerIconPalette.DeviceTree),

            ".apk" or ".apks" or ".apkm" or ".xapk" or ".aab" =>
                new(AppIconKind.Android, ExplorerIconPalette.Android),

            ".dex" or ".odex" or ".vdex" or ".oat" or ".smali" or ".jar"
                or ".c" or ".h" or ".cc" or ".cpp" or ".hpp" or ".java" or ".kt" or ".kts" or ".aidl" =>
                new(AppIconKind.FileCode, ExplorerIconPalette.Code),

            ".arsc" => new(AppIconKind.Database, ExplorerIconPalette.Resource),

            ".axml" => new(AppIconKind.FileXml, ExplorerIconPalette.Markup),
            ".xml" => new(AppIconKind.Xml, ExplorerIconPalette.Markup),

            ".json" or ".jsonc" or ".jsonl" => new(AppIconKind.Json, ExplorerIconPalette.Json),

            ".prop" or ".rc" or ".te" or ".fsconfig" or ".fstab" or ".ini" or ".cfg" or ".conf" or ".toml" =>
                new(AppIconKind.FileCog, ExplorerIconPalette.Document),

            ".sh" or ".bash" or ".bat" or ".cmd" or ".ps1" or ".psm1" or ".py" or ".pl" or ".rb" or ".lua" =>
                new(AppIconKind.Script, ExplorerIconPalette.Script),

            ".zip" or ".7z" or ".rar" => new(AppIconKind.Zip, ExplorerIconPalette.Archive),
            ".tar" or ".tgz" or ".gz" or ".xz" or ".bz2" or ".zst" or ".lz4" or ".lzma" or ".lz" or ".br" or ".cpio" =>
                new(AppIconKind.Archive, ExplorerIconPalette.Archive),

            ".pac" or ".ops" or ".nb0" or ".ofp" or ".ozip" or ".kdz" or ".ftf" =>
                new(AppIconKind.Package, ExplorerIconPalette.Firmware),

            ".pem" or ".der" or ".crt" or ".cer" or ".cert" or ".x509" =>
                new(AppIconKind.Certificate, ExplorerIconPalette.Security),
            ".key" or ".pk8" or ".jks" or ".keystore" or ".p12" or ".pfx" or ".avbpubkey" =>
                new(AppIconKind.Key, ExplorerIconPalette.Security),

            ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".ico" or ".svg" =>
                new(AppIconKind.Image, ExplorerIconPalette.Media),
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" or ".3gp" or ".flv" =>
                new(AppIconKind.Movie, ExplorerIconPalette.Media),
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac" or ".opus" =>
                new(AppIconKind.Music, ExplorerIconPalette.Media),

            ".md" or ".markdown" => new(AppIconKind.Markdown, ExplorerIconPalette.Document),
            ".csv" or ".tsv" => new(AppIconKind.Csv, ExplorerIconPalette.Document),
            ".pdf" => new(AppIconKind.Pdf, ExplorerIconPalette.Pdf),
            ".doc" or ".docx" => new(AppIconKind.Word, ExplorerIconPalette.Document),
            ".xls" or ".xlsx" => new(AppIconKind.Excel, ExplorerIconPalette.Document),
            ".ppt" or ".pptx" => new(AppIconKind.PowerPoint, ExplorerIconPalette.Document),
            ".log" or ".logcat" or ".adblog" =>
                new(AppIconKind.TextLines, ExplorerIconPalette.Document),
            ".txt" or ".text" or ".md5" or ".sha1" or ".sha256" =>
                new(AppIconKind.File, ExplorerIconPalette.Document),

            _ => new(AppIconKind.File, ExplorerIconPalette.Document)
        };
    }
}

/// <summary>
/// A glyph paired with its family colour, so the extension is not re-resolved on every property access.
/// <para>图标与家族配色的组合，避免每次访问属性时重复解析扩展名。</para>
/// </summary>
/// <param name="Kind">The semantic Iconica icon. <para>Iconica 语义图标。</para></param>
/// <param name="Brush">The icon colour. <para>图标颜色。</para></param>
public readonly record struct ExplorerIcon(AppIconKind Kind, IBrush Brush);