using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Contains the validated non-IoT Legacy NAND inventory without interpreting spare contents.
/// <para>包含已校验的非 IoT Legacy NAND 清单，不解释备用区内容。</para></summary>
public sealed class MtkLegacyNandGeometry
{
    private MtkLegacyNandGeometry(long capacity, int page, int spare, int block, bool bmt,
        ReadOnlySpan<byte> inventory, ReadOnlySpan<byte> ids, ReadOnlySpan<byte> details)
    {
        CapacityBytes = capacity;
        PageSize = page;
        SpareSize = spare;
        EraseBlockSize = block;
        HasBadBlockTable = bmt;
        InventoryHex = Convert.ToHexString(inventory);
        DeviceIdsHex = Convert.ToHexString(ids);
        DetailsHex = Convert.ToHexString(details);
    }

    /// <summary>Gets the reported main-area capacity without assuming it is all logically available.
    /// <para>获取上报的主区容量，不假定它全部可用于逻辑寻址。</para></summary>
    /// <value>A positive erase-block multiple bounded by Int64.
    /// <para>不超过 Int64 的正数擦除块倍数。</para></value>
    public long CapacityBytes { get; }

    /// <summary>Gets the main-area bytes per page.
    /// <para>获取每页主区字节数。</para></summary>
    /// <value>A power of two from 512 through 32768 in the 16-bit inventory field.
    /// <para>16 位清单字段中 512 至 32768 范围内的 2 的幂。</para></value>
    public int PageSize { get; }

    /// <summary>Gets the reported spare bytes per page.
    /// <para>获取上报的每页备用字节数。</para></summary>
    /// <value>A count from zero through the main-page size.
    /// <para>零至主页大小范围内的字节数。</para></value>
    public int SpareSize { get; }

    /// <summary>Gets the main-area bytes per erase block.
    /// <para>获取每个擦除块的主区字节数。</para></summary>
    /// <value>A positive page multiple no larger than 64 MiB.
    /// <para>不超过 64 MiB 的正数页大小倍数。</para></value>
    public int EraseBlockSize { get; }

    /// <summary>Gets the agent's bad-block table flag without interpreting its contents.
    /// <para>获取代理的坏块表标志，不解释表内容。</para></summary>
    /// <value>The reported Boolean value.
    /// <para>上报的布尔值。</para></value>
    public bool HasBadBlockTable { get; }

    /// <summary>Gets the original primary inventory for diagnostics.
    /// <para>获取用于诊断的原始主清单。</para></summary>
    /// <value>The 13-byte or 17-byte inventory encoded as hexadecimal.
    /// <para>编码为十六进制的 13 或 17 字节清单。</para></value>
    public string InventoryHex { get; }

    /// <summary>Gets the unmodified device ID array in wire byte order.
    /// <para>获取按线协议字节顺序保留的原始设备标识数组。</para></summary>
    /// <value>Hexadecimal encoding of at most 256 two-byte identifiers.
    /// <para>最多 256 个双字节标识的十六进制编码。</para></value>
    public string DeviceIdsHex { get; }

    /// <summary>Gets the original page, spare, block, interface, address-cycle and BMT fields.
    /// <para>获取原始页、备用区、块、接口、地址周期和 BMT 字段。</para></summary>
    /// <value>The nine-byte supplementary inventory encoded as hexadecimal.
    /// <para>编码为十六进制的九字节补充清单。</para></value>
    public string DetailsHex { get; }

    internal void ValidateRange(long offset, long length, long capacity)
    {
        if (capacity <= 0 || capacity > CapacityBytes || capacity % PageSize != 0
            || offset < 0 || length <= 0 || offset % PageSize != 0 || length % PageSize != 0
            || offset > capacity || length > capacity - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(length),
                "The Legacy NAND range or selected address-space bound is unaligned or outside capacity.");
        }
    }

    internal static MtkLegacyNandGeometry Parse(ReadOnlySpan<byte> inventory, ReadOnlySpan<byte> ids,
        ReadOnlySpan<byte> details, bool size64Bit)
    {
        if (inventory.Length != (size64Bit ? 17 : 13) || details.Length != 9)
        {
            throw new MtkProtocolException("The Legacy NAND inventory has an invalid length.");
        }
        var status = BinaryPrimitives.ReadUInt32BigEndian(inventory);
        if (status != 0)
        {
            throw new MtkProtocolException("Legacy NAND initialization failed.", status);
        }
        var count = BinaryPrimitives.ReadUInt16BigEndian(inventory[^2..]);
        var capacity = size64Bit
            ? BinaryPrimitives.ReadUInt64BigEndian(inventory[7..])
            : BinaryPrimitives.ReadUInt32BigEndian(inventory[7..]);
        var page = BinaryPrimitives.ReadUInt16BigEndian(details);
        var spare = BinaryPrimitives.ReadUInt16BigEndian(details[2..]);
        var pagesPerBlock = BinaryPrimitives.ReadUInt16BigEndian(details[4..]);
        var block = (long)page * pagesPerBlock;
        if (count > 256 || ids.Length != count * 2
            || page < 512 || (page & (page - 1)) != 0 || spare > page
            || block == 0 || block > 64 * 1024 * 1024
            || capacity == 0 || capacity > long.MaxValue || capacity % (ulong)block != 0 || details[8] > 1)
        {
            throw new MtkProtocolException("The Legacy NAND identifiers, page, spare, block, capacity or BMT fields are invalid.");
        }
        return new((long)capacity, page, spare, (int)block, details[8] != 0, inventory, ids, details);
    }
}