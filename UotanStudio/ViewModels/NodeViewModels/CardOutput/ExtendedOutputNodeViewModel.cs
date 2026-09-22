using System.Globalization;
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
    public static InteractionNodeViewModel CreateNotifyNode() => InteractionNodeViewModel.Create("UI.Notification.Show");

    /// <summary>
    /// <para>创建默认的控制台输出节点。</para>
    /// Creates a default console-output node that prints text to stdout.
    /// </summary>
    public static ExtendedOutputNodeViewModel CreateConsoleOutputNode()
    {
        var node = new ExtendedOutputNodeViewModel("Console Output")
        {
            Location = new Point(100, 100),
            TypeKey = "Feedback.Console.Write"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Text", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的文件导出节点，将数据写入指定路径。</para>
    /// Creates a default file-export node that writes data to a specified path.
    /// </summary>
    public static OutputNodeViewModel CreateFileExportNode() => OutputNodeViewModel.CreateSaveNode();

    /// <summary>
    /// <para>执行控制台输出。通知与文件发布工厂返回各自的专用节点。</para>
    /// Executes console output; notification and publication factories return their dedicated node implementations.
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
            case "Feedback.Console.Write":
                ExecuteConsole(context);
                break;
            default:
                return base.ExecuteAsync(context);
        }

        context.CancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void ExecuteConsole(INodeExecutionContext context)
    {
        var text = NodeDataUtil.ToDisplay(ReadInput("Text"));
        System.Console.WriteLine($"[{Title}] {text}");
        context.Log.Write(LogLevel.Info, Title, text);
    }

}