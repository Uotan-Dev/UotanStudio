using System.Runtime.InteropServices;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Dz;

public static class DzConstants
{
    public static readonly byte[] DzFileHeader = [0x32, 0x96, 0x18, 0x74];

    public static readonly byte[] DzChunkHeader = [0x30, 0x12, 0x95, 0x78];

    public static readonly byte[] KdzHeaderV0 = [0x28, 0x05, 0x00, 0x00, 0x34, 0x31, 0x25, 0x80];

    public static readonly byte[] KdzHeaderV1 = [0x18, 0x05, 0x00, 0x00, 0x32, 0x79, 0x44, 0x50];

    public static readonly byte[] KdzHeaderV2 = [0x28, 0x05, 0x00, 0x00, 0x24, 0x38, 0x22, 0x25];
}

public sealed class DzFileHeader
{
    public byte[] Header = new byte[4];

    public uint FormatMajor;

    public uint FormatMinor;

    public uint Reserved0;

    public byte[] Device = new byte[32];

    public byte[] Version = new byte[144];

    public uint ChunkCount;

    public byte[] Md5 = new byte[16];

    public uint Unknown0;

    public uint Reserved1;

    public ushort Reserved4;

    public byte[] Unknown1 = new byte[16];

    public byte[] Unknown2 = new byte[50];

    public byte[] BuildType = new byte[20];

    public byte[] Unknown3 = new byte[4];

    public byte[] AndroidVer = new byte[10];

    public byte[] OldDateCode = new byte[10];

    public uint Reserved5;

    public uint Unknown4;

    public ulong Unknown5;

    public byte[] Pad = new byte[164];

    public const int Size = 512;

    /// <summary>
    /// Parses the DZ file header from a byte array.
    /// <para>从字节数组解析 DZ 文件头。</para>
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

    /// <summary>
    /// Gets the device model string.
    /// <para>获取设备型号字符串。</para>
    /// </summary>
    public string GetDevice() => Encoding.UTF8.GetString(Device).TrimEnd('\0');

    /// <summary>
    /// Gets the version string.
    /// <para>获取版本字符串。</para>
    /// </summary>
    public string GetVersion() => Encoding.UTF8.GetString(Version).TrimEnd('\0');

    /// <summary>
    /// Gets the Android version string.
    /// <para>获取 Android 版本字符串。</para>
    /// </summary>
    public string GetAndroidVersion() => Encoding.UTF8.GetString(AndroidVer).TrimEnd('\0');
}

public sealed class DzChunkHeader
{
    public byte[] Header = new byte[4];

    public byte[] SliceName = new byte[32];

    public byte[] ChunkName = new byte[64];

    public uint TargetSize;

    public uint DataSize;

    public byte[] Md5 = new byte[16];

    public uint TargetAddr;

    public uint TrimCount;

    public uint Dev;

    public uint Crc32;

    public byte[] Pad = new byte[372];

    public const int Size = 512;

    /// <summary>
    /// Parses a DZ chunk header from a byte array.
    /// <para>从字节数组解析 DZ 块头。</para>
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

    /// <summary>
    /// Gets the block name string.
    /// <para>获取块名称字符串。</para>
    /// </summary>
    public string GetChunkName() => Encoding.UTF8.GetString(ChunkName).TrimEnd('\0');

    /// <summary>
    /// Gets the slice name string.
    /// <para>获取切片名称字符串。</para>
    /// </summary>
    public string GetSliceName() => Encoding.UTF8.GetString(SliceName).TrimEnd('\0');
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct KdzHeader
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public byte[] Name;

    public ulong Length;

    public ulong Offset;

    public const int Size = 272;

    /// <summary>
    /// Parses a KDZ partition header from a byte array.
    /// <para>从字节数组解析 KDZ 分区头。</para>
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

    /// <summary>
    /// Gets the partition name string.
    /// <para>获取分区名称字符串。</para>
    /// </summary>
    public readonly string GetName() => Encoding.UTF8.GetString(Name).TrimEnd('\0');
}
