using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
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
/// the build tool reports the gap instead of pretending to work. Builds run through the public
/// <see cref="DeliveryService"/> facade, so the sandbox-confined plan is re-serialized back to
/// the JSON contract before the facade re-parses it.
/// <para>交付后端有意不感知 UI 与密钥存储：校验与输出列举可独立运行；构建则需要宿主注入
/// <see cref="IDeliveryTools"/>（工具准备、平台二进制）；未注入时构建工具如实报告缺口。
/// 构建经公共 <see cref="DeliveryService"/> 门面对外执行，因此沙箱收敛后的计划会先重新序列化
/// 回 JSON 契约，再由门面解析。</para>
/// Every delegate ends with the ambient <see cref="IProgress{T}"/> and
/// <see cref="CancellationToken"/> parameters the MCP server binds itself; neither appears in the
/// emitted <c>inputSchema</c>, so clients cannot pass them but cancellation and progress propagate
/// into the delivery build that supports them.
/// <para>每个委托末尾都是 MCP 服务器自行绑定的环境参数 <see cref="IProgress{T}"/> 与
/// <see cref="CancellationToken"/>；两者都不出现在生成的 <c>inputSchema</c> 中，客户端无法传入，
/// 但取消与进度会传导到支持它们的交付构建。</para>
/// </summary>
internal static class DeliveryToolFactory
{
    /// <summary>
    /// Builds the delivery tools for one host, binding the tool-preparation provider that host was
    /// given; the provider is no longer a process-wide mutable static, so a headless server can never
    /// inherit another session's injection and the honest-gap result depends only on this host's config.
    /// <para>为单个宿主构建交付工具，并绑定该宿主获得的工具准备服务提供器；该提供器不再是进程级可变静态，
    /// 因此无头服务器不会继承其他会话的注入，能力缺口的如实上报只取决于本宿主配置。</para>
    /// </summary>
    /// <param name="sandbox"><para>约束计划与输出路径的沙箱。</para>The sandbox confining plan and output paths.</param>
    /// <param name="deliveryTools"><para>本宿主的工具准备服务提供器；为 <see langword="null"/> 时 <c>delivery_build</c> 如实报告能力缺口。</para>
    /// This host's tool-preparation provider; when <see langword="null"/> <c>delivery_build</c> reports the capability gap.</param>
    /// <param name="signingKeys"><para>本宿主的签名密钥供应器；为 <see langword="null"/> 时签名计划如实失败。</para>
    /// This host's signing key provider; signed plans fail honestly when it is <see langword="null"/>.</param>
    internal static IReadOnlyList<McpServerTool> CreateTools(
        PathSandbox sandbox,
        Func<IDeliveryTools?>? deliveryTools = null,
        Func<IDeliverySigningKeyProvider?>? signingKeys = null)
    {
        var validate = McpServerTool.Create(
            ([Description("Delivery plan JSON (DeliveryPlan schema; paths sandbox-relative or absolute inside the sandbox).")] string planJson,
             [Description("Skip source-image existence checks.")] bool skipSources = false,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => Task.FromResult(DeliveryToolHandlers.Validate(sandbox, planJson, skipSources)), cancellationToken),
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
            ([Description("Delivery plan JSON.")] string planJson,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => Task.FromResult(DeliveryToolHandlers.ListOutputs(sandbox, planJson)), cancellationToken),
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
             [Description("Existing output file names allowed to be replaced (defaults to none).")] string[]? overwrite = null,
             IProgress<ProgressNotificationValue>? progress = null,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => DeliveryToolHandlers.Build(sandbox, planJson, overwrite, progress, deliveryTools, signingKeys, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "delivery_build",
                Description = "Build the delivery output (Super/Payload/Recovery/organized files) "
                    + "from a validated delivery plan. Fails honestly when this host has no "
                    + "tool-preparation service injected. Honors request cancellation and "
                    + "emits notifications/progress when the client supplies a progress token.",
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
/// All handlers return the <see cref="ToolResult"/> envelope and accept the caller's
/// <see cref="CancellationToken"/>; <see cref="ToolGuard"/> turns the envelope into the protocol result.
/// <para>所有处理器返回 <see cref="ToolResult"/> 信封并接受调用方的 <see cref="CancellationToken"/>；
/// 由 <see cref="ToolGuard"/> 把信封转为协议结果。</para>
/// </summary>
internal static class DeliveryToolHandlers
{
    public static ToolResult Validate(PathSandbox sandbox, string planJson, bool skipSources)
    {
        var plan = ParsePlan(sandbox, planJson);
        try
        {
            DeliveryValidation.Validate(plan, sources: !skipSources);
            return new ToolResult(true, "Delivery plan is valid.");
        }
        catch (Exception ex)
        {
            return new ToolResult(false, ex.Message);
        }
    }

    public static ToolResult ListOutputs(PathSandbox sandbox, string planJson)
    {
        var plan = ParsePlan(sandbox, planJson);
        var planned = DeliveryOutputService.PlannedOutputPaths(plan).ToList();
        var existing = DeliveryOutputService.FindExistingOutputs(plan);
        return new ToolResult(true,
            $"{planned.Count} planned output path(s), {existing.Length} already exist.",
            ToolJson.Data(new DeliveryOutputsPayload(planned, existing)));
    }

    /// <summary>
    /// Runs a full delivery build through the public <see cref="DeliveryService"/> facade, threading
    /// the request's cancellation token and progress sink all the way down. The sandbox-confined plan
    /// is re-serialized back to the delivery JSON contract first, because the facade parses plain
    /// JSON and must only ever see sandbox-resolved paths.
    /// <para>通过公共 <see cref="DeliveryService"/> 门面执行完整交付构建，把请求的取消令牌与进度
    /// 接收端一路贯穿到底。沙箱收敛后的计划会先重新序列化回交付 JSON 契约——门面只解析纯 JSON，
    /// 必须只看到经沙箱解析后的路径。</para>
    /// </summary>
    /// <param name="sandbox"><para>约束计划路径的沙箱。</para>The sandbox confining the plan's paths.</param>
    /// <param name="planJson"><para>待构建的交付计划 JSON。</para>The delivery plan JSON to build from.</param>
    /// <param name="overwrite"><para>允许覆盖的既有输出文件名，可为 null。</para>Existing output file names allowed to be replaced, possibly null.</param>
    /// <param name="progress"><para>MCP 绑定的进度实例，可能为 <see langword="null"/>。</para>The MCP-bound progress instance, possibly <see langword="null"/>.</param>
    /// <param name="deliveryTools"><para>本宿主的工具准备服务提供器；为 <see langword="null"/> 或未产出实例时如实报告能力缺口。</para>
    /// This host's tool-preparation provider; when <see langword="null"/> or empty the capability gap is reported honestly.</param>
    /// <param name="signingKeys"><para>本宿主的签名密钥供应器；为 <see langword="null"/> 时签名计划如实失败。</para>
    /// This host's signing key provider; signed plans fail honestly when it is <see langword="null"/>.</param>
    /// <param name="cancellationToken"><para>取消进行中构建或工具下载的令牌。</para>Token cancelling the in-flight build or tool download.</param>
    /// <returns><para>描述产出路径数量的结果信封。</para>An envelope summarizing the produced output paths.</returns>
    /// <exception cref="OperationCanceledException"><para>客户端在构建中请求取消时重新抛出，交由 <see cref="ToolGuard"/> 映射为取消结果。</para>
    /// Rethrown when the client requests cancellation during the build so <see cref="ToolGuard"/> maps it to a cancellation result.</exception>
    public static async Task<ToolResult> Build(
        PathSandbox sandbox, string planJson, string[]? overwrite,
        IProgress<ProgressNotificationValue>? progress, Func<IDeliveryTools?>? deliveryTools,
        Func<IDeliverySigningKeyProvider?>? signingKeys, CancellationToken cancellationToken)
    {
        if (deliveryTools?.Invoke() is null)
        {
            return new ToolResult(false,
                "Delivery build requires a tool-preparation service (IDeliveryTools), which is "
                + "only injected when UotanStudio runs with its desktop host. Use delivery_validate "
                + "and delivery_list_outputs instead, or launch the companion mode.");
        }

        var plan = ParsePlan(sandbox, planJson);
        var confinedPlanJson = JsonSerializer.Serialize(plan, DeliveryJsonContext.Default.DeliveryPlan);
        var overwriteSet = overwrite is { Length: > 0 } ? new HashSet<string>(overwrite) : null;
        try
        {
            using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var outputs = await new DeliveryService(httpClient, signingKeys: signingKeys?.Invoke())
                .BuildAsync(confinedPlanJson, overwriteSet, ToBuildProgress(progress), cancellationToken)
                .ConfigureAwait(false);
            return new ToolResult(true,
                $"Delivery build produced {outputs.Count} output path(s).",
                ToolJson.Data(outputs.ToArray()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Parses a plan from JSON and confines its paths to the sandbox.
    /// <para>解析计划 JSON 并把其中的路径限制到沙箱内。</para>
    /// </summary>
    private static DeliveryPlan ParsePlan(PathSandbox sandbox, string planJson)
        => DeliveryPlanJson.ParseConfinedPlan(sandbox, planJson);

    /// <summary>
    /// <para>把协议进度目标转为交付门面的强类型进度接收端；没有进度令牌时返回 <see langword="null"/>，
    /// 底层实现因此不会消耗上报开销。</para>
    /// Converts the protocol progress sink into the facade's typed progress sink; without a progress token
    /// this returns <see langword="null"/> so backing implementations skip reporting overhead.
    /// </summary>
    /// <param name="progress"><para>MCP 绑定的进度实例，可能为 <see langword="null"/>。</para>The MCP-bound progress instance, possibly <see langword="null"/>.</param>
    /// <returns><para>供交付构建上报进度值的接收端。</para>A progress sink the delivery build reports to.</returns>
    private static IProgress<DeliveryBuildProgress>? ToBuildProgress(IProgress<ProgressNotificationValue>? progress) =>
        progress is null
            ? null
            : new Progress<DeliveryBuildProgress>(value => progress.Report(ToNotification(value)));

    /// <summary>
    /// <para>把门面进度值转为协议进度通知；总进度固定为 100，消息取门面的可读文本。</para>
    /// Maps a facade progress value onto a protocol progress notification with the total pinned to 100 and
    /// the facade's human-readable text as the message.
    /// </summary>
    /// <param name="value"><para>门面输出的一帧进度值。</para>One progress value emitted by the delivery facade.</param>
    /// <returns><para>百分比与文本组成的协议进度值。</para>A protocol progress value carrying the percent and the text.</returns>
    private static ProgressNotificationValue ToNotification(DeliveryBuildProgress value)
        => new() { Progress = (float)value.Percent, Total = 100f, Message = value.Message };
}