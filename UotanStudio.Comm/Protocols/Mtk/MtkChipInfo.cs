using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk.Security;

/// <summary>The chip identity reported by the download agent device-control query.
/// <para>下载代理设备控制查询上报的芯片身份。</para></summary>
/// <remarks>This client sends the get-chip-id control code and decodes the reply as five little-endian 16-bit words in this order. The raw reply bytes are preserved so a later revision of the agent can be compared byte for byte instead of re-derived from the decoded fields.
/// <para>本客户端发送取得芯片标识的控制码，并把回复按五个小端 16 位字依此顺序解码。 保留原始回复字节，便于在代理版本变更时逐字节比较，而不是仅从解码字段反推。</para></remarks>
/// <param name="HardwareCode">The raw hardware code word.<para>原始硬件代码字。</para></param>
/// <param name="HardwareSubCode">The raw hardware subcode word.<para>原始硬件子代码字。</para></param>
/// <param name="HardwareVersion">The raw hardware revision word.<para>原始硬件修订字。</para></param>
/// <param name="SoftwareVersion">The raw software revision word.<para>原始软件修订字。</para></param>
/// <param name="ChipEvolution">The raw chip identification word.<para>原始芯片标识字。</para></param>
/// <param name="ReplyHex">The exact reply bytes as uppercase hex.
/// <para>回复字节的大写十六进制表示。</para></param>
public sealed record MtkXFlashChipId(
    ushort HardwareCode,
    ushort HardwareSubCode,
    ushort HardwareVersion,
    ushort SoftwareVersion,
    ushort ChipEvolution,
    string ReplyHex)
{
    /// <summary>Decodes a chip-identity reply.
    /// <para>解码芯片标识回复。</para></summary>
    /// <param name="reply">The bytes returned by the device-control query.
    /// <para>设备控制查询返回的字节。</para></param>
    /// <returns>The decoded identity with its raw reply preserved.
    /// <para>解码后的身份信息，并保留原始回复。</para></returns>
    /// <exception cref="MtkProtocolException"><paramref name="reply"/> is not exactly the ten bytes
    /// the documented layout requires. <para><paramref name="reply"/> 不是已记载布局要求的恰好十个字节。</para></exception>
    public static MtkXFlashChipId Decode(ReadOnlySpan<byte> reply)
    {
        if (reply.Length != 5 * 2)
        {
            throw new MtkProtocolException(
                $"The chip-identity reply must be exactly {5 * 2} bytes; the agent returned {reply.Length}.");
        }

        return new MtkXFlashChipId(
            BinaryPrimitives.ReadUInt16LittleEndian(reply),
            BinaryPrimitives.ReadUInt16LittleEndian(reply[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(reply[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(reply[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(reply[8..]),
            Convert.ToHexString(reply));
    }
}

/// <summary>A device-control query whose reply is consumed as opaque bytes.
/// <para>回复被当作不透明字节消费的器件控制查询。</para></summary>
/// <remarks>
/// Neither reply is decoded, so the bytes are returned unchanged and no claim is made about their
/// meaning.
/// <para>这两个控制码的回复都不解码，因此原样返回字节，并不对其含义作出结论。</para>
/// </remarks>
public enum MtkXFlashRawControlCode
{
    /// <summary>The replay-protected-memory-block state query.
    /// <para>重放保护块状态查询。</para></summary>
    ReplayProtectedAreaStatus = 0x040010,
    /// <summary>The hardware identifier query.
    /// <para>硬件标识符查询。</para></summary>
    HardwareIdentifier = 0x040014
}

/// <summary>One RAM region reported by the device-control RAM-info query.
/// <para>器件控制内存信息查询上报的一个 RAM 区域。</para></summary>
/// <param name="Type">The region type as reported; zero means the region is treated as
/// absent. <para>按上报原样保留的区域类型；0 表示该区域被视为不存在。</para></param>
/// <param name="BaseAddress">The region base address. 区域基地址。</param>
/// <param name="Size">The region size in bytes. 区域字节大小。</param>
public sealed record MtkXFlashRamRegion(ulong Type, ulong BaseAddress, ulong Size);

/// <summary>The SRAM and DRAM regions reported by the device-control RAM-info query.
/// <para>器件控制内存信息查询上报的 SRAM 与 DRAM 区域。</para></summary>
/// <remarks>
/// The reply is either six little-endian doublewords or six little-endian quadwords, decoded as SRAM
/// type, SRAM base, SRAM size, DRAM type, DRAM base and DRAM size. Any other reply width is rejected
/// here instead of being silently truncated.
/// <para>回复为六个小端双字或六个小端四字，按 SRAM 类型、SRAM 基址、SRAM 大小、DRAM 类型、DRAM 基址、
/// DRAM 大小解码。其他回复宽度在这里都会被拒绝，而不是被静默截断。</para>
/// </remarks>
/// <param name="Sram">The SRAM region. SRAM 区域。</param>
/// <param name="Dram">The DRAM region. DRAM 区域。</param>
/// <param name="ReplyHex">The exact reply bytes as uppercase hex.
/// <para>回复字节的大写十六进制表示。</para></param>
public sealed record MtkXFlashRamInfo(MtkXFlashRamRegion Sram, MtkXFlashRamRegion Dram, string ReplyHex)
{
    /// <summary>Decodes a RAM-info reply of the two documented widths.
    /// <para>按两种已记载宽度解码内存信息回复。</para></summary>
    /// <param name="reply">The bytes returned by the device-control query.
    /// <para>器件控制查询返回的字节。</para></param>
    /// <returns>The decoded regions with the raw reply preserved.
    /// <para>解码后的区域，并保留原始回复。</para></returns>
    /// <exception cref="MtkProtocolException"><paramref name="reply"/> is neither 24 bytes of
    /// doublewords nor 48 bytes of quadwords.
    /// <para><paramref name="reply"/> 既不是 24 字节双字也不是 48 字节四字。</para></exception>
    public static MtkXFlashRamInfo Decode(ReadOnlySpan<byte> reply)
    {
        var hex = Convert.ToHexString(reply);
        if (reply.Length == 6 * 4)
        {
            return new MtkXFlashRamInfo(
                new MtkXFlashRamRegion(
                    BinaryPrimitives.ReadUInt32LittleEndian(reply),
                    BinaryPrimitives.ReadUInt32LittleEndian(reply[4..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(reply[8..])),
                new MtkXFlashRamRegion(
                    BinaryPrimitives.ReadUInt32LittleEndian(reply[12..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(reply[16..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(reply[20..])),
                hex);
        }
        if (reply.Length == 6 * 8)
        {
            return new MtkXFlashRamInfo(
                new MtkXFlashRamRegion(
                    BinaryPrimitives.ReadUInt64LittleEndian(reply),
                    BinaryPrimitives.ReadUInt64LittleEndian(reply[8..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(reply[16..])),
                new MtkXFlashRamRegion(
                    BinaryPrimitives.ReadUInt64LittleEndian(reply[24..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(reply[32..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(reply[40..])),
                hex);
        }
        throw new MtkProtocolException(
            $"The RAM-info reply must be 24 doubleword bytes or 48 quadword bytes; the agent returned " +
            $"{reply.Length}.");
    }
}

/// <summary>The partition-table families the device-control category query distinguishes.
/// <para>器件控制类别查询可区分的分区表家族。</para></summary>
/// <remarks>
/// This implementation decodes one little-endian doubleword and recognizes exactly these two values;
/// any other
/// value is rejected here instead of being reported as a bare zero.
/// <para>本实现解码一个小端双字且只识别这两个取值；其他取值在这里被拒绝，而不是仅返回一个 0。</para>
/// </remarks>
public enum MtkPartitionTableCategory
{
    /// <summary>The GUID partition table (category 0x64). <para>GUID 分区表（类别 0x64）。</para></summary>
    Gpt = 0x64,
    /// <summary>The proprietary partition table (category 0x65).
    /// <para>专有分区表（类别 0x65）。</para></summary>
    Pmt = 0x65
}