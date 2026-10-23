using Lua;
using UotanStudio.Projects;
using UotanStudio.Services.Scripting;
using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Registry;

namespace UotanStudio.Workflow.Nodes;

/// <summary>Node that runs a script-plugin's Lua source against the injected tool surface.<para>在注入的工具表面上运行脚本插件的 Lua 源码的节点。</para></summary>
public sealed class ScriptNodeViewModel : FlowNodeViewModel
{
    private static readonly HttpClient SharedHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly VisualPluginPackage _package;
    private readonly Func<IReadOnlyList<string>>? _validateExecution;
    private readonly IScriptToolGateway _gateway;
    private readonly HttpClient _httpClient;

    internal VisualPluginPackage Package => _package;

    /// <summary>Creates the node surface for one script package: Flow-in and Completed-out only.<para>为单个脚本包创建节点表面：仅有 Flow 入口与 Completed 出口。</para></summary>
    /// <param name="package">Script package supplying the manifest and Lua source.<para>提供清单与 Lua 源码的脚本包。</para></param>
    /// <param name="validateExecution">Optional hook that returns validation errors before execution.<para>可选回调，在执行前返回校验错误。</para></param>
    /// <param name="gateway">Optional tool gateway behind device-facing script calls.<para>可选工具网关，承接面向设备的脚本调用。</para></param>
    /// <param name="httpClient">Optional HTTP client shared with script tools.<para>可选的 HTTP 客户端，与脚本工具共享。</para></param>
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

    /// <summary>Installed package identity.<para>已安装软件包标识。</para></summary>
    /// <value>The id from the installed package manifest.<para>来自已安装软件包清单的标识。</para></value>
    public string PluginId => _package.Manifest.Id;

    /// <summary>Installed package version.<para>已安装软件包版本。</para></summary>
    /// <value>The version from the installed package manifest.<para>来自已安装软件包清单的版本。</para></value>
    public string PluginVersion => _package.Manifest.Version;

    /// <summary>Validates the installed script version and runs the Lua source to completion.<para>校验已安装脚本版本并运行 Lua 源码直至完成。</para></summary>
    /// <param name="context">Context supplying logging, cancellation and script tool services.<para>提供日志、取消与脚本工具服务的上下文。</para></param>
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

        var tools = ScriptToolCatalog.Create(context, _gateway, _httpClient, PluginPermissions.Derive(_package));
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