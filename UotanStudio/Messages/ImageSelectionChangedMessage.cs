namespace UotanStudio.Messages;

/// <summary>
/// Message announcing that the selected image entry changed.
/// <para>宣告选中的镜像条目发生变化的消息。</para>
/// </summary>
/// <param name="Image">
/// The image entry now selected, or <see langword="null"/> when the selection was cleared.
/// <para>当前选中的镜像条目，选中被清空时为空。</para>
/// </param>
public record ImageSelectionChangedMessage(UotanStudio.ViewModels.Workspace.ImageEntryViewModel? Image);