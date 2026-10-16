using System.Globalization;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Output node view model, providing log output, progress report, and result saving capabilities.
/// <para>输出节点视图模型，提供日志输出、进度报告和结果保存功能。</para>
/// </summary>
public class OutputNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// Creates an output node with the specified title.
    /// <para>创建具有指定标题的输出节点。</para>
    /// </summary>
    /// <param name="title">
    /// The display title of the node.
    /// <para>节点的显示标题。</para>
    /// </param>
    public OutputNodeViewModel(string title) : base(title, NodeCategory.Output)
    {
    }

    /// <summary>
    /// Creates a default log output node.
    /// <para>创建默认的日志输出节点。</para>
    /// </summary>
    public static OutputNodeViewModel CreateLogNode()
    {
        var node = new OutputNodeViewModel("Log Output")
        {
            Location = new Point(100, 100),
            TypeKey = "Feedback.Log.Write"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Message", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Level", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Stdout", PortType = NodePortType.Boolean, InitialValue = false });
        return node;
    }

    /// <summary>
    /// Creates a default progress report node.
    /// <para>创建默认的进度报告节点。</para>
    /// </summary>
    public static OutputNodeViewModel CreateProgressNode()
    {
        var node = new OutputNodeViewModel("Progress Report")
        {
            Location = new Point(100, 100),
            TypeKey = "Feedback.Progress.Report"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Progress", PortType = NodePortType.Number });
        node.Input.Add(new NodeConnectorViewModel { Title = "Status", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// Creates a default result save node.
    /// <para>创建默认的结果保存节点。</para>
    /// </summary>
    public static OutputNodeViewModel CreateSaveNode()
    {
        var node = new OutputNodeViewModel("Save Result")
        {
            Location = new Point(100, 100),
            TypeKey = "File.Artifact.Publish"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Data", PortType = NodePortType.Data });
        node.Input.Add(new NodeConnectorViewModel { Title = "Artifact", PortType = NodePortType.Artifact });
        node.Input.Add(new NodeConnectorViewModel { Title = "Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Artifact", PortType = NodePortType.Artifact });
        return node;
    }

    /// <summary>
    /// Dispatches the output logic by the node type key (TypeKey): Feedback.Log.Write writes a log entry, Feedback.Progress.Report reports
    /// progress, and File.Artifact.Publish writes a file.
    /// <para>按节点类型键（TypeKey）分发对应的输出逻辑：Feedback.Log.Write 写日志、Feedback.Progress.Report 上报进度、File.Artifact.Publish 保存文件。</para>
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
            case "Feedback.Log.Write":
                ExecuteLog(context);
                break;
            case "Feedback.Progress.Report":
                ExecuteProgress(context);
                break;
            case "File.Artifact.Publish":
                return ExecuteSaveAsync(context);
            default:
                return base.ExecuteAsync(context);
        }

        context.CancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void ExecuteLog(INodeExecutionContext context)
    {
        var message = NodeDataUtil.ToDisplay(ReadInput("Message"));
        var levelText = (Convert.ToString(ReadInput("Level"), CultureInfo.InvariantCulture) ?? "info").Trim().ToLowerInvariant();
        var level = ParseLogLevel(levelText) ?? LogLevel.Info;
        context.Log.Write(level, Title, message);

        if (NodeDataUtil.ToBool(ReadInput("Stdout"), false))
        {
            Console.WriteLine($"[{Title}] {message}");
        }
    }

    private void ExecuteProgress(INodeExecutionContext context)
    {
        var progress = NodeDataUtil.ToDouble(ReadInput("Progress"), 0);
        context.ReportProgress(Math.Clamp(progress, 0.0, 1.0));
        var status = NodeDataUtil.ToDisplay(ReadInput("Status"));
        var percent = Math.Clamp(progress, 0.0, 1.0) * 100;
        context.Log.Write(LogLevel.Info, Title,
            string.IsNullOrEmpty(status) ? $"Progress {percent:0.0}%  进度。" : $"Progress {percent:0.0}% — {status}  进度。");
    }

    private async Task ExecuteSaveAsync(INodeExecutionContext context)
    {
        var path = Convert.ToString(ReadInput("Path"), CultureInfo.InvariantCulture);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var data = ReadInput("Data");
        var artifact = ReadInput("Artifact");
        if ((data is null) == (artifact is null))
        {
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Publish_ChooseSource"));
        }
        var length = await NodeArtifactPublisher.PublishAsync(artifact ?? data, path, context.CancellationToken).ConfigureAwait(false);
        var fullPath = Path.GetFullPath(path);
        NodeArtifactRegistry.Record(fullPath);
        WriteOutput("Path", fullPath);
        WriteOutput("Artifact", new FileArtifact(fullPath));
        context.Log.Write(LogLevel.Info, Title, $"Saved {length} bytes to '{path}'.");
    }

    private static LogLevel? ParseLogLevel(string text)
    {
        return text switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "info" or "information" => LogLevel.Info,
            "warn" or "warning" => LogLevel.Warn,
            "error" => LogLevel.Error,
            "fatal" or "critical" => LogLevel.Fatal,
            _ => null
        };
    }
}