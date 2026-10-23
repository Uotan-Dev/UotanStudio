using System.Globalization;
using Avalonia;
using UotanStudio.Compress;
using UotanStudio.Services;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// Decompress node view model supporting GZip and Deflate decompression algorithms.
/// <para>解压节点视图模型：支持 GZip 与 Deflate 两种解压算法。</para>
/// </summary>
public class DecompressNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// Creates a decompress node with the specified title.
    /// <para>创建具有指定标题的解压节点。</para>
    /// </summary>
    /// <param name="title">
    /// The display title of the node.
    /// <para>节点的显示标题。</para>
    /// </param>
    public DecompressNodeViewModel(string title) : base(title, NodeCategory.Data)
    {
    }

    /// <summary>
    /// Creates a default data decompress node that decompresses the input data.
    /// <para>创建默认的数据解压节点，解压输入数据。</para>
    /// </summary>
    public static DecompressNodeViewModel CreateDecompressNode()
    {
        var node = new DecompressNodeViewModel("Decompress")
        {
            Location = new Point(100, 100),
            TypeKey = "Data.Compression.Decompress"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Data", PortType = NodePortType.Data });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Algorithm",
            PortType = NodePortType.Text,
            InitialValue = "gzip",
            Choices =
            [
                .. new[]
                {
                    "gzip", "zlib", "deflate", "brotli", "lz4", "lz4legacy",
                    "lzma", "xz", "bzip2", "zstd", "lzop"
                }.Select(value => new NodePortChoice(value))
            ]
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Data", PortType = NodePortType.Data });
        return node;
    }

    /// <summary>
    /// Dispatches decompression logic by the node type key (TypeKey): Data.Compression.Decompress decompresses the input data.
    /// <para>按节点类型键（TypeKey）分发对应的解压逻辑：Data.Compression.Decompress 解压输入数据。</para>
    /// </summary>
    /// <param name="context">
    /// The execution context.
    /// <para>执行上下文。</para>
    /// </param>
    /// <returns>
    /// A task representing the asynchronous execution.
    /// <para>表示异步执行的任务。</para>
    /// </returns>
    public override Task ExecuteAsync(INodeExecutionContext context)
    {
        switch (TypeKey)
        {
            case "Data.Compression.Decompress":
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
        var data = NodeDataUtil.ToBytes(ReadInput("Data"), context.CancellationToken);
        if (data == null || data.Length == 0)
        {
            throw new InvalidDataException("No compressed data was provided.");
        }

        var algorithm = (Convert.ToString(ReadInput("Algorithm"), CultureInfo.InvariantCulture) ?? "gzip").Trim().ToLowerInvariant();
        var format = ExtendedDataNodeViewModel.ResolveCompressionFormat(algorithm);
        var decompressed = DecompressWith(data, mode => CompressionService.CreateDecompressionStream(mode, format), context.CancellationToken);
        WriteOutput("Data", decompressed);
        context.Log.Write(LogLevel.Info, Title, $"Decompressed {data.Length} -> {decompressed.Length} bytes ({algorithm}).  已解压。");
    }

    private static byte[] DecompressWith(byte[] data, Func<Stream, Stream> factory, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();
        using (var stream = factory(input))
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (output.Length + read > NodeDataUtil.MaxInMemoryBytes)
                {
                    throw new InvalidDataException("The decompressed data exceeds the in-memory node limit.");
                }
                output.Write(buffer, 0, read);
            }
        }

        return output.ToArray();
    }
}