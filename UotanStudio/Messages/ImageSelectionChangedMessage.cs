namespace UotanStudio.Messages;

/// <summary>
/// <para>镜像选中变更消息，当镜像面板中选中的镜像条目发生变化时发送，
/// 供属性面板等订阅方刷新显示镜像详细参数。</para>
/// Image selection changed message, sent when the selected image entry in the image panel changes,
/// allowing subscribers such as the properties panel to refresh and display detailed image parameters.
/// </summary>
public record ImageSelectionChangedMessage(UotanStudio.ViewModels.Workspace.ImageEntryViewModel? Image);