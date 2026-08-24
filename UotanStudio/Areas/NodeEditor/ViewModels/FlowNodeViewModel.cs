using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using NodifyM.Avalonia.ViewModelBase;
using UotanStudio.ViewModels;

namespace UotanStudio.Areas.NodeEditor.ViewModels;

/// <summary>
/// <para>节点类别枚举，定义不同类型的工具箱节点。</para>
/// Enum defining different categories of toolbox nodes.
/// </summary>
public enum NodeCategory
{
    /// <summary>
    /// <para>固件操作节点。</para>
    /// Firmware operation node.
    /// </summary>
    Firmware,
    /// <summary>
    /// <para>烧录操作节点。</para>
    /// Flash operation node.
    /// </summary>
    Flash,
    /// <summary>
    /// <para>数据处理节点。</para>
    /// Data processing node.
    /// </summary>
    Data,
    /// <summary>
    /// <para>流程控制节点。</para>
    /// Flow control node.
    /// </summary>
    Flow,
    /// <summary>
    /// <para>输出节点。</para>
    /// Output node.
    /// </summary>
    Output
}

/// <summary>
/// <para>节点视图模型，表示节点编辑器画布上的一个节点。实现 INodePosition 接口以支持节点定位。</para>
/// View model for a node, representing a single node on the node editor canvas. Implements INodePosition for node positioning.
/// </summary>
public partial class FlowNodeViewModel : ViewModelBase, INodePosition
{
    private string _title = string.Empty;
    private NodeCategory _category;
    private string _key = string.Empty;

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
        get;
        set => SetProperty(ref field, value);
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
    /// <para>获取或设置节点的固定键（本地化资源键，与语言无关，用于执行分派与持久化映射）。</para>
    /// Gets or sets the fixed key of the node (a locale-independent resource key used for
    /// execution dispatch and persistence mapping).
    /// </summary>
    public string Key
    {
        get => _key;
        set => SetProperty(ref _key, value);
    }

    /// <summary>
    /// <para>获取或设置节点的唯一标识，用于流程持久化时定位节点。</para>
    /// Gets or sets the unique identifier of the node, used to locate the node when
    /// persisting a flow.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// <para>获取或设置节点是否被选中。</para>
    /// Gets or sets whether the node is selected.
    /// </summary>
    public bool IsSelected
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// <para>获取节点的输入连接器集合。添加连接器时自动绑定 Node 反向引用。</para>
    /// Gets the collection of input connectors for the node. Node back-reference is auto-bound when a connector is added.
    /// </summary>
    public ObservableCollection<NodeConnectorViewModel> Input { get; }

    /// <summary>
    /// <para>获取节点的输出连接器集合。添加连接器时自动绑定 Node 反向引用。</para>
    /// Gets the collection of output connectors for the node. Node back-reference is auto-bound when a connector is added.
    /// </summary>
    public ObservableCollection<NodeConnectorViewModel> Output { get; }

    /// <summary>
    /// <para>获取该节点上的全部连接（运行时由 NodeEditorViewModel 维护，用于连接器断开回溯）。</para>
    /// Gets all connections on this node (maintained at runtime by NodeEditorViewModel, used for connector disconnect backtracking).
    /// </summary>
    public ObservableCollection<NodeConnectionViewModel> NodeConnections { get; } = [];

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
    /// <param name="key">
    /// <para>节点的固定键（本地化资源键）。</para>
    /// The fixed key of the node (localization resource key).
    /// </param>
    public FlowNodeViewModel(string title, NodeCategory category, string key)
    {
        _title = title;
        _category = category;
        _key = key;
        Input = [];
        Output = [];
        Input.CollectionChanged += OnConnectorsChanged;
        Output.CollectionChanged += OnConnectorsChanged;
    }

    private void OnConnectorsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (NodeConnectorViewModel connector in e.NewItems)
            {
                connector.Node = this;
            }
        }
    }
}

/// <summary>
/// <para>节点固定键注册表：为每种节点提供与语言无关的资源键，用于执行分派与流程持久化。</para>
/// Node key registry: provides a locale-independent resource key for each node kind,
/// used for execution dispatch and flow persistence.
/// </summary>
public static class NodeKeys
{
    /// <summary>固件解包。 Firmware unpack.</summary>
    public const string FirmwareUnpack = "Node_FirmwareUnpack";

    /// <summary>固件打包。 Firmware pack.</summary>
    public const string FirmwarePack = "Node_FirmwarePack";

    /// <summary>签名验证。 Signature verification.</summary>
    public const string FirmwareVerify = "Node_FirmwareVerify";

    /// <summary>分区提取。 Partition extraction.</summary>
    public const string FirmwareExtract = "Node_FirmwareExtract";

    /// <summary>连接设备。 Connect device.</summary>
    public const string FlashConnect = "Node_FlashConnect";

    /// <summary>刷写分区。 Flash partition.</summary>
    public const string FlashPartition = "Node_FlashPartition";

    /// <summary>擦除分区。 Erase partition.</summary>
    public const string FlashErase = "Node_FlashErase";

    /// <summary>重启设备。 Reboot device.</summary>
    public const string FlashReboot = "Node_FlashReboot";

    /// <summary>读取文件。 Read file.</summary>
    public const string DataReadFile = "Node_DataReadFile";

    /// <summary>格式转换。 Format convert.</summary>
    public const string DataConvert = "Node_DataConvert";

    /// <summary>校验和。 Checksum.</summary>
    public const string DataChecksum = "Node_DataChecksum";

    /// <summary>比较。 Compare.</summary>
    public const string DataCompare = "Node_DataCompare";

    /// <summary>条件分支。 If branch.</summary>
    public const string FlowIf = "Node_FlowIf";

    /// <summary>循环。 Loop.</summary>
    public const string FlowLoop = "Node_FlowLoop";

    /// <summary>等待。 Wait.</summary>
    public const string FlowWait = "Node_FlowWait";

    /// <summary>日志输出。 Log output.</summary>
    public const string OutputLog = "Node_OutputLog";

    /// <summary>进度报告。 Progress report.</summary>
    public const string OutputProgress = "Node_OutputProgress";

    /// <summary>结果保存。 Save result.</summary>
    public const string OutputSave = "Node_OutputSave";
}