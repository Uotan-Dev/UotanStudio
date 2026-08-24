namespace UotanStudio.Messages;

/// <summary>
/// Selection changed message, broadcasting the currently selected item across panels.
/// <para>选中变化消息，向各面板广播当前选中对象。</para>
/// </summary>
public class SelectionChangedMessage(object? selectedItem)
{
    /// <summary>
    /// Gets the selected item; null when the selection is cleared.
    /// <para>选中的对象；清空选中时为 null。</para>
    /// </summary>
    public object? SelectedItem { get; } = selectedItem;
}