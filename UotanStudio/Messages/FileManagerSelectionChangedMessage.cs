using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Messages;

/// <summary>
/// <para>文件管理器内部条目选中变更消息，当文件管理器编辑器中选中的文件/目录条目发生变化时发送，
/// 供属性面板等订阅方刷新显示条目详情。</para>
/// File-manager entry selection changed message, sent when the selected file/directory entry inside
/// the file-manager editor changes, allowing subscribers such as the properties panel to refresh.
/// </summary>
public record FileManagerSelectionChangedMessage(FileManagerEntryViewModel? Entry, FileManagerEditorViewModel Document);