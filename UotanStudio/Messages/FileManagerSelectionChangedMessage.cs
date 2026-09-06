using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Messages;

/// <summary>
/// <para>文件管理器内部条目选中变更消息，当文件管理器编辑器中选中的文件/目录条目发生变化时发送，
/// 供属性面板等订阅方刷新显示条目详情。</para>
/// File-manager entry selection changed message, sent when the selected file/directory entry inside
/// the file-manager editor changes, allowing subscribers such as the properties panel to refresh.
/// </summary>
public class FileManagerSelectionChangedMessage
{
    /// <summary>
    /// <para>获取当前选中的文件系统条目；<c>null</c> 表示已清空选择。</para>
    /// Gets the currently selected filesystem entry; <c>null</c> means the selection was cleared.
    /// </summary>
    public FileManagerEntryViewModel? Entry { get; }

    /// <summary>
    /// <para>获取所属的文件管理器文档。</para>
    /// Gets the owning file-manager document.
    /// </summary>
    public FileManagerEditorViewModel Document { get; }

    /// <summary>
    /// <para>创建文件管理器条目选中变更消息。</para>
    /// Creates a file-manager entry selection changed message.
    /// </summary>
    /// <param name="entry">
    /// <para>当前选中的条目；<c>null</c> 表示清空选择。</para>
    /// The currently selected entry; <c>null</c> clears the selection.
    /// </param>
    /// <param name="document">
    /// <para>所属的文件管理器文档。</para>
    /// The owning file-manager document.
    /// </param>
    public FileManagerSelectionChangedMessage(FileManagerEntryViewModel? entry, FileManagerEditorViewModel document)
    {
        Entry = entry;
        Document = document;
    }
}