using System.Globalization;
using System.IO.Compression;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>解压节点视图模型：支持 GZip 与 Deflate 两种解压算法。</para>
/// Decompress node view model supporting GZip and Deflate decompression algorithms.
/// </summary>
public class DecompressNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// <para>创建具有指定标题的解压节点。</para>
    /// Creates a decompress node with the specified title.
    /// </summary>
    /// <param name="title">
    /// <para>节点的显示标题。</para>
    /// The display title of the node.
    /// </param>
    public DecompressNodeViewModel(string title) : base(title, NodeCategory.Data)
    {
    }

    /// <summary>
    /// <para>创建默认的数据解压节点，解压输入数据。</para>
    /// Creates a default data decompress node that decompresses the input data.
    /// </summary>
    public static DecompressNodeViewModel CreateDecompressNode()
    {
        var node = new DecompressNodeViewModel("Decompress")
        {
            Location = new Point(100, 100),
            TypeKey = "Data.Decompress"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Data", PortType = NodePortType.Data });
        node.Input.Add(new NodeConnectorViewModel { Title = "Algorithm", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Data", PortType = NodePortType.Data });
        return node;
    }

    /// <summary>
    /// <para>按节点类型键（TypeKey）分发对应的解压逻辑：Data.Decompress 解压输入数据。</para>
    /// Dispatches decompression logic by the node type key (TypeKey): Data.Decompress decompresses the input data.
    /// </summary>
    /// <param name="context">
    /// <para>执行上下文。</para>
    /// The execution context.
    /// </param>
    /// <returns>
    /// <para>表示异步执行的任务。</para>
    /// A task representing the asynchronous execution.
    /// </returns>
    public override Task ExecuteAsync(INodeExecutionContext context)
    {
        switch (TypeKey)
        {
            case "Data.Decompress":
                ExecuteDecompress(context);
                break;
            default:
                return base.ExecuteAsync(context);
        }

        context.CancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void ExecuteDecompress(INodeExecutionContext context)
    {
        var data = NodeDataUtil.ToBytes(ReadInput("Data"));
        if (data == null || data.Length == 0)
        {
            context.Log.Write(LogLevel.Warn, Title, "No Data input.  缺少输入数据。");
            return;
        }

        var algorithm = (Convert.ToString(ReadInput("Algorithm"), CultureInfo.InvariantCulture) ?? "gzip").Trim().ToLowerInvariant();

        byte[] decompressed;
        try
        {
            decompressed = algorithm switch
            {
                "deflate" => DecompressWith(data, mode => new DeflateStream(mode, CompressionMode.Decompress)),
                _ => DecompressWith(data, mode => new GZipStream(mode, CompressionMode.Decompress))
            };
        }
        catch (InvalidDataException ex)
        {
            context.Log.Write(LogLevel.Warn, Title, $"Decompression failed: {ex.Message}  解压失败。");
            return;
        }

        WriteOutput("Data", decompressed);
        context.Log.Write(LogLevel.Info, Title, $"Decompressed {data.Length} -> {decompressed.Length} bytes ({algorithm}).  已解压。");
    }

    private static byte[] DecompressWith(byte[] data, Func<Stream, Stream> factory)
    {
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();
        using (var stream = factory(input))
        {
            stream.CopyTo(output);
        }

        return output.ToArray();
    }
}