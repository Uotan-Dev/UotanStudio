using UotanStudio.ViewModels.Compare;

namespace UotanStudio.Messages;

/// <summary>
/// <para>对比视图行选中变更消息，当对比视图中选中的文件/文件夹行发生变化时发送，
/// 供属性面板等订阅方刷新显示该条目详情。</para>
/// Compare-view row selection changed message, sent when the selected file/folder row in the compare
/// view changes, allowing subscribers such as the properties panel to refresh the entry details.
/// </summary>
public class CompareSelectionChangedMessage
{
    /// <summary>
    /// <para>获取选中条目的绝对路径；<c>null</c> 表示已清空选择。</para>
    /// Gets the selected entry's absolute path; <c>null</c> means the selection was cleared.
    /// </summary>
    public string? Path { get; }

    /// <summary>
    /// <para>获取选中条目是否为目录。</para>
    /// Gets whether the selected entry is a directory.
    /// </summary>
    public bool IsDirectory { get; }

    /// <summary>
    /// <para>创建对比视图行选中变更消息。</para>
    /// Creates a compare-view row selection changed message.
    /// </summary>
    /// <param name="path">
    /// <para>选中条目的绝对路径；<c>null</c> 表示清空选择。</para>
    /// The selected entry's absolute path; <c>null</c> clears the selection.
    /// </param>
    /// <param name="isDirectory">
    /// <para>是否为目录。</para>
    /// Whether the entry is a directory.
    /// </param>
    public CompareSelectionChangedMessage(string? path, bool isDirectory)
    {
        Path = path;
        IsDirectory = isDirectory;
    }
}
