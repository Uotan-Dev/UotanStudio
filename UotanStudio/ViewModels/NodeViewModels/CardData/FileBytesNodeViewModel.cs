using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Binary file write node view model. Publishes port data to a file path through a same-directory temporary
/// file, so a failed or cancelled write never replaces the existing destination.
/// <para>二进制文件写出节点视图模型。通过同目录临时文件把端口数据发布到目标路径，失败或被取消时不会替换既有目标。</para>
/// </summary>
public sealed class FileBytesNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// Creates a file write node with the specified display title.
    /// <para>创建具有指定显示标题的文件写出节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public FileBytesNodeViewModel(string title) : base(title, NodeCategory.Data)
    {
    }

    /// <summary>
    /// Creates the binary write node that publishes any supported port payload to one path.
    /// <para>创建把任意受支持端口载荷发布到单个路径的二进制写出节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static FileBytesNodeViewModel CreateWriteNode()
    {
        var node = new FileBytesNodeViewModel("Write Bytes")
        {
            Location = new Point(100, 100),
            TypeKey = "File.Bytes.Write"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Data", PortType = NodePortType.Data, IsRequired = true });
        node.Input.Add(new NodeConnectorViewModel { Title = "Path", PortType = NodePortType.Path, IsRequired = true });
        node.Output.Add(new NodeConnectorViewModel { Title = "Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Bytes", PortType = NodePortType.Number });
        return node;
    }

    /// <summary>
    /// Publishes the Data input to the Path input and reports the written length.
    /// <para>把 Data 输入发布到 Path 输入并回报写入长度。</para>
    /// </summary>
    /// <param name="context">The execution context. <para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution. <para>表示异步执行的任务。</para></returns>
    /// <exception cref="InvalidOperationException">The Data or Path input is missing. <para>缺少 Data 或 Path 输入。</para></exception>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        if (!string.Equals(TypeKey, "File.Bytes.Write", StringComparison.Ordinal))
        {
            await base.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var data = ReadInput("Data");
        if (data is null)
        {
            throw new InvalidOperationException("The Data input is required before writing bytes.");
        }

        var path = Convert.ToString(ReadInput("Path"), System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("The Path input is required before writing bytes.");
        }

        var length = await NodeArtifactPublisher.PublishAsync(data, path, context.CancellationToken).ConfigureAwait(false);
        WriteOutput("Path", path);
        WriteOutput("Bytes", (double)length);
        context.Log.Write(LogLevel.Info, Title, $"Wrote {length} bytes to '{path}'.  已写出字节。");
    }
}