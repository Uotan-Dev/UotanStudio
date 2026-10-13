using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Messages;

/// <summary>
/// Message announcing that the entry selected inside a file manager document changed.
/// <para>宣告文件管理器文档内选中的条目发生变化的消息。</para>
/// </summary>
/// <param name="Entry">
/// The file or directory entry now selected, or <see langword="null"/> when the selection was cleared.
/// <para>当前选中的文件或目录条目，选中被清空时为空。</para>
/// </param>
/// <param name="Document">
/// The file manager document the selected entry belongs to.
/// <para>选中条目所属的文件管理器文档。</para>
/// </param>
public record FileManagerSelectionChangedMessage(FileManagerEntryViewModel? Entry, FileManagerEditorViewModel Document);