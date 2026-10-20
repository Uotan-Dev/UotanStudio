using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>
/// Offers resource identifier ports a selection over the project resources published through
/// <see cref="NodePortChoiceSource"/>, so a mistyped identifier surfaces in the editor instead of at execution time.
/// The port keeps <see cref="NodePortType.Text"/> and the candidate list is attached on top of it, because a text
/// port owns no dedicated selection editor: an identifier outside the published list stays typable and is validated
/// when the node runs.
/// <para>为资源标识端口提供经 <see cref="NodePortChoiceSource"/> 发布的项目资源选择项，使拼错的标识在编辑器内就能发现，
/// 而不是等到执行期。端口保持 <see cref="NodePortType.Text"/>，候选列表附加在其上，因为文本端口没有专门的选择编辑器：
/// 发布列表之外的标识仍可填写，并在节点运行时校验。</para>
/// </summary>
public static class NodeResourceIdChoices
{
    /// <summary>
    /// Attaches the process-level candidates to the port and keeps it current while the port is bound; nothing is
    /// attached when no candidate has been published, so a project without resources keeps the plain text editor.
    /// <para>把进程级候选附加到端口，并在端口绑定期间保持其最新；尚未发布任何候选时不附加，没有资源的项目仍使用纯文本编辑器。</para>
    /// </summary>
    /// <param name="port">The resource identifier port, whose title identifies the resource it reads.
    /// <para>资源标识端口，其标题标识它读取的资源。</para></param>
    public static void Apply(NodeConnectorViewModel port)
    {
        ArgumentNullException.ThrowIfNull(port);
        if (NodePortChoiceSource.Resources.Count > 0)
        {
            port.TrackResourceChoices();
        }
    }
}