namespace UotanStudio.Messages;

/// <summary>
/// <para>打开节点项目请求消息，当资源管理器请求把解决方案登记的节点项目载入节点编辑器画布时发送。</para>
/// Open node project request message, sent when a panel such as the project explorer asks the node editor to
/// load a node project registered by the solution into its canvas.
/// </summary>
public record OpenNodeProjectRequestMessage(string NodeProjectName);