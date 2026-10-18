namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Resolves the sector count a raw <c>program</c> command actually writes.
/// <para>解析原始 <c>program</c> 命令实际写入的扇区数。</para>
/// </summary>
/// <remarks>
/// <para>Semantics: the manifest's <c>num_partition_sectors</c>
/// is the partition capacity, not the payload length, so the written count is the source-derived
/// whole-plus-partial sector count capped by the declared value. A source shorter than the declared
/// capacity is programmed without implicit zero-padding of the untouched sectors, and a source longer
/// than the declared capacity is truncated.</para>
/// <para>语义：清单中的 <c>num_partition_sectors</c> 是分区容量而非载荷长度，因此写入数量
/// 为由源文件推导的扇区数（含末尾不完整扇区）并以声明值为上限。源文件短于声明容量时按实际数据写入，
/// 不会对未覆盖的扇区做隐式零填充；源文件长于声明容量时按声明值截断。</para>
/// </remarks>
public static class FlashProgramLength
{
    /// <summary>Computes the sector count to program from the readable source length and the declared count.
    /// <para>由可读源长度与声明扇区数计算待编程的扇区数。</para></summary>
    /// <param name="availableBytes">Bytes readable from the current source position.
    /// <para>从源文件当前位置起可读的字节数。</para></param>
    /// <param name="declaredSectors">The manifest's <c>num_partition_sectors</c>; zero means the manifest declares no capacity and the source length alone decides.
    /// <para>清单的 <c>num_partition_sectors</c>；为 0 表示清单未声明容量，仅由源文件长度决定。</para></param>
    /// <param name="sectorSize">The sector size in bytes applied to both operands.
    /// <para>两个操作数共同使用的每扇区字节数。</para></param>
    /// <returns>The sector count to write, including one partial tail sector when the source is not a whole-sector multiple; zero when the source holds no bytes.
    /// <para>待写入的扇区数；源文件不是整扇区倍数时包含一个末尾不完整扇区；源文件无字节时为 0。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="availableBytes"/> or <paramref name="declaredSectors"/> is negative, or <paramref name="sectorSize"/> is outside the 1..65536 range.
    /// <para><paramref name="availableBytes"/> 或 <paramref name="declaredSectors"/> 为负，或 <paramref name="sectorSize"/> 超出 1..65536 范围。</para></exception>
    public static long ResolveSectors(long availableBytes, long declaredSectors, long sectorSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(availableBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(declaredSectors);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectorSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sectorSize, 65536);

        var sourceSectors = availableBytes / sectorSize + (availableBytes % sectorSize == 0 ? 0 : 1);
        return declaredSectors == 0 ? sourceSectors : Math.Min(sourceSectors, declaredSectors);
    }

    /// <summary>Reports whether the declared capacity truncates the source.
    /// <para>报告声明容量是否截断了源文件。</para></summary>
    /// <param name="availableBytes">Bytes readable from the current source position.
    /// <para>从源文件当前位置起可读的字节数。</para></param>
    /// <param name="declaredSectors">The manifest's <c>num_partition_sectors</c>.
    /// <para>清单的 <c>num_partition_sectors</c>。</para></param>
    /// <param name="sectorSize">The sector size in bytes.<para>每扇区字节数。</para></param>
    /// <returns><see langword="true"/> when the source would need more sectors than the manifest declares, so the tail is not programmed.
    /// <para>当源文件所需扇区数超过清单声明值、因此尾部不会被编程时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="availableBytes"/> or <paramref name="declaredSectors"/> is negative, or <paramref name="sectorSize"/> is outside the 1..65536 range.
    /// <para><paramref name="availableBytes"/> 或 <paramref name="declaredSectors"/> 为负，或 <paramref name="sectorSize"/> 超出 1..65536 范围。</para></exception>
    public static bool IsTruncatedByDeclaration(long availableBytes, long declaredSectors, long sectorSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(availableBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(declaredSectors);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectorSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sectorSize, 65536);

        if (declaredSectors == 0)
        {
            return false;
        }

        var sourceSectors = availableBytes / sectorSize + (availableBytes % sectorSize == 0 ? 0 : 1);
        return sourceSectors > declaredSectors;
    }

    /// <summary>Computes the zero-fill byte count for a source whose last sector is only partially filled.
    /// <para>计算源文件末尾扇区仅部分填充时需要补零的字节数。</para></summary>
    /// <param name="availableBytes">Bytes readable from the current source position.
    /// <para>从源文件当前位置起可读的字节数。</para></param>
    /// <param name="numSectors">The resolved sector count to program.<para>解析出的待编程扇区数。</para></param>
    /// <param name="sectorSize">The sector size in bytes.<para>每扇区字节数。</para></param>
    /// <returns>The trailing bytes the writer must supply as zeros, always below one sector; zero when the source already covers every programmed sector.
    /// <para>写入方必须以零补齐的尾部字节数，恒小于一个扇区；源文件已覆盖全部待编程扇区时为 0。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="availableBytes"/> or <paramref name="numSectors"/> is negative, the source is short by a whole sector or more, or <paramref name="sectorSize"/> is outside the 1..65536 range.
    /// <para><paramref name="availableBytes"/> 或 <paramref name="numSectors"/> 为负、源文件短达一个或多个整扇区， 或 <paramref name="sectorSize"/> 超出 1..65536 范围。</para></exception>
    public static long ResolveTailPadding(long availableBytes, long numSectors, long sectorSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(availableBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(numSectors);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectorSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sectorSize, 65536);

        var totalBytes = checked(numSectors * sectorSize);
        if (availableBytes >= totalBytes)
        {
            return 0;
        }

        var shortfall = totalBytes - availableBytes;
        if (shortfall >= sectorSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(availableBytes),
                $"The source is short by {shortfall} bytes, which is a whole sector or more of the {totalBytes} byte program range; only the final partial sector may be zero-filled.");
        }

        return shortfall;
    }
}