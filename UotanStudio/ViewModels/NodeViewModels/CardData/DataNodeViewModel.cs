using System.Security.Cryptography;
using System.Text;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Hashing;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>数据处理节点视图模型，提供文件读取、格式转换、校验和与比较功能。</para>
/// Data processing node view model, providing file reading, format conversion, checksum, and comparison capabilities.
/// </summary>
public class DataNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// <para>创建具有指定标题的数据处理节点。</para>
    /// Creates a data processing node with the specified title.
    /// </summary>
    /// <param name="title">
    /// <para>节点的显示标题。</para>
    /// The display title of the node.
    /// </param>
    public DataNodeViewModel(string title) : base(title, NodeCategory.Data)
    {
    }

    /// <summary>
    /// <para>创建默认的文件读取节点。</para>
    /// Creates a default file read node.
    /// </summary>
    public static DataNodeViewModel CreateFileReadNode()
    {
        var node = new DataNodeViewModel("Read File")
        {
            Location = new Point(100, 100),
            TypeKey = "File.Binary.Read"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Data", PortType = NodePortType.Data });
        return node;
    }

    /// <summary>
    /// <para>创建默认的格式转换节点。</para>
    /// Creates a default format conversion node.
    /// </summary>
    public static DataNodeViewModel CreateConvertNode()
    {
        var node = new DataNodeViewModel("Format Convert")
        {
            Location = new Point(100, 100),
            TypeKey = "Data.Value.Convert"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Input", PortType = NodePortType.Data });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Format",
            PortType = NodePortType.Text,
            InitialValue = "utf8",
            Choices = [.. new[] { "hex", "base64", "utf8", "text", "string" }.Select(value => new NodePortChoice(value))]
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Output", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的校验和节点。</para>
    /// Creates a default checksum node.
    /// </summary>
    public static DataNodeViewModel CreateChecksumNode()
    {
        var node = new DataNodeViewModel("Checksum")
        {
            Location = new Point(100, 100),
            TypeKey = "Validation.Data.Hash"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Data", PortType = NodePortType.Data });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Algorithm",
            PortType = NodePortType.Text,
            Choices = [.. new[] { "md5", "sha1", "sha256", "sha384", "sha512" }.Select(value => new NodePortChoice(value))],
            InitialValue = "sha256"
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Hash", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的比较节点。</para>
    /// Creates a default comparison node.
    /// </summary>
    public static DataNodeViewModel CreateCompareNode()
    {
        var node = new DataNodeViewModel("Compare")
        {
            Location = new Point(100, 100),
            TypeKey = "Data.Value.Compare"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Data A", PortType = NodePortType.Data });
        node.Input.Add(new NodeConnectorViewModel { Title = "Data B", PortType = NodePortType.Data });
        node.Output.Add(new NodeConnectorViewModel { Title = "Match", PortType = NodePortType.Boolean });
        return node;
    }

    /// <summary>
    /// <para>按节点类型键（TypeKey）分发对应的数据处理逻辑：File.Binary.Read 读取文件、Data.Value.Convert 做格式转换、
    /// Validation.Data.Hash 计算哈希、Data.Value.Compare 比较两段数据。</para>
    /// Dispatches data-processing logic by the node type key (TypeKey): File.Binary.Read reads a file, Data.Value.Convert performs
    /// conversion, Validation.Data.Hash computes a hash, and Data.Value.Compare compares two data chunks.
    /// </summary>
    /// <param name="context">
    /// <para>执行上下文。</para>
    /// The execution context.
    /// </param>
    /// <returns>
    /// <para>表示异步执行的任务。</para>
    /// A task representing the asynchronous execution.
    /// </returns>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        var token = context.CancellationToken;
        switch (TypeKey)
        {
            case "File.Binary.Read":
                await ExecuteReadFileAsync(context).ConfigureAwait(false);
                break;
            case "Data.Value.Convert":
                ExecuteConvert(context);
                break;
            case "Validation.Data.Hash":
                ExecuteChecksum(context);
                break;
            case "Data.Value.Compare":
                ExecuteCompare(context);
                break;
            default:
                await base.ExecuteAsync(context).ConfigureAwait(false);
                return;
        }

        token.ThrowIfCancellationRequested();
    }

    private async Task ExecuteReadFileAsync(INodeExecutionContext context)
    {
        var path = Convert.ToString(ReadInput("Path"), System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("File input was not found.", path);
        }

        var bytes = await NodeDataUtil.ReadFileBytesAsync(path, context.CancellationToken).ConfigureAwait(false);
        WriteOutput("Data", bytes);
        context.Log.Write(LogLevel.Info, Title, $"Read {bytes.Length} bytes from '{path}'  已读取。");
    }

    private void ExecuteConvert(INodeExecutionContext context)
    {
        var data = NodeDataUtil.ToBytes(ReadInput("Input"), context.CancellationToken);
        if (data == null)
        {
            throw new InvalidDataException("No Input data was provided.");
        }

        var format = (Convert.ToString(ReadInput("Format"), System.Globalization.CultureInfo.InvariantCulture) ?? "text").Trim().ToLowerInvariant();
        switch (format)
        {
            case "hex":
                WriteOutput("Output", Convert.ToHexStringLower(data));
                context.Log.Write(LogLevel.Info, Title, $"Converted {data.Length} bytes to hex.  已转换为十六进制。");
                break;
            case "base64":
                WriteOutput("Output", Convert.ToBase64String(data));
                context.Log.Write(LogLevel.Info, Title, $"Converted {data.Length} bytes to base64.  已转换为 Base64。");
                break;
            case "utf8":
            case "text":
            case "string":
                WriteOutput("Output", Encoding.UTF8.GetString(data));
                context.Log.Write(LogLevel.Info, Title, $"Decoded {data.Length} bytes to UTF-8 text.  已解码为文本。");
                break;
            default:
                throw new InvalidDataException($"Unsupported conversion format '{format}'.");
        }
    }

    private void ExecuteChecksum(INodeExecutionContext context)
    {
        var data = NodeDataUtil.ToBytes(ReadInput("Data"), context.CancellationToken);
        if (data == null)
        {
            throw new InvalidDataException("No Data input was provided.");
        }

        var algorithm = (Convert.ToString(ReadInput("Algorithm"), System.Globalization.CultureInfo.InvariantCulture) ?? "sha256").Trim().ToLowerInvariant();
        var hash = ComputeHash(data, algorithm);
        if (hash == null)
        {

            throw new InvalidDataException($"Unsupported hash algorithm '{algorithm}'.  不支持的哈希算法。");
        }

        WriteOutput("Hash", hash);
        context.Log.Write(LogLevel.Info, Title, $"{algorithm}: {hash}  校验和已计算。");
    }

    private void ExecuteCompare(INodeExecutionContext context)
    {
        var a = NodeDataUtil.ToBytes(ReadInput("Data A"), context.CancellationToken);
        var b = NodeDataUtil.ToBytes(ReadInput("Data B"), context.CancellationToken);
        if (a is null || b is null)
        {
            throw new InvalidDataException("Both comparison inputs are required.");
        }
        var match = CryptographicOperations.FixedTimeEquals(a, b);
        WriteOutput("Match", match);
        context.Log.Write(LogLevel.Info, Title, $"Compare result: {match}  比较结果。");
    }

    private static string? ComputeHash(byte[] data, string algorithm) => HashFormatter.ComputeHex(data, algorithm);
}