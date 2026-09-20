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
    public uint Tag { get; set; } = MagicRkfw;
    /// <summary>Gets or sets the header length.<para>获取或设置头部长度。</para></summary>
    public ushort HeaderSize { get; set; } = StructSize;
    /// <summary>Gets or sets the firmware version.<para>获取或设置固件版本。</para></summary>
    public uint Version { get; set; }
    /// <summary>Gets or sets the merger version.<para>获取或设置合并工具版本。</para></summary>
    public uint MergeVersion { get; set; }
    /// <summary>Gets or sets the seven-byte release time.<para>获取或设置七字节发布时间。</para></summary>
    public byte[] ReleaseTime { get; set; } = new byte[7];
    /// <summary>Gets or sets the chip identifier.<para>获取或设置芯片标识。</para></summary>
    public uint Chip { get; set; }
    /// <summary>Gets or sets the loader offset.<para>获取或设置加载器偏移。</para></summary>
    public uint BootOffset { get; set; }
    /// <summary>Gets or sets the loader length.<para>获取或设置加载器长度。</para></summary>
    public uint BootSize { get; set; }
    /// <summary>Gets or sets the embedded RKAF offset.<para>获取或设置内嵌 RKAF 偏移。</para></summary>
    public uint ImageOffset { get; set; }
    /// <summary>Gets or sets the embedded RKAF length.<para>获取或设置内嵌 RKAF 长度。</para></summary>
    public uint ImageSize { get; set; }
    /// <summary>Gets or sets the reserved bytes.<para>获取或设置保留字节。</para></summary>
    public byte[] Reserved { get; set; } = new byte[61];

    /// <summary>Reads the 102-byte RKFW header.<para>读取 102 字节 RKFW 头。</para></summary>
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
/// <param name="Name">The relative output path. 相对输出路径。</param>
/// <param name="Offset">The byte offset. 字节偏移。</param>
/// <param name="Size">The byte length. 字节长度。</param>
/// <param name="Type">The entry type. 条目类型。</param>
public sealed record RkfwEntry(string Name, long Offset, long Size, RkfwEntryType Type);