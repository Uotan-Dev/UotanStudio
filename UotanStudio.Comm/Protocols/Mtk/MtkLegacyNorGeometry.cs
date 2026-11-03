using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Contains the non-IoT Legacy NOR inventory without treating OTP as ordinary storage.
/// <para>包含非 IoT Legacy NOR 清单，不将 OTP 作为普通存储。</para></summary>
public sealed class MtkLegacyNorGeometry
{
    /// <summary>
    /// The write alignment the Legacy NOR inventory is assumed to require.
    /// <para>Legacy NOR 清单被假定要求的写对齐。</para>
    /// </summary>
    /// <remarks>
    /// The 28-byte Legacy NOR reply carries status, flash id and capacity only; unlike the 16-byte XFlash
    /// NOR structure it exposes no page-size field that this inventory can be proven to carry, so writes
    /// stay at the 512-byte sector size. XFlash NOR takes an explicit erase block size instead.
    /// <para>28 字节 Legacy NOR 应答只有状态、闪存标识与容量；与 16 字节 XFlash NOR 结构不同，它没有可证实的
    /// 页大小字段，因此写入仍按 512 字节扇区对齐。XFlash NOR 则显式接收擦除块大小。</para>
    /// </remarks>
    private const int WriteAlignment = 512;

    private MtkLegacyNorGeometry(ReadOnlySpan<byte> bytes)
    {
        CapacityBytes = BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]);
        FlashId = BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]);
        InventoryHex = Convert.ToHexString(bytes);
    }

    /// <summary>Gets the reported ordinary NOR capacity, excluding OTP.
    /// <para>获取上报的普通 NOR 容量，不含 OTP。</para></summary>
    /// <value>The positive byte capacity from the 32-bit inventory field.
    /// <para>32 位清单字段提供的正数字节容量。</para></value>
    public long CapacityBytes { get; }

    /// <summary>Gets the device-reported flash identifier.
    /// <para>获取设备上报的闪存标识。</para></summary>
    /// <value>The unmodified 16-bit identifier.
    /// <para>未经修改的 16 位标识。</para></value>
    public ushort FlashId { get; }

    /// <summary>Gets the original inventory for diagnostics without granting OTP access.
    /// <para>获取用于诊断的原始清单，不授予 OTP 访问能力。</para></summary>
    /// <value>The 28-byte wire response encoded as hexadecimal.
    /// <para>编码为十六进制的 28 字节线协议响应。</para></value>
    public string InventoryHex { get; }

    internal void ValidateRange(long offset, long length, bool write)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (offset > CapacityBytes || length > CapacityBytes - offset
            || (write && (offset % WriteAlignment != 0 || length % WriteAlignment != 0)))
        {
            throw new ArgumentOutOfRangeException(nameof(length),
                $"The NOR range exceeds capacity or the write range is not aligned to {WriteAlignment} bytes.");
        }
    }

    internal static MtkLegacyNorGeometry Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 28)
        {
            throw new MtkProtocolException("The Legacy NOR inventory has an invalid length.");
        }
        var status = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (status != 0)
        {
            throw new MtkProtocolException("Legacy NOR initialization failed.", status);
        }
        if (BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) == 0)
        {
            throw new MtkProtocolException("The selected Legacy NOR storage is absent.");
        }
        return new(bytes);
    }
}