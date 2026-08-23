using System.Text;

namespace UotanStudio.Firmware.Pac;

/// <summary>
/// <para>SPRD PAC 固件包头部结构。</para>
/// The header structure of an SPRD PAC firmware package.
/// </summary>
public sealed class SprdHead
{
    /// <summary>PAC 版本号(UTF-16 数组)。</summary>
    public ushort[] PacVersion = new ushort[24];

    /// <summary>PAC 文件总大小。</summary>
    public uint PacSize;

    /// <summary>固件名称(UTF-16 数组)。</summary>
    public ushort[] FwName = new ushort[256];

    /// <summary>固件版本(UTF-16 数组)。</summary>
    public ushort[] FwVersion = new ushort[256];

    /// <summary>文件条目数量。</summary>
    public uint FileCount;

    /// <summary>目录表偏移量。</summary>
    public uint DirOffset;

    /// <summary>保留字段 1。</summary>
    public uint[] Unknown1 = new uint[5];

    /// <summary>固件别名(UTF-16 数组)。</summary>
    public ushort[] FwAlias = new ushort[100];

    /// <summary>保留字段 2。</summary>
    public uint[] Unknown2 = new uint[3];

    /// <summary>保留字段。</summary>
    public uint[] Unknown = new uint[200];

    /// <summary>PAC 魔数(应为 0xFFFAFFFA)。</summary>
    public uint PacMagic;

    /// <summary>头部 CRC16。</summary>
    public ushort HeadCrc;

    /// <summary>数据区 CRC16。</summary>
    public ushort DataCrc;

    /// <summary>头部结构固定大小(字节)。</summary>
    public const int Size = 2124;

    /// <summary>
    /// <para>从二进制读取器中读取头部。</para>
    /// Reads the header from a binary reader.
    /// </summary>
    public static SprdHead Read(BinaryReader reader)
    {
        var head = new SprdHead();
        for (var i = 0; i < 24; i++)
        {
            head.PacVersion[i] = reader.ReadUInt16();
        }

        head.PacSize = reader.ReadUInt32();
        for (var i = 0; i < 256; i++)
        {
            head.FwName[i] = reader.ReadUInt16();
        }

        for (var i = 0; i < 256; i++)
        {
            head.FwVersion[i] = reader.ReadUInt16();
        }

        head.FileCount = reader.ReadUInt32();
        head.DirOffset = reader.ReadUInt32();
        for (var i = 0; i < 5; i++)
        {
            head.Unknown1[i] = reader.ReadUInt32();
        }

        for (var i = 0; i < 100; i++)
        {
            head.FwAlias[i] = reader.ReadUInt16();
        }

        for (var i = 0; i < 3; i++)
        {
            head.Unknown2[i] = reader.ReadUInt32();
        }

        for (var i = 0; i < 200; i++)
        {
            head.Unknown[i] = reader.ReadUInt32();
        }

        head.PacMagic = reader.ReadUInt32();
        head.HeadCrc = reader.ReadUInt16();
        head.DataCrc = reader.ReadUInt16();
        return head;
    }

    /// <summary>
    /// <para>将头部序列化为字节数组。</para>
    /// Serializes the header to a byte array.
    /// </summary>
    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        for (var i = 0; i < 24; i++)
        {
            writer.Write(PacVersion[i]);
        }

        writer.Write(PacSize);
        for (var i = 0; i < 256; i++)
        {
            writer.Write(FwName[i]);
        }

        for (var i = 0; i < 256; i++)
        {
            writer.Write(FwVersion[i]);
        }

        writer.Write(FileCount);
        writer.Write(DirOffset);
        for (var i = 0; i < 5; i++)
        {
            writer.Write(Unknown1[i]);
        }

        for (var i = 0; i < 100; i++)
        {
            writer.Write(FwAlias[i]);
        }

        for (var i = 0; i < 3; i++)
        {
            writer.Write(Unknown2[i]);
        }

        for (var i = 0; i < 200; i++)
        {
            writer.Write(Unknown[i]);
        }

        writer.Write(PacMagic);
        writer.Write(HeadCrc);
        writer.Write(DataCrc);
        return ms.ToArray();
    }

    /// <summary>获取 PAC 版本字符串。</summary>
    public string GetPacVersion() => ConvertU16ToString(PacVersion);

    /// <summary>获取固件名称字符串。</summary>
    public string GetFwName() => ConvertU16ToString(FwName);

    /// <summary>获取固件版本字符串。</summary>
    public string GetFwVersion() => ConvertU16ToString(FwVersion);

    /// <summary>获取固件别名字符串。</summary>
    public string GetFwAlias() => ConvertU16ToString(FwAlias);

    private static string ConvertU16ToString(ushort[] data)
    {
        var bytes = new byte[data.Length * 2];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }
}

/// <summary>
/// <para>SPRD PAC 固件包中的单个文件条目。</para>
/// A single file entry within an SPRD PAC firmware package.
/// </summary>
public sealed class SprdFile
{
    /// <summary>结构体大小(应为 2580)。</summary>
    public uint StructSize;

    /// <summary>条目 ID(UTF-16 数组)。</summary>
    public ushort[] Id = new ushort[256];

    /// <summary>条目名称(UTF-16 数组)。</summary>
    public ushort[] Name = new ushort[256];

    /// <summary>保留字段 1。</summary>
    public ushort[] Unknown1 = new ushort[256];

    /// <summary>文件大小。</summary>
    public uint Size;

    /// <summary>文件类型。</summary>
    public uint Type;

    /// <summary>Flash 用途。</summary>
    public uint FlashUse;

    /// <summary>数据在 PAC 文件中的偏移量。</summary>
    public uint PacOffset;

    /// <summary>省略标志。</summary>
    public uint OmitFlag;

    /// <summary>地址数量。</summary>
    public uint AddrNum;

    /// <summary>地址列表。</summary>
    public uint[] Addr = new uint[5];

    /// <summary>保留字段 2。</summary>
    public uint[] Unknown2 = new uint[249];

    /// <summary>条目结构固定大小(字节)。</summary>
    public const int StructFixedSize = 2580;

    /// <summary>
    /// <para>从二进制读取器中读取文件条目。</para>
    /// Reads a file entry from a binary reader.
    /// </summary>
    public static SprdFile Read(BinaryReader reader)
    {
        var file = new SprdFile
        {
            StructSize = reader.ReadUInt32()
        };
        for (var i = 0; i < 256; i++)
        {
            file.Id[i] = reader.ReadUInt16();
        }

        for (var i = 0; i < 256; i++)
        {
            file.Name[i] = reader.ReadUInt16();
        }

        for (var i = 0; i < 256; i++)
        {
            file.Unknown1[i] = reader.ReadUInt16();
        }

        file.Size = reader.ReadUInt32();
        file.Type = reader.ReadUInt32();
        file.FlashUse = reader.ReadUInt32();
        file.PacOffset = reader.ReadUInt32();
        file.OmitFlag = reader.ReadUInt32();
        file.AddrNum = reader.ReadUInt32();
        for (var i = 0; i < 5; i++)
        {
            file.Addr[i] = reader.ReadUInt32();
        }

        for (var i = 0; i < 249; i++)
        {
            file.Unknown2[i] = reader.ReadUInt32();
        }

        return file;
    }

    /// <summary>获取条目 ID 字符串。</summary>
    public string GetId() => ConvertU16ToString(Id);

    /// <summary>获取条目名称字符串。</summary>
    public string GetName() => ConvertU16ToString(Name);

    private static string ConvertU16ToString(ushort[] data)
    {
        var bytes = new byte[data.Length * 2];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }
}

/// <summary>
/// <para>SPRD PAC 固件包中的文件类型。</para>
/// File types within an SPRD PAC firmware package.
/// </summary>
public enum PacFileType : uint
{
    /// <summary>未知类型。</summary>
    Unknown = 0,

    /// <summary>分区镜像。</summary>
    Image = 1,

    /// <summary>XML 描述文件。</summary>
    Xml = 2,

    /// <summary>证书文件。</summary>
    Certificate = 3
}

/// <summary>
/// <para>提供 CRC-16 校验和计算。</para>
/// Provides CRC-16 checksum calculation.
/// </summary>
public static class Crc16
{
    /// <summary>
    /// <para>使用给定种子对数据计算 CRC-16 校验和。</para>
    /// Calculates the CRC-16 checksum of the data with the given seed.
    /// </summary>
    public static ushort Calculate(ushort seed, byte[] data)
    {
        var crc = seed;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }
        return crc;
    }
}