using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

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

        /// <summary>
        /// <para>执行与节点标题对应的流程控制逻辑：Start/End 标记、Branch 按条件选路径、Loop 记录循环次数、Delay 延迟、
        /// Parallel/Sequence 记录并行/汇聚语义、Terminate 中止整个工作流。</para>
        /// Executes the flow-control logic matching the node title: Start/End are markers, Branch selects a path by condition,
        /// Loop records the iteration count, Delay pauses, Parallel/Sequence record concurrency/join semantics, and Terminate aborts the workflow.
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
                case "Start":
                    context.Log.Write(LogLevel.Info, Title, "Workflow entered.  工作流入口。");
                    break;
                case "End":
                    context.Log.Write(LogLevel.Info, Title, "Workflow reached the end node.  到达结束节点。");
                    break;
                case "Branch":
                    ExecuteBranch(context);
                    break;
                case "Loop":
                    ExecuteLoop(context);
                    break;
                case "Delay":
                    return ExecuteDelayAsync(context);
                case "Parallel":
                    context.Log.Write(LogLevel.Info, Title, "Parallel branches dispatched.  分支并行分派。");
                    break;
                case "Sequence":
                    context.Log.Write(LogLevel.Info, Title, "All inputs joined before proceeding.  汇聚等待完成。");
                    break;
                case "Terminate":
                    context.Log.Write(LogLevel.Warn, Title, "Terminate requested.  请求终止工作流。");
                    throw new OperationCanceledException("Workflow terminated by Terminate node");
                default:
                    return base.ExecuteAsync(context);
            }

            context.CancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private void ExecuteBranch(INodeExecutionContext context)
        {
            var condition = NodeDataUtil.ToBool(ReadInput("Condition"), false);
            WriteOutput("True", condition);
            WriteOutput("False", !condition);
            context.Log.Write(LogLevel.Info, Title, $"Branch condition = {condition}.  分支条件评估。");
        }

        private void ExecuteLoop(INodeExecutionContext context)
        {
            var count = NodeDataUtil.ToInt(ReadInput("Count"), 0);
            WriteOutput("Body", count);
            WriteOutput("Index", count);
            context.Log.Write(LogLevel.Info, Title, $"Loop iterations planned: {count}.  计划循环 {count} 次。");
        }

        private async Task ExecuteDelayAsync(INodeExecutionContext context)
        {
            var milliseconds = NodeDataUtil.ToInt(ReadInput("Milliseconds"), 0);
            await Task.Delay(Math.Max(0, milliseconds), context.CancellationToken).ConfigureAwait(false);
            context.Log.Write(LogLevel.Info, Title, $"Paused for {Math.Max(0, milliseconds)} ms.  已延迟。");
        }
    }
}