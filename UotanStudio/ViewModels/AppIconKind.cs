namespace UotanStudio.ViewModels;

/// <summary>
/// Semantic icon identifiers for icons chosen at runtime (explorer nodes, compare rows, generated menus and
/// split-pane controls). Each identifier maps to one native Irihi Iconica TDesign icon in
/// <see cref="Views.Controls.AppIconFactory"/>; static XAML uses the strongly-typed Iconica controls directly.
/// <para>供运行时选择图标（资源管理器节点、对比行、生成菜单与分栏控件）使用的语义图标标识。
/// 每个标识在 <see cref="Views.Controls.AppIconFactory"/> 中映射到一个 Irihi Iconica TDesign 原生图标；
/// 静态 XAML 直接使用强类型的 Iconica 控件。</para>
/// </summary>
public enum AppIconKind
{
    File,
    Folder,
    ProjectFolder,
    FileTransfer,
    HardDisk,
    Chip,
    Android,
    DeviceTree,
    FileCode,
    Database,
    FileXml,
    Xml,
    Json,
    FileCog,
    Script,
    Zip,
    Archive,
    Package,
    Certificate,
    Key,
    Image,
    Movie,
    Music,
    Markdown,
    Csv,
    Pdf,
    Word,
    Excel,
    PowerPoint,
    TextLines,
    FolderSearch,
    RadioTower,
    CodeBraces,
    Float,
    FloatAll,
    Show,
    Pin,
    PinOff,
    Tab,
    CloseOthers,
    CloseAll,
    Close,
    TabsLeft,
    TabsTop,
    TabsRight,
    DragHandle,
    SplitHorizontal,
    SplitVertical,
}