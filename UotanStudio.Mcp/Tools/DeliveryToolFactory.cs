using System.ComponentModel;
using ModelContextProtocol.Server;
using UotanStudio.Services.Delivery;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Builds the delivery MCP tools backed by <see cref="DeliveryValidation"/>,
/// <see cref="DeliveryOutputService"/> and the <see cref="DeliveryPlan"/> JSON contract.
/// <para>基于 <see cref="DeliveryValidation"/>、<see cref="DeliveryOutputService"/> 与
/// <see cref="DeliveryPlan"/> JSON 契约构建交付域 MCP 工具。</para>
/// The delivery backend intentionally knows nothing about UI or key storage: validation and
/// output listing run standalone, while building requires the host to inject an
/// <see cref="IDeliveryTools"/> implementation (tool preparation, platform binaries); without it
/// the build tool reports the gap instead of pretending to work.
/// <para>交付后端有意不感知 UI 与密钥存储：校验与输出列举可独立运行；构建则需要宿主注入
/// <see cref="IDeliveryTools"/>（工具准备、平台二进制）；未注入时构建工具如实报告缺口。</para>
/// </summary>
internal static class DeliveryToolFactory
{
    /// <summary>
    /// Injected by the desktop host in companion mode; null in headless mode.
    /// <para>伴随模式下由桌面宿主注入；无头模式下为 null。</para>
    /// </summary>
    internal static Func<IDeliveryTools?>? ToolsProvider { get; set; }

    internal static IReadOnlyList<McpServerTool> CreateTools(PathSandbox sandbox)
    {
        var validate = McpServerTool.Create(
            ([Description("Delivery plan JSON (DeliveryPlan schema; paths sandbox-relative or absolute inside the sandbox).")] string planJson,
             [Description("Skip source-image existence checks.")] bool skipSources = false) => DeliveryToolHandlers.Validate(sandbox, planJson, skipSources),
            new McpServerToolCreateOptions
            {
                Name = "delivery_validate",
                Description = "Validate a delivery plan (mutual exclusion of Super/Payload/Recovery "
                    + "modes, DAT/BR grouping, script names, paths) without building anything.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

        var outputs = McpServerTool.Create(
            ([Description("Delivery plan JSON.")] string planJson => DeliveryToolHandlers.ListOutputs(sandbox, planJson),
            new McpServerToolCreateOptions
            {
                Name = "delivery_list_outputs",
                Description = "List the output paths a delivery plan would produce and which of "
                    + "them already exist on disk.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

        var build = McpServerTool.Create(
            ([Description("Delivery plan JSON.")] string planJson,
             [Description("Existing output file names allowed to be replaced (defaults to none).")] string[]? overwrite = null) => DeliveryToolHandlers.Build(sandbox, planJson, overwrite),
            new McpServerToolCreateOptions
            {
                Name = "delivery_build",
                Description = "Build the delivery output (Super/Payload/Recovery/organized files) "
                    + "from a validated delivery plan. Fails honestly when no tool-preparation "
                    + "service has been injected (headless mode).",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });

        return [validate, outputs, build];
    }
}

/// <summary>
/// Implementations for the delivery tool delegates; kept separate for direct unit testing.
/// <para>交付工具委托的实现；独立存放便于直接单元测试。</para>
/// </summary>
internal static class DeliveryToolHandlers
{
    public static Task<string> Validate(PathSandbox sandbox, string planJson, bool skipSources)
        => Task.Run(() =>
        {
            var plan = ParsePlan(sandbox, planJson);
            try
            {
                DeliveryValidation.Validate(plan, sources: !skipSources);
                return ToolJson.Serialize(new ToolResult(true, "Delivery plan is valid."));
            }
            catch (Exception ex)
            {
                return ToolJson.Serialize(new ToolResult(false, ex.Message));
            }
        });

    public static Task<string> ListOutputs(PathSandbox sandbox, string planJson)
        => Task.Run(() =>
        {
            var plan = ParsePlan(sandbox, planJson);
            var planned = DeliveryOutputService.PlannedOutputPaths(plan).ToList();
            var existing = DeliveryOutputService.FindExistingOutputs(plan);
            return ToolJson.Serialize(new ToolResult(true,
                $"{planned.Count} planned output path(s), {existing.Length} already exist.",
                new { planned, existing }));
        });

    public static Task<string> Build(PathSandbox sandbox, string planJson, string[]? overwrite)
        => Task.Run(async () =>
        {
            var tools = DeliveryToolFactory.ToolsProvider?.Invoke();
            if (tools is null)
            {
                return ToolJson.Serialize(new ToolResult(false,
                    "Delivery build requires a tool-preparation service (IDeliveryTools), which is "
                    + "only injected when UotanStudio runs with its desktop host. Use delivery_validate "
                    + "and delivery_list_outputs instead, or launch the companion mode."));
            }

            var plan = ParsePlan(sandbox, planJson);
            var overwriteSet = overwrite is { Length: > 0 } ? new HashSet<string>(overwrite) : null;
            try
            {
                var service = new DeliveryOutputService(tools);
                var outputs = await service.ExportAsync(plan, progress: null, token: default,
                    overwrite: overwriteSet).ConfigureAwait(false);
                return ToolJson.Serialize(new ToolResult(true,
                    $"Delivery build produced {outputs.Length} output path(s).", outputs));
            }
            catch (Exception ex)
            {
                return ToolJson.Serialize(new ToolResult(false, ex.Message));
            }
        });

    /// <summary>
    /// Parses a plan from JSON and confines its paths to the sandbox.
    /// <para>解析计划 JSON 并把其中的路径限制到沙箱内。</para>
    /// </summary>
    private static DeliveryPlan ParsePlan(PathSandbox sandbox, string planJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planJson);
        var plan = DeliveryPlanJson.Deserialize(planJson)
            ?? throw new InvalidDataException("Delivery plan JSON could not be parsed.");
        return Confine(plan, sandbox);
    }

    /// <summary>
    /// Rewrites every path-bearing field to its sandbox-resolved equivalent before validation.
    /// <para>校验前把所有含路径的字段重写为沙箱解析后的等价路径。</para>
    /// </summary>
    private static DeliveryPlan Confine(DeliveryPlan plan, PathSandbox sandbox) => plan with
    {
        OutputDirectory = sandbox.ResolveOutput(plan.OutputDirectory),
        Package = plan.Package with
        {
            CertificatePath = ConfineOptional(plan.Package.CertificatePath, sandbox),
            SigningKeyPath = ConfineOptional(plan.Package.SigningKeyPath, sandbox),
            RecoveryUpdaterPath = ConfineOptional(plan.Package.RecoveryUpdaterPath, sandbox)
        },
        Payload = plan.Payload with
        {
            DynamicPartitionsFile = ConfineOptional(plan.Payload.DynamicPartitionsFile, sandbox),
            SignatureFile = ConfineOptional(plan.Payload.SignatureFile, sandbox),
            SigningKeyPath = ConfineOptional(plan.Payload.SigningKeyPath, sandbox),
            PostinstallFile = ConfineOptional(plan.Payload.PostinstallFile, sandbox)
        },
        LocalZstd = plan.LocalZstd.ToDictionary(p => p.Key, p => ConfineOptional(p.Value, sandbox)),
        Container = plan.Container.Select(f => ConfineFile(f, sandbox)).ToList(),
        Files = plan.Files.Select(f => ConfineFile(f, sandbox)).ToList()
    };

    private static string ConfineOptional(string path, PathSandbox sandbox)
        => string.IsNullOrEmpty(path) ? path : sandbox.Resolve(path);

    private static Services.Delivery.DeliveryFile ConfineFile(Services.Delivery.DeliveryFile file, PathSandbox sandbox)
        => file with { SourcePath = string.IsNullOrEmpty(file.SourcePath) ? "" : sandbox.Resolve(file.SourcePath) };
}