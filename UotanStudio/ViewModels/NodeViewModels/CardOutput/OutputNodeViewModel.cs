using Avalonia;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>输出节点视图模型，提供日志输出、进度报告和结果保存功能。</para>
    /// Output node view model, providing log output, progress report, and result saving capabilities.
    /// </summary>
    public class OutputNodeViewModel : FlowNodeViewModel
    {
        /// <summary>
        /// <para>创建具有指定标题的输出节点。</para>
        /// Creates an output node with the specified title.
        /// </summary>
        /// <param name="title">
        /// <para>节点的显示标题。</para>
        /// The display title of the node.
        /// </param>
        public OutputNodeViewModel(string title) : base(title, NodeCategory.Output)
        {
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
            node.Input.Add(new NodeConnectorViewModel { Title = "Message" });
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
}
