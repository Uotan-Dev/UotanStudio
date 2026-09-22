using System.Text.Json;

namespace UotanStudio.Services.Delivery;

/// <summary>
/// Public, reflection-free entry point that lets headless consumers drive delivery
/// validation, output listing, tool preparation and builds from a serialized <see cref="DeliveryPlan"/>
/// JSON contract. It deliberately exposes only plain types so no internal plan/build record crosses the
/// assembly boundary and no <c>InternalsVisibleTo</c> grant is needed.
/// <para>面向无头调用方（命令行）的公共无反射入口：以交付计划 JSON 契约驱动校验、输出列举、工具准备与构建。
/// 有意只暴露普通类型，内部计划/构建记录不跨越程序集边界，也无需 <c>InternalsVisibleTo</c> 授权。</para>
/// </summary>
/// <remarks>
/// Validation and output listing run fully standalone. Build and tool preparation download pinned
/// platform-tools and zstd over the network, so they require a caller-supplied <see cref="HttpClient"/>
/// and a writable tool cache; plan-level image signing is driven by the key paths already inside the plan
/// rather than an injected keystore, so an unsigned-only host fails honestly when a key is required.
/// <para>校验与输出列举完全独立。构建与工具准备会通过网络下载钉定的 platform-tools 与 zstd，
/// 因此需要调用方提供的 <see cref="HttpClient"/> 与可写工具缓存；镜像签名由计划内已有的密钥路径驱动，
/// 而非注入的密钥库，因此缺少密钥时纯无签名宿主会如实失败。</para>
/// </remarks>
public sealed class DeliveryService(HttpClient httpClient, string? toolCacheDirectory = null)
{
    /// <summary><para>默认工具缓存目录（当前用户 LocalApplicationData 下）。</para>
    /// Default tool cache directory under the current user's local application data.</summary>
    public static string DefaultToolCacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UotanStudio", "delivery-tools");

    /// <summary>
    /// <para>校验交付计划；合法返回 <see langword="null"/>，否则返回首条错误消息。</para>
    /// Validates a delivery plan; returns <see langword="null"/> when valid, otherwise the first error message.
    /// </summary>
    /// <param name="planJson"><para>序列化后的 <c>DeliveryPlan</c> JSON。</para>Serialized <c>DeliveryPlan</c> JSON.</param>
    /// <param name="checkSources"><para>是否检查源镜像存在。</para>Whether source images must exist.</param>
    public static string? Validate(string planJson, bool checkSources = true)
    {
        var plan = ParsePlan(planJson);
        try
        {
            DeliveryValidation.Validate(plan, sources: checkSources);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// <para>返回计划将产出的输出路径及其中已存在于磁盘的子集。</para>
    /// Returns the planned output paths and the subset that already exists on disk.
    /// </summary>
    public static (IReadOnlyList<string> Planned, IReadOnlyList<string> Existing) ListOutputs(string planJson)
    {
        var plan = ParsePlan(planJson);
        return ([.. DeliveryOutputService.PlannedOutputPaths(plan)], DeliveryOutputService.FindExistingOutputs(plan));
    }

    /// <summary>
    /// <para>把交付所需工具下载到可写缓存并物化到 <paramref name="directory"/> 下的 <c>bin/</c> 布局；
    /// 目录为空时回退到计划的输出目录。此操作需要网络。</para>
    /// Downloads the delivery tools into a writable cache and materializes them under <c>bin/</c> beneath
    /// <paramref name="directory"/> (falling back to the plan's output directory when blank). Network-bound.
    /// </summary>
    public async Task PrepareToolsAsync(string planJson, string? directory = null,
        IProgress<string>? log = null, CancellationToken cancellationToken = default)
    {
        var plan = ParsePlan(planJson);
        DeliveryValidation.Validate(plan);
        var target = string.IsNullOrWhiteSpace(directory) ? plan.OutputDirectory : directory;
        if (string.IsNullOrWhiteSpace(target)) throw new InvalidDataException("Select an output directory.");
        target = Path.GetFullPath(target);
        Directory.CreateDirectory(target);
        await NewToolService().PrepareAsync(plan, target, ToProgress(log), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>按已校验的计划执行完整构建，返回产出的输出路径；内部会准备平台工具。此操作需要网络。</para>
    /// Runs a full build from a validated plan and returns the produced output paths; it prepares the
    /// platform tools internally. Network-bound.
    /// </summary>
    public async Task<IReadOnlyList<string>> BuildAsync(string planJson, IReadOnlySet<string>? overwrite = null,
        IProgress<string>? log = null, CancellationToken cancellationToken = default)
    {
        var plan = ParsePlan(planJson);
        var outputs = await new DeliveryOutputService(NewToolService())
            .ExportAsync(plan, ToProgress(log), cancellationToken, overwrite).ConfigureAwait(false);
        return outputs;
    }

    private DeliveryToolService NewToolService()
        => new(httpClient, string.IsNullOrWhiteSpace(toolCacheDirectory) ? DefaultToolCacheDirectory : toolCacheDirectory);

    private static DeliveryPlan ParsePlan(string planJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planJson);
        return JsonSerializer.Deserialize(planJson, DeliveryJsonContext.Default.DeliveryPlan)
            ?? throw new InvalidDataException("Delivery plan JSON could not be parsed.");
    }

    private static IProgress<DeliveryProgress>? ToProgress(IProgress<string>? log)
        => log is null ? null : new ProgressAdapter(log);

    private sealed class ProgressAdapter(IProgress<string> log) : IProgress<DeliveryProgress>
    {
        public void Report(DeliveryProgress value) => log.Report($"{value.Percent:0}% {value.Message}");
    }
}
