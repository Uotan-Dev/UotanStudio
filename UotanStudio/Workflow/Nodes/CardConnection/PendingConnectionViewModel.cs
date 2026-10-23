using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using NodifyM.Avalonia.ViewModelBase;
using UotanStudio.ViewModels.Nodes;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// View model for pending connections, managing the state during drag-connection.
/// <para>待处理连接视图模型，管理拖拽连接时的状态。</para>
/// </summary>
public class PendingConnectionViewModel
{
    private readonly NodeEditorViewModel _editor;
    private ConnectorViewModelBase? _source;
    private object? _previewTarget;
    private string? _previewText;

    /// <summary>
    /// Gets or sets the source connector (output port).
    /// <para>获取或设置源连接器（输出端口）。</para>
    /// </summary>
    public ConnectorViewModelBase? Source
    {
        get => _source;
        set => _source = value;
    }

    /// <summary>
    /// Gets or sets the preview target connector.
    /// <para>获取或设置预览目标连接器。</para>
    /// </summary>
    public object? PreviewTarget
    {
        get => _previewTarget;
        set => _previewTarget = value;
    }

    /// <summary>
    /// Gets or sets the preview text.
    /// <para>获取或设置预览文本。</para>
    /// </summary>
    public string? PreviewText
    {
        get => _previewText;
        set => _previewText = value;
    }

    /// <summary>
    /// Gets the start connection command, triggered when dragging from a connector.
    /// <para>获取开始连接命令，当从连接器拖出连线时触发。</para>
    /// </summary>
    public ICommand StartCommand { get; }

    /// <summary>
    /// Gets the finish connection command, triggered when the connection is dropped on a target connector.
    /// <para>获取完成连接命令，当连线拖到目标连接器时触发。</para>
    /// </summary>
    public ICommand FinishCommand { get; }

    /// <summary>
    /// Creates a pending connection view model.
    /// <para>创建待处理连接视图模型。</para>
    /// </summary>
    /// <param name="editor">
    /// The node editor view model.
    /// <para>节点编辑器视图模型。</para>
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