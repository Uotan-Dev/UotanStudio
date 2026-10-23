using Avalonia.Media;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// Explorer node icons: directories are split into project folders and plain folders, and files are mapped by
/// extension to an Iconica TDesign icon and a family colour. The mapping only picks the glyph and colour shown
/// in the tree; file format detection, open-mode routing and partition ownership stay with <c>DocumentFormatPolicy</c>
/// and the format-handler registry.
/// <para>资源管理器节点的图标：目录按「项目文件夹 / 普通文件夹」区分，文件按扩展名映射到 Iconica TDesign
/// 图标与家族配色。该映射只决定树中显示的图形与颜色，不参与文件格式判定、打开方式路由或分区归属，
/// 那些规则仍由 <c>DocumentFormatPolicy</c> 与格式处理器注册表维护。</para>
/// </summary>
public sealed partial class ExplorerNode
{
    /// <summary>
    /// The icon shown for this node in the explorer, matching the flat look of the VS Solution Explorer.
    /// <para>节点在资源管理器中显示的图标，与 VS 解决方案管理器的扁平风格一致。</para>
    /// </summary>
    public AppIconKind IconKind => Icon.Kind;

    /// <summary>
    /// The node icon colour, taken from <see cref="ExplorerIconPalette"/> by file family.
    /// <para>节点图标颜色，按文件家族取自 <see cref="ExplorerIconPalette"/>。</para>
    /// </summary>
    public IBrush IconBrush => Icon.Brush;

    private ExplorerIcon? _icon;
    private ExplorerIcon Icon => _icon ??= ExplorerIconCatalog.Resolve(Path, IsDirectory, IsProjectFolder, IsPlaceholder);
}