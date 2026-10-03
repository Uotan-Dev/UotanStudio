using Lua;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Scripting;
using UotanStudio.ViewModels.Nodes;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>Host-owned node that runs a script-plugin's Lua source against the injected tool surface.</summary>
public sealed class ScriptNodeViewModel : FlowNodeViewModel
{
    private static readonly HttpClient SharedHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly VisualPluginPackage _package;
    private readonly Func<IReadOnlyList<string>>? _validateExecution;
    private readonly IScriptToolGateway _gateway;
    private readonly HttpClient _httpClient;

    internal VisualPluginPackage Package => _package;

    /// <summary>Creates the node surface for one script package: Flow-in and Completed-out only.</summary>
    public ScriptNodeViewModel(VisualPluginPackage package, Func<IReadOnlyList<string>>? validateExecution = null,
        IScriptToolGateway? gateway = null, HttpClient? httpClient = null)
        : base(package.Manifest.Name, NodeCategory.Plugin)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        _validateExecution = validateExecution;
        _gateway = gateway ?? DeviceScriptToolGateway.Default;
        _httpClient = httpClient ?? SharedHttpClient;
        TypeKey = package.NodeTypeKey;
        Input.Add(new NodeConnectorViewModel
        {
            Title = "Flow",
            PortType = NodePortType.Flow,
            PortId = "flow",
            MaxConnections = 1
        });
        Output.Add(new NodeConnectorViewModel
        {
            Title = "Completed",
            PortType = NodePortType.Flow,
            PortId = "completed"
        });
    }

    /// <summary>Installed package identity.</summary>
    public string PluginId => _package.Manifest.Id;

    /// <summary>Installed package version.</summary>
    public string PluginVersion => _package.Manifest.Version;

    /// <inheritdoc />
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        foreach (var output in Output)
        {
            output.Data = null;
        }
        if (_validateExecution is not null)
        {
            var errors = _validateExecution();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
            }
        }
        if (!NodeFactoryRegistrar.TryGetVisualPluginPackage(TypeKey, out var installed)
            || installed.Kind != VisualPluginKind.ScriptNode
            || installed.Manifest.Version != PluginVersion
            || !string.Equals(installed.Script, _package.Script, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Script '{PluginId}' requires its exact installed script version {PluginVersion}.");
        }

        var tools = ScriptToolCatalog.Create(context, _gateway, _httpClient);
        try
        {
            await new LuaScriptEngine().RunAsync(_package.Script, TypeKey, tools, context,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LuaRuntimeException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        WriteOutput("Completed", true);
    }
}