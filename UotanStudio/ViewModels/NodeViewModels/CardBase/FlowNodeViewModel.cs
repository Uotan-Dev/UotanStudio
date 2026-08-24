using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using NodifyM.Avalonia.ViewModelBase;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>节点视图模型，表示节点编辑器画布上的一个节点。实现 INodePosition 接口以支持节点定位。</para>
    /// View model for a node, representing a single node on the node editor canvas. Implements INodePosition for node positioning.
    /// </summary>
    public partial class FlowNodeViewModel : ViewModelBase, INodePosition
    {
        private string _title = string.Empty;
        private Point _location;
        private NodeCategory _category;
        private bool _isSelected;
        private NodeExecutionStatus _executionStatus = NodeExecutionStatus.Idle;

        /// <summary>
        /// <para>节点实例唯一标识。</para>
        /// Unique identifier of the node instance.
        /// </summary>
        public string InstanceId { get; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// <para>获取或设置节点的显示标题。</para>
        /// Gets or sets the display title of the node.
        /// </summary>
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        /// <summary>
        /// <para>获取或设置节点在画布上的位置。</para>
        /// Gets or sets the position of the node on the canvas.
        /// </summary>
        public Point Location
        {
            get => _location;
            set => SetProperty(ref _location, value);
        }

        /// <summary>
        /// <para>获取或设置节点的类别。</para>
        /// Gets or sets the category of the node.
        /// </summary>
        public NodeCategory Category
        {
            get => _category;
            set => SetProperty(ref _category, value);
        }

        /// <summary>
        /// <para>获取或设置节点是否被选中。</para>
        /// Gets or sets whether the node is selected.
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        /// <summary>
        /// <para>获取或设置节点执行状态。</para>
        /// Gets or sets the node execution status.
        /// </summary>
        public NodeExecutionStatus ExecutionStatus
        {
            get => _executionStatus;
            set => SetProperty(ref _executionStatus, value);
        }

        /// <summary>
        /// <para>获取节点的输入连接器集合。</para>
        /// Gets the collection of input connectors for the node.
        /// </summary>
        public ObservableCollection<NodeConnectorViewModel> Input { get; } = [];

        /// <summary>
        /// <para>获取节点的输出连接器集合。</para>
        /// Gets the collection of output connectors for the node.
        /// </summary>
        public ObservableCollection<NodeConnectorViewModel> Output { get; } = [];

        /// <summary>
        /// <para>创建具有指定标题和类别的节点。</para>
        /// Creates a node with the specified title and category.
        /// </summary>
        /// <param name="title">
        /// <para>节点的显示标题。</para>
        /// The display title of the node.
        /// </param>
        /// <param name="category">
        /// <para>节点的类别。</para>
        /// The category of the node.
        /// </param>
        public FlowNodeViewModel(string title, NodeCategory category)
        {
            _title = title;
            _category = category;
        }

        /// <summary>
        /// <para>按端口标题读取输入值。若节点不存在同标题输入端口，返回 <c>default</c>。</para>
        /// Reads the input value by connector title. Returns the default value if no input connector matches.
        /// </summary>
        /// <param name="title">
        /// <para>输入端口标题。</para>
        /// The input connector title.
        /// </param>
        /// <returns>
        /// <para>端口承载的值；无匹配端口时返回 <c>null</c>。</para>
        /// The value carried by the port; <c>null</c> if no matching port exists.
        /// </returns>
        public object? ReadInput(string title)
        {
            var connector = Input.FirstOrDefault(c => string.Equals(c.Title, title, StringComparison.Ordinal));
            return connector?.Data;
        }

        /// <summary>
        /// <para>按端口标题把结果写入输出端口。</para>
        /// Writes a result to the output port by connector title.
        /// </summary>
        /// <param name="title">
        /// <para>输出端口标题。</para>
        /// The output connector title.
        /// </param>
        /// <param name="value">
        /// <para>要写入的值。</para>
        /// The value to write.
        /// </param>
        public void WriteOutput(string title, object? value)
        {
            var connector = Output.FirstOrDefault(c => string.Equals(c.Title, title, StringComparison.Ordinal));
            if (connector != null)
            {
                connector.Data = value;
            }
        }

        /// <summary>
        /// <para>执行节点业务逻辑。子类应重写此方法，通过 <see cref="ReadInput"/> 读取输入值并调用 <see cref="WriteOutput"/> 写入结果；默认实现仅记录一条日志。</para>
        /// Executes the node business logic. Subclasses should override this method, reading inputs via <see cref="ReadInput"/>
        /// and writing results via <see cref="WriteOutput"/>; the default implementation only writes a log entry.
        /// </summary>
        /// <param name="context">
        /// <para>执行上下文，提供日志、取消令牌与进度报告。</para>
        /// The execution context, providing logging, a cancellation token, and progress reporting.
        /// </param>
        /// <returns>
        /// <para>表示异步执行的任务。</para>
        /// A task representing the asynchronous execution.
        /// </returns>
        public virtual Task ExecuteAsync(INodeExecutionContext context)
        {
            context.Log.Write(LogLevel.Info, Title, $"Node '{Title}' has no execution logic (no-op).  节点无执行逻辑。");
            return Task.CompletedTask;
        }
    }
}