namespace UotanStudio.Messages;

/// <summary>
/// <para>镜像选中变更消息，当镜像面板中选中的镜像条目发生变化时发送，
/// 供属性面板等订阅方刷新显示镜像详细参数。</para>
/// Image selection changed message, sent when the selected image entry in the image panel changes,
/// allowing subscribers such as the properties panel to refresh and display detailed image parameters.
/// </summary>
public class ImageSelectionChangedMessage
{
    /// <summary>
    /// <para>获取当前选中的镜像条目；<c>null</c> 表示已清空选择。</para>
    /// Gets the currently selected image entry; <c>null</c> means the selection was cleared.
    /// </summary>
    public UotanStudio.ViewModels.Workspace.ImageEntryViewModel? Image { get; }

    /// <summary>
    /// <para>创建镜像选中变更消息。</para>
    /// Creates an image selection changed message.
    /// </summary>
    /// <param name="image">
    /// <para>当前选中的镜像条目；<c>null</c> 表示清空选择。</para>
    /// The currently selected image entry; <c>null</c> clears the selection.
    /// </param>
    public ImageSelectionChangedMessage(UotanStudio.ViewModels.Workspace.ImageEntryViewModel? image)
    {
        Image = image;
    }
}