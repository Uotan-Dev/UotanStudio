using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;

namespace UotanStudio.Areas.NodeEditor.ViewModels;

/// <summary>
/// <para>数据处理节点视图模型，提供文件读取、格式转换、校验和与比较功能。</para>
/// Data processing node view model, providing file reading, format conversion, checksum, and comparison capabilities.
/// <para>执行逻辑通过 FirmwareService 接入 SHA256 校验真实后端；Read/Compare 直接调用系统文件 API。</para>
/// Execution logic connects to SHA256 verification via FirmwareService; Read/Compare directly call system file APIs.
/// </summary>
/// <remarks>
/// <para>创建具有指定标题的数据处理节点。</para>
/// Creates a data processing node with the specified title.
/// </remarks>
/// <param name="title">
/// <para>节点的显示标题。</para>
/// The display title of the node.
/// </param>
public class DataNodeViewModel(string title) : FlowNodeViewModel(title, NodeCategory.Data)
{

    /// <summary>
    /// <para>执行该数据节点的真实操作，依据节点标题分派到对应文件/校验/比较逻辑。</para>
    /// Executes the real operation of this data node, dispatching to file/checksum/compare logic based on the node title.
    /// </summary>
    /// <param name="pathOrInput">文件路径（Read File / Checksum / Compare 的 Data A）或输入数据（Convert）。</param>
    /// <param name="formatOrAlgorithm">格式（Convert）或算法（Checksum，留空则 SHA256）。</param>
    /// <param name="dataB">比较节点的第二份数据（文件路径）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果摘要文本；结果数据也会写回节点的 Output 端口 Value。</returns>
    public async Task<string> ExecuteAsync(string pathOrInput, string formatOrAlgorithm, string dataB, CancellationToken cancellationToken)
    {
        try
        {
            switch (Title)
            {
                case "Read File":
                    {
                        if (string.IsNullOrEmpty(pathOrInput))
                        {
                            return "[Read File] FAIL: path is empty.";
                        }

                        if (!File.Exists(pathOrInput))
                        {
                            return $"[Read File] FAIL: file not found: {pathOrInput}";
                        }

                        var bytes = await File.ReadAllBytesAsync(pathOrInput, cancellationToken).ConfigureAwait(false);
                        var summary = $"[Read File] OK: {bytes.Length} bytes from {pathOrInput}";
                        if (Output.Count > 0)
                        {
                            Output[0].Value = $"{bytes.Length} bytes";
                        }

                        return summary;
                    }
                case "Format Convert":
                    {
                        if (Output.Count > 0)
                        {
                            Output[0].Value = pathOrInput ?? string.Empty;
                        }

                        return "[Format Convert] WAIT: format conversion backend not yet implemented.";
                    }
                case "Checksum":
                    {
                        if (string.IsNullOrEmpty(pathOrInput))
                        {
                            return "[Checksum] FAIL: data/path is empty.";
                        }

                        var result = await FirmwareService.Instance.VerifyChecksumAsync(pathOrInput, expectedHash: formatOrAlgorithm, cancellationToken).ConfigureAwait(false);
                        var summary = result.Success ? $"[Checksum] OK: {result.Summary}" : $"[Checksum] FAIL: {result.Summary}";
                        if (Output.Count > 0)
                        {
                            Output[0].Value = result.Summary;
                        }

                        return summary;
                    }
                case "Compare":
                    {
                        if (string.IsNullOrEmpty(pathOrInput) || string.IsNullOrEmpty(dataB))
                        {
                            return "[Compare] FAIL: both data A and B are required.";
                        }

                        if (!File.Exists(pathOrInput) || !File.Exists(dataB))
                        {
                            return "[Compare] FAIL: one or both files not found.";
                        }

                        var a = await File.ReadAllBytesAsync(pathOrInput, cancellationToken).ConfigureAwait(false);
                        var b = await File.ReadAllBytesAsync(dataB, cancellationToken).ConfigureAwait(false);
                        var match = a.Length == b.Length && a.SequenceEqual(b);
                        if (Output.Count > 0)
                        {
                            Output[0].Value = match ? "1" : "0";
                        }

                        return match ? "[Compare] OK: files are identical." : "[Compare] OK: files differ.";
                    }
                default:
                    return $"[Data] Unknown node: {Title}";
            }
        }
        catch (System.Exception ex)
        {
            return $"[{Title}] FAIL: {ex.Message}";
        }
    }

    /// <summary>
    /// <para>创建默认的文件读取节点。</para>
    /// Creates a default file read node.
    /// </summary>
    public static DataNodeViewModel CreateFileReadNode()
    {
        var node = new DataNodeViewModel("Read File")
        {
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Path" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Data" });
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
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Input" });
        node.Input.Add(new NodeConnectorViewModel { Title = "Format" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Output" });
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
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Data" });
        node.Input.Add(new NodeConnectorViewModel { Title = "Algorithm" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Hash" });
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
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Data A" });
        node.Input.Add(new NodeConnectorViewModel { Title = "Data B" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Match" });
        return node;
    }
}