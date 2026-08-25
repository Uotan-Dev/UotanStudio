using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

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
            TypeKey = "Data.ReadFile"
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
            TypeKey = "Data.Convert"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Input", PortType = NodePortType.Data });
        node.Input.Add(new NodeConnectorViewModel { Title = "Format", PortType = NodePortType.Text });
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
            TypeKey = "Data.Checksum"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Data", PortType = NodePortType.Data });
        node.Input.Add(new NodeConnectorViewModel { Title = "Algorithm", PortType = NodePortType.Text });
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
            TypeKey = "Data.Compare"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Data A", PortType = NodePortType.Data });
        node.Input.Add(new NodeConnectorViewModel { Title = "Data B", PortType = NodePortType.Data });
        node.Output.Add(new NodeConnectorViewModel { Title = "Match", PortType = NodePortType.Boolean });
        return node;
    }

    /// <summary>
    /// <para>执行与节点标题对应的数据处理逻辑：Read File 读取文件、Format Convert 做格式转换、Checksum 计算哈希、Compare 比较两段数据。</para>
    /// Executes the data-processing logic matching the node title: Read File reads a file, Format Convert performs conversion,
    /// Checksum computes a hash, and Compare compares two data chunks.
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
        var token = context.CancellationToken;
        switch (Title)
        {
            case "Read File":
                ExecuteReadFile(context);
                break;
            case "Format Convert":
                ExecuteConvert(context);
                break;
            case "Checksum":
                ExecuteChecksum(context);
                break;
            case "Compare":
                ExecuteCompare(context);
                break;
            default:
                return base.ExecuteAsync(context);
        }

        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void ExecuteReadFile(INodeExecutionContext context)
    {
        var path = Convert.ToString(ReadInput("Path"), System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            context.Log.Write(LogLevel.Warn, Title, $"Path not found: {path ?? "<empty>"}  找不到路径。");
            return;
        }

        var bytes = File.ReadAllBytes(path);
        WriteOutput("Data", bytes);
        context.Log.Write(LogLevel.Info, Title, $"Read {bytes.Length} bytes from '{path}'  已读取。");
    }

    private void ExecuteConvert(INodeExecutionContext context)
    {
        var data = NodeDataUtil.ToBytes(ReadInput("Input"));
        if (data == null)
        {
            context.Log.Write(LogLevel.Warn, Title, "No Input data.  缺少输入数据。");
            return;
        }

        var format = (Convert.ToString(ReadInput("Format"), System.Globalization.CultureInfo.InvariantCulture) ?? "text").Trim().ToLowerInvariant();
        switch (format)
        {
            case "hex":
                WriteOutput("Output", Convert.ToHexString(data).ToLowerInvariant());
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
            case "bytes":
                WriteOutput("Output", data);
                context.Log.Write(LogLevel.Info, Title, $"Passed through {data.Length} bytes.  原样透传字节。");
                break;
            default:
                context.Log.Write(LogLevel.Warn, Title, $"Unsupported format '{format}'.  不支持的格式。");
                break;
        }
    }

    private void ExecuteChecksum(INodeExecutionContext context)
    {
        var data = NodeDataUtil.ToBytes(ReadInput("Data"));
        if (data == null)
        {
            context.Log.Write(LogLevel.Warn, Title, "No Data input.  缺少输入数据。");
            return;
        }

        var algorithm = (Convert.ToString(ReadInput("Algorithm"), System.Globalization.CultureInfo.InvariantCulture) ?? "sha256").Trim().ToLowerInvariant();
        var hash = ComputeHash(data, algorithm);
        if (hash == null)
        {
            context.Log.Write(LogLevel.Warn, Title, $"Unsupported algorithm '{algorithm}'.  不支持的算法。");
            return;
        }

        WriteOutput("Hash", hash);
        context.Log.Write(LogLevel.Info, Title, $"{algorithm}: {hash}  校验和已计算。");
    }

    private void ExecuteCompare(INodeExecutionContext context)
    {
        var a = NodeDataUtil.ToBytes(ReadInput("Data A"));
        var b = NodeDataUtil.ToBytes(ReadInput("Data B"));
        var match = a != null && b != null && CryptographicOperations.FixedTimeEquals(a, b);
        WriteOutput("Match", match);
        context.Log.Write(LogLevel.Info, Title, $"Compare result: {match}  比较结果。");
    }

    private static string? ComputeHash(byte[] data, string algorithm)
    {
        return algorithm switch
        {
            "md5" => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant(),
            "sha1" => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant(),
            "sha256" => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
            "sha384" => Convert.ToHexString(SHA384.HashData(data)).ToLowerInvariant(),
            "sha512" => Convert.ToHexString(SHA512.HashData(data)).ToLowerInvariant(),
            _ => null
        };
    }
}