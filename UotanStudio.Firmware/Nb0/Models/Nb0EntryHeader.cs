using System.Text;

namespace UotanStudio.Firmware.Nb0;

/// <summary>
/// <para>表示 FIH Mobile NB0 格式中的条目头,描述单个固件条目的偏移量、大小和名称。</para>
/// Represents an entry header in the FIH Mobile NB0 format, describing the offset, size, and name of a single firmware entry.
/// </summary>
/// <remarks>
/// <para>二进制布局(共 64 字节):LoDataOffset(4) | LoFileSize(4) | HiDataOffset(4) | HiFileSize(4) | Name(48, ASCII)</para>
/// Binary layout (64 bytes): LoDataOffset(4) | LoFileSize(4) | HiDataOffset(4) | HiFileSize(4) | Name(48, ASCII)
/// </remarks>
public sealed class Nb0EntryHeader
{
    /// <summary>
    /// <para>条目头结构的固定大小(字节)。</para>
    /// The fixed size of the entry header structure in bytes.
    /// </summary>
    public const int StructSize = 64;

    /// <summary>
    /// <para>名称字段的最大长度(字节)。</para>
    /// The maximum length of the name field in bytes.
    /// </summary>
    public const int NameLength = 48;

    /// <summary>
    /// <para>数据偏移量的低 32 位。</para>
    /// The low 32 bits of the data offset.
    /// </summary>
    public uint LoDataOffset { get; set; }

    /// <summary>
    /// <para>文件大小的低 32 位。</para>
    /// The low 32 bits of the file size.
    /// </summary>
    public uint LoFileSize { get; set; }

    /// <summary>
    /// <para>数据偏移量的高 32 位。</para>
    /// The high 32 bits of the data offset.
    /// </summary>
    public uint HiDataOffset { get; set; }

    /// <summary>
    /// <para>文件大小的高 32 位。</para>
    /// The high 32 bits of the file size.
    /// </summary>
    public uint HiFileSize { get; set; }

    /// <summary>
    /// <para>条目名称(ASCII)。</para>
    /// The entry name (ASCII).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置 64 位数据偏移量。</para>
    /// Gets or sets the 64-bit data offset.
    /// </summary>
    public long DataOffset
    {
        get => LoDataOffset + ((long)HiDataOffset << 32);
        set
        {
            LoDataOffset = (uint)(value & 0xFFFFFFFF);
            HiDataOffset = (uint)((value >> 32) & 0xFFFFFFFF);
        }
    }

    /// <summary>
    /// <para>获取或设置 64 位文件大小。</para>
    /// Gets or sets the 64-bit file size.
    /// </summary>
    public long FileSize
    {
        get => LoFileSize + ((long)HiFileSize << 32);
        set
        {
            LoFileSize = (uint)(value & 0xFFFFFFFF);
            HiFileSize = (uint)((value >> 32) & 0xFFFFFFFF);
        }
    }

    /// <summary>
    /// <para>从二进制读取器中读取并构造一个 Nb0EntryHeader 实例。</para>
    /// Reads and constructs an Nb0EntryHeader instance from a binary reader.
    /// </summary>
    public static Nb0EntryHeader Read(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var header = new Nb0EntryHeader
        {
            LoDataOffset = reader.ReadUInt32(),
            LoFileSize = reader.ReadUInt32(),
            HiDataOffset = reader.ReadUInt32(),
            HiFileSize = reader.ReadUInt32()
        };

        var nameBytes = reader.ReadBytes(NameLength);
        header.Name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0', ' ');

        return header;
    }

    /// <summary>
    /// <para>将当前条目头写入指定的二进制写入器。</para>
    /// Writes the current entry header to the specified binary writer.
    /// </summary>
    public void Write(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Write(LoDataOffset);
        writer.Write(LoFileSize);
        writer.Write(HiDataOffset);
        writer.Write(HiFileSize);

        var nameBytes = new byte[NameLength];
        var encoded = Encoding.ASCII.GetBytes(Name);
        var copyLen = Math.Min(encoded.Length, NameLength - 1);
        Array.Copy(encoded, nameBytes, copyLen);
        writer.Write(nameBytes, 0, NameLength);
    }

    /// <summary>
    /// <para>将条目头写入预分配的字节缓冲区,避免中间分配。</para>
    /// Writes the entry header to a pre-allocated byte buffer, avoiding intermediate allocation.
    /// </summary>
    public void WriteToBuffer(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length < StructSize)
        {
            throw new ArgumentException($"Buffer must be at least {StructSize} bytes.", nameof(buffer));
        }

        WriteUInt32LE(buffer, 0, LoDataOffset);
        WriteUInt32LE(buffer, 4, LoFileSize);
        WriteUInt32LE(buffer, 8, HiDataOffset);
        WriteUInt32LE(buffer, 12, HiFileSize);

        var encoded = Encoding.ASCII.GetBytes(Name);
        var copyLen = Math.Min(encoded.Length, NameLength - 1);
        Array.Clear(buffer, 16, NameLength);
        Array.Copy(encoded, 0, buffer, 16, copyLen);
    }

    /// <summary>
    /// <para>从字节缓冲区读取条目头。</para>
    /// Reads the entry header from a byte buffer.
    /// </summary>
    public static Nb0EntryHeader ReadFromBuffer(byte[] buffer, int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length < offset + StructSize)
        {
            throw new ArgumentException("Buffer too small for Nb0EntryHeader.", nameof(buffer));
        }

        var header = new Nb0EntryHeader
        {
            LoDataOffset = ReadUInt32LE(buffer, offset),
            LoFileSize = ReadUInt32LE(buffer, offset + 4),
            HiDataOffset = ReadUInt32LE(buffer, offset + 8),
            HiFileSize = ReadUInt32LE(buffer, offset + 12)
        };

        var nameBytes = new byte[NameLength];
        Array.Copy(buffer, offset + 16, nameBytes, 0, NameLength);
        header.Name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0', ' ');

        return header;
    }

    /// <summary>
    /// <para>将条目头序列化为字节数组。</para>
    /// Serializes the entry header to a byte array.
    /// </summary>
    public byte[] ToBytes()
    {
        var buffer = new byte[StructSize];
        WriteToBuffer(buffer);
        return buffer;
    }

    private static void WriteUInt32LE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static uint ReadUInt32LE(byte[] buffer, int offset)
    {
        return (uint)(buffer[offset]
            | (buffer[offset + 1] << 8)
            | (buffer[offset + 2] << 16)
            | (buffer[offset + 3] << 24));
    }
}