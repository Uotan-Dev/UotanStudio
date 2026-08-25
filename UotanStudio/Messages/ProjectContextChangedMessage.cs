namespace UotanStudio.Messages;

/// <summary>
/// <para>工程上下文变更消息，当打开或关闭工程（目录布局变化）时发送，供项目资源管理器等订阅方刷新显示。</para>
/// Project context changed message, sent when a project is opened or closed (directory layout changed),
/// letting subscribers such as the project explorer refresh their display.
/// </summary>
public class ProjectContextChangedMessage
{
    /// <summary>
    /// <para>创建工程上下文变更消息。</para>
    /// Creates a project context changed message.
    /// </summary>
    public ProjectContextChangedMessage()
    {
    }
}