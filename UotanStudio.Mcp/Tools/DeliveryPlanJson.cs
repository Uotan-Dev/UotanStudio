using System.Text.Json;
using UotanStudio.Delivery;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// JSON entry for delivery plans on the MCP boundary, reusing the delivery library's
/// AOT source-generated context so wire format and host persistence stay identical.
/// <para>MCP 边界上交付计划的 JSON 入口，复用交付库的 AOT source-gen 上下文，
/// 保证线上格式与宿主持久化一致。</para>
/// </summary>
internal static class DeliveryPlanJson
{
    /// <summary>
    /// Deserializes a delivery plan; returns null for structurally invalid JSON.
    /// <para>反序列化交付计划；结构非法的 JSON 返回 null。</para>
    /// </summary>
    /// <param name="json">Plan JSON text read with the delivery library's source-generated context; a JSON <c>null</c> document yields no plan. <para>用交付库的源生成上下文读取的 JSON 文本；JSON 为 <c>null</c> 的文档不会得到计划。</para></param>
    public static DeliveryPlan? Deserialize(string json)
        => JsonSerializer.Deserialize(json, DeliveryJsonContext.Default.DeliveryPlan);

    /// <summary>
    /// Parses a plan JSON and confines every path-bearing field to the sandbox.
    /// <para>解析计划 JSON 并把其中的路径限制到沙箱内。</para>
    /// </summary>
    /// <param name="sandbox">Sandbox that resolves every path field of the plan. <para>用于解析计划中各路径字段的沙箱。</para></param>
    /// <param name="planJson">
    /// Plan JSON document; null or whitespace-only text is rejected before parsing.
    /// <para>计划 JSON 文档；null 或仅含空白的文本会在解析前被拒绝。</para>
    /// </param>
    public static DeliveryPlan ParseConfinedPlan(PathSandbox sandbox, string planJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planJson);
        var plan = Deserialize(planJson)
            ?? throw new InvalidDataException("Delivery plan JSON could not be parsed.");
        return Confine(plan, sandbox);
    }

    /// <summary>
    /// Rewrites every path-bearing field to its sandbox-resolved equivalent before validation.
    /// <para>校验前把所有含路径的字段重写为沙箱解析后的等价路径。</para>
    /// An unset output directory stays unset: it is a draft plan the backend must reject with its own
    /// validation message, not a path the sandbox may resolve, and resolving an empty string throws
    /// before the client ever learns which field is missing.
    /// <para>未设置的输出目录保持未设置：这是应由后端以自身校验信息拒绝的草稿计划，而不是沙箱可解析的路径；
    /// 对空串做解析会在客户端得知缺失字段之前先抛出异常。</para>
    /// Deserialization leaves every omitted section null because the plan type is built without running
    /// its initializers, so each section and collection is normalized to its empty form here: a client
    /// plan that skips a section must reach plan validation, not a null reference. Local zstd paths
    /// normalize null to the empty string, matching the plan's non-nullable value contract.
    /// <para>计划类型在构造时不会执行其初始化器，因此 JSON 中省略的节会全部反序列化为 null，所以这里把每一节
    /// 与集合归一为空形态：省略节的客户端计划必须进入计划校验，而不是空引用异常。本地 zstd 路径把 null 归一为
    /// 空字符串，与计划值的非空契约一致。</para>
    /// </summary>
    private static DeliveryPlan Confine(DeliveryPlan plan, PathSandbox sandbox)
    {
        var package = plan.Package ?? new DeliveryPackageOptions();
        var payload = plan.Payload ?? new DeliveryPayloadOptions();
        return plan with
        {
            OutputDirectory = string.IsNullOrWhiteSpace(plan.OutputDirectory)
                ? plan.OutputDirectory
                : sandbox.ResolveOutput(plan.OutputDirectory),
            Package = package with
            {
                CertificatePath = ConfineOptional(package.CertificatePath, sandbox),
                SigningKeyPath = ConfineOptional(package.SigningKeyPath, sandbox),
                RecoveryUpdaterPath = ConfineOptional(package.RecoveryUpdaterPath, sandbox)
            },
            Payload = payload with
            {
                DynamicPartitionsFile = ConfineOptional(payload.DynamicPartitionsFile, sandbox),
                SignatureFile = ConfineOptional(payload.SignatureFile, sandbox),
                SigningKeyPath = ConfineOptional(payload.SigningKeyPath, sandbox),
                PostinstallFile = ConfineOptional(payload.PostinstallFile, sandbox)
            },
            LocalZstd = (plan.LocalZstd ?? []).ToDictionary(p => p.Key, p => ConfineOptional(p.Value, sandbox) ?? string.Empty),
            Container = [.. (plan.Container ?? []).Select(f => ConfineFile(f, sandbox))],
            Files = [.. (plan.Files ?? []).Select(f => ConfineFile(f, sandbox))]
        };
    }

    private static string ConfineOptional(string path, PathSandbox sandbox)
        => string.IsNullOrEmpty(path) ? path : sandbox.Resolve(path);

    private static DeliveryFile ConfineFile(DeliveryFile file, PathSandbox sandbox)
        => file with { SourcePath = string.IsNullOrEmpty(file.SourcePath) ? "" : sandbox.Resolve(file.SourcePath) };
}