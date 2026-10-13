namespace UotanStudio.Messages;

/// <summary>
/// Message requesting that a registered node project loads onto a node canvas.
/// <para>请求把已登记的节点项目载入节点画布的消息。</para>
/// </summary>
/// <param name="NodeProjectName">
/// The name the node project is registered under.
/// <para>节点项目的登记名称。</para>
/// </param>
public record OpenNodeProjectRequestMessage(string NodeProjectName);