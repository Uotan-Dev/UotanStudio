using UotanStudio.Firmware.BlockImage.Lp;

namespace UotanStudio.Delivery;

/// <summary>
/// Shared Super capacity math for the delivery panel and the LP builder. Keeping the reserved-area
/// and block-alignment rules in one place means the panel's pre-check and the builder's extent
/// allocation can never disagree about whether a partition set fits.
/// <para>交付面板与 LP 构建器共享的 Super 容量计算。保留区与块对齐规则集中在这里，
/// 使面板预检与构建器 extent 分配不会对“分区是否放得下”给出不同结论。</para>
/// </summary>
public static class DeliverySuperCapacity
{
    /// <summary>Bytes in the reserved header/geometry/metadata area the builder excludes from the data area.
    /// <para>构建器从数据区中排除的头部、geometry 与元数据保留区字节数。</para></summary>
    internal static ulong ReservedBytes(DeliverySuperOptions options) => checked(
        MetadataFormat.LP_PARTITION_RESERVED_BYTES + 2UL * MetadataFormat.LP_METADATA_GEOMETRY_SIZE
        + (ulong)options.MetadataSize * options.Slots * 2);

    /// <summary>Bytes left for partition data after the reserved area is rounded up to one logical block.
    /// <para>保留区向上取整到一个逻辑块后，可供分区数据使用的字节数。</para></summary>
    internal static ulong AvailableBytes(DeliverySuperOptions options)
    {
        var reserved = AlignUp(ReservedBytes(options), options.BlockSize);
        return options.DeviceSize > reserved ? options.DeviceSize - reserved : 0;
    }

    /// <summary>
    /// Sums the images while rounding each up to one logical block, matching the extent sizing the
    /// builder applies. Non-positive lengths (auto-zero B slots and missing sources) contribute nothing.
    /// <para>按构建器 extent 的取整规则把每个镜像向上取整到一个逻辑块后求和。
    /// 非正长度（自动零 B 槽与缺失源）不参与计算。</para>
    /// </summary>
    internal static ulong RequiredBytes(IEnumerable<long> lengths, uint blockSize)
    {
        var total = 0UL;
        foreach (var length in lengths)
        {
            if (length <= 0) continue;
            total = checked(total + AlignUp((ulong)length, blockSize));
        }
        return total;
    }

    /// <summary>Smallest valid device size that holds the reserved area plus the aligned partitions.
    /// <para>能容纳保留区与对齐分区的最小合法设备大小。</para></summary>
    internal static ulong MinimumDeviceSize(IEnumerable<long> lengths, DeliverySuperOptions options)
        => checked(AlignUp(ReservedBytes(options), options.BlockSize) + RequiredBytes(lengths, options.BlockSize));

    /// <summary>
    /// Throws an actionable error before the builder allocates any extent. The message names the byte
    /// shortage, the reserved area and the exact minimum Super size, so the delivery panel can tell the
    /// author what to change instead of surfacing a bare <c>ArgumentException</c> from LP metadata.
    /// <para>在构建器分配任何 extent 前抛出可操作的错误。消息给出短缺字节数、保留区与精确的最小
    /// Super 大小，使交付面板能告诉作者要改什么，而不是抛出 LP 元数据的裸 <c>ArgumentException</c>。</para>
    /// </summary>
    /// <summary>
    /// Rejects an unset Super size before any LP geometry is built. The size is baked into the geometry and the
    /// generated scripts compare it against the device reading, so a guessed default is worse than no value at all.
    /// <para>在构建任何 LP 几何之前拒绝未填写的 Super 大小。该数值会被烘焙进几何信息，生成的脚本再拿它与设备读数
    /// 比对，因此一个猜测的默认值比留空更糟。</para>
    /// </summary>
    /// <param name="options"><para>待校验的 Super 选项。/ The Super options to check.</para></param>
    internal static void ValidateDeviceSize(DeliverySuperOptions options)
    {
        if (options.DeviceSize == 0)
            throw new InvalidDataException("Super 大小必须按设备读数显式填写 / Super size must be entered explicitly; read the device with fastboot getvar super-partition-size.");
    }

    /// <summary>
    /// Runs Validate.
    /// <para>执行 Validate。</para>
    /// </summary>
    public static void Validate(IReadOnlyList<long> lengths, DeliverySuperOptions options)
    {
        ValidateDeviceSize(options);
        if (options.BlockSize == 0)
            throw new InvalidDataException("Super 逻辑块大小必须大于 0 / Super logical block size must be positive.");
        var required = RequiredBytes(lengths, options.BlockSize);
        if (required <= AvailableBytes(options)) return;
        var minimum = MinimumDeviceSize(lengths, options);
        throw new InvalidDataException(
            $"Super 空间不足 / Super space exceeded: partitions need {FormatSize(required)}, "
            + $"only {FormatSize(AvailableBytes(options))} available of {FormatSize(options.DeviceSize)} "
            + $"(metadata reserves {FormatSize(ReservedBytes(options))}). "
            + $"请把 Super 大小至少调为 {minimum} 字节（{FormatSize(minimum)}）或减小分区镜像。");
    }

    /// <summary>Rounds <paramref name="size"/> up to <paramref name="alignment"/>; a zero alignment is a no-op.
    /// <para>把 <paramref name="size"/> 向上取整到 <paramref name="alignment"/>；对齐为 0 时不改变。</para></summary>
    internal static ulong AlignUp(ulong size, uint alignment) => alignment == 0
        ? size : checked(size + alignment - 1) / alignment * alignment;

    internal static string FormatSize(long bytes) => FormatSize(bytes < 0 ? 0UL : (ulong)bytes);

    internal static string FormatSize(ulong bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.##} {units[index]}";
    }
}