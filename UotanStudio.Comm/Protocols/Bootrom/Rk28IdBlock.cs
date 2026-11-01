using System.Buffers.Binary;
using UotanStudio.Hash.Crc16Xmodem;
using UotanStudio.Hash.RockchipCrc32;

namespace UotanStudio.Comm.Protocols.Bootrom;

/// <summary>
/// Builds the RK28 ID block that <c>upgrade_loader</c> writes at LBA 64, in both the legacy four-header-sector form and
/// the newer head/data/code form.
/// <para>构建 <c>upgrade_loader</c> 写入 LBA 64 的 RK28 ID block，既支持旧版四个头部扇区的形式，也支持较新的
/// 头部/数据/代码形式。</para>
/// </summary>
/// <remarks>
/// The legacy layout is <c>MakeIDBlockData</c>: sectors 0 to 3 carry the RK28 headers, loader data follows at sector 4
/// and loader code right after it. When the loader's <c>ucRc4Flag</c> is non-zero, loader data and code are
/// RC4-encrypted first (whole 512-byte sectors only), then sectors 0, 2 and 3 are encrypted while sector 1 stays
/// plaintext; each of the three CRC-16 values covers a still-plaintext header sector and the boot-code CRC covers the
/// already encrypted data and code. The new layout stores the loader head sector range in front of data and code and has
/// no headers: the same <c>ucRc4Flag</c> rule encrypts head, data and code, but nothing is checksummed there.
/// <para>旧版布局即 <c>MakeIDBlockData</c>：扇区 0 到 3 承载 RK28 头部，loader 数据从扇区 4 开始，loader 代码紧随其后。
/// loader 的 <c>ucRc4Flag</c> 非 0 时，loader 数据与代码先被 RC4 加密（只处理完整 512 字节扇区），随后加密扇区 0、2、3，
/// 扇区 1 保持明文；三个 CRC-16 各自覆盖仍为明文的头部扇区，boot code CRC 覆盖已加密的数据与代码。新布局把 loader 头
/// 扇区区间放在数据与代码之前且不含头部：同一条 <c>ucRc4Flag</c> 规则加密头部、数据与代码，但不做任何校验和。</para>
/// </remarks>
internal static class Rk28IdBlock
{
    /// <summary>
    /// Number of header sectors in the legacy layout. <para>旧版布局的头部扇区数。</para>
    /// </summary>
    public const int LegacyHeaderSectors = 4;

    private const int SectorSize = RkdlConstants.SectorSize;

    private const uint Sector0Tag = 0x0FF0AA55;
    private const ushort Sector1SysReservedBlock = 0xC;
    private const ushort Sector1Disk0Size = 0xFFFF;
    private const uint Sector1ChipTag = 0x38324B52;

    private const int Sector0Rc4FlagOffset = 8;
    private const int Sector0BootCode1Offset = 12;
    private const int Sector0BootCode2Offset = 14;
    private const int Sector0BootDataSizeOffset = 506;
    private const int Sector0BootCodeSizeOffset = 508;

    private const int Sector1ChipTagOffset = 10;

    private const int Sector2VcTagOffset = 491;
    private const int Sector2Sec0CrcOffset = 494;
    private const int Sector2Sec1CrcOffset = 496;
    private const int Sector2BootCodeCrcOffset = 498;
    private const int Sector2CrcTagOffset = 506;
    private const int Sector2Sec3CrcOffset = 510;

    /// <summary>
    /// Builds the legacy four-header-sector ID block.
    /// <para>构建旧版四个头部扇区的 ID block。</para>
    /// </summary>
    /// <param name="loaderData">The loader's <c>FlashData</c> payload; encrypted in place. <para>loader 的
    /// <c>FlashData</c> 载荷；会被就地加密。</para></param>
    /// <param name="loaderCode">The loader's <c>FlashBoot</c> payload; encrypted in place. <para>loader 的
    /// <c>FlashBoot</c> 载荷；会被就地加密。</para></param>
    /// <param name="flashDataSectors">Sector count of the data range, already rounded up to
    /// <see cref="RkdlConstants.LoaderIdBlockAlignment"/>. <para>数据区扇区数，已按
    /// <see cref="RkdlConstants.LoaderIdBlockAlignment"/> 向上取整。</para></param>
    /// <param name="flashBootSectors">Sector count of the code range, already rounded the same way.
    /// <para>代码区扇区数，同样已向上取整。</para></param>
    /// <param name="rc4Flag">The loader's <c>ucRc4Flag</c>; it is stored as <c>uiRc4Flag</c> and, when non-zero, makes
    /// the build RC4-encrypt data and code before storing them. <para>loader 的 <c>ucRc4Flag</c>；会写入
    /// <c>uiRc4Flag</c>，非 0 时构建过程会先对数据与代码施加 RC4 再存放。</para></param>
    /// <returns>The ID block bytes, <c>(4 + data + code) * 512</c> long. <para>ID block 字节，长度为
    /// <c>(4 + data + code) * 512</c>。</para></returns>
    /// <exception cref="RkdlException">A sector count is negative, the payloads do not fit their sector ranges, or the
    /// total sector count does not fit the 16-bit field the device reads.
    /// <para>扇区数为负、载荷超出其扇区区间，或总扇区数装不进设备读取的 16 位字段。</para></exception>
    public static byte[] Build(byte[] loaderData, byte[] loaderCode, int flashDataSectors, int flashBootSectors,
        bool rc4Flag)
    {
        ArgumentNullException.ThrowIfNull(loaderData);
        ArgumentNullException.ThrowIfNull(loaderCode);

        var totalSectors = (long)LegacyHeaderSectors + flashDataSectors + flashBootSectors;
        ValidateSectorCounts(flashDataSectors, flashBootSectors, totalSectors);
        if (loaderData.Length > (long)flashDataSectors * SectorSize)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"Loader data ({loaderData.Length} B) does not fit its {flashDataSectors} reserved sectors.");
        }

        if (loaderCode.Length > (long)flashBootSectors * SectorSize)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"Loader code ({loaderCode.Length} B) does not fit its {flashBootSectors} reserved sectors.");
        }

        var block = new byte[totalSectors * SectorSize];
        var sector0 = block.AsSpan(0, SectorSize);
        var sector1 = block.AsSpan(SectorSize, SectorSize);
        var sector2 = block.AsSpan(SectorSize * 2, SectorSize);
        var sector3 = block.AsSpan(SectorSize * 3, SectorSize);

        BinaryPrimitives.WriteUInt32LittleEndian(sector0, Sector0Tag);
        BinaryPrimitives.WriteUInt32LittleEndian(sector0[Sector0Rc4FlagOffset..], rc4Flag ? 1u : 0u);
        BinaryPrimitives.WriteUInt16LittleEndian(sector0[Sector0BootCode1Offset..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(sector0[Sector0BootCode2Offset..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(sector0[Sector0BootDataSizeOffset..], (ushort)flashDataSectors);
        BinaryPrimitives.WriteUInt16LittleEndian(sector0[Sector0BootCodeSizeOffset..],
            (ushort)(flashDataSectors + flashBootSectors));

        BinaryPrimitives.WriteUInt16LittleEndian(sector1, Sector1SysReservedBlock);
        BinaryPrimitives.WriteUInt16LittleEndian(sector1[2..], Sector1Disk0Size);
        BinaryPrimitives.WriteUInt32LittleEndian(sector1[Sector1ChipTagOffset..], Sector1ChipTag);

        "VC\0"u8.CopyTo(sector2[Sector2VcTagOffset..]);
        "CRC\0"u8.CopyTo(sector2[Sector2CrcTagOffset..]);

        if (rc4Flag)
        {
            RkdlRc4.EncryptSectors(loaderData);
            RkdlRc4.EncryptSectors(loaderCode);
        }

        loaderData.CopyTo(block, LegacyHeaderSectors * SectorSize);
        loaderCode.CopyTo(block, (LegacyHeaderSectors + flashDataSectors) * SectorSize);

        var bootCodeBytes = (flashDataSectors + flashBootSectors) * SectorSize;
        var bootCodeCrc = RockchipCrc32.Compute(block.AsSpan(LegacyHeaderSectors * SectorSize, bootCodeBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(sector2[Sector2BootCodeCrcOffset..], bootCodeCrc);

        BinaryPrimitives.WriteUInt16LittleEndian(sector2[Sector2Sec0CrcOffset..], Crc16Xmodem.Compute(sector0));
        BinaryPrimitives.WriteUInt16LittleEndian(sector2[Sector2Sec1CrcOffset..], Crc16Xmodem.Compute(sector1));
        BinaryPrimitives.WriteUInt16LittleEndian(sector2[Sector2Sec3CrcOffset..], Crc16Xmodem.Compute(sector3));

        RkdlRc4.Encrypt(sector0);
        RkdlRc4.Encrypt(sector2);
        RkdlRc4.Encrypt(sector3);
        return block;
    }

    /// <summary>
    /// Builds the newer ID block that stores a loader head sector range in front of data and code.
    /// <para>构建较新的 ID block：在数据与代码之前存放一段 loader 头扇区。</para>
    /// </summary>
    /// <param name="loaderHead">The loader's <c>FlashHead</c> payload. <para>loader 的 <c>FlashHead</c> 载荷。</para></param>
    /// <param name="loaderData">The loader's <c>FlashData</c> payload, stored at the data sector boundary.
    /// <para>loader 的 <c>FlashData</c> 载荷，存于数据扇区边界。</para></param>
    /// <param name="loaderCode">The loader's <c>FlashBoot</c> payload, stored after the data range.
    /// <para>loader 的 <c>FlashBoot</c> 载荷，存于数据区之后。</para></param>
    /// <param name="flashHeadSectors">Sector count of the head range. <para>头部区扇区数。</para></param>
    /// <param name="flashDataSectors">Sector count of the data range. <para>数据区扇区数。</para></param>
    /// <param name="flashBootSectors">Sector count of the code range. <para>代码区扇区数。</para></param>
    /// <param name="rc4Flag">The loader's <c>ucRc4Flag</c>; when non-zero, head, data and code are RC4-encrypted in place
    /// before being stored, matching the <c>upgrade_loader</c> new-ID-block branch, which has no headers to checksum.
    /// <para>loader 的 <c>ucRc4Flag</c>；非 0 时头部、数据与代码在存放前被就地 RC4 加密，与
    /// <c>upgrade_loader</c> 的新 ID block 分支一致，该分支无需校验头部。</para></param>
    /// <returns>The ID block bytes, <c>(head + data + code) * 512</c> long. <para>ID block 字节，长度为
    /// <c>(head + data + code) * 512</c>。</para></returns>
    /// <exception cref="RkdlException">A sector count is negative, a payload does not fit its sector range, or the total
    /// sector count does not fit the 16-bit field the device reads.
    /// <para>扇区数为负、某个载荷超出其扇区区间，或总扇区数装不进设备读取的 16 位字段。</para></exception>
    public static byte[] BuildNewIdBlock(byte[] loaderHead, byte[] loaderData, byte[] loaderCode, int flashHeadSectors,
        int flashDataSectors, int flashBootSectors, bool rc4Flag)
    {
        ArgumentNullException.ThrowIfNull(loaderHead);
        ArgumentNullException.ThrowIfNull(loaderData);
        ArgumentNullException.ThrowIfNull(loaderCode);

        var totalSectors = (long)flashHeadSectors + flashDataSectors + flashBootSectors;
        ValidateSectorCounts(flashHeadSectors, flashDataSectors + (long)flashBootSectors, totalSectors);
        RequireFits("Loader head", loaderHead.Length, flashHeadSectors);
        RequireFits("Loader data", loaderData.Length, flashDataSectors);
        RequireFits("Loader code", loaderCode.Length, flashBootSectors);

        if (rc4Flag)
        {
            RkdlRc4.EncryptSectors(loaderHead);
            RkdlRc4.EncryptSectors(loaderData);
            RkdlRc4.EncryptSectors(loaderCode);
        }

        var block = new byte[totalSectors * SectorSize];
        loaderHead.CopyTo(block, 0);
        loaderData.CopyTo(block, flashHeadSectors * SectorSize);
        loaderCode.CopyTo(block, (flashHeadSectors + flashDataSectors) * SectorSize);
        return block;
    }

    /// <summary>
    /// Converts a byte count to the sector count the ID block reserves for it, rounding up to
    /// <see cref="RkdlConstants.LoaderIdBlockAlignment"/> first. <para>把字节数换算为 ID block 为其预留的扇区数，
    /// 先按 <see cref="RkdlConstants.LoaderIdBlockAlignment"/> 向上取整。</para>
    /// </summary>
    /// <param name="bytes">The payload byte count. <para>载荷字节数。</para></param>
    /// <returns>The reserved sector count. <para>预留扇区数。</para></returns>
    public static int SectorsFor(int bytes)
    {
        var aligned = (long)bytes + RkdlConstants.LoaderIdBlockAlignment - 1;
        return (int)(aligned / RkdlConstants.LoaderIdBlockAlignment * RkdlConstants.LoaderIdBlockAlignment
            / SectorSize);
    }

    /// <summary>
    /// Rejects sector counts the device cannot address through the ID block's 16-bit fields.
    /// <para>拒绝设备无法通过 ID block 的 16 位字段寻址的扇区数。</para>
    /// </summary>
    /// <param name="first">First range sector count. <para>第一个区间的扇区数。</para></param>
    /// <param name="second">Second range sector count. <para>第二个区间的扇区数。</para></param>
    /// <param name="total">Total sector count. <para>总扇区数。</para></param>
    /// <exception cref="RkdlException">A count is negative, exceeds <see cref="ushort.MaxValue"/>, or the total does.
    /// <para>某个计数为负、超过 <see cref="ushort.MaxValue"/>，或总数超限。</para></exception>
    private static void ValidateSectorCounts(long first, long second, long total)
    {
        if (first < 0 || second < 0 || first > ushort.MaxValue || second > ushort.MaxValue || total > ushort.MaxValue)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"Loader ID block sector layout is out of range: {first} + {second} = {total} sectors.");
        }
    }

    private static void RequireFits(string what, int length, int sectors)
    {
        if (length > (long)sectors * SectorSize)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"{what} ({length} B) does not fit its {sectors} reserved sectors.");
        }
    }
}