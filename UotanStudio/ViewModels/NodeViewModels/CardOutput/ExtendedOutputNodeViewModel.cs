using Avalonia;

namespace UotanStudio.ViewModels.NodeViewModels
{
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
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Message" });
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
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "To" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Subject" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Body" });
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
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Text" });
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
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Data" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Path" });
            return node;
        }
    }
}
