using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Contains a validated device-reported Legacy erase summary, not host verification.
/// <para>包含经过边界校验的设备端 Legacy 擦除汇总，不代表主机验证。</para></summary>
public sealed class MtkLegacyEraseStatistics
{
    private MtkLegacyEraseStatistics(long offset, long length, uint totalBlocks, uint badBlocks, uint errorBlocks)
    {
        Offset = offset;
        Length = length;
        TotalBlocks = totalBlocks;
        BadBlocks = badBlocks;
        ErrorBlocks = errorBlocks;
    }

    /// <summary>Gets the device-reported physical main-area start.
    /// <para>获取设备上报的物理主区起始位置。</para></summary>
    /// <value>A nonnegative bounded byte offset.
    /// <para>非负且有界的字节偏移。</para></value>
    public long Offset { get; }

    /// <summary>Gets the device-reported physical main-area span.
    /// <para>获取设备上报的物理主区跨度。</para></summary>
    /// <value>A positive bounded byte length.
    /// <para>正数且有界的字节长度。</para></value>
    public long Length { get; }

    /// <summary>Gets the reported total block count.
    /// <para>获取上报的总块数。</para></summary>
    /// <value>A count bounded by the reported range and selected erase-block size.
    /// <para>受上报范围和选定擦除块大小限制的块数。</para></value>
    public uint TotalBlocks { get; }

    /// <summary>Gets the reported bad-block count without claiming those blocks were erased.
    /// <para>获取上报的坏块数，不声称这些块已被擦除。</para></summary>
    /// <value>A count no greater than the reported total.
    /// <para>不超过上报总块数的计数。</para></value>
    public uint BadBlocks { get; }

    /// <summary>Gets the reported error-block count.
    /// <para>获取上报的错误块数。</para></summary>
    /// <value>A count no greater than the reported total; nonzero counts prevent successful erase completion.
    /// <para>不超过上报总块数的计数；非零时不允许报告擦除成功。</para></value>
    public uint ErrorBlocks { get; }

    internal static MtkLegacyEraseStatistics Parse(ReadOnlySpan<byte> bytes, long capacity, int blockSize,
        long requestedOffset, long requestedLength, bool logicalNand)
    {
        if (bytes.Length != 28)
        {
            throw new MtkProtocolException("The Legacy erase statistics have an invalid length.");
        }
        var offset = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        var length = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        var total = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
        var bad = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
        var errors = BinaryPrimitives.ReadUInt32BigEndian(bytes[24..]);
        if (offset > (ulong)capacity || length == 0 || length > (ulong)capacity - offset
            || offset % (uint)blockSize != 0 || length % (uint)blockSize != 0
            || total > length / (uint)blockSize || bad > total || errors > total
            || (!logicalNand && (offset != (ulong)requestedOffset || length != (ulong)requestedLength)))
        {
            throw new MtkProtocolException("The Legacy erase statistics contain an invalid range or block count.");
        }
        return new((long)offset, (long)length, total, bad, errors);
    }
}