using Avalonia;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>流程控制节点视图模型，提供开始、结束、条件分支、循环、延迟、并行、顺序和终止功能。</para>
    /// Flow control node view model, providing start, end, conditional branch, loop, delay, parallel, sequence, and termination capabilities.
    /// </summary>
    public class FlowControlNodeViewModel : FlowNodeViewModel
    {
        /// <summary>
        /// <para>创建具有指定标题的流程控制节点。</para>
        /// Creates a flow control node with the specified title.
        /// </summary>
        /// <param name="title">
        /// <para>节点的显示标题。</para>
        /// The display title of the node.
        /// </param>
        public FlowControlNodeViewModel(string title) : base(title, NodeCategory.Flow)
        {
        }

        /// <summary>
        /// <para>创建默认的开始节点（工作流入口）。</para>
        /// Creates a default start node (workflow entry point).
        /// </summary>
        public static FlowControlNodeViewModel CreateStartNode()
        {
            var node = new FlowControlNodeViewModel("Start")
            {
                Location = new Point(100, 100)
            };
            node.Output.Add(new NodeConnectorViewModel { Title = "Flow" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的结束节点（工作流出口）。</para>
        /// Creates a default end node (workflow exit point).
        /// </summary>
        public static FlowControlNodeViewModel CreateEndNode()
        {
            var node = new FlowControlNodeViewModel("End")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Flow" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的条件分支节点，根据布尔输入选择执行路径。</para>
        /// Creates a default branch node that selects execution paths based on a boolean input.
        /// </summary>
        public static FlowControlNodeViewModel CreateBranchNode()
        {
            var node = new FlowControlNodeViewModel("Branch")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Condition" });
            node.Output.Add(new NodeConnectorViewModel { Title = "True" });
            node.Output.Add(new NodeConnectorViewModel { Title = "False" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的循环节点，按指定次数重复执行子流程。</para>
        /// Creates a default loop node that repeats a sub-flow a specified number of times.
        /// </summary>
        public static FlowControlNodeViewModel CreateLoopNode()
        {
            var node = new FlowControlNodeViewModel("Loop")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Count" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Body" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Index" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的延迟节点，在流程中暂停指定毫秒数。</para>
        /// Creates a default delay node that pauses the flow for a specified number of milliseconds.
        /// </summary>
        public static FlowControlNodeViewModel CreateDelayNode()
        {
            var node = new FlowControlNodeViewModel("Delay")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Milliseconds" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Flow" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Flow" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的并行执行节点，同时运行多个子流程。</para>
        /// Creates a default parallel node that runs multiple sub-flows concurrently.
        /// </summary>
        public static FlowControlNodeViewModel CreateParallelNode()
        {
            var node = new FlowControlNodeViewModel("Parallel")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Flow" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Branch A" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Branch B" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的顺序执行节点，等待多个输入全部完成后再输出。</para>
        /// Creates a default sequence node that waits for all inputs to complete before firing its output.
        /// </summary>
        public static FlowControlNodeViewModel CreateSequenceNode()
        {
            var node = new FlowControlNodeViewModel("Sequence")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Input A" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Input B" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Flow" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的终止节点，立即中止整个工作流执行。</para>
        /// Creates a default terminate node that immediately aborts the entire workflow execution.
        /// </summary>
        public static FlowControlNodeViewModel CreateTerminateNode()
        {
            var node = new FlowControlNodeViewModel("Terminate")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Flow" });
            node.Output.Add(new NodeConnectorViewModel { Title = "Reason" });
            return node;
        }
    }
}