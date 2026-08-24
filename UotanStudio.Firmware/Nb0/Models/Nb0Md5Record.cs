namespace UotanStudio.Firmware.Nb0;

/// <summary>
/// <para>表示 FIH Mobile NB0 格式中的 MD5 校验记录,包含数据偏移量、数据长度和 MD5 校验和。</para>
/// Represents an MD5 verification record in the FIH Mobile NB0 format, containing data offset, data length, and MD5 checksum.
/// </summary>
/// <remarks>
/// <para>二进制布局(共 24 字节):Offset(4) | Length(4) | Md5Checksum(16)</para>
/// Binary layout (24 bytes): Offset(4) | Length(4) | Md5Checksum(16)
/// </remarks>
public sealed class Nb0Md5Record
{
    /// <summary>
    /// <para>MD5 校验记录结构的固定大小(字节)。</para>
    /// The fixed size of the MD5 record structure in bytes.
    /// </summary>
    public const int StructSize = 24;

    private const int Md5Size = 16;

    /// <summary>
    /// <para>获取或设置数据偏移量。</para>
    /// Gets or sets the data offset.
    /// </summary>
    public uint Offset { get; set; }

    /// <summary>
    /// <para>获取或设置数据长度。</para>
    /// Gets or sets the data length.
    /// </summary>
    public uint Length { get; set; }

    /// <summary>
    /// <para>获取或设置 MD5 校验和(16 字节)。</para>
    /// Gets or sets the MD5 checksum (16 bytes).
    /// </summary>
    public byte[] Md5Checksum { get; set; } = new byte[Md5Size];

    /// <summary>
    /// <para>获取 MD5 校验和的十六进制字符串表示。</para>
    /// Gets the hexadecimal string representation of the MD5 checksum.
    /// </summary>
    public string Md5Hex => Convert.ToHexString(Md5Checksum).ToLowerInvariant();

    /// <summary>
    /// <para>从二进制读取器中读取并构造一个 Nb0Md5Record 实例。</para>
    /// Reads and constructs an Nb0Md5Record instance from a binary reader.
    /// </summary>
    public static Nb0Md5Record Read(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var record = new Nb0Md5Record
        {
            Offset = reader.ReadUInt32(),
            Length = reader.ReadUInt32(),
            Md5Checksum = reader.ReadBytes(Md5Size)
        };
        return record;
    }

    /// <summary>
    /// <para>将当前 MD5 校验记录写入指定的二进制写入器。</para>
    /// Writes the current MD5 record to the specified binary writer.
    /// </summary>
    public void Write(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Write(Offset);
        writer.Write(Length);
        writer.Write(Md5Checksum, 0, Md5Size);
    }
}