namespace UotanStudio.Firmware.Rockchip;

/// <summary>
/// <para>Rockchip RKAF/RKFW 固件镜像头部常量与字段。</para>
/// Header constants and fields of a Rockchip RKAF/RKFW firmware image.
/// </summary>
/// <remarks>
/// <para>字段布局依据 Rockchip 官方 afptool / rkdeveloptool（rkfw.c / afptool.c）公开源码，
/// 全部字段为小端序 u32（version 与 header_size 均按上游为 u32，而非 16 位）。前 0x30 字节
/// 为解析字段；头部在文件中实际占用 header_size（RKFW 通常 0x21C=540）字节，其余为零填充保留区。
/// 无原生工具（afptool / imgRePackerRK）交叉验证，格式仅依据上游公开源码并经测试内自洽回环镜像验证
/// （同 Zucchini 先例）。</para>
/// Field layout follows the Rockchip upstream afptool / rkdeveloptool (rkfw.c / afptool.c) public
/// source; all fields are little-endian u32 (version and header_size are u32 per upstream, not 16-bit).
/// The first 0x30 bytes are the parsed fields; the on-disk header occupies header_size bytes
/// (0x21C = 540 for RKFW) with the remainder zero-padded reserved space. There is no native-tool
/// cross-validation; the layout rests on the upstream public source plus self-consistent synthetic
/// round-trip tests (same precedent as Zucchini).
/// </remarks>
public sealed class RkFwHeader
{
    /// <summary>魔数 "Rkfw"（小端 u32）。<para>Magic "Rkfw" (little-endian u32).</para></summary>
    public const uint MagicRkfw = 0x77666B52;

    /// <summary>魔数 "RKAF"（小端 u32）。<para>Magic "RKAF" (little-endian u32).</para></summary>
    public const uint MagicRkaf = 0x46414B52;

    /// <summary>解析字段的固定结构大小（字节）。<para>Fixed size of the parsed fields in bytes.</para></summary>
    public const int StructSize = 48;

    /// <summary>上游工具写出的默认头部大小（0x21C=540 字节）。<para>Default header size written by upstream tools (0x21C = 540 bytes).</para></summary>
    public const uint DefaultHeaderSize = 0x21C;

    /// <summary>魔数。<para>Magic tag.</para></summary>
    public uint Tag;

    /// <summary>格式版本（通常为 1）。<para>Format version (usually 1).</para></summary>
    public uint Version;

    /// <summary>头部大小（字节）。<para>Header size in bytes.</para></summary>
    public uint HeaderSize;

    /// <summary>FDT（DTB 资源）在文件中的偏移。<para>File offset of the FDT (DTB resource).</para></summary>
    public uint FdtOffset;

    /// <summary>FDT 数据大小。<para>Size of the FDT data.</para></summary>
    public uint FdtSize;

    /// <summary>镜像区在文件中的偏移。<para>File offset of the image section.</para></summary>
    public uint ImageOffset;

    /// <summary>镜像区数据大小。<para>Size of the image section.</para></summary>
    public uint ImageSize;

    /// <summary>保留字段。<para>Reserved field.</para></summary>
    public uint Unknown1;

    /// <summary>保留字段。<para>Reserved field.</para></summary>
    public uint Unknown2;

    /// <summary>保留字段。<para>Reserved field.</para></summary>
    public uint Unknown3;

    /// <summary>保留字段。<para>Reserved field.</para></summary>
    public uint Unknown4;

    /// <summary>保留字段。<para>Reserved field.</para></summary>
    public uint Reserved;

    /// <summary>
    /// <para>从二进制读取器中读取头部字段（前 48 字节）。</para>
    /// Reads the header fields (first 48 bytes) from a binary reader.
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
    /// <para>将头部字段序列化并零填充到 header_size 字节（用于测试合成镜像）。</para>
    /// Serializes the header fields zero-padded to header_size bytes (for synthesizing test images).
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

/// <summary>
/// <para>Rockchip RKAF/RKFW 镜像条目的类型。</para>
/// Type of an entry in a Rockchip RKAF/RKFW firmware image.
/// </summary>
public enum RkfwEntryType
{
    /// <summary>FDT（DTB 资源）区。<para>The FDT (DTB resource) region.</para></summary>
    Fdt = 0,

    /// <summary>镜像区。<para>The image section.</para></summary>
    Image = 1,
}

/// <summary>
/// <para>Rockchip RKAF/RKFW 镜像中的条目（名称/偏移/大小/类型）。</para>
/// An entry (name/offset/size/type) within a Rockchip RKAF/RKFW firmware image.
/// </summary>
/// <param name="Name">条目输出文件名。<para>The output file name of the entry.</para></param>
/// <param name="Offset">条目在镜像文件中的偏移。<para>Offset of the entry within the image file.</para></param>
/// <param name="Size">条目数据大小（字节）。<para>Size of the entry data in bytes.</para></param>
/// <param name="Type">条目类型。<para>The entry type.</para></param>
public sealed record RkfwEntry(string Name, long Offset, long Size, RkfwEntryType Type);