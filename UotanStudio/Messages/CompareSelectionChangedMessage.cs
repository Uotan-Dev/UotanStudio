namespace UotanStudio.Messages;

/// <summary>
/// Message announcing that the selected row of a comparison result changed.
/// <para>宣告比较结果中的选中行发生变化的消息。</para>
/// </summary>
/// <param name="Path">
/// The file or directory path the row stands for, or <see langword="null"/> when no row is selected.
/// <para>该行对应的文件或目录路径，未选中行时为空。</para>
/// </param>
/// <param name="IsDirectory">
/// Whether the selected row stands for a directory rather than a single file.
/// <para>选中的行代表目录而非单个文件时为真。</para>
/// </param>
public record CompareSelectionChangedMessage(string? Path, bool IsDirectory);