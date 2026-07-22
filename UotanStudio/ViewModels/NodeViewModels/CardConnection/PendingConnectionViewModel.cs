using CommunityToolkit.Mvvm.Input;
using NodifyM.Avalonia.ViewModelBase;
using System.Windows.Input;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>待处理连接视图模型，管理拖拽连接时的状态。</para>
    /// View model for pending connections, managing the state during drag-connection.
    /// </summary>
    public class PendingConnectionViewModel
    {
        private readonly NodeEditorViewModel _editor;
        private ConnectorViewModelBase? _source;
        private object? _previewTarget;
        private string? _previewText;

        /// <summary>
        /// <para>获取或设置源连接器（输出端口）。</para>
        /// Gets or sets the source connector (output port).
        /// </summary>
        public ConnectorViewModelBase? Source
        {
            get => _source;
            set => _source = value;
        }

        /// <summary>
        /// <para>获取或设置预览目标连接器。</para>
        /// Gets or sets the preview target connector.
        /// </summary>
        public object? PreviewTarget
        {
            get => _previewTarget;
            set => _previewTarget = value;
        }

        /// <summary>
        /// <para>获取或设置预览文本。</para>
        /// Gets or sets the preview text.
        /// </summary>
        public string? PreviewText
        {
            get => _previewText;
            set => _previewText = value;
        }

        /// <summary>
        /// <para>获取开始连接命令，当从连接器拖出连线时触发。</para>
        /// Gets the start connection command, triggered when dragging from a connector.
        /// </summary>
        public ICommand StartCommand { get; }

        /// <summary>
        /// <para>获取完成连接命令，当连线拖到目标连接器时触发。</para>
        /// Gets the finish connection command, triggered when the connection is dropped on a target connector.
        /// </summary>
        public ICommand FinishCommand { get; }

        /// <summary>
        /// <para>创建待处理连接视图模型。</para>
        /// Creates a pending connection view model.
        /// </summary>
        /// <param name="editor">
        /// <para>节点编辑器视图模型。</para>
        /// The node editor view model.
        /// </param>
        public PendingConnectionViewModel(NodeEditorViewModel editor)
        {
            _editor = editor;
            StartCommand = new RelayCommand<ConnectorViewModelBase>(source =>
            {
                Source = source;
                PreviewText = source?.Title;
            });
            FinishCommand = new RelayCommand<ConnectorViewModelBase?>(target =>
            {
                if (target == null || Source == null || target == Source) return;
                if (target is NodeConnectorViewModel t && Source is NodeConnectorViewModel s)
                {
                    _editor.CreateConnection(s, t);
                }
                Source = null;
            });
        }
    }
}