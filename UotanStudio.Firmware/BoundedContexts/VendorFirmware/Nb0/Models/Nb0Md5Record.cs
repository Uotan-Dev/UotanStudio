namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Models;

public sealed class Nb0Md5Record
{
    public const int StructSize = 24;

    private const int Md5Size = 16;

    public uint Offset { get; set; }

    public uint Length { get; set; }

    public byte[] Md5Checksum { get; set; } = new byte[Md5Size];

    public string Md5Hex => Convert.ToHexString(Md5Checksum).ToLowerInvariant();

    /// <summary>
    /// Reads and constructs an Nb0Md5Record instance from a binary reader.
    /// <para>从二进制读取器中读取并构造一个 Nb0Md5Record 实例。</para>
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
    /// Writes the current MD5 record to the specified binary writer.
    /// <para>将当前 MD5 校验记录写入指定的二进制写入器。</para>
    /// </summary>
    public void Write(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Write(Offset);
        writer.Write(Length);
        writer.Write(Md5Checksum, 0, Md5Size);
    }
}