using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Edl.Streaming;

/// <summary>
/// NAND geometry decoded from the ONFI parameter page the loader dumps after an ONFI read:
/// vendor and model strings, page and spare sizes, partial-page fields, pages per block, blocks
/// per unit, unit count, address cycles, and the derived row/column address byte counts and
/// total page count. Transcribed from the reference <c>nand_onfi</c> decode
/// (streaming.py:114-131).
/// <para>从 loader 在 ONFI 读后转储的 ONFI 参数页解码出的 NAND 几何信息：厂商与型号串、页与
/// spare 大小、部分页字段、每块页数、每单元块数、单元数、地址周期，以及派生的行/列地址字节数
/// 与总页数。转写自参考 <c>nand_onfi</c> 的解码（streaming.py:114-131）。</para>
/// </summary>
/// <param name="Vendor">The vendor string bytes 32..44 of the parameter page
/// (streaming.py:124). <para>参数页第 32..44 字节的厂商串（streaming.py:124）。</para></param>
/// <param name="Model">The model string bytes 44..64 of the parameter page (streaming.py:124).
/// <para>参数页第 44..64 字节的型号串（streaming.py:124）。</para></param>
/// <param name="BytesPerPage">The usable bytes per page. <para>每页可用字节数。</para></param>
/// <param name="SparePerPage">The spare bytes per page. <para>每页 spare 字节数。</para></param>
/// <param name="BytesPerPartialPage">The obsolete partial-page byte count (streaming.py:120).
/// <para>已废弃的部分页字节数（streaming.py:120）。</para></param>
/// <param name="SparePerPartialPage">The obsolete partial-page spare count (streaming.py:120).
/// <para>已废弃的部分页 spare 数（streaming.py:120）。</para></param>
/// <param name="PagesPerBlock">The pages inside one erase block. <para>一个擦除块内的页数。</para></param>
/// <param name="BlocksPerUnit">The blocks per LUN. <para>每个 LUN 的块数。</para></param>
/// <param name="Units">The LUN count. <para>LUN 数量。</para></param>
/// <param name="AddressCycles">The raw address-cycle byte: low nibble row bytes, high nibble
/// column bytes (streaming.py:128). <para>原始地址周期字节：低半字节为行字节数，高半字节为列字节数
/// （streaming.py:128）。</para></param>
internal sealed record StreamingNandGeometry(
    string Vendor,
    string Model,
    uint BytesPerPage,
    ushort SparePerPage,
    uint BytesPerPartialPage,
    ushort SparePerPartialPage,
    uint PagesPerBlock,
    byte BlocksPerUnit,
    byte Units,
    byte AddressCycles)
{
    /// <summary>
    /// Gets the row-address byte count, the low nibble of the address-cycles byte.
    /// <para>获取行地址字节数，即地址周期字节的低半字节。</para>
    /// </summary>
    /// <value>The row address bytes. <para>行地址字节数。</para></value>
    internal int RowAddressBytes => AddressCycles & 0x0F;

    /// <summary>
    /// Gets the column-address byte count, the high nibble of the address-cycles byte.
    /// <para>获取列地址字节数，即地址周期字节的高半字节。</para>
    /// </summary>
    /// <value>The column address bytes. <para>列地址字节数。</para></value>
    internal int ColumnAddressBytes => AddressCycles >> 4;

    /// <summary>
    /// Gets the total page count, pages per block times blocks per unit times units, computed
    /// with checked arithmetic (streaming.py:129).
    /// <para>获取总页数，即每块页数乘以每单元块数乘以单元数，使用 checked 运算
    /// （streaming.py:129）。</para>
    /// </summary>
    /// <value>The total pages. <para>总页数。</para></value>
    internal uint TotalPages => checked(PagesPerBlock * (uint)BlocksPerUnit * Units);

    /// <summary>
    /// Decodes the fixed-layout fields of a 512-byte ONFI parameter page. The reference unpacks
    /// eight values from a format string that names only seven (streaming.py:120-122), so the
    /// address-cycle byte is read from its documented position instead of reusing that broken
    /// format; every slice is bounds-checked before it is read.
    /// <para>解码 512 字节 ONFI 参数页的固定布局字段。参考实现用只声明七个字段的格式串解出八个
    /// 值（streaming.py:120-122），因此这里改从其文档化位置读取地址周期字节，而不是照搬那个错误
    /// 格式；每个切片在读取前都完成边界检查。</para>
    /// </summary>
    /// <param name="parameterPage">The raw ONFI parameter page dump, at least 99 bytes.
    /// <para>原始 ONFI 参数页转储，至少 99 字节。</para></param>
    /// <param name="geometry">The decoded geometry on success. <para>解码成功时的几何信息。</para></param>
    /// <returns><see langword="true"/> when the page carried every field.
    /// <para>参数页携带了全部字段时为 <see langword="true"/>。</para></returns>
    internal static bool TryParse(ReadOnlySpan<byte> parameterPage, out StreamingNandGeometry? geometry)
    {
        geometry = null;
        if (parameterPage.Length < 99)
        {
            return false;
        }

        var bytesPerPage = BinaryPrimitives.ReadUInt32LittleEndian(parameterPage[80..]);
        var sparePerPage = BinaryPrimitives.ReadUInt16LittleEndian(parameterPage[84..]);
        var bytesPerPartialPage = BinaryPrimitives.ReadUInt32LittleEndian(parameterPage[86..]);
        var sparePerPartialPage = BinaryPrimitives.ReadUInt16LittleEndian(parameterPage[90..]);
        var pagesPerBlock = BinaryPrimitives.ReadUInt32LittleEndian(parameterPage[92..]);
        var blocksPerUnit = parameterPage[96];
        var units = parameterPage[97];
        var addressCycles = parameterPage[98];
        if (pagesPerBlock == 0 || blocksPerUnit == 0 || units == 0)
        {
            return false;
        }

        geometry = new StreamingNandGeometry(
            Vendor: DecodeAscii(parameterPage[32..44]),
            Model: DecodeAscii(parameterPage[44..64]),
            BytesPerPage: bytesPerPage,
            SparePerPage: sparePerPage,
            BytesPerPartialPage: bytesPerPartialPage,
            SparePerPartialPage: sparePerPartialPage,
            PagesPerBlock: pagesPerBlock,
            BlocksPerUnit: blocksPerUnit,
            Units: units,
            AddressCycles: addressCycles);
        return true;
    }

    /// <summary>
    /// Trims the parameter-page strings at the first NUL, matching the reference
    /// <c>rstrip(b"\x00")</c> behaviour (streaming.py:124).
    /// <para>把参数页字符串在首个 NUL 处截断，对应参考的 <c>rstrip(b"\x00")</c> 行为
    /// （streaming.py:124）。</para>
    /// </summary>
    private static string DecodeAscii(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOfAny((byte)0, (byte)' ');
        var trimmed = end < 0 ? field : field[..end];
        return Encoding.ASCII.GetString(TrimEnd(trimmed, (byte)' '));
    }

    /// <summary>
    /// Removes trailing padding bytes from a decoded field.
    /// <para>去除解码字段尾部的填充字节。</para>
    /// </summary>
    private static ReadOnlySpan<byte> TrimEnd(ReadOnlySpan<byte> field, byte padding)
    {
        var end = field.Length;
        while (end > 0 && field[end - 1] == padding)
        {
            end--;
        }

        return field[..end];
    }
}