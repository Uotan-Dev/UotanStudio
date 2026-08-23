using System.Runtime.InteropServices;
using System.Text;

namespace UotanStudio.Firmware.Dz;

/// <summary>
/// <para>LG DZ/KDZ 固件格式的常量定义。</para>
/// Constants for the LG DZ/KDZ firmware formats.
/// </summary>
public static class DzConstants
{
    /// <summary>DZ 文件头魔数。</summary>
    public static readonly byte[] DzFileHeader = [0x32, 0x96, 0x18, 0x74];

    /// <summary>DZ 块头魔数。</summary>
    public static readonly byte[] DzChunkHeader = [0x30, 0x12, 0x95, 0x78];

    /// <summary>KDZ V0 头魔数。</summary>
    public static readonly byte[] KdzHeaderV0 = [0x28, 0x05, 0x00, 0x00, 0x34, 0x31, 0x25, 0x80];

    /// <summary>KDZ V1 头魔数。</summary>
    public static readonly byte[] KdzHeaderV1 = [0x18, 0x05, 0x00, 0x00, 0x32, 0x79, 0x44, 0x50];

    /// <summary>KDZ V2 头魔数(DZ 头)。</summary>
    public static readonly byte[] KdzHeaderV2 = [0x28, 0x05, 0x00, 0x00, 0x24, 0x38, 0x22, 0x25];
}

/// <summary>
/// <para>LG DZ 文件头结构。</para>
/// The LG DZ file header structure.
/// </summary>
public sealed class DzFileHeader
{
    /// <summary>文件头魔数。</summary>
    public byte[] Header = new byte[4];

    /// <summary>格式主版本。</summary>
    public uint FormatMajor;

    /// <summary>格式次版本。</summary>
    public uint FormatMinor;

    /// <summary>保留字段 0。</summary>
    public uint Reserved0;

    /// <summary>设备型号(32 字节)。</summary>
    public byte[] Device = new byte[32];

    /// <summary>版本信息(144 字节)。</summary>
    public byte[] Version = new byte[144];

    /// <summary>块数量。</summary>
    public uint ChunkCount;

    /// <summary>MD5(16 字节)。</summary>
    public byte[] Md5 = new byte[16];

    /// <summary>未知字段 0。</summary>
    public uint Unknown0;

    /// <summary>保留字段 1。</summary>
    public uint Reserved1;

    /// <summary>保留字段 4。</summary>
    public ushort Reserved4;

    /// <summary>未知字段 1。</summary>
    public byte[] Unknown1 = new byte[16];

    /// <summary>未知字段 2。</summary>
    public byte[] Unknown2 = new byte[50];

    /// <summary>构建类型(20 字节)。</summary>
    public byte[] BuildType = new byte[20];

    /// <summary>未知字段 3。</summary>
    public byte[] Unknown3 = new byte[4];

    /// <summary>Android 版本(10 字节)。</summary>
    public byte[] AndroidVer = new byte[10];

    /// <summary>旧日期代码(10 字节)。</summary>
    public byte[] OldDateCode = new byte[10];

    /// <summary>保留字段 5。</summary>
    public uint Reserved5;

    /// <summary>未知字段 4。</summary>
    public uint Unknown4;

    /// <summary>未知字段 5。</summary>
    public ulong Unknown5;

    /// <summary>填充(164 字节)。</summary>
    public byte[] Pad = new byte[164];

    /// <summary>头部固定大小(字节)。</summary>
    public const int Size = 512;

    /// <summary>
    /// <para>从字节数组解析 DZ 文件头。</para>
    /// Parses the DZ file header from a byte array.
    /// </summary>
    public static DzFileHeader FromBytes(byte[] buffer)
    {
        if (buffer.Length < Size)
        {
            throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(buffer));
        }

        var header = new DzFileHeader();
        var offset = 0;
        header.Header = buffer.AsSpan(offset, 4).ToArray(); offset += 4;
        header.FormatMajor = BitConverter.ToUInt32(buffer, offset); offset += 4;
        header.FormatMinor = BitConverter.ToUInt32(buffer, offset); offset += 4;
        header.Reserved0 = BitConverter.ToUInt32(buffer, offset); offset += 4;
        header.Device = buffer.AsSpan(offset, 32).ToArray(); offset += 32;
        header.Version = buffer.AsSpan(offset, 144).ToArray(); offset += 144;
        header.ChunkCount = BitConverter.ToUInt32(buffer, offset); offset += 4;
        header.Md5 = buffer.AsSpan(offset, 16).ToArray(); offset += 16;
        header.Unknown0 = BitConverter.ToUInt32(buffer, offset); offset += 4;
        header.Reserved1 = BitConverter.ToUInt32(buffer, offset); offset += 4;
        header.Reserved4 = BitConverter.ToUInt16(buffer, offset); offset += 2;
        header.Unknown1 = buffer.AsSpan(offset, 16).ToArray(); offset += 16;
        header.Unknown2 = buffer.AsSpan(offset, 50).ToArray(); offset += 50;
        header.BuildType = buffer.AsSpan(offset, 20).ToArray(); offset += 20;
        header.Unknown3 = buffer.AsSpan(offset, 4).ToArray(); offset += 4;
        header.AndroidVer = buffer.AsSpan(offset, 10).ToArray(); offset += 10;
        header.OldDateCode = buffer.AsSpan(offset, 10).ToArray(); offset += 10;
        header.Reserved5 = BitConverter.ToUInt32(buffer, offset); offset += 4;
        header.Unknown4 = BitConverter.ToUInt32(buffer, offset); offset += 4;
        header.Unknown5 = BitConverter.ToUInt64(buffer, offset); offset += 8;
        header.Pad = buffer.AsSpan(offset, 164).ToArray();
        return header;
    }

    /// <summary>获取设备型号字符串。</summary>
    public string GetDevice() => Encoding.UTF8.GetString(Device).TrimEnd('\0');

    /// <summary>获取版本字符串。</summary>
    public string GetVersion() => Encoding.UTF8.GetString(Version).TrimEnd('\0');

    /// <summary>获取 Android 版本字符串。</summary>
    public string GetAndroidVersion() => Encoding.UTF8.GetString(AndroidVer).TrimEnd('\0');
}

/// <summary>
/// <para>LG DZ 块头结构。</para>
/// The LG DZ chunk header structure.
/// </summary>
public sealed class DzChunkHeader
{
    /// <summary>块头魔数。</summary>
    public byte[] Header = new byte[4];

    /// <summary>分区切片名称(32 字节)。</summary>
    public byte[] SliceName = new byte[32];

    /// <summary>块名称(64 字节)。</summary>
    public byte[] ChunkName = new byte[64];

    /// <summary>目标大小。</summary>
    public uint TargetSize;

    /// <summary>数据大小(压缩后)。</summary>
    public uint DataSize;

    /// <summary>MD5(16 字节)。</summary>
    public byte[] Md5 = new byte[16];

    /// <summary>目标地址。</summary>
    public uint TargetAddr;

    /// <summary>裁剪计数。</summary>
    public uint TrimCount;

    /// <summary>设备。</summary>
    public uint Dev;

    /// <summary>CRC32。</summary>
    public uint Crc32;

    /// <summary>填充(372 字节)。</summary>
    public byte[] Pad = new byte[372];

    /// <summary>块头固定大小(字节)。</summary>
    public const int Size = 512;

    /// <summary>
    /// <para>从字节数组解析 DZ 块头。</para>
    /// Parses a DZ chunk header from a byte array.
    /// </summary>
    public static DzChunkHeader FromBytes(byte[] buffer)
    {
        if (buffer.Length < Size)
        {
            throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(buffer));
        }

        var chunk = new DzChunkHeader();
        var offset = 0;
        chunk.Header = buffer.AsSpan(offset, 4).ToArray(); offset += 4;
        chunk.SliceName = buffer.AsSpan(offset, 32).ToArray(); offset += 32;
        chunk.ChunkName = buffer.AsSpan(offset, 64).ToArray(); offset += 64;
        chunk.TargetSize = BitConverter.ToUInt32(buffer, offset); offset += 4;
        chunk.DataSize = BitConverter.ToUInt32(buffer, offset); offset += 4;
        chunk.Md5 = buffer.AsSpan(offset, 16).ToArray(); offset += 16;
        chunk.TargetAddr = BitConverter.ToUInt32(buffer, offset); offset += 4;
        chunk.TrimCount = BitConverter.ToUInt32(buffer, offset); offset += 4;
        chunk.Dev = BitConverter.ToUInt32(buffer, offset); offset += 4;
        chunk.Crc32 = BitConverter.ToUInt32(buffer, offset); offset += 4;
        chunk.Pad = buffer.AsSpan(offset, 372).ToArray();
        return chunk;
    }

    /// <summary>获取块名称字符串。</summary>
    public string GetChunkName() => Encoding.UTF8.GetString(ChunkName).TrimEnd('\0');

    /// <summary>获取切片名称字符串。</summary>
    public string GetSliceName() => Encoding.UTF8.GetString(SliceName).TrimEnd('\0');
}

/// <summary>
/// <para>LG KDZ 分区头结构。</para>
/// The LG KDZ partition header structure.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct KdzHeader
{
    /// <summary>分区名称(256 字节)。</summary>
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public byte[] Name;

    /// <summary>数据长度。</summary>
    public ulong Length;

    /// <summary>数据偏移量。</summary>
    public ulong Offset;

    /// <summary>KDZ 分区头固定大小(字节)。</summary>
    public const int Size = 272;

    /// <summary>
    /// <para>从字节数组解析 KDZ 分区头。</para>
    /// Parses a KDZ partition header from a byte array.
    /// </summary>
    public static KdzHeader FromBytes(byte[] buffer)
    {
        return buffer.Length < Size
            ? throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(buffer))
            : new KdzHeader
            {
                Name = buffer.AsSpan(0, 256).ToArray(),
                Length = BitConverter.ToUInt64(buffer, 256),
                Offset = BitConverter.ToUInt64(buffer, 264)
            };
    }

    /// <summary>获取分区名称字符串。</summary>
    public readonly string GetName() => Encoding.UTF8.GetString(Name).TrimEnd('\0');
}