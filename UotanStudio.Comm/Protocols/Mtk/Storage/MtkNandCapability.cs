using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Mtk.Storage;

/// <summary>
/// NAND page, block, spare-area and ECC semantics shared by the XFlash and Legacy wire models.
/// <para>XFlash 与 Legacy 线协议模型共用的 NAND 页、块、备用区与 ECC 语义。</para>
/// Spare access and agent-side ECC availability describe what the reporting protocol defines for
/// the observed device; they never claim that a real device accepted a page-stream command.
/// <para>备用区访问与代理侧 ECC 可用性描述的是上报协议就所观测设备定义了哪些能力；
/// 绝不声称真实设备已接受某条页流命令。</para>
/// </summary>
public sealed record MtkNandCapability
{
    /// <summary>
    /// Gets the main-area bytes per page.
    /// <para>获取每页主区字节数。</para>
    /// </summary>
    /// <value>
    /// The reported positive page size; page-stream offsets and lengths align to it.
    /// <para>上报的正数页大小；页流偏移与长度按其对齐。</para>
    /// </value>
    public required int PageSizeBytes { get; init; }

    /// <summary>
    /// Gets the main-area bytes per erase block.
    /// <para>获取每个擦除块的主区字节数。</para>
    /// </summary>
    /// <value>
    /// A positive page multiple; erase ranges align to it rather than to the page size.
    /// <para>正数页倍数；擦除范围按其而非页大小对齐。</para>
    /// </value>
    public required int EraseBlockSizeBytes { get; init; }

    /// <summary>
    /// Gets the spare (OOB) bytes per page.
    /// <para>获取每页备用区（OOB）字节数。</para>
    /// </summary>
    /// <value>
    /// The reported count, from zero through the page size; zero disables every spare-bearing transfer.
    /// <para>上报的字节数，范围为零至页大小；为零时所有含备用区的传输均不可用。</para>
    /// </value>
    public required int SpareSizeBytes { get; init; }

    /// <summary>
    /// Gets the physical main-area capacity, excluding spare bytes.
    /// <para>获取不含备用字节的物理主区容量。</para>
    /// </summary>
    public required long TotalCapacityBytes { get; init; }

    /// <summary>
    /// Gets the agent's logical available main-area capacity.
    /// <para>获取代理上报的逻辑可用主区容量。</para>
    /// </summary>
    /// <value>
    /// The XFlash-reported bound, or <see langword="null"/> when the profile only reported physical
    /// capacity; <c>0</c> is kept as reported and is never replaced by the total.
    /// <para>XFlash 上报的界限；配置仅上报物理容量时为 <see langword="null"/>；<c>0</c> 按原样保留，绝不被总容量替换。</para>
    /// </value>
    public long? AvailableCapacityBytes { get; init; }

    /// <summary>
    /// Gets whether the agent reported a bad-block management table.
    /// <para>获取代理是否上报了坏块管理表。</para>
    /// </summary>
    /// <value>
    /// <see cref="FeatureSupport.Supported"/> or <see cref="FeatureSupport.Unsupported"/> from the
    /// reported flag; table entries are never read or validated by this observation.
    /// <para>来自上报标志的 <see cref="FeatureSupport.Supported"/> 或 <see cref="FeatureSupport.Unsupported"/>；
    /// 本观测从不读取或校验表项。</para>
    /// </value>
    public required FeatureSupport BadBlockTableSupport { get; init; }

    /// <summary>
    /// Gets whether spare-area-bearing page streams are available for the reported device.
    /// <para>获取所上报设备是否可用含备用区的页流。</para>
    /// </summary>
    /// <value>
    /// <see cref="FeatureSupport.Supported"/> when <see cref="SpareSizeBytes"/> is positive,
    /// otherwise <see cref="FeatureSupport.Unsupported"/>.
    /// <para><see cref="SpareSizeBytes"/> 为正时为 <see cref="FeatureSupport.Supported"/>，
    /// 否则为 <see cref="FeatureSupport.Unsupported"/>。</para>
    /// </value>
    public required FeatureSupport SpareAccess { get; init; }

    /// <summary>
    /// Gets whether the reporting protocol defines agent-side ECC transfer modes for this family.
    /// <para>获取上报协议是否为本家族定义了代理侧 ECC 传输模式。</para>
    /// </summary>
    /// <value>
    /// <see cref="FeatureSupport.Supported"/> whenever the profile exposes page-with-ECC streams; the
    /// device-side ECC scheme itself is never reported, so it remains unverified.
    /// <para>配置暴露带 ECC 的页流时为 <see cref="FeatureSupport.Supported"/>；设备端 ECC 方案本身从不上报，仍未经验证。</para>
    /// </value>
    public required FeatureSupport EccHandling { get; init; }
}