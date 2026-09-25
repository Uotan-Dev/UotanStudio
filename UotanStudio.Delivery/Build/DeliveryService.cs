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
/// <param name="httpClient">
/// HTTP client used to download the pinned platform-tools and zstd during tool preparation and builds; validation and output listing ignore it.
/// <para>构建与工具准备期间下载钉定的 platform-tools 与 zstd 所使用的 HTTP 客户端；校验与输出列举不使用它。</para>
/// </param>
/// <param name="toolCacheDirectory">
/// Writable directory holding the downloaded tool cache; null or blank falls back to <see cref="DefaultToolCacheDirectory"/>.
/// <para>存放已下载工具缓存的可写目录；为 null 或空白时回退到 <see cref="DefaultToolCacheDirectory"/>。</para>
/// </param>
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
    /// <value>The absolute, once-resolved cache directory used when no explicit tool cache is supplied.
    /// <para>未显式提供工具缓存时使用的绝对路径，仅解析一次。</para></value>
    public static string DefaultToolCacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UotanStudio", "delivery-tools");

    /// <summary>
    /// <para>校验交付计划；合法返回 <see langword="null"/>，否则返回首条错误消息。</para>
    /// Validates a delivery plan; returns <see langword="null"/> when valid, otherwise the first error message.
    /// </summary>
    /// <param name="planJson"><para>序列化后的 <c>DeliveryPlan</c> JSON。</para>Serialized <c>DeliveryPlan</c> JSON.</param>
    /// <param name="checkSources"><para>是否检查源镜像存在。</para>Whether source images must exist.</param>
    /// <returns>The first validation error message, or <see langword="null"/> when the plan is valid.
    /// <para>首条校验错误消息；计划合法时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="System.ArgumentException"><paramref name="planJson"/> is null, empty or whitespace.
    /// <para><paramref name="planJson"/> 为 null、空或仅空白。</para></exception>
    /// <exception cref="System.IO.InvalidDataException"><paramref name="planJson"/> is not a parsable delivery plan.
    /// <para><paramref name="planJson"/> 不是可解析的交付计划。</para></exception>
    /// <exception cref="System.OperationCanceledException">Validation observed a cancellation request; it is rethrown.
    /// <para>校验过程中收到取消请求；该异常会被重新抛出。</para></exception>
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
    /// <param name="planJson">Serialized <c>DeliveryPlan</c> JSON describing the intended outputs.
    /// <para>描述预期输出的序列化 <c>DeliveryPlan</c> JSON。</para></param>
    /// <returns>A tuple of every planned output path and the subset already present on disk.
    /// <para>包含所有计划输出路径以及其中已存在于磁盘子集的元组。</para></returns>
    /// <exception cref="System.ArgumentException"><paramref name="planJson"/> is null, empty or whitespace.
    /// <para><paramref name="planJson"/> 为 null、空或仅空白。</para></exception>
    /// <exception cref="System.IO.InvalidDataException"><paramref name="planJson"/> is not a parsable delivery plan.
    /// <para><paramref name="planJson"/> 不是可解析的交付计划。</para></exception>
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
    /// <param name="planJson">Serialized <c>DeliveryPlan</c> JSON; the plan is validated before any download.
    /// <para>序列化 <c>DeliveryPlan</c> JSON；下载前会先校验计划。</para></param>
    /// <param name="directory">Directory to host the <c>bin/</c> layout; when null or blank the plan's output directory is used.
    /// <para>承载 <c>bin/</c> 布局的目录；为 null 或空白时使用计划的输出目录。</para></param>
    /// <param name="log">Optional sink for human-readable progress lines. <para>可选的进度文本接收器。</para></param>
    /// <param name="cancellationToken">Cancels an in-flight download or copy. <para>取消进行中的下载或复制。</para></param>
    /// <returns>A task that completes once the tools are materialized under the target <c>bin/</c> layout.
    /// <para>在目标 <c>bin/</c> 布局下完成工具物化后结束的任务。</para></returns>
    /// <exception cref="System.ArgumentException"><paramref name="planJson"/> is null, empty or whitespace.
    /// <para><paramref name="planJson"/> 为 null、空或仅空白。</para></exception>
    /// <exception cref="System.IO.InvalidDataException">The plan cannot be parsed, or resolves to no writable target directory.
    /// <para>计划无法解析，或解析后没有可写的目标目录。</para></exception>
    /// <exception cref="System.OperationCanceledException">The operation was canceled via <paramref name="cancellationToken"/>.
    /// <para>通过 <paramref name="cancellationToken"/> 取消了操作。</para></exception>
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
    /// <param name="planJson">Serialized <c>DeliveryPlan</c> JSON to build from.
    /// <para>用于构建的序列化 <c>DeliveryPlan</c> JSON。</para></param>
    /// <param name="overwrite">Paths allowed to replace existing outputs; others are left untouched. Null permits no overwrites.
    /// <para>允许覆盖既有输出的路径；其余保持不变。为 null 时不允许覆盖。</para></param>
    /// <param name="log">Optional sink for human-readable progress lines. <para>可选的进度文本接收器。</para></param>
    /// <param name="cancellationToken">Cancels an in-flight build or tool download. <para>取消进行中的构建或工具下载。</para></param>
    /// <returns>The absolute paths of the outputs produced by the build.
    /// <para>本次构建产出的输出绝对路径。</para></returns>
    /// <exception cref="System.ArgumentException"><paramref name="planJson"/> is null, empty or whitespace.
    /// <para><paramref name="planJson"/> 为 null、空或仅空白。</para></exception>
    /// <exception cref="System.IO.InvalidDataException">The plan cannot be parsed.
    /// <para>计划无法解析。</para></exception>
    /// <exception cref="System.OperationCanceledException">The operation was canceled via <paramref name="cancellationToken"/>.
    /// <para>通过 <paramref name="cancellationToken"/> 取消了操作。</para></exception>
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