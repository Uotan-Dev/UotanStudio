using System.Text.Json;

namespace UotanStudio.Services.Delivery;

/// <summary>
/// Reflection-free entry point that lets hosts and headless consumers drive delivery validation, output listing, tool preparation and builds from a serialized JSON contract; only plain types cross the assembly boundary.
/// <para>无反射的公开入口，使宿主与无头消费者能通过序列化 JSON 契约驱动交付校验、输出列表、工具准备与构建；跨越程序集边界的都是普通类型。</para>
/// </summary>
/// <param name="httpClient">
/// HTTP client used to download the pinned platform-tools and zstd during tool preparation and builds; validation and output listing ignore it.
/// <para>构建与工具准备期间下载钉定的 platform-tools 与 zstd 所使用的 HTTP 客户端；校验与输出列举不使用它。</para>
/// </param>
/// <param name="toolCacheDirectory">
/// Writable directory holding the downloaded tool cache; null or blank falls back to <see cref="DefaultToolCacheDirectory"/>.
/// <para>存放已下载工具缓存的可写目录；为 null 或空白时回退到 <see cref="DefaultToolCacheDirectory"/>。</para>
/// </param>
/// <param name="signingKeys">
/// Key provider used when the plan requests signed output; null keeps the build unsigned-capable only, and a signed plan fails honestly.
/// <para>计划要求签名输出时使用的密钥供应器；为 null 时仅能产出无签名输出，签名计划会如实失败。</para>
/// </param>
/// <remarks>
/// Validation and output listing run fully standalone. Build and tool preparation download pinned
/// platform-tools and zstd over the network, so they require a caller-supplied <see cref="HttpClient"/>
/// and a writable tool cache. Payload and package signing is driven by the key paths inside the plan
/// resolved through the injected <paramref name="signingKeys"/>; hosts without a provider must not attempt
/// a signed plan.
/// <para>校验与输出列举完全独立。构建与工具准备会通过网络下载钉定的 platform-tools 与 zstd，
/// 因此需要调用方提供的 <see cref="HttpClient"/> 与可写工具缓存。Payload 与卡刷包签名由计划内的密钥路径
/// 经注入的 <paramref name="signingKeys"/> 解析，缺少供应器的宿主不得尝试签名计划。</para>
/// </remarks>
public sealed class DeliveryService(HttpClient httpClient, string? toolCacheDirectory = null,
    IDeliverySigningKeyProvider? signingKeys = null)
{
    /// <summary><para>注入的工具准备服务覆盖实例；非空时构建与工具准备不再自行创建
    /// <see cref="DeliveryToolService"/>，供测试以无网络替身驱动门面。</para>
    /// Injected tool-preparation override; when set, builds and tool preparation reuse it instead of creating
    /// a <see cref="DeliveryToolService"/>, letting tests drive the facade without network.</summary>
    private readonly IDeliveryTools? toolOverride;

    /// <summary>
    /// Builds the facade over an injected tool stand-in so the build and signing paths can be verified without network.
    /// <para>以注入的工具替身构造门面，用于在无网络条件下验证构建与签名链路。</para>
    /// </summary>
    /// <param name="tools">The tool-preparation stand-in. <para>工具准备替身。</para></param>
    /// <param name="signingKeys">Optional signing key provider. <para>签名密钥供应器，可为 null。</para></param>
    internal DeliveryService(IDeliveryTools tools, IDeliverySigningKeyProvider? signingKeys = null)
        : this(httpClient: null!, toolCacheDirectory: null, signingKeys: signingKeys)
    {
        toolOverride = tools;
    }
    /// <summary><para>默认工具缓存目录（当前用户 LocalApplicationData 下）。</para>
    /// Default tool cache directory under the current user's local application data.</summary>
    /// <value>The absolute, once-resolved cache directory used when no explicit tool cache is supplied.
    /// <para>未显式提供工具缓存时使用的绝对路径，仅解析一次。</para></value>
    public static string DefaultToolCacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UotanStudio", "delivery-tools");

    /// <summary>
    /// Validates a delivery plan; returns <see langword="null"/> when valid, otherwise the first error message.
    /// <para>校验交付计划；合法返回 <see langword="null"/>，否则返回首条错误消息。</para>
    /// </summary>
    /// <param name="planJson">Serialized <c>DeliveryPlan</c> JSON. <para>序列化后的 <c>DeliveryPlan</c> JSON。</para></param>
    /// <param name="checkSources">Whether source images must exist. <para>是否检查源镜像存在。</para></param>
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
    /// Returns the planned output paths and the subset that already exists on disk.
    /// <para>返回计划将产出的输出路径及其中已存在于磁盘的子集。</para>
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
    /// Downloads the delivery tools into a writable cache and materializes them under <c>bin/</c> beneath
    /// <paramref name="directory"/> (falling back to the plan's output directory when blank). Network-bound.
    /// <para>把交付所需工具下载到可写缓存并物化到 <paramref name="directory"/> 下的 <c>bin/</c> 布局；
    /// 目录为空时回退到计划的输出目录。此操作需要网络。</para>
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
    /// Runs a full build from a validated plan and returns the produced output paths; it prepares the
    /// platform tools internally. Network-bound.
    /// <para>按已校验的计划执行完整构建，返回产出的输出路径；内部会准备平台工具。此操作需要网络。</para>
    /// </summary>
    /// <param name="planJson">Serialized <c>DeliveryPlan</c> JSON to build from.
    /// <para>用于构建的序列化 <c>DeliveryPlan</c> JSON。</para></param>
    /// <param name="overwrite">Paths allowed to replace existing outputs; others are left untouched. Null permits no overwrites.
    /// <para>允许覆盖既有输出的路径；其余保持不变。为 null 时不允许覆盖。</para></param>
    /// <param name="log">Optional typed progress sink carrying stage, byte and item metrics.
    /// <para>可选的强类型进度接收端，携带阶段、字节与条目度量。</para></param>
    /// <param name="cancellationToken">Cancels an in-flight build or tool download. <para>取消进行中的构建或工具下载。</para></param>
    /// <returns>The absolute paths of the outputs produced by the build.
    /// <para>本次构建产出的输出绝对路径。</para></returns>
    /// <exception cref="System.ArgumentException"><paramref name="planJson"/> is null, empty or whitespace.
    /// <para><paramref name="planJson"/> 为 null、空或仅空白。</para></exception>
    /// <exception cref="System.IO.InvalidDataException">The plan cannot be parsed.
    /// <para>计划无法解析。</para></exception>
    /// <exception cref="System.InvalidOperationException">The plan requires signed output but no
    /// <see cref="IDeliverySigningKeyProvider"/> was injected.
    /// <para>计划需要签名输出，但未注入 <see cref="IDeliverySigningKeyProvider"/>。</para></exception>
    /// <exception cref="System.OperationCanceledException">The operation was canceled via <paramref name="cancellationToken"/>.
    /// <para>通过 <paramref name="cancellationToken"/> 取消了操作。</para></exception>
    public async Task<IReadOnlyList<string>> BuildAsync(string planJson, IReadOnlySet<string>? overwrite = null,
        IProgress<DeliveryBuildProgress>? log = null, CancellationToken cancellationToken = default)
    {
        var plan = ParsePlan(planJson);
        var outputs = await new DeliveryOutputService(CreateTools(), signingKeys)
            .ExportAsync(plan, log is null ? null : new ProgressAdapter(log), cancellationToken, overwrite)
            .ConfigureAwait(false);
        return outputs;
    }

    private IDeliveryTools CreateTools()
        => toolOverride ?? new DeliveryToolService(httpClient,
            string.IsNullOrWhiteSpace(toolCacheDirectory) ? DefaultToolCacheDirectory : toolCacheDirectory);

    private DeliveryToolService NewToolService()
        => new(httpClient, string.IsNullOrWhiteSpace(toolCacheDirectory) ? DefaultToolCacheDirectory : toolCacheDirectory);

    private static DeliveryPlan ParsePlan(string planJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planJson);
        return JsonSerializer.Deserialize(planJson, DeliveryJsonContext.Default.DeliveryPlan)
            ?? throw new InvalidDataException("Delivery plan JSON could not be parsed.");
    }

    private static IProgress<DeliveryProgress>? ToProgress(IProgress<string>? log)
        => log is null ? null : new TextProgressAdapter(log);

    private sealed class TextProgressAdapter(IProgress<string> log) : IProgress<DeliveryProgress>
    {
        public void Report(DeliveryProgress value) => log.Report($"{value.Percent:0}% {value.Message}");
    }

    private sealed class ProgressAdapter(IProgress<DeliveryBuildProgress> log) : IProgress<DeliveryProgress>
    {
        public void Report(DeliveryProgress value) => log.Report(value.ToBuildProgress());
    }
}