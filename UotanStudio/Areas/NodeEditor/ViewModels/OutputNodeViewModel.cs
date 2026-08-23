using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;

namespace UotanStudio.Areas.NodeEditor.ViewModels;

/// <summary>
/// <para>输出节点视图模型，提供日志输出、进度报告和结果保存功能。</para>
/// Output node view model, providing log output, progress report, and result saving capabilities.
/// <para>可选通过构造函数注入 <see cref="LogViewModel"/>，运行时将日志写入面板。</para>
/// Optionally injects a <see cref="LogViewModel"/> via the constructor; runtime writes logs to the panel.
/// </summary>
/// <remarks>
/// <para>创建具有指定标题的输出节点。</para>
/// Creates an output node with the specified title.
/// </remarks>
/// <param name="title">
/// <para>节点的显示标题。</para>
/// The display title of the node.
/// </param>
public class OutputNodeViewModel(string title) : FlowNodeViewModel(title, NodeCategory.Output)
{

    /// <summary>
    /// <para>执行该输出节点的真实操作，依据节点标题分派到 Log/Progress/Save 逻辑。</para>
    /// Executes the real operation of this output node, dispatching to Log/Progress/Save logic based on the node title.
    /// </summary>
    /// <param name="messageOrData">日志消息（Log）、进度数值（Progress）或结果数据（Save）。</param>
    /// <param name="levelOrStatusOrPath">日志级别（Log）、状态文本（Progress）或保存路径（Save）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果摘要文本。</returns>
    public async Task<string> ExecuteAsync(string messageOrData, string levelOrStatusOrPath, CancellationToken cancellationToken)
    {
        switch (Title)
        {
            case "Log Output":
                {
                    var level = string.IsNullOrEmpty(levelOrStatusOrPath) ? "INFO" : levelOrStatusOrPath.ToUpperInvariant();
                    AppendLog(level, "Node", messageOrData ?? string.Empty);
                    return $"[Log Output] OK: {messageOrData}";
                }
            case "Progress Report":
                {
                    var status = string.IsNullOrEmpty(levelOrStatusOrPath) ? "running" : levelOrStatusOrPath;
                    AppendLog("INFO", "Progress", $"{messageOrData}% — {status}");
                    return $"[Progress Report] OK: {messageOrData}% ({status})";
                }
            case "Save Result":
                {
                    var path = levelOrStatusOrPath;
                    if (string.IsNullOrEmpty(path))
                    {
                        return "[Save Result] FAIL: save path is empty.";
                    }

                    var data = messageOrData ?? string.Empty;
                    await System.IO.File.WriteAllTextAsync(path, data, cancellationToken).ConfigureAwait(false);
                    return $"[Save Result] OK: saved {data.Length} chars to {path}";
                }
            default:
                return $"[Output] Unknown node: {Title}";
        }
    }

    /// <summary>
    /// <para>将一条日志写入统一日志服务（<see cref="LogService"/>）。</para>
    /// Writes a log entry to the unified log service (<see cref="LogService"/>).
    /// </summary>
    private static void AppendLog(string level, string source, string message)
    {
        var parsed = level?.ToUpperInvariant() switch
        {
            "TRACE" or "T" or "VERBOSE" or "V" => LogLevel.Trace,
            "DEBUG" or "D" => LogLevel.Debug,
            "WARN" or "W" or "WARNING" => LogLevel.Warn,
            "ERROR" or "E" => LogLevel.Error,
            "FATAL" or "F" => LogLevel.Fatal,
            _ => LogLevel.Info,
        };
        LogService.Instance.Write(parsed, source, message);
    }

    /// <summary>
    /// <para>创建默认的日志输出节点。</para>
    /// Creates a default log output node.
    /// </summary>
    public static OutputNodeViewModel CreateLogNode()
    {
        var node = new OutputNodeViewModel("Log Output")
        {
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "message" });
        node.Input.Add(new NodeConnectorViewModel { Title = "Level" });
        return node;
    }

    /// <summary>
    /// <para>创建默认的进度报告节点。</para>
    /// Creates a default progress report node.
    /// </summary>
    public static OutputNodeViewModel CreateProgressNode()
    {
        var node = new OutputNodeViewModel("Progress Report")
        {
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Progress" });
        node.Input.Add(new NodeConnectorViewModel { Title = "Status" });
        return node;
    }

    /// <summary>
    /// <para>创建默认的结果保存节点。</para>
    /// Creates a default result save node.
    /// </summary>
    public static OutputNodeViewModel CreateSaveNode()
    {
        var node = new OutputNodeViewModel("Save Result")
        {
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Data" });
        node.Input.Add(new NodeConnectorViewModel { Title = "Path" });
        return node;
    }
}