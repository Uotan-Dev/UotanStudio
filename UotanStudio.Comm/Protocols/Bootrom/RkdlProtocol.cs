using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Hash.Crc16CcittFalse;
using UotanStudio.Hash.Crc32Ieee;

namespace UotanStudio.Comm.Protocols.Bootrom;

/// <summary>
/// Rockchip rockusb CBW/CSW frame structures for the UFI-style bulk protocol. The CBW is
/// 31 bytes and the CSW is 13 bytes.
/// <para>Rockchip rockusb 的 UFI 风格批量协议 CBW/CSW 帧结构。CBW 为 31 字节，
/// CSW 为 13 字节。</para>
/// </summary>
internal static class RkdlFrame
{
    /// <summary>
    /// CBW signature bytes in transmission order: the DWORD 0x43425355 stored
    /// little-endian, which puts the ASCII bytes USBC on the wire.
    /// <para>CBW 签名的传输顺序字节：DWORD 0x43425355 按小端存储，线上即为 ASCII
    /// 的 USBC。</para>
    /// </summary>
    public static readonly byte[] CbwSignature = [0x55, 0x53, 0x42, 0x43];

    /// <summary>
    /// CSW signature bytes in transmission order: the DWORD 0x53425355 stored
    /// little-endian, which puts the ASCII bytes USBS on the wire.
    /// <para>CSW 签名的传输顺序字节：DWORD 0x53425355 按小端存储，线上即为 ASCII
    /// 的 USBS。</para>
    /// </summary>
    public static readonly byte[] CswSignature = [0x55, 0x53, 0x42, 0x53];

    /// <summary>
    /// CBW flags: device-to-host (IN) direction.
    /// <para>CBW 标志：设备到主机（IN）方向。</para>
    /// </summary>
    public const byte DirectionIn = 0x80;

    /// <summary>
    /// CBW flags: host-to-device (OUT) direction.
    /// <para>CBW 标志：主机到设备（OUT）方向。</para>
    /// </summary>
    public const byte DirectionOut = 0x00;

    /// <summary>
    /// Length of a serialized CBW in bytes.
    /// <para>序列化 CBW 的字节长度。</para>
    /// </summary>
    public const int CbwLength = 31;

    /// <summary>
    /// Length of a serialized CSW in bytes.
    /// <para>序列化 CSW 的字节长度。</para>
    /// </summary>
    public const int CswLength = 13;

    /// <summary>
    /// CBWCB length for commands without a data phase (0x06).
    /// <para>无数据阶段命令的 CBWCB 长度（0x06）。</para>
    /// </summary>
    public const byte CbwcbLengthShort = 0x06;

    /// <summary>
    /// CBWCB length for LBA/address based commands (0x0A).
    /// <para>基于 LBA/地址的命令的 CBWCB 长度（0x0A）。</para>
    /// </summary>
    public const byte CbwcbLengthLong = 0x0A;
}

/// <summary>
/// DEVICE_RESET sub codes sent with the reset command (RESET_SUBCODE).
/// <para>随复位命令发送的 DEVICE_RESET 子码（RESET_SUBCODE）。</para>
/// </summary>
internal static class RkdlResetSubCode
{
    /// <summary>No sub code. <para>无子码。</para></summary>
    public const byte None = 0;

    /// <summary>Reset the USB Mass Storage class. <para>复位 USB 大容量存储类。</para></summary>
    public const byte ResetMsc = 1;

    /// <summary>Power the device off. <para>设备关机。</para></summary>
    public const byte PowerOff = 2;

    /// <summary>Reset back into mask ROM mode. <para>复位回到 Mask ROM 模式。</para></summary>
    public const byte ResetMaskRom = 3;

    /// <summary>Disconnect and reset. <para>断开并复位。</para></summary>
    public const byte DisconnectReset = 4;
}

/// <summary>
/// Command Block Wrapper (CBW) for the Rockchip rockusb protocol.
/// <para>Rockchip rockusb 协议的命令块包装（CBW）。</para>
/// </summary>
/// <param name="operationCode">The rockusb operation code. <para>rockusb 操作码。</para></param>
/// <param name="flags">The direction flags for the data phase. <para>数据阶段的方向标志。</para></param>
/// <param name="transferLength">The data phase length in bytes (dwCBWTransferLength). <para>数据阶段长度（字节，dwCBWTransferLength）。</para></param>
/// <param name="cbwcbLength">Length of the command block (0x06 or 0x0A). <para>命令块长度（0x06 或 0x0A）。</para></param>
/// <param name="subCode">The value stored in the reserved command-block byte. <para>存入命令块保留字节的值。</para></param>
/// <param name="address">The LBA address, stored big-endian in the command block. <para>LBA 地址，在命令块中按大端序存储。</para></param>
/// <param name="sectorLength">The sector count, stored big-endian in the command block. <para>扇区数，在命令块中按大端序存储。</para></param>
internal sealed class RkdlCbw(RkdlCommand operationCode, byte flags, uint transferLength, byte cbwcbLength,
    byte subCode = 0, uint address = 0, ushort sectorLength = 0)
{

    /// <summary>
    /// Gets the random CBW tag used to match the corresponding CSW.
    /// <para>获取用于匹配对应 CSW 的随机 CBW 标签。</para>
    /// </summary>
    /// <value>The random tag. <para>随机标签。</para></value>
    public uint Tag { get; } = MakeTag();

    /// <summary>
    /// Gets the operation code.
    /// <para>获取操作码。</para>
    /// </summary>
    /// <value>The rockusb operation code. <para>rockusb 操作码。</para></value>
    public byte OperationCode { get; } = (byte)operationCode;

    /// <summary>
    /// Gets the direction flags.
    /// <para>获取方向标志。</para>
    /// </summary>
    /// <value>The data phase direction flags. <para>数据阶段的方向标志。</para></value>
    public byte Flags { get; } = flags;

    /// <summary>
    /// Gets the data phase transfer length in bytes.
    /// <para>获取数据阶段的传输长度（字节）。</para>
    /// </summary>
    /// <value>The transfer length. <para>传输长度。</para></value>
    public uint TransferLength { get; } = transferLength;

    /// <summary>
    /// Gets the CBWCB (command block) length.
    /// <para>获取 CBWCB（命令块）长度。</para>
    /// </summary>
    /// <value>The command block length. <para>命令块长度。</para></value>
    public byte CbwcbLength { get; } = cbwcbLength;

    /// <summary>
    /// Gets the sub code stored in the reserved command-block byte.
    /// <para>获取存储在命令块保留字节中的子码。</para>
    /// </summary>
    /// <value>The sub code. <para>子码。</para></value>
    public byte SubCode { get; } = subCode;

    /// <summary>
    /// Gets the LBA address, kept in host byte order and serialized big-endian.
    /// <para>获取 LBA 地址，保持主机字节序并按大端序序列化。</para>
    /// </summary>
    /// <value>The LBA address. <para>LBA 地址。</para></value>
    public uint Address { get; } = address;

    /// <summary>
    /// Gets the sector count, kept in host byte order and serialized big-endian.
    /// <para>获取扇区数，保持主机字节序并按大端序序列化。</para>
    /// </summary>
    /// <value>The sector count. <para>扇区数。</para></value>
    public ushort SectorLength { get; } = sectorLength;

    /// <summary>
    /// Generates a random 4-byte CBW tag.
    /// <para>生成随机的 4 字节 CBW 标签。</para>
    /// </summary>
    /// <returns>The random tag. <para>随机标签。</para></returns>
    public static uint MakeTag()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    /// <summary>
    /// Serializes the CBW into its 31-byte wire format.
    /// <para>将 CBW 序列化为 31 字节的线上格式。</para>
    /// </summary>
    /// <returns>The serialized CBW bytes. <para>序列化后的 CBW 字节。</para></returns>
    public byte[] ToBytes()
    {
        var bytes = new byte[RkdlFrame.CbwLength];
        bytes[0] = RkdlFrame.CbwSignature[0];
        bytes[1] = RkdlFrame.CbwSignature[1];
        bytes[2] = RkdlFrame.CbwSignature[2];
        bytes[3] = RkdlFrame.CbwSignature[3];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), Tag);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), TransferLength);
        bytes[12] = Flags;
        bytes[13] = 0;
        bytes[14] = CbwcbLength;
        bytes[15] = OperationCode;
        bytes[16] = SubCode;
        bytes[17] = (byte)(Address >> 24);
        bytes[18] = (byte)(Address >> 16);
        bytes[19] = (byte)(Address >> 8);
        bytes[20] = (byte)Address;
        bytes[21] = 0;
        bytes[22] = (byte)(SectorLength >> 8);
        bytes[23] = (byte)SectorLength;
        return bytes;
    }
}

/// <summary>
/// Command Status Wrapper (CSW) for the Rockchip rockusb protocol.
/// <para>Rockchip rockusb 协议的命令状态包装（CSW）。</para>
/// </summary>
internal sealed class RkdlCsw
{
    private RkdlCsw(uint tag, uint dataResidue, byte status)
    {
        Tag = tag;
        DataResidue = dataResidue;
        Status = status;
    }

    /// <summary>
    /// Gets the CBW tag echoed back by the device.
    /// <para>获取设备回显的 CBW 标签。</para>
    /// </summary>
    /// <value>The echoed tag. <para>回显的标签。</para></value>
    public uint Tag { get; }

    /// <summary>
    /// Gets the data residue reported by the device.
    /// <para>获取设备报告的未处理数据余量。</para>
    /// </summary>
    /// <value>The residue in bytes. <para>余量的字节数。</para></value>
    public uint DataResidue { get; }

    /// <summary>
    /// Gets the CSW status (0 = success, 1 = failure).
    /// <para>获取 CSW 状态（0 = 成功，1 = 失败）。</para>
    /// </summary>
    /// <value>The raw status byte. <para>原始状态字节。</para></value>
    public byte Status { get; }

    /// <summary>
    /// Gets a value indicating whether the command succeeded, that is, the status byte is zero.
    /// <para>获取命令是否成功的值，即状态字节为零。</para>
    /// </summary>
    /// <value><c>true</c> when the status byte is zero. <para>状态字节为零时为 <c>true</c>。</para></value>
    public bool IsSuccess => Status == 0;

    /// <summary>
    /// Attempts to parse a CSW from raw bytes and validates signature + tag (UFI_CHECK_SIGN).
    /// <para>尝试从原始字节解析 CSW，并校验签名与标签（UFI_CHECK_SIGN）。</para>
    /// </summary>
    /// <param name="bytes">The raw bytes read from the bulk IN endpoint. <para>从 bulk IN 端点读取的原始字节。</para></param>
    /// <param name="expectedTag">The CBW tag the CSW must echo. <para>CSW 必须回显的 CBW 标签。</para></param>
    /// <param name="csw">The parsed CSW when successful. <para>成功时解析出的 CSW。</para></param>
    /// <returns><c>true</c> when the CSW signature and tag match. <para>CSW 签名与标签匹配时返回 <c>true</c>。</para></returns>
    public static bool TryParse(ReadOnlySpan<byte> bytes, uint expectedTag, out RkdlCsw? csw)
    {
        csw = null;
        if (bytes.Length < RkdlFrame.CswLength)
        {
            return false;
        }

        if (!bytes[..4].SequenceEqual(RkdlFrame.CswSignature))
        {
            return false;
        }

        var tag = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
        if (tag != expectedTag)
        {
            return false;
        }

        csw = new RkdlCsw(tag,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4)),
            bytes[12]);
        return true;
    }
}

/// <summary>
/// CRC helpers for the RKDL protocol: CRC-16/CCITT for the loader download and CRC-32
/// for the GPT header checksums. Both parameter sets come from
/// <c>UotanStudio.Hash</c>.
/// <para>RKDL 协议的 CRC 辅助方法：loader 下载用 CRC-16/CCITT，GPT 头校验用 CRC-32。两组参数都取自
/// <c>UotanStudio.Hash</c>。</para>
/// </summary>
internal static class RkdlCrc
{
    /// <summary>
    /// Computes CRC-16/CCITT (poly 0x1021, init 0xFFFF, MSB-first, no final XOR) over the data.
    /// The polynomial, seed and absence of a final XOR come from
    /// <c>Crc16CcittFalse.Compute</c> in <c>UotanStudio.Hash</c>.
    /// <para>计算数据的 CRC-16/CCITT（多项式 0x1021，初值 0xFFFF，高位优先，无最终异或）。多项式、初值与
    /// 不做最终异或的约定都取自 <c>UotanStudio.Hash</c> 的 <c>Crc16CcittFalse.Compute</c>。</para>
    /// </summary>
    /// <param name="data">The data to checksum. <para>待校验数据。</para></param>
    /// <returns>The CRC-16/CCITT value. <para>CRC-16/CCITT 值。</para></returns>
    public static ushort Crc16Ccitt(ReadOnlySpan<byte> data)
    {
        return Crc16CcittFalse.Compute(data);
    }

    /// <summary>
    /// Computes the standard CRC-32 (IEEE 802.3, poly 0xEDB88320, init 0xFFFFFFFF, final XOR).
    /// The polynomial, seed and final XOR come from <c>Crc32Ieee.Compute</c> in <c>UotanStudio.Hash</c>.
    /// <para>计算标准 CRC-32（IEEE 802.3，多项式 0xEDB88320，初值 0xFFFFFFFF，最终异或）。多项式、初值与
    /// 最终异或的约定都取自 <c>UotanStudio.Hash</c> 的 <c>Crc32Ieee.Compute</c>。</para>
    /// </summary>
    /// <param name="data">The data to checksum. <para>待校验数据。</para></param>
    /// <returns>The CRC-32 value. <para>CRC-32 值。</para></returns>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        return Crc32Ieee.Compute(data);
    }
}

/// <summary>
/// GPT layout helpers that build and update the master and backup GPT tables.
/// <para>构建并更新主 GPT 表与备份 GPT 表的布局辅助方法。</para>
/// </summary>
internal static class RkdlGpt
{
    /// <summary>Sectors of the primary GPT table (protective MBR + header + 128 entries). <para>主 GPT 表扇区数（保护 MBR + 头 + 128 个条目）。</para></summary>
    public const int PrimarySectors = 34;

    /// <summary>Sectors of the backup GPT table (128 entries + header). <para>备份 GPT 表扇区数（128 个条目 + 头）。</para></summary>
    public const int BackupSectors = 33;

    /// <summary>GPT header signature "EFI PART" (0x5452415020494645). <para>GPT 头签名 "EFI PART"（0x5452415020494645）。</para></summary>
    public const ulong HeaderSignature = 0x5452415020494645UL;

    /// <summary>
    /// Builds the 33-sector backup GPT from a 34-sector master GPT image.
    /// <para>从 34 扇区的主 GPT 镜像构建 33 扇区的备份 GPT。</para>
    /// </summary>
    /// <param name="masterGpt">The 34-sector master GPT image. <para>34 扇区的主 GPT 镜像。</para></param>
    /// <returns>The 33-sector backup GPT image. <para>33 扇区的备份 GPT 镜像。</para></returns>
    public static byte[] BuildBackupGpt(byte[] masterGpt)
    {
        if (masterGpt == null || masterGpt.Length < PrimarySectors * RkdlConstants.SectorSize)
        {
            throw new ArgumentException("Master GPT must span at least 34 sectors.", nameof(masterGpt));
        }

        var backup = new byte[BackupSectors * RkdlConstants.SectorSize];
        var headerOff = 32 * RkdlConstants.SectorSize;

        Array.Copy(masterGpt, 2 * RkdlConstants.SectorSize, backup, 0, 32 * RkdlConstants.SectorSize);
        Array.Copy(masterGpt, 1 * RkdlConstants.SectorSize, backup, headerOff, RkdlConstants.SectorSize);

        var myLba = BinaryPrimitives.ReadUInt64LittleEndian(masterGpt.AsSpan(RkdlConstants.SectorSize + 24));
        var alternateLba = BinaryPrimitives.ReadUInt64LittleEndian(masterGpt.AsSpan(RkdlConstants.SectorSize + 32));
        BinaryPrimitives.WriteUInt64LittleEndian(backup.AsSpan(headerOff + 24), alternateLba);
        BinaryPrimitives.WriteUInt64LittleEndian(backup.AsSpan(headerOff + 32), myLba);

        var lastUsableLba = BinaryPrimitives.ReadUInt64LittleEndian(masterGpt.AsSpan(RkdlConstants.SectorSize + 48));
        BinaryPrimitives.WriteUInt64LittleEndian(backup.AsSpan(headerOff + 72), lastUsableLba + 1);

        BinaryPrimitives.WriteUInt32LittleEndian(backup.AsSpan(headerOff + 16), 0);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(backup.AsSpan(headerOff + 12));
        BinaryPrimitives.WriteUInt32LittleEndian(backup.AsSpan(headerOff + 16),
            RkdlCrc.Crc32(backup.AsSpan(headerOff, (int)headerSize)));

        return backup;
    }

    /// <summary>
    /// Updates the master and backup GPT tables for the given total sector count, growing
    /// the final partition when needed and recomputing the CRCs.
    /// <para>按给定总扇区数更新主 GPT 与备份 GPT 表，必要时扩展最后一个分区并重算 CRC。</para>
    /// </summary>
    /// <param name="masterGpt">The 34-sector master GPT image (updated in place). <para>34 扇区的主 GPT 镜像（原地更新）。</para></param>
    /// <param name="backupGpt">The 33-sector backup GPT image (rebuilt). <para>33 扇区的备份 GPT 镜像（重建）。</para></param>
    /// <param name="totalSectors">Total sector count of the storage device. <para>存储设备的总扇区数。</para></param>
    public static void UpdateDiskSize(byte[] masterGpt, byte[] backupGpt, uint totalSectors)
    {
        if (masterGpt == null || masterGpt.Length < PrimarySectors * RkdlConstants.SectorSize)
        {
            throw new ArgumentException("Master GPT must span at least 34 sectors.", nameof(masterGpt));
        }

        if (backupGpt == null || backupGpt.Length < BackupSectors * RkdlConstants.SectorSize)
        {
            throw new ArgumentException("Backup GPT must span at least 33 sectors.", nameof(backupGpt));
        }

        if (totalSectors <= BackupSectors)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSectors), "Total sector count must exceed the backup GPT size.");
        }

        var hdr = RkdlConstants.SectorSize;
        var oldDiskSize = BinaryPrimitives.ReadUInt64LittleEndian(masterGpt.AsSpan(hdr + 32)) + 1;

        BinaryPrimitives.WriteUInt64LittleEndian(masterGpt.AsSpan(hdr + 32), (ulong)totalSectors - 1);
        BinaryPrimitives.WriteUInt64LittleEndian(masterGpt.AsSpan(hdr + 48), (ulong)totalSectors - 34);

        var numEntries = BinaryPrimitives.ReadUInt32LittleEndian(masterGpt.AsSpan(hdr + 80));
        var lastEntryIndex = -1;
        for (var i = 0; i < (int)numEntries; i++)
        {
            var off = (2 * RkdlConstants.SectorSize) + (i * 128);
            if (IsZeroBlock(masterGpt, off, 128))
            {
                break;
            }

            lastEntryIndex = i;
        }
        if (lastEntryIndex >= 0)
        {
            var entryOff = (2 * RkdlConstants.SectorSize) + (lastEntryIndex * 128);
            var endingLba = BinaryPrimitives.ReadUInt64LittleEndian(masterGpt.AsSpan(entryOff + 48));
            if (endingLba == oldDiskSize - 34)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(masterGpt.AsSpan(entryOff + 48), (ulong)totalSectors - 34);
                BinaryPrimitives.WriteUInt32LittleEndian(masterGpt.AsSpan(hdr + 88),
                    RkdlCrc.Crc32(masterGpt.AsSpan(2 * RkdlConstants.SectorSize, 128 * 128)));
            }
        }

        BinaryPrimitives.WriteUInt32LittleEndian(masterGpt.AsSpan(hdr + 16), 0);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(masterGpt.AsSpan(hdr + 12));
        BinaryPrimitives.WriteUInt32LittleEndian(masterGpt.AsSpan(hdr + 16),
            RkdlCrc.Crc32(masterGpt.AsSpan(hdr, (int)headerSize)));

        var rebuilt = BuildBackupGpt(masterGpt);
        Array.Copy(rebuilt, backupGpt, rebuilt.Length);
    }

    private static bool IsZeroBlock(byte[] data, int offset, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (data[offset + i] != 0)
            {
                return false;
            }
        }
        return true;
    }
}