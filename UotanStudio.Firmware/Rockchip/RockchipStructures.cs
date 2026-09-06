namespace UotanStudio.Firmware.Rockchip;

public sealed class RkFwHeader
{
    public const uint MagicRkfw = 0x77666B52;

    public const uint MagicRkaf = 0x46414B52;

    public const int StructSize = 48;

    public const uint DefaultHeaderSize = 0x21C;

    public uint Tag;

    public uint Version;

    public uint HeaderSize;

    public uint FdtOffset;

    public uint FdtSize;

    public uint ImageOffset;

    public uint ImageSize;

    public uint Unknown1;

    public uint Unknown2;

    public uint Unknown3;

    public uint Unknown4;

    public uint Reserved;

    /// <summary>
    /// Reads the header fields (first 48 bytes) from a binary reader.
    /// <para>从二进制读取器中读取头部字段（前 48 字节）。</para>
    /// </summary>
    public static RkFwHeader Read(BinaryReader reader)
    {
        return new RkFwHeader
        {
            Tag = reader.ReadUInt32(),
            Version = reader.ReadUInt32(),
            HeaderSize = reader.ReadUInt32(),
            FdtOffset = reader.ReadUInt32(),
            FdtSize = reader.ReadUInt32(),
            ImageOffset = reader.ReadUInt32(),
            ImageSize = reader.ReadUInt32(),
            Unknown1 = reader.ReadUInt32(),
            Unknown2 = reader.ReadUInt32(),
            Unknown3 = reader.ReadUInt32(),
            Unknown4 = reader.ReadUInt32(),
            Reserved = reader.ReadUInt32(),
        };
    }

    /// <summary>
    /// Serializes the header fields zero-padded to header_size bytes (for synthesizing test images).
    /// <para>将头部字段序列化并零填充到 header_size 字节（用于测试合成镜像）。</para>
    /// </summary>
    public byte[] ToBytes()
    {
        var size = (int)Math.Clamp((long)HeaderSize, StructSize, 1 << 20);
        using var ms = new MemoryStream(size);
        using var writer = new BinaryWriter(ms);
        writer.Write(Tag);
        writer.Write(Version);
        writer.Write(HeaderSize);
        writer.Write(FdtOffset);
        writer.Write(FdtSize);
        writer.Write(ImageOffset);
        writer.Write(ImageSize);
        writer.Write(Unknown1);
        writer.Write(Unknown2);
        writer.Write(Unknown3);
        writer.Write(Unknown4);
        writer.Write(Reserved);
        writer.Write(new byte[size - (int)ms.Length]);
        return ms.ToArray();
    }
}

public enum RkfwEntryType
{
    Fdt = 0,

    Image = 1,
}

public sealed record RkfwEntry(string Name, long Offset, long Size, RkfwEntryType Type);
