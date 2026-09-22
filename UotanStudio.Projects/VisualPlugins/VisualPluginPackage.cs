using System.Text.Json.Serialization;

namespace UotanStudio.Services.Projects;

/// <summary>Portable JSON package containing either a workflow template or a composite node graph.</summary>
public sealed class VisualPluginPackage
{
    /// <summary>Current visual-plugin file format.</summary>
    public const int CurrentFormatVersion = 2;

    /// <summary>File format version.</summary>
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>How the package is exposed by the host.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<VisualPluginKind>))]
    public VisualPluginKind Kind { get; set; }

    /// <summary>Stable package identity and display metadata.</summary>
    public VisualPluginManifest Manifest { get; set; } = new();

    /// <summary>Nodes and connections that implement the template or composite node.</summary>
    public WorkflowSnapshot Graph { get; set; } = new();

    /// <summary>Composite-node inputs mapped to internal node inputs.</summary>
    public List<VisualPluginPortDefinition> Inputs { get; set; } = [];

    /// <summary>Composite-node outputs mapped to internal node outputs.</summary>
    public List<VisualPluginPortDefinition> Outputs { get; set; } = [];

    /// <summary>Portable resources carried by the binary bundle, never embedded as JSON data.</summary>
    public List<VisualPluginAsset> Assets { get; set; } = [];

    /// <summary>Trusted archive location assigned by the loader, never accepted from JSON.</summary>
    [JsonIgnore]
    public string? BundlePath { get; internal set; }

    /// <summary>Stable node type registered by a composite-node package.</summary>
    [JsonIgnore]
    public string NodeTypeKey => $"VisualPlugin.{Manifest.Id}";
}

/// <summary>Portable package role.</summary>
public enum VisualPluginKind
{
    /// <summary>Creates editable nodes on the current canvas.</summary>
    WorkflowTemplate,

    /// <summary>Registers the contained graph as one reusable node.</summary>
    CompositeNode
}

/// <summary>Visual-plugin identity, compatibility, and capability declaration.</summary>
public sealed class VisualPluginManifest
{
    /// <summary>Stable reverse-domain-style identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Package version.</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>User-visible package name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>User-visible package description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Package author or publisher.</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>Required host node API version.</summary>
    public int NodeApiVersion { get; set; } = 1;

    /// <summary>Supported operating-system identifiers. An empty list means all host platforms.</summary>
    public List<string> Platforms { get; set; } = [];

    /// <summary>Capabilities derived from or declared by the graph.</summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>Stable node type keys required by the graph.</summary>
    public List<string> RequiredNodeTypes { get; set; } = [];

    /// <summary>Required installed and enabled composite packages, not already-applied patch prerequisites.</summary>
    public List<VisualPluginDependency> Dependencies { get; set; } = [];

    /// <summary>Package ids that cannot be enabled together with this package.</summary>
    public List<string> Conflicts { get; set; } = [];
}

/// <summary>Public composite-node port mapped to one internal graph endpoint.</summary>
public sealed class VisualPluginPortDefinition
{
    /// <summary>Stable public port identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>User-visible port name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Serialized node port type name understood by the host.</summary>
    public string PortType { get; set; } = "Any";

    /// <summary>Internal graph node instance identifier.</summary>
    public string NodeInstanceId { get; set; } = string.Empty;

    /// <summary>Stable internal port identifier.</summary>
    public string PortId { get; set; } = string.Empty;

    /// <summary>Default value used by an unconnected public input.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Whether an input must receive a connection or a non-empty default value.</summary>
    public bool Required { get; set; }
}