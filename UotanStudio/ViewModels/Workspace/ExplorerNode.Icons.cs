using Avalonia.Media;
using Material.Icons;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// Explorer node icons: directories are split into project folders and plain folders, files are mapped by
/// extension to an icon and a family colour. Types that have an outline variant in the Material icon set
/// always use it; only the few families without one (hard disk, chip, Android, XML, JSON, PDF) stay filled.
/// The mapping only picks the glyph and colour shown in the tree; file format detection, open-mode routing
/// and partition ownership stay with <c>FileOpenMode</c> and the format-handler registry.
/// <para>资源管理器节点的图标：目录按「项目文件夹 / 普通文件夹」区分，文件按扩展名映射到图标与家族配色。
/// 凡 Material 图标集提供空心（Outline）版本的类型一律使用空心版，只有硬盘、芯片、Android、XML、JSON、PDF
/// 等少数家族没有空心版时才保留实心图标。该映射只决定树中显示的图形与颜色，不参与文件格式判定、
/// 打开方式路由或分区归属，那些规则仍由 <c>FileOpenMode</c> 与格式处理器注册表维护。</para>
/// </summary>
public sealed partial class ExplorerNode
{
    /// <summary>
    /// The icon shown for this node in the explorer (outline variants preferred, matching the flat look
    /// of the VS Solution Explorer).
    /// <para>节点在资源管理器中显示的图标（空心版优先，与 VS 解决方案管理器的扁平风格一致）。</para>
    /// </summary>
    public MaterialIconKind IconKind => Icon.Kind;

    /// <summary>
    /// The node icon colour, taken from <see cref="ExplorerIconPalette"/> by file family.
    /// <para>节点图标颜色，按文件家族取自 <see cref="ExplorerIconPalette"/>。</para>
    /// </summary>
    public IBrush IconBrush => Icon.Brush;

    /// <summary>
    /// Whether this node is an image file; gates the explorer's "convert format" context-menu entry.
    /// <para>是否为镜像类文件，用作资源管理器右键「格式转换」入口的显示条件。</para>
    /// </summary>
    public bool IsImageFile => !IsDirectory && !IsPlaceholder &&
        ExplorerIconCatalog.IsImageExtension(Path);

    private ExplorerIcon? _icon;
    private ExplorerIcon Icon => _icon ??= ExplorerIconCatalog.Resolve(Path, IsDirectory, IsProjectFolder, IsPlaceholder);
}