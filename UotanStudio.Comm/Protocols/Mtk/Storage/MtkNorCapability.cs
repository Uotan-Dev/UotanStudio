using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Mtk.Storage;

/// <summary>
/// NOR capacity, write alignment and erase-sector semantics shared by the XFlash and Legacy wire models.
/// <para>XFlash 与 Legacy 线协议模型共用的 NOR 容量、写入对齐与擦除扇区语义。</para>
/// Neither profile reports a NOR erase-sector table, so <see cref="EraseSectorSizeBytes"/> stays
/// <see langword="null"/> and the erase granularity remains a caller-selected wire parameter.
/// <para>两种配置都不上报 NOR 擦除扇区表，因此 <see cref="EraseSectorSizeBytes"/> 保持
/// <see langword="null"/>，擦除粒度仍是由调用方选择的线协议参数。</para>
/// </summary>
public sealed record MtkNorCapability
{
    /// <summary>
    /// Gets the reported whole-device ordinary capacity.
    /// <para>获取上报的整片设备普通容量。</para>
    /// </summary>
    /// <value>
    /// A positive byte count; one-time-programmable areas are never folded into it.
    /// <para>正数字节数；一次性可编程区域绝不计入其中。</para>
    /// </value>
    public required long CapacityBytes { get; init; }

    /// <summary>
    /// Gets the byte alignment ordinary writes must satisfy.
    /// <para>获取普通写入必须满足的字节对齐。</para>
    /// </summary>
    /// <value>
    /// The profile's write granularity: the Legacy fixed 512-byte rule or the XFlash reported block size.
    /// <para>配置的写入粒度：Legacy 固定的 512 字节规则，或 XFlash 上报的块大小。</para>
    /// </value>
    public required int WriteGranularityBytes { get; init; }

    /// <summary>
    /// Gets the device-reported erase sector size, when any profile supplied one.
    /// <para>获取设备上报的擦除扇区大小（若某配置提供）。</para>
    /// </summary>
    /// <value>
    /// Always <see langword="null"/> for the current wire profiles; the caller must still pass an
    /// explicit erase block size to the erase commands, and guessing one would exceed the evidence.
    /// <para>对当前线协议配置恒为 <see langword="null"/>；调用方仍必须向擦除命令显式传入擦除块大小，
    /// 猜测数值会超出证据范围。</para>
    /// </value>
    public int? EraseSectorSizeBytes { get; init; }

    /// <summary>
    /// Gets whether hardware or firmware write protection was reported for the device.
    /// <para>获取是否上报了设备的硬件或固件写保护。</para>
    /// </summary>
    /// <value>
    /// <see cref="FeatureSupport.Unknown"/> until a real-device probe supplies evidence.
    /// <para>在真实设备探测提供证据之前为 <see cref="FeatureSupport.Unknown"/>。</para>
    /// </value>
    public FeatureSupport WriteProtection { get; init; } = FeatureSupport.Unknown;

    /// <summary>
    /// Gets the device-reported NOR flash identifier, when the Legacy profile supplied one.
    /// <para>获取设备上报的 NOR 闪存标识（若 Legacy 配置提供）。</para>
    /// </summary>
    /// <value>
    /// The unmodified Legacy 16-bit identifier, or <see langword="null"/> for XFlash queries.
    /// <para>未经修改的 Legacy 16 位标识；XFlash 查询为 <see langword="null"/>。</para>
    /// </value>
    public ushort? FlashDeviceId { get; init; }
}