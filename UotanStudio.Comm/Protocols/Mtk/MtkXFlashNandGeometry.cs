using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Contains validated XFlash NAND geometry without interpreting its spare-area contents.
/// <para>包含经校验的 XFlash NAND 几何信息，不解释备用区内容。</para></summary>
public sealed class MtkXFlashNandGeometry
{
    private MtkXFlashNandGeometry(int pageSize, int eraseBlockSize, int spareSize, long totalSize,
        long availableSize, bool hasBadBlockTable, string deviceIdHex)
    {
        PageSize = pageSize;
        EraseBlockSize = eraseBlockSize;
        SpareSize = spareSize;
        TotalSize = totalSize;
        AvailableSize = availableSize;
        HasBadBlockTable = hasBadBlockTable;
        DeviceIdHex = deviceIdHex;
    }

    /// <summary>Gets the main-area bytes per page. <para>获取每页主区字节数。</para></summary>
    /// <value>A power of two from 512 through 65536. <para>512 至 65536 的 2 的幂。</para></value>
    public int PageSize { get; }
    /// <summary>Gets main-area bytes per erase block. <para>获取每个擦除块的主区字节数。</para></summary>
    /// <value>A positive page multiple no larger than 64 MiB. <para>不超过 64 MiB 的正数页大小倍数。</para></value>
    public int EraseBlockSize { get; }
    /// <summary>Gets spare-area bytes per page. <para>获取每页备用区字节数。</para></summary>
    /// <value>The reported count, from zero through the page size. <para>上报的字节数，范围为零至页大小。</para></value>
    public int SpareSize { get; }
    /// <summary>Gets physical main-area capacity, excluding spare bytes. <para>获取不含备用字节的物理主区容量。</para></summary>
    /// <value>A positive erase-block multiple bounded by Int64. <para>不超过 Int64 范围的正数擦除块倍数。</para></value>
    public long TotalSize { get; }
    /// <summary>Gets the agent's logical available main-area capacity. <para>获取代理上报的逻辑可用主区容量。</para></summary>
    /// <value>A page multiple no greater than total capacity; zero is not replaced with physical capacity.
    /// <para>不超过总容量的页大小倍数；零不会替换为物理容量。</para></value>
    public long AvailableSize { get; }
    /// <summary>Gets whether the agent reports a bad-block management table. <para>获取代理是否上报坏块管理表。</para></summary>
    /// <value>The reported Boolean flag; it does not expose or validate table entries.
    /// <para>上报的布尔标志；不表示已读取或校验表项。</para></value>
    public bool HasBadBlockTable { get; }
    /// <summary>Gets the unmodified NAND identifier as hexadecimal. <para>获取未经修改的十六进制 NAND 标识。</para></summary>
    /// <value>Twenty-four hexadecimal characters from the twelve device-reported bytes.
    /// <para>来自设备上报十二字节的二十四个十六进制字符。</para></value>
    public string DeviceIdHex { get; }

    internal void ValidateRange(MtkXFlashNandAddressType addressType, long offset, long length, bool erase)
    {
        if (!Enum.IsDefined(addressType))
        {
            throw new ArgumentOutOfRangeException(nameof(addressType));
        }
        var capacity = addressType == MtkXFlashNandAddressType.Logical ? AvailableSize : TotalSize;
        var alignment = erase ? EraseBlockSize : PageSize;
        if (offset < 0 || length <= 0 || offset % alignment != 0 || length % alignment != 0
            || offset > capacity || length > capacity - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The NAND main-area range is unaligned or exceeds the selected address space.");
        }
    }

    internal static MtkXFlashNandGeometry Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 45 || bytes.Length > 4096)
        {
            throw new MtkProtocolException("The NAND geometry is truncated or oversized.");
        }
        var page = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var block = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var spare = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        var total = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        var available = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
        if (page < 512 || page > 65536 || (page & (page - 1)) != 0
            || block < page || block > 64 * 1024 * 1024 || block % page != 0 || spare > page
            || total == 0 || total > long.MaxValue || total % block != 0
            || available > total || available % page != 0 || bytes[32] > 1)
        {
            throw new MtkProtocolException("The NAND page, erase, spare, capacity or BMT geometry is invalid.");
        }
        return new MtkXFlashNandGeometry((int)page, (int)block, (int)spare, (long)total, (long)available,
            bytes[32] != 0, Convert.ToHexString(bytes.Slice(33, 12)));
    }
}