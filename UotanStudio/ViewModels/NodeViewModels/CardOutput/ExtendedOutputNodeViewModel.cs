using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>扩展输出节点视图模型，提供通知、邮件、控制台与文件导出功能。</para>
/// Extended output node view model, providing notification, email, console, and file export capabilities.
/// </summary>
public class ExtendedOutputNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// <para>创建具有指定标题的扩展输出节点。</para>
    /// Creates an extended output node with the specified title.
    /// </summary>
    /// <param name="title">
    /// <para>节点的显示标题。</para>
    /// The display title of the node.
    /// </param>
    public ExtendedOutputNodeViewModel(string title) : base(title, NodeCategory.Output)
    {
    }

    /// <summary>
    /// <para>创建默认的通知节点。</para>
    /// Creates a default notification node that emits a desktop notification.
    /// </summary>
    public static ExtendedOutputNodeViewModel CreateNotifyNode()
    {
        var node = new ExtendedOutputNodeViewModel("Notify")
        {
            Location = new Point(100, 100),
            TypeKey = "Output.Notify"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Message", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的邮件发送节点。</para>
    /// Creates a default send-email node.
    /// </summary>
    public static ExtendedOutputNodeViewModel CreateSendEmailNode()
    {
        var node = new ExtendedOutputNodeViewModel("Send Email")
        {
            Location = new Point(100, 100),
            TypeKey = "Output.Email"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "To", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Subject", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Body", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的控制台输出节点。</para>
    /// Creates a default console-output node that prints text to stdout.
    /// </summary>
    public static ExtendedOutputNodeViewModel CreateConsoleOutputNode()
    {
        var node = new ExtendedOutputNodeViewModel("Console Output")
        {
            Location = new Point(100, 100),
            TypeKey = "Output.Console"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Text", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的文件导出节点，将数据写入指定路径。</para>
    /// Creates a default file-export node that writes data to a specified path.
    /// </summary>
    public static ExtendedOutputNodeViewModel CreateFileExportNode()
    {
        var node = new ExtendedOutputNodeViewModel("File Export")
        {
            Location = new Point(100, 100),
            TypeKey = "Output.FileExport"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Data", PortType = NodePortType.Data });
        node.Input.Add(new NodeConnectorViewModel { Title = "Path", PortType = NodePortType.Path });
        return node;
    }

    /// <summary>
    /// <para>执行与节点标题对应的扩展输出逻辑：Notify 通知、Send Email 发邮件（在当前环境记录日志）、Console Output 控制台输出、File Export 文件导出。</para>
    /// Executes the extended output logic matching the node title: Notify emits a notification-style log entry, Send Email
    /// records an email summary, Console Output prints to the console and log, and File Export writes a file.
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
        switch (Title)
        {
            case "Notify":
                context.Log.Write(LogLevel.Warn, Title, $"NOTIFY: {NodeDataUtil.ToDisplay(ReadInput("Message"))}  通知。");
                break;
            case "Send Email":
                ExecuteEmail(context);
                break;
            case "Console Output":
                ExecuteConsole(context);
                break;
            case "File Export":
                ExecuteFileExport(context);
                break;
            default:
                return base.ExecuteAsync(context);
        }

        context.CancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void ExecuteEmail(INodeExecutionContext context)
    {
        var to = NodeDataUtil.ToDisplay(ReadInput("To"));
        var subject = NodeDataUtil.ToDisplay(ReadInput("Subject"));
        var body = NodeDataUtil.ToDisplay(ReadInput("Body"));
        // 应用未接入 SMTP 客户端；此处将待发邮件详情写入日志，供后续接入真实邮件服务。
        // No SMTP client is wired into the app; the pending email is written to the log for later integration.
        context.Log.Write(LogLevel.Info, Title, $"EMAIL to:{to} subject:'{subject}' body:{body.Length} chars  待发邮件已记录。");
    }

    private void ExecuteConsole(INodeExecutionContext context)
    {
        var text = NodeDataUtil.ToDisplay(ReadInput("Text"));
        System.Console.WriteLine($"[{Title}] {text}");
        context.Log.Write(LogLevel.Info, Title, text);
    }

    private void ExecuteFileExport(INodeExecutionContext context)
    {
        var data = NodeDataUtil.ToBytes(ReadInput("Data")) ?? [];
        var path = Convert.ToString(ReadInput("Path"), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(path))
        {
            context.Log.Write(LogLevel.Warn, Title, "No export Path.  缺少导出路径。");
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, data);
        context.Log.Write(LogLevel.Info, Title, $"Exported {data.Length} bytes to '{path}'  已导出。");
    }
}