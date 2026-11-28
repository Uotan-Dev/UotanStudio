using Avalonia.Media;
using UotanStudio.Services;

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
    /// Resolves the bundled PNG asset name used by the in-app file icon for an extension. The mapping mirrors
    /// the OS file-association icons so Explorer and the shell show the same artwork.
    /// <para>解析扩展名对应的随包 PNG 资源名，供应用内文件图标使用；映射与系统文件关联图标一致，
    /// 使资源管理器与系统外壳显示相同的图案。</para>
    /// </summary>
    /// <param name="extension">A dotted extension, case-insensitive. <para>带点的扩展名，不区分大小写。</para></param>
    /// <returns>The PNG asset base name, or null when the extension has no dedicated icon.
    /// <para>PNG 资源基名；扩展名没有专用图标时为 null。</para></returns>
    public static string? FormatIconName(string extension) => FileAssociationService.IconNameFor(extension);

    /// <summary>
    /// Resolves a node icon: files use the single generic file glyph regardless of their format,
    /// while directories split into project folders and plain folders.
    /// <para>解析节点图标：文件不再按格式区分，统一使用单一文件图标；目录按「项目文件夹 / 普通文件夹」区分。</para>
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

        return new(AppIconKind.File, ExplorerIconPalette.Document);
    }
}

/// <summary>
/// A glyph paired with its family colour, so the extension is not re-resolved on every property access.
/// <para>图标与家族配色的组合，避免每次访问属性时重复解析扩展名。</para>
/// </summary>
/// <param name="Kind">The semantic Iconica icon. <para>Iconica 语义图标。</para></param>
/// <param name="Brush">The icon colour. <para>图标颜色。</para></param>
public readonly record struct ExplorerIcon(AppIconKind Kind, IBrush Brush);