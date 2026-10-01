using UotanStudio.ViewModels.Compare;

namespace UotanStudio.Messages;

/// <summary>
/// <para>对比视图行选中变更消息，当对比视图中选中的文件/文件夹行发生变化时发送，
/// 供属性面板等订阅方刷新显示该条目详情。</para>
/// Compare-view row selection changed message, sent when the selected file/folder row in the compare
/// view changes, allowing subscribers such as the properties panel to refresh the entry details.
/// </summary>
public record CompareSelectionChangedMessage(string? Path, bool IsDirectory);