using System.Text;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Cli;

/// <summary>
/// Entry point for the <c>workflow</c> CLI subcommand. Runs a saved node workflow
/// (<c>.uotannode</c>) headlessly: loads the snapshot, rebuilds nodes via
/// <see cref="NodeFactoryRegistrar"/>, applies <c>--set</c> input overrides, executes with
/// <see cref="NodeExecutionEngine"/>, and returns a process exit code. All output goes to
/// <see cref="Console"/>; no Avalonia UI is started. AOT-friendly (no reflection).
/// <para><c>workflow</c> CLI 子命令入口。无头运行已保存的节点工作流（<c>.uotannode</c>）：
/// 加载快照、经 <see cref="NodeFactoryRegistrar"/> 重建节点、应用 <c>--set</c> 输入覆盖、
/// 用 <see cref="NodeExecutionEngine"/> 执行并返回进程退出码。全部输出走 <see cref="Console"/>，
/// 不启动 Avalonia UI。AOT 友好（无反射）。</para>
/// </summary>
public static class WorkflowCli
{
    /// <summary>
    /// Runs the workflow CLI with the given arguments (the tokens after the literal
    /// <c>workflow</c> token). Returns a process exit code.
    /// </summary>
    /// <para>以给定参数运行 workflow CLI（字面量 <c>workflow</c> 之后的 token），返回进程退出码。</para>
    public static int Run(string[]? args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 控制台不支持编码则忽略 */ }

        WorkflowCliArguments parse;
        try
        {
            parse = Parse(args);
        }
        catch (WorkflowCliUsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage(Console.Error);
            return FirmwareCliExitCodes.Usage;
        }

        if (parse.HelpRequested)
        {
            PrintUsage(Console.Out);
            return FirmwareCliExitCodes.Ok;
        }

        if (parse.ListNodesRequested)
        {
            return RunListNodes(parse.JsonOutput);
        }

        if (parse.InspectRequested)
        {
            return RunInspect(parse.WorkflowPath!, parse.JsonOutput);
        }

        return RunWorkflowAsync(parse).GetAwaiter().GetResult();
    }

    /// <summary>
    /// <para>加载并执行工作流；返回进程退出码（0 成功 / 1 失败或取消 / 2 用法错误）。</para>
    /// Loads and executes the workflow; returns the process exit code (0 success / 1 failure or cancelled / 2 usage).
    /// </summary>
    private static async Task<int> RunWorkflowAsync(WorkflowCliArguments cli)
    {
        var path = cli.WorkflowPath!;
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Workflow file not found: {path}  工作流文件未找到");
            return FirmwareCliExitCodes.Failure;
        }

        var snapshot = await WorkflowPersistenceService.LoadAsync(path).ConfigureAwait(false);
        if (snapshot is null)
        {
            Console.Error.WriteLine($"Failed to load workflow: {path}  工作流加载失败");
            return FirmwareCliExitCodes.Failure;
        }

        // 重建节点：按 TypeKey 查注册表；未注册类型跳过并告警。
        // Rebuild nodes via the registry by TypeKey; unknown types are skipped with a warning.
        NodeFactoryRegistrar.RegisterAll();

        var nodes = new List<FlowNodeViewModel>(snapshot.Nodes.Count);
        var created = new Dictionary<string, FlowNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in snapshot.Nodes)
        {
            if (!WorkflowReconstruction.TryCreateNode(dto, out var node))
            {
                Console.Error.WriteLine($"Unknown node type '{dto.TypeKey}' skipped  未注册节点类型已跳过");
                continue;
            }
            node.Title = dto.Title;
            WorkflowReconstruction.RestoreInitialValues(node, dto.InputInitialValues);
            nodes.Add(node);
            if (!string.IsNullOrEmpty(dto.InstanceId)) created[dto.InstanceId] = node;
        }

        if (nodes.Count == 0)
        {
            Console.Error.WriteLine("Workflow contains no executable nodes  工作流无可执行节点");
            return FirmwareCliExitCodes.Failure;
        }

        // 构建引擎连接：优先实例 ID；旧快照缺失时回退按连接器标题匹配。
        // Build engine connections: prefer instance IDs; fall back to connector-title matching for legacy snapshots.
        var connections = new List<NodeConnection>(snapshot.Connections.Count);
        foreach (var cdto in snapshot.Connections)
        {
            var (src, _) = WorkflowReconstruction.ResolveEndpoint(nodes, created,
                cdto.SourceInstanceId, cdto.SourceConnectorTitle, output: true, fallbackOnMissingPort: false);
            var (tgt, _) = WorkflowReconstruction.ResolveEndpoint(nodes, created,
                cdto.TargetInstanceId, cdto.TargetConnectorTitle, output: false, fallbackOnMissingPort: false);

            if (src is null || tgt is null)
            {
                Console.Error.WriteLine(
                    $"Could not resolve connection '{cdto.SourceConnectorTitle}' → '{cdto.TargetConnectorTitle}'  连接无法解析");
                return FirmwareCliExitCodes.Failure;
            }
            connections.Add(new NodeConnection
            {
                SourceInstanceId = src.InstanceId,
                SourceConnector = cdto.SourceConnectorTitle,
                TargetInstanceId = tgt.InstanceId,
                TargetConnector = cdto.TargetConnectorTitle
            });
        }

        // 应用 --set 覆盖：NodeTitle.Port=Value，写入对应输入端口的 InitialValue。
        // Apply --set overrides: NodeTitle.Port=Value written to the matching input port's InitialValue.
        foreach (var (nodeSelector, port, value) in cli.Overrides)
        {
            var node = nodes.FirstOrDefault(n => string.Equals(n.Title, nodeSelector, StringComparison.OrdinalIgnoreCase))
                       ?? nodes.FirstOrDefault(n => string.Equals(n.InstanceId, nodeSelector, StringComparison.OrdinalIgnoreCase));
            if (node is null)
            {
                Console.Error.WriteLine($"--set: node not found: {nodeSelector}  节点未找到");
                return FirmwareCliExitCodes.Usage;
            }
            var input = node.Input.FirstOrDefault(c => string.Equals(c.Title, port, StringComparison.Ordinal));
            if (input is null)
            {
                Console.Error.WriteLine($"--set: input port '{port}' not found on node '{node.Title}'  输入端口未找到");
                return FirmwareCliExitCodes.Usage;
            }
            input.InitialValue = WorkflowReconstruction.ConvertInputValue(input.PortType, value);
            input.Data = null; // 覆盖值经 InitialValue 注入  The override is injected via InitialValue.
        }

        var engine = new NodeExecutionEngine(new ConsoleLog());
        var result = await engine.ExecuteAsync(nodes, connections, CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine($"Execution finished: {result.Status}  执行完成");
        if (result.FailedNodes.Count > 0)
        {
            Console.Error.WriteLine($"Failed nodes ({result.FailedNodes.Count}): {string.Join(", ", result.FailedNodes)}");
        }

        return result.Status switch
        {
            ExecutionStatus.Succeeded => FirmwareCliExitCodes.Ok,
            _ => FirmwareCliExitCodes.Failure,
        };
    }

    /// <summary>
    /// Parsed workflow CLI arguments.
    /// <para>解析后的 workflow CLI 参数。</para>
    /// </summary>
    private sealed class WorkflowCliArguments
    {
        /// <summary>Workflow file path (positional).</summary>
        /// <para>工作流文件路径（位置参数）。</para>
        public string? WorkflowPath { get; set; }

        /// <summary>Input overrides from <c>--set Node.Port=Value</c> (repeatable).</summary>
        /// <para><c>--set Node.Port=Value</c> 输入覆盖（可重复）。</para>
        public List<(string Node, string Port, string Value)> Overrides { get; } = [];

        /// <summary>Whether help was requested.</summary>
        /// <para>是否请求了帮助。</para>
        public bool HelpRequested { get; set; }

        /// <summary>Whether list-nodes was requested.</summary>
        /// <para>是否请求了列出节点类型。</para>
        public bool ListNodesRequested { get; set; }

        /// <summary>Whether list-nodes output should be JSON (default is human-readable).</summary>
        /// <para>list-nodes 输出是否为 JSON（默认人类可读）。</para>
        public bool JsonOutput { get; set; }

        /// <summary>Whether inspect was requested.</summary>
        /// <para>是否请求了 inspect 子命令。</para>
        public bool InspectRequested { get; set; }
    }

    /// <summary>
    /// Parses workflow CLI arguments (hand-rolled, AOT-friendly).
    /// <para>解析 workflow CLI 参数（手写解析器，AOT 友好）。</para>
    /// </summary>
    private static WorkflowCliArguments Parse(string[]? args)
    {
        var result = new WorkflowCliArguments();
        if (args is null || args.Length == 0 || IsHelp(args[0]))
        {
            result.HelpRequested = true;
            return result;
        }

        if (string.Equals(args[0], "list-nodes", StringComparison.OrdinalIgnoreCase))
        {
            result.ListNodesRequested = true;
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--json")
                {
                    result.JsonOutput = true;
                }
                else if (IsHelp(args[i]))
                {
                    result.HelpRequested = true;
                }
                else
                {
                    throw new WorkflowCliUsageException($"Unknown flag for 'list-nodes': {args[i]}");
                }
            }
            return result;
        }

        if (string.Equals(args[0], "inspect", StringComparison.OrdinalIgnoreCase))
        {
            result.InspectRequested = true;
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--json")
                {
                    result.JsonOutput = true;
                }
                else if (IsHelp(args[i]))
                {
                    result.HelpRequested = true;
                }
                else if (args[i].StartsWith("-", StringComparison.Ordinal))
                {
                    throw new WorkflowCliUsageException($"Unknown flag for 'inspect': {args[i]}");
                }
                else if (result.WorkflowPath is null)
                {
                    result.WorkflowPath = args[i];
                }
                else
                {
                    throw new WorkflowCliUsageException($"'inspect' takes one workflow path; got extra: {args[i]}");
                }
            }
            if (string.IsNullOrWhiteSpace(result.WorkflowPath) && !result.HelpRequested)
            {
                throw new WorkflowCliUsageException("'inspect' requires a workflow file path: workflow inspect <file.uotannode>");
            }
            return result;
        }

        if (!string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowCliUsageException($"Unknown workflow subcommand: {args[0]}  未知 workflow 子命令");
        }

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (IsHelp(arg))
            {
                result.HelpRequested = true;
                return result;
            }
            if (arg == "--set" || arg == "-s")
            {
                if (i + 1 >= args.Length)
                {
                    throw new WorkflowCliUsageException("'--set' requires a value: --set Node.Port=Value");
                }
                var spec = args[++i];
                var separator = spec.IndexOf('.');
                var assign = spec.IndexOf('=');
                if (separator <= 0 || assign <= separator + 1 || assign == spec.Length - 1)
                {
                    throw new WorkflowCliUsageException($"Malformed --set value (expected Node.Port=Value): {spec}");
                }
                result.Overrides.Add((spec[..separator], spec[(separator + 1)..assign], spec[(assign + 1)..]));
                continue;
            }
            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                throw new WorkflowCliUsageException($"Unknown flag for 'workflow run': {arg}");
            }
            if (result.WorkflowPath is null)
            {
                result.WorkflowPath = arg;
            }
            else
            {
                throw new WorkflowCliUsageException($"'workflow run' takes one workflow path; got extra: {arg}");
            }
        }

        if (string.IsNullOrWhiteSpace(result.WorkflowPath))
        {
            throw new WorkflowCliUsageException("'workflow run' requires a workflow file path: workflow run <file.uotannode>");
        }

        return result;
    }

    /// <summary>
    /// Usage error carrier so <see cref="Parse"/> stays allocation-simple at call sites.
    /// <para>用法错误载体，让 <see cref="Parse"/> 的调用点保持简洁。</para>
    /// </summary>
    private sealed class WorkflowCliUsageException(string message) : Exception(message);

    private static bool IsHelp(string arg) => arg is "--help" or "-h";

    /// <summary>
    /// <para>检查工作流文件结构并输出摘要（节点数、连接数、各节点类型与标题）。</para>
    /// Inspects a workflow file and prints a summary (node count, connection count, per-node type and title).
    /// </summary>
    private static int RunInspect(string path, bool json)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Workflow file not found: {path}  工作流文件未找到");
            return FirmwareCliExitCodes.Failure;
        }

        var snapshot = WorkflowPersistenceService.LoadAsync(path).GetAwaiter().GetResult();
        if (snapshot is null)
        {
            Console.Error.WriteLine($"Failed to load workflow: {path}  工作流加载失败");
            return FirmwareCliExitCodes.Failure;
        }

        if (json)
        {
            var obj = new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = path,
                ["nodeCount"] = snapshot.Nodes.Count,
                ["connectionCount"] = snapshot.Connections.Count,
            };

            var nodesArr = new System.Text.Json.Nodes.JsonArray();
            foreach (var n in snapshot.Nodes)
            {
                nodesArr.Append(new System.Text.Json.Nodes.JsonObject
                {
                    ["typeKey"] = n.TypeKey ?? "(unknown)",
                    ["title"] = n.Title ?? "(untitled)",
                    ["instanceId"] = n.InstanceId ?? "",
                    ["inputCount"] = n.InputInitialValues?.Count ?? 0,
                });
            }
            obj["nodes"] = nodesArr;

            var connsArr = new System.Text.Json.Nodes.JsonArray();
            foreach (var c in snapshot.Connections)
            {
                connsArr.Append(new System.Text.Json.Nodes.JsonObject
                {
                    ["source"] = c.SourceConnectorTitle ?? "",
                    ["target"] = c.TargetConnectorTitle ?? "",
                    ["sourceInstance"] = c.SourceInstanceId ?? "",
                    ["targetInstance"] = c.TargetInstanceId ?? "",
                });
            }
            obj["connections"] = connsArr;

            Console.WriteLine(obj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"Workflow: {Path.GetFileName(path)}");
            Console.WriteLine($"  Nodes:       {snapshot.Nodes.Count}");
            Console.WriteLine($"  Connections: {snapshot.Connections.Count}");
            Console.WriteLine();
            Console.WriteLine("  Nodes:");
            foreach (var n in snapshot.Nodes)
            {
                var inputs = n.InputInitialValues is { Count: > 0 }
                    ? $"  [inputs: {string.Join(", ", n.InputInitialValues.Keys)}]"
                    : "";
                Console.WriteLine($"    [{n.TypeKey ?? "?"}] {n.Title ?? "(untitled)"}{inputs}");
            }
            Console.WriteLine();
            Console.WriteLine("  Connections:");
            foreach (var c in snapshot.Connections)
            {
                Console.WriteLine($"    {c.SourceConnectorTitle ?? "?"} → {c.TargetConnectorTitle ?? "?"}");
            }
        }

        return FirmwareCliExitCodes.Ok;
    }

    /// <summary>
    /// <para>列出全部已注册节点类型及其输入/输出端口定义。</para>
    /// Lists all registered node types with their input/output port definitions.
    /// </summary>
    private static int RunListNodes(bool json)
    {
        NodeFactoryRegistrar.RegisterAll();
        var keys = NodeTypeRegistry.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();

        if (json)
        {
            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var key in keys)
            {
                var node = NodeTypeRegistry.Create(key);
                if (node is null) continue;

                var obj = new System.Text.Json.Nodes.JsonObject
                {
                    ["typeKey"] = key,
                    ["title"] = node.Title,
                };

                var inputs = new System.Text.Json.Nodes.JsonArray();
                foreach (var c in node.Input)
                {
                    inputs.Append(new System.Text.Json.Nodes.JsonObject
                    {
                        ["title"] = c.Title,
                        ["portType"] = c.PortType.ToString(),
                    });
                }
                obj["input"] = inputs;

                var outputs = new System.Text.Json.Nodes.JsonArray();
                foreach (var c in node.Output)
                {
                    outputs.Append(new System.Text.Json.Nodes.JsonObject
                    {
                        ["title"] = c.Title,
                        ["portType"] = c.PortType.ToString(),
                    });
                }
                obj["output"] = outputs;

                arr.Append(obj);
            }

            Console.WriteLine(arr.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        else
        {
            Console.WriteLine($"Registered node types ({keys.Length}):");
            Console.WriteLine("  已注册节点类型：");
            Console.WriteLine();
            foreach (var key in keys)
            {
                var node = NodeTypeRegistry.Create(key);
                if (node is null) continue;

                Console.WriteLine($"  [{key}] {node.Title}");
                if (node.Input.Count > 0)
                {
                    Console.Write("    Input:  ");
                    Console.WriteLine(string.Join(", ", node.Input.Select(c => $"{c.Title}({c.PortType})")));
                }
                if (node.Output.Count > 0)
                {
                    Console.Write("    Output: ");
                    Console.WriteLine(string.Join(", ", node.Output.Select(c => $"{c.Title}({c.PortType})")));
                }
            }
        }

        return FirmwareCliExitCodes.Ok;
    }

    /// <summary>
    /// Prints the workflow CLI usage to the given writer.
    /// <para>向给定文本写入器打印 workflow CLI 用法。</para>
    /// </summary>
    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine();
        writer.WriteLine("UotanStudio workflow CLI  工作流命令行工具");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  workflow run <file.uotannode> [options]");
        writer.WriteLine("  workflow list-nodes [--json]");
        writer.WriteLine("  workflow inspect <file.uotannode> [--json]");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        writer.WriteLine("  run          Execute a saved workflow file headlessly");
        writer.WriteLine("  list-nodes   List all registered node types and their ports");
        writer.WriteLine("  inspect      Show the structure summary of a workflow file");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  -s, --set <Node.Port=Value>   Override an input port's initial value (repeatable, run only)");
        writer.WriteLine("      --json                    Output as JSON (list-nodes only)");
        writer.WriteLine("  -h, --help                    Show this help and exit");
        writer.WriteLine();
        writer.WriteLine("Exit codes:");
        writer.WriteLine("  0  Success");
        writer.WriteLine("  1  Failure (execution failed/cancelled, workflow not found)");
        writer.WriteLine("  2  Usage error (missing/extra args, unknown flag, bad --set)");
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine("  workflow run my_flow.uotannode");
        writer.WriteLine("  workflow run my_flow.uotannode --set \"ReadFile.Path=boot.img\" --set \"Loop.Count=3\"");
        writer.WriteLine("  workflow list-nodes");
        writer.WriteLine("  workflow list-nodes --json");
    }

    /// <summary>
    /// A console-backed <see cref="IUnifiedLog"/> that streams engine/node logs to stdout.
    /// <para>基于控制台的 <see cref="IUnifiedLog"/>，把引擎与节点日志流式输出到 stdout。</para>
    /// </summary>
    private sealed class ConsoleLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message)
        {
            if (level >= LogLevel.Info)
            {
                Console.WriteLine($"[{source}] {message}");
            }
        }

        public void Write(LogLevel level, string source, string format, params object[] args)
            => Write(level, source, string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));

        public void SetMinimumLevel(LogLevel level) { }
    }
}
