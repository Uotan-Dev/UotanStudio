using System.Text.Json.Serialization;

namespace UotanStudio.Projects;

/// <summary>
/// Portable JSON package containing either a workflow template or a composite node graph.
/// <para>可移植的 JSON 包，内容为工作流模板或复合节点图二者之一。</para>
/// </summary>
public sealed class VisualPluginPackage
{
    /// <summary>Current visual-plugin file format. <para>当前可视化插件文件格式版本。</para></summary>
    public const int CurrentFormatVersion = 4;

    /// <summary>Oldest file format this host still reads. <para>本宿主仍可读取的最旧文件格式。</para></summary>
    public const int MinimumReadableFormatVersion = 3;

    /// <summary>File format version. <para>文件格式版本。</para></summary>
    /// <value>
    /// Format level recorded in the document; required on read, and new packages seed it from <see cref="CurrentFormatVersion"/>.
    /// <para>文档中记录的格式级别；读取时必填，新建包以 <see cref="CurrentFormatVersion"/> 作为初始值。</para>
    /// </value>
    [JsonRequired]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>How the package is exposed by the host. <para>宿主公开该包的方式。</para></summary>
    /// <value>
    /// Role deciding whether the graph is instantiated as editable template nodes or registered as one composite node.
    /// <para>决定图是实例化为可编辑模板节点、还是注册为单个复合节点的角色。</para>
    /// </value>
    [JsonConverter(typeof(JsonStringEnumConverter<VisualPluginKind>))]
    public VisualPluginKind Kind { get; set; }

    /// <summary>Stable package identity and display metadata. <para>稳定的包标识与展示元数据。</para></summary>
    /// <value>
    /// Manifest consumed by validation, dependency resolution and the package list; never null, defaults to an empty declaration.
    /// <para>由校验、依赖解析和包列表消费的清单；永不为 null，默认是空声明。</para>
    /// </value>
    public VisualPluginManifest Manifest { get; set; } = new();

    /// <summary>Nodes and connections that implement the template or composite node. <para>实现该模板或复合节点的节点与连线。</para></summary>
    /// <value>
    /// Workflow snapshot carrying the whole internal graph of the package.
    /// <para>承载包内部完整图的工作流快照。</para>
    /// </value>
    public WorkflowSnapshot Graph { get; set; } = new();

    /// <summary>Lua source executed when <see cref="Kind"/> is <c>ScriptNode</c>. <para>当 <see cref="Kind"/> 为 <c>ScriptNode</c> 时执行的 Lua 源码。</para></summary>
    /// <value>
    /// Inline script text; empty for every other kind, and required within the script size bound when the kind is a script node.
    /// <para>内联脚本文本；其它种类为空，脚本节点种类下必须在脚本大小上限内非空。</para>
    /// </value>
    public string Script { get; set; } = string.Empty;

    /// <summary>Composite-node inputs mapped to internal node inputs. <para>复合节点输入到内部节点输入的映射。</para></summary>
    /// <value>
    /// Public input ports exposed when <see cref="Kind"/> is <c>CompositeNode</c>; an empty list means the package takes no external input.
    /// <para>当 <see cref="Kind"/> 为 <c>CompositeNode</c> 时对外暴露的输入端口；空列表表示不接受外部输入。</para>
    /// </value>
    public List<VisualPluginPortDefinition> Inputs { get; set; } = [];

    /// <summary>Composite-node outputs mapped to internal node outputs. <para>复合节点输出到内部节点输出的映射。</para></summary>
    /// <value>
    /// Public output ports exposed when <see cref="Kind"/> is <c>CompositeNode</c>; an empty list means the package provides no external output.
    /// <para>当 <see cref="Kind"/> 为 <c>CompositeNode</c> 时对外暴露的输出端口；空列表表示不对外提供输出。</para>
    /// </value>
    public List<VisualPluginPortDefinition> Outputs { get; set; } = [];

    /// <summary>Portable resources carried by the binary bundle, never embedded as JSON data. <para>由二进制 bundle 承载的可移植资源，绝不以 JSON 数据内嵌。</para></summary>
    /// <value>
    /// Asset descriptors resolved from the enclosing <c>.uotanplugin</c> archive; a non-empty list makes plain JSON persistence invalid.
    /// <para>从同包 <c>.uotanplugin</c> 归档解析出的资源描述符；列表非空时纯 JSON 持久化无效。</para>
    /// </value>
    public List<VisualPluginAsset> Assets { get; set; } = [];

    /// <summary>Whether the package carries a publisher signature. <para>包是否携带发布者签名。</para></summary>
    /// <value>
    /// <see langword="true"/> when the manifest holds a non-empty algorithm and value; the signature itself is not verified here.
    /// <para>清单中算法与值均非空时为 <see langword="true"/>；此处不校验签名本身。</para>
    /// </value>
    [JsonIgnore]
    public bool IsSigned => Manifest?.Signature is { } signature
        && !string.IsNullOrEmpty(signature.Algorithm) && !string.IsNullOrEmpty(signature.Value);

    /// <summary>Trusted archive location assigned by the loader, never accepted from JSON. <para>由加载器赋值的可信归档位置，绝不从 JSON 接受。</para></summary>
    /// <value>
    /// Absolute bundle path assigned during load, or <see langword="null"/> for JSON-only or in-memory packages.
    /// <para>加载时赋值的 bundle 绝对路径；纯 JSON 或内存构建的包为 <see langword="null"/>。</para>
    /// </value>
    [JsonIgnore]
    public string? BundlePath { get; set; }

    /// <summary>Validated root manifest owning the shared archive of an embedded package.</summary>
    [JsonIgnore]
    public VisualPluginPackage? BundleRoot { get; set; }

    /// <summary>Stable node type registered by a composite-node package. <para>复合节点包注册的可复用稳定节点类型键。</para></summary>
    /// <value>
    /// Host node-catalog key formed as <c>VisualPlugin.{Manifest.Id}</c>.
    /// <para>按 <c>VisualPlugin.{Manifest.Id}</c> 拼装的宿主节点目录键。</para>
    /// </value>
    [JsonIgnore]
    public string NodeTypeKey => $"VisualPlugin.{Manifest.Id}";
}

/// <summary>
/// Portable package role.
/// <para>可移植包的角色。</para>
/// </summary>
public enum VisualPluginKind
{
    /// <summary>
    /// Creates editable nodes on the current canvas.
    /// <para>在当前画布上创建可编辑节点。</para>
    /// </summary>
    WorkflowTemplate,

    /// <summary>
    /// Registers the contained graph as one reusable node.
    /// <para>将所含图注册为一个可复用节点。</para>
    /// </summary>
    CompositeNode,

    /// <summary>
    /// Runs a host-sandboxed Lua script against the injected tool surface.
    /// <para>对注入的工具面执行宿主沙箱内的 Lua 脚本。</para>
    /// </summary>
    ScriptNode
}

/// <summary>
/// Visual-plugin identity, compatibility, and capability declaration.
/// <para>可视化插件的身份、兼容性与能力声明。</para>
/// </summary>
public sealed class VisualPluginManifest
{
    /// <summary>Highest node API level this host implements. <para>本宿主实现的最高节点 API 级别。</para></summary>
    public const int CurrentNodeApiVersion = 1;

    /// <summary>Stable reverse-domain-style identifier. <para>稳定的反向域名风格标识符。</para></summary>
    /// <value>
    /// Identity used for dependency, conflict and node-type resolution; persists unchanged across package versions.
    /// <para>用于依赖、冲突和节点类型解析的标识；跨包版本保持不变。</para>
    /// </value>
    public string Id { get; set; } = string.Empty;

    /// <summary>Package version. <para>包版本。</para></summary>
    /// <value>
    /// Version text compared through <see cref="VisualPluginDependency.MatchesVersion"/>; defaults to <c>1.0.0</c>.
    /// <para>经由 <see cref="VisualPluginDependency.MatchesVersion"/> 比较的版本文本，默认 <c>1.0.0</c>。</para>
    /// </value>
    public string Version { get; set; } = "1.0.0";

    /// <summary>User-visible package name. <para>用户可见的包名称。</para></summary>
    /// <value>
    /// Display label for the package; identity resolution uses <see cref="Id"/> instead.
    /// <para>包的显示名称；标识解析使用 <see cref="Id"/> 而非此名称。</para>
    /// </value>
    public string Name { get; set; } = string.Empty;

    /// <summary>User-visible package description. <para>用户可见的包描述。</para></summary>
    /// <value>
    /// Free-form explanatory text shown to the user; empty means no description.
    /// <para>展示给用户的自由文本说明；为空表示无描述。</para>
    /// </value>
    public string Description { get; set; } = string.Empty;

    /// <summary>Package author or publisher. <para>包作者或发布者。</para></summary>
    /// <value>
    /// Display-only publisher attribution; carries no trust or permission meaning.
    /// <para>仅用于展示的署名，不代表任何信任或权限含义。</para>
    /// </value>
    public string Author { get; set; } = string.Empty;

    /// <summary>Minimum node API level this package requires. <para>本例包要求的最低节点 API 级别。</para></summary>
    /// <value>
    /// Minimum node API level the graph relies on; the level implemented by the host is given by <see cref="CurrentNodeApiVersion"/>, and a package is accepted while this value is between <c>1</c> and that level inclusive. Defaults to <see cref="CurrentNodeApiVersion"/>.
    /// <para>图所依赖的最低节点 API 级别；宿主实现的级别由 <see cref="CurrentNodeApiVersion"/> 给出，取值在 <c>1</c> 到该级别之间（含两端）时包即被接受。默认值为 <see cref="CurrentNodeApiVersion"/>。</para>
    /// </value>
    public int NodeApiVersion { get; set; } = CurrentNodeApiVersion;

    /// <summary>Supported operating-system identifiers. An empty list means all host platforms. <para>支持的操作系统标识；空列表表示不限宿主平台。</para></summary>
    /// <value>
    /// OS identifier allow-list consulted before the package is offered on the current host.
    /// <para>在当前宿主上提供该包前会查阅的操作系统标识白名单。</para>
    /// </value>
    public List<string> Platforms { get; set; } = [];

    /// <summary>Capabilities derived from or declared by the graph. <para>由图派生或声明的能力。</para></summary>
    /// <value>
    /// Capability tags describing what the package's nodes can do.
    /// <para>描述包内节点能力的标签集合。</para>
    /// </value>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>Stable node type keys required by the graph. <para>图所需的稳定节点类型键。</para></summary>
    /// <value>
    /// Host node types that must exist before the graph can be instantiated.
    /// <para>实例化该图前宿主必须具备的节点类型。</para>
    /// </value>
    public List<string> RequiredNodeTypes { get; set; } = [];

    /// <summary>Required installed and enabled composite packages, not already-applied patch prerequisites. <para>需要已安装且已启用的复合包，不含已应用的补丁前置项。</para></summary>
    /// <value>
    /// Dependency entries whose id and version range are matched against other manifests; empty means no prerequisite package.
    /// <para>以标识和版本区间与其他清单匹配的依赖项；为空表示无前置包。</para>
    /// </value>
    public List<VisualPluginDependency> Dependencies { get; set; } = [];

    /// <summary>Package ids that cannot be enabled together with this package. <para>不能与本包同时启用的包标识。</para></summary>
    /// <value>
    /// Package identities declared mutually exclusive with this package.
    /// <para>声明为与本包互斥的包标识。</para>
    /// </value>
    public List<string> Conflicts { get; set; } = [];

    /// <summary>Declared permissions requested by the package. <para>包请求的声明式权限。</para></summary>
    /// <value>
    /// Stable <see cref="PluginPermission"/> names; the host adds the permissions derived from the graph and parses
    /// these entries with <c>PluginPermissions.TryParse</c>.
    /// <para>稳定的 <see cref="PluginPermission"/> 名称；宿主会并入由图推导的权限，并用 <c>PluginPermissions.TryParse</c> 解析这些条目。</para>
    /// </value>
    public List<string> Permissions { get; set; } = [];

    /// <summary>Publisher identity bound by the signature. <para>由签名绑定的发布者身份。</para></summary>
    /// <value>
    /// Present on signed packages only; an unsigned package may carry no publisher at all.
    /// <para>仅已签名包存在；未签名包可以完全没有发布者信息。</para>
    /// </value>
    public VisualPluginPublisher? Publisher { get; set; }

    /// <summary>Signature over the canonical package payload. <para>覆盖规范化包载荷的签名。</para></summary>
    /// <value>
    /// <see langword="null"/> for an unsigned package; a present value is verified by <see cref="PluginSignature"/> on load.
    /// <para>未签名包为 <see langword="null"/>；存在时由 <see cref="PluginSignature"/> 在加载时校验。</para>
    /// </value>
    public VisualPluginSignature? Signature { get; set; }
}

/// <summary>
/// Public composite-node port mapped to one internal graph endpoint.
/// <para>映射到单个内部图端点的复合节点对外端口。</para>
/// </summary>
public sealed class VisualPluginPortDefinition
{
    /// <summary>Whether values must be masked and omitted from ordinary persisted defaults. <para>取值是否必须脱敏并在常规持久化默认值中省略。</para></summary>
    /// <value>
    /// <see langword="true"/> to mask values and exclude them from ordinary persisted defaults; <see langword="false"/> for a normal port.
    /// <para><see langword="true"/> 表示脱敏且不写入常规持久化默认值；<see langword="false"/> 表示普通端口。</para>
    /// </value>
    public bool IsSensitive { get; set; }
    /// <summary>Additional internal input targets sharing this public input. <para>共享该对外输入的额外内部目标。</para></summary>
    /// <value>
    /// Extra endpoints that also receive the public input value, on top of the primary <see cref="NodeInstanceId"/>/<see cref="PortId"/> pair; empty when the port binds to a single target.
    /// <para>除主 <see cref="NodeInstanceId"/>/<see cref="PortId"/> 外额外接收该输入值的端点；单目标绑定时为空。</para>
    /// </value>
    public List<VisualPluginPortBinding> AdditionalBindings { get; set; } = [];

    /// <summary>
    /// Enumerates the stable internal endpoints of this interface.
    /// <para>枚举该对外接口对应的全部稳定内部端点。</para>
    /// </summary>
    /// <returns>
    /// The primary binding built from <see cref="NodeInstanceId"/> and <see cref="PortId"/>, followed by each entry of <see cref="AdditionalBindings"/> in list order.
    /// <para>先返回由 <see cref="NodeInstanceId"/> 与 <see cref="PortId"/> 构成的主绑定，随后按列表顺序返回 <see cref="AdditionalBindings"/> 中的每个端点。</para>
    /// </returns>
    public IEnumerable<VisualPluginPortBinding> GetBindings()
    {
        yield return new VisualPluginPortBinding { NodeInstanceId = NodeInstanceId, PortId = PortId };
        foreach (var binding in AdditionalBindings)
        {
            yield return binding;
        }
    }
    /// <summary>Stable public port identifier. <para>稳定的对外端口标识。</para></summary>
    /// <value>
    /// Identifier connections and bindings use to address this port from outside the composite node.
    /// <para>外部通过该标识在连线与绑定中寻址此端口。</para>
    /// </value>
    public string Id { get; set; } = string.Empty;

    /// <summary>User-visible port name. <para>用户可见的端口名称。</para></summary>
    /// <value>
    /// Display label rendered beside the port; identity uses <see cref="Id"/> instead.
    /// <para>端口旁的显示名称；标识解析使用 <see cref="Id"/>。</para>
    /// </value>
    public string Name { get; set; } = string.Empty;

    /// <summary>Serialized node port type name understood by the host. <para>宿主可识别的序列化节点端口类型名。</para></summary>
    /// <value>
    /// Port type token used for compatibility matching; defaults to <c>Any</c>.
    /// <para>用于兼容性匹配的端口类型记号，默认 <c>Any</c>。</para>
    /// </value>
    public string PortType { get; set; } = "Any";

    /// <summary>Internal graph node instance identifier. <para>内部图节点实例标识。</para></summary>
    /// <value>
    /// Instance id of the graph node this port's primary endpoint belongs to.
    /// <para>该端口主端点所属图节点的实例标识。</para>
    /// </value>
    public string NodeInstanceId { get; set; } = string.Empty;

    /// <summary>Stable internal port identifier. <para>稳定的内部端口标识。</para></summary>
    /// <value>
    /// Port id on the node named by <see cref="NodeInstanceId"/> that this public port is mapped to.
    /// <para>映射到的 <see cref="NodeInstanceId"/> 所指节点上的内部端口标识。</para>
    /// </value>
    public string PortId { get; set; } = string.Empty;

    /// <summary>Default value used by an unconnected public input. <para>未连线时对外输入使用的默认值。</para></summary>
    /// <value>
    /// Serialized default applied while no connection feeds this input; <see langword="null"/> means no default is provided.
    /// <para>输入未接入连线时套用的序列化默认值；<see langword="null"/> 表示不提供默认值。</para>
    /// </value>
    public string? DefaultValue { get; set; }

    /// <summary>Whether an input must receive a connection or a non-empty default value. <para>输入是否必须获得连线或非空默认值。</para></summary>
    /// <value>
    /// <see langword="true"/> to require a connection or a non-empty <see cref="DefaultValue"/>; <see langword="false"/> allows the input to stay unfed.
    /// <para><see langword="true"/> 表示必须有连线或非空 <see cref="DefaultValue"/>；<see langword="false"/> 允许输入悬空。</para>
    /// </value>
    public bool Required { get; set; }
}

/// <summary>
/// A stable endpoint inside a composite graph.
/// <para>复合图内部的稳定端点。</para>
/// </summary>
public sealed class VisualPluginPortBinding
{
    /// <summary>Internal node identity. <para>内部节点标识。</para></summary>
    /// <value>
    /// Instance id of the node the bound endpoint lives on.
    /// <para>被绑定端点所在节点的实例标识。</para>
    /// </value>
    public string NodeInstanceId { get; set; } = string.Empty;

    /// <summary>Internal port identity. <para>内部端口标识。</para></summary>
    /// <value>
    /// Stable id of the port on <see cref="NodeInstanceId"/> addressed by this binding.
    /// <para>本绑定在 <see cref="NodeInstanceId"/> 上寻址的稳定端口标识。</para>
    /// </value>
    public string PortId { get; set; } = string.Empty;
}