namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>节点端口数据类型，用于约束连接兼容性与在节点图标上标记输出类型。</para>
/// Port data type of a node connector, used to constrain connection compatibility
/// and to mark output types on the node icon.
/// </summary>
public enum NodePortType
{
    /// <summary><para>任意类型（通配，可与任何类型连接）。</para>Any type (wildcard, connects to anything).</summary>
    Any,

    /// <summary><para>设备句柄（<c>DetectedDevice</c>/<c>DeviceInfo</c>）。</para>Device handle (DetectedDevice/DeviceInfo).</summary>
    Device,

    /// <summary><para>流程控制信号（Start/End/Branch 等）。</para>Flow-control signal (Start/End/Branch, etc.).</summary>
    Flow,

    /// <summary><para>文件或目录路径。</para>File or directory path.</summary>
    Path,

    /// <summary><para>二进制数据。</para>Binary data.</summary>
    Data,

    /// <summary><para>文本字符串。</para>Text string.</summary>
    Text,

    /// <summary><para>数值。</para>Number.</summary>
    Number,

    /// <summary><para>布尔值。</para>Boolean value.</summary>
    Boolean,

    /// <summary>Execution-owned patch directory and its staged file overlay.</summary>
    PatchWorkspace,

    /// <summary>Immutable package resource reference with verified consumption.</summary>
    PatchAsset,
    /// <summary>Immutable configuration edits for one image rebuild.</summary>
    ImageEdits,
    /// <summary>Declarative user-interface component, never a native control.</summary>
    UiComponent,
    /// <summary>Validated declarative form layout and field schema.</summary>
    UiForm,
    /// <summary>Named typed values returned by an operation.</summary>
    Record,
    /// <summary>Ordered collection of values.</summary>
    List,
    /// <summary>File-backed artifact reference for bounded processing.</summary>
    Artifact,
    Document,
}