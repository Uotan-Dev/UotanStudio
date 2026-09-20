using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Pac.Models;

public sealed class SprdHead
{
    public ushort[] PacVersion = new ushort[24];

    public uint PacSize;

    public ushort[] FwName = new ushort[256];

    public ushort[] FwVersion = new ushort[256];

    public uint FileCount;

    public uint DirOffset;

    public uint[] Unknown1 = new uint[5];

    public ushort[] FwAlias = new ushort[100];

    public uint[] Unknown2 = new uint[3];

    public uint[] Unknown = new uint[200];

    public uint PacMagic;

    public ushort HeadCrc;

    public ushort DataCrc;

    public const int Size = 2124;

    /// <summary>
    /// Reads the header from a binary reader.
    /// <para>从二进制读取器中读取头部。</para>
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
    /// Serializes the header to a byte array.
    /// <para>将头部序列化为字节数组。</para>
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

    /// <summary>
    /// Gets the PAC version string.
    /// <para>获取 PAC 版本字符串。</para>
    /// </summary>
    public string GetPacVersion() => ConvertU16ToString(PacVersion);

    /// <summary>
    /// Gets the firmware name string.
    /// <para>获取固件名称字符串。</para>
    /// </summary>
    public string GetFwName() => ConvertU16ToString(FwName);

    /// <summary>
    /// Gets the firmware version string.
    /// <para>获取固件版本字符串。</para>
    /// </summary>
    public string GetFwVersion() => ConvertU16ToString(FwVersion);

    /// <summary>
    /// Gets the firmware alias string.
    /// <para>获取固件别名字符串。</para>
    /// </summary>
    public string GetFwAlias() => ConvertU16ToString(FwAlias);

    private static string ConvertU16ToString(ushort[] data)
    {
        var bytes = new byte[data.Length * 2];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }
}

public sealed class SprdFile
{
    public uint StructSize;

    public ushort[] Id = new ushort[256];

    public ushort[] Name = new ushort[256];

    public ushort[] Unknown1 = new ushort[256];

    public uint Size;

    public uint Type;

    public uint FlashUse;

    public uint PacOffset;

    public uint OmitFlag;

    public uint AddrNum;

    public uint[] Addr = new uint[5];

    public uint[] Unknown2 = new uint[249];

    public const int StructFixedSize = 2580;

    /// <summary>
    /// Reads a file entry from a binary reader.
    /// <para>从二进制读取器中读取文件条目。</para>
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

    /// <summary>
    /// Gets the entry ID string.
    /// <para>获取条目 ID 字符串。</para>
    /// </summary>
    public string GetId() => ConvertU16ToString(Id);

    /// <summary>
    /// Gets the entry name string.
    /// <para>获取条目名称字符串。</para>
    /// </summary>
    public string GetName() => ConvertU16ToString(Name);

    private static string ConvertU16ToString(ushort[] data)
    {
        var bytes = new byte[data.Length * 2];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }
}

public enum PacFileType : uint
{
    Unknown = 0,

    Image = 1,

    Xml = 2,

    Certificate = 3
}

public static class Crc16
{
    /// <summary>
    /// Calculates the CRC-16 checksum of the data with the given seed.
    /// <para>使用给定种子对数据计算 CRC-16 校验和。</para>
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