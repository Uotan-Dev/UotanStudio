using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Rockchip.Models;

/// <summary>Represents the packed RKFW header.<para>表示 RKFW 紧凑头部。</para></summary>
public sealed class RkFwHeader
{
    /// <summary>Defines the RKFW magic.<para>定义 RKFW 魔数。</para></summary>
    public const uint MagicRkfw = 0x57464B52;
    /// <summary>Defines the RKAF magic.<para>定义 RKAF 魔数。</para></summary>
    public const uint MagicRkaf = 0x46414B52;
    /// <summary>Defines the packed header length.<para>定义紧凑头部长度。</para></summary>
    public const int StructSize = 102;
    /// <summary>Gets or sets the magic.<para>获取或设置魔数。</para></summary>
    /// <value>The four-byte container signature; RKFW files carry <see cref="MagicRkfw"/> here.
    /// <para>四字节容器签名；RKFW 文件此处的值为 <see cref="MagicRkfw"/>。</para></value>
    public uint Tag { get; set; } = MagicRkfw;
    /// <summary>Gets or sets the header length.<para>获取或设置头部长度。</para></summary>
    /// <value>The byte length declared by the header; callers check it against <see cref="StructSize"/>,
    /// and <see cref="ToBytes"/> uses it as the serialized buffer size.
    /// <para>头部声明的字节长度；调用方将其与 <see cref="StructSize"/> 比对，
    /// <see cref="ToBytes"/> 以其作为序列化缓冲区大小。</para></value>
    public ushort HeaderSize { get; set; } = StructSize;
    /// <summary>Gets or sets the firmware version.<para>获取或设置固件版本。</para></summary>
    /// <value>The little-endian firmware version field, stored without any interpretation.
    /// <para>小端序的固件版本字段，原样存储且不作解释。</para></value>
    public uint Version { get; set; }
    /// <summary>Gets or sets the merger version.<para>获取或设置合并工具版本。</para></summary>
    /// <value>The little-endian version of the tool that merged the container, stored without any interpretation.
    /// <para>小端序的容器合并工具版本，原样存储且不作解释。</para></value>
    public uint MergeVersion { get; set; }
    /// <summary>Gets or sets the seven-byte release time.<para>获取或设置七字节发布时间。</para></summary>
    /// <value>Exactly seven raw release-time bytes; no encoding is applied when reading or writing.
    /// <para>恰好七个原始发布时间字节；读写时不做任何编码转换。</para></value>
    public byte[] ReleaseTime { get; set; } = new byte[7];
    /// <summary>Gets or sets the chip identifier.<para>获取或设置芯片标识。</para></summary>
    /// <value>The little-endian chip identifier of the target Rockchip SoC.
    /// <para>小端序的目标瑞芯微 SoC 芯片标识。</para></value>
    public uint Chip { get; set; }
    /// <summary>Gets or sets the loader offset.<para>获取或设置加载器偏移。</para></summary>
    /// <value>The absolute byte offset at which the loader region starts.
    /// <para>加载器区域起始的绝对字节偏移。</para></value>
    public uint BootOffset { get; set; }
    /// <summary>Gets or sets the loader length.<para>获取或设置加载器长度。</para></summary>
    /// <value>The byte length of the loader region.
    /// <para>加载器区域的字节长度。</para></value>
    public uint BootSize { get; set; }
    /// <summary>Gets or sets the embedded RKAF offset.<para>获取或设置内嵌 RKAF 偏移。</para></summary>
    /// <value>The absolute byte offset at which the embedded RKAF image starts.
    /// <para>内嵌 RKAF 镜像起始的绝对字节偏移。</para></value>
    public uint ImageOffset { get; set; }
    /// <summary>Gets or sets the embedded RKAF length.<para>获取或设置内嵌 RKAF 长度。</para></summary>
    /// <value>The byte length of the embedded RKAF image.
    /// <para>内嵌 RKAF 镜像的字节长度。</para></value>
    public uint ImageSize { get; set; }
    /// <summary>Gets or sets the reserved bytes.<para>获取或设置保留字节。</para></summary>
    /// <value>Exactly 61 bytes occupying the remainder of the packed header; their content is carried through
    /// unchanged by <see cref="Read"/> and <see cref="ToBytes"/>.
    /// <para>占据紧凑头部剩余空间的恰好 61 个字节；<see cref="Read"/> 与 <see cref="ToBytes"/> 原样保留其内容。</para></value>
    public byte[] Reserved { get; set; } = new byte[61];

    /// <summary>Reads the 102-byte RKFW header.<para>读取 102 字节 RKFW 头。</para></summary>
    /// <param name="reader">
    /// Reader whose base stream is positioned at the first header byte; exactly <see cref="StructSize"/>
    /// bytes are consumed.
    /// <para>基础流定位在头部首字节的读取器；恰好消耗 <see cref="StructSize"/> 字节。</para>
    /// </param>
    /// <returns>A header populated from the little-endian fields; the magic itself is not validated here.
    /// <para>按小端字段填充的头部；此处不校验魔数本身。</para></returns>
    /// <exception cref="EndOfStreamException">
    /// Fewer than <see cref="StructSize"/> bytes remain on the stream.
    /// <para>流中剩余可读字节少于 <see cref="StructSize"/>。</para>
    /// </exception>
    public static RkFwHeader Read(BinaryReader reader)
    {
        Span<byte> data = stackalloc byte[StructSize];
        reader.BaseStream.ReadExactly(data);
        return new RkFwHeader
        {
            Tag = BinaryPrimitives.ReadUInt32LittleEndian(data),
            HeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
            Version = BinaryPrimitives.ReadUInt32LittleEndian(data[6..]),
            MergeVersion = BinaryPrimitives.ReadUInt32LittleEndian(data[10..]),
            ReleaseTime = data.Slice(14, 7).ToArray(),
            Chip = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]),
            BootOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[25..]),
            BootSize = BinaryPrimitives.ReadUInt32LittleEndian(data[29..]),
            ImageOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[33..]),
            ImageSize = BinaryPrimitives.ReadUInt32LittleEndian(data[37..]),
            Reserved = data.Slice(41, 61).ToArray()
        };
    }

    /// <summary>Serializes the packed RKFW header.<para>序列化 RKFW 紧凑头。</para></summary>
    /// <returns>A little-endian byte array of exactly <see cref="HeaderSize"/> bytes; when that size exceeds
    /// <see cref="StructSize"/>, the additional trailing bytes stay zero.
    /// <para>长度恰为 <see cref="HeaderSize"/> 字节的小端字节数组；当该长度超过 <see cref="StructSize"/> 时，
    /// 多余的尾部字节保持为零。</para></returns>
    /// <exception cref="InvalidDataException"><see cref="Tag"/> is not <see cref="MagicRkfw"/>,
    /// <see cref="HeaderSize"/> is below <see cref="StructSize"/>, or an array property has the wrong length.
    /// <para><see cref="Tag"/> 不是 <see cref="MagicRkfw"/>、<see cref="HeaderSize"/> 小于
    /// <see cref="StructSize"/>，或某个数组属性长度不正确。</para></exception>
    public byte[] ToBytes()
    {
        if (Tag != MagicRkfw || HeaderSize < StructSize || ReleaseTime.Length != 7 || Reserved.Length != 61)
            throw new InvalidDataException("Invalid RKFW header fields.");
        var data = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(data, Tag);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(6), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10), MergeVersion);
        ReleaseTime.CopyTo(data, 14);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(21), Chip);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(25), BootOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(29), BootSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(33), ImageOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(37), ImageSize);
        Reserved.CopyTo(data, 41);
        return data;
    }
}

/// <summary>Identifies a Rockchip entry.<para>标识 Rockchip 条目类型。</para></summary>
public enum RkfwEntryType
{
    /// <summary>Identifies the loader.<para>标识加载器。</para></summary>
    Boot = 0,
    /// <summary>Identifies an embedded RKAF image.<para>标识内嵌 RKAF 镜像。</para></summary>
    Image = 1,
    /// <summary>Identifies an RKAF package file.<para>标识 RKAF 包文件。</para></summary>
    Partition = 2
}

/// <summary>Describes a bounded Rockchip entry.<para>描述带边界的 Rockchip 条目。</para></summary>
/// <param name="Name">The relative output path.
/// <para>相对输出路径。</para></param>
/// <param name="Offset">The byte offset.
/// <para>字节偏移。</para></param>
/// <param name="Size">The byte length.
/// <para>字节长度。</para></param>
/// <param name="Type">The entry type.
/// <para>条目类型。</para></param>
public sealed record RkfwEntry(string Name, long Offset, long Size, RkfwEntryType Type);