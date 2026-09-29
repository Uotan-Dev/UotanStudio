namespace UotanStudio.Messages;

/// <summary>
/// <para>打开节点项目请求消息，当资源管理器请求把解决方案登记的节点项目载入节点编辑器画布时发送。</para>
/// Open node project request message, sent when a panel such as the project explorer asks the node editor to
/// load a node project registered by the solution into its canvas.
/// </summary>
public class OpenNodeProjectRequestMessage
{
    /// <summary>
    /// <para>获取要打开的节点项目名称。</para>
    /// Gets the name of the node project to open.
    /// </summary>
    public string NodeProjectName { get; }

    /// <summary>
    /// <para>创建打开节点项目请求消息。</para>
    /// Creates an open node project request message.
    /// </summary>
    /// <param name="nodeProjectName">
    /// <para>解决方案中已登记的节点项目名称。</para>
    /// The name registered in the solution for the node project.
    /// </param>
    public OpenNodeProjectRequestMessage(string nodeProjectName) => NodeProjectName = nodeProjectName;
}