namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// The kind of context menu a row offers: entry operations, compared transfers, text transfers or none.
/// <para>行右键菜单的类型：决定该行提供哪一套菜单项（条目操作 / 对比传输 / 文本传输 / 无菜单）。</para>
/// </summary>
public enum CompareRowMenuKind
{
    /// <summary>
    /// No context menu (hex rows, metadata rows and text rows while browsing).
    /// <para>无右键菜单（十六进制行、元数据行、浏览状态下的文本行）。</para>
    /// </summary>
    None,

    /// <summary>
    /// A file/folder entry while browsing: copy/move to the left or right column, delete, rename, create folder,
    /// copy the path and open in the file explorer.
    /// <para>浏览状态下的文件/文件夹条目：复制/移动到左边或右边的列、删除、重命名、新建文件夹、复制路径、
    /// 在文件资源管理器中打开。</para>
    /// </summary>
    BrowsingEntry,

    /// <summary>
    /// A file/folder entry while comparing: copy/move to the other side, delete, rename, create folder, copy the
    /// path and open in the file explorer.
    /// <para>对比状态下的文件/文件夹条目：复制/移动到另一边、删除、重命名、新建文件夹、复制路径、
    /// 在文件资源管理器中打开。</para>
    /// </summary>
    ComparedEntry,

    /// <summary>
    /// A text row inside a text comparison: copy the selection to the other side.
    /// <para>文本对比中的文本行：把选中内容复制到另一侧。</para>
    /// </summary>
    ComparedText,
}