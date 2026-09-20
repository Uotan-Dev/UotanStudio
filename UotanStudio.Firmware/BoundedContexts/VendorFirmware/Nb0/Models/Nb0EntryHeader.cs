using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Models;

public sealed class Nb0EntryHeader
{
    public const int StructSize = 64;

    public const int NameLength = 48;

    public uint LoDataOffset { get; set; }

    public uint LoFileSize { get; set; }

    public uint HiDataOffset { get; set; }

    public uint HiFileSize { get; set; }

    public string Name { get; set; } = string.Empty;

    public long DataOffset
    {
        get => LoDataOffset + ((long)HiDataOffset << 32);
        set
        {
            LoDataOffset = (uint)(value & 0xFFFFFFFF);
            HiDataOffset = (uint)((value >> 32) & 0xFFFFFFFF);
        }
    }

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
    /// Reads and constructs an Nb0EntryHeader instance from a binary reader.
    /// <para>从二进制读取器中读取并构造一个 Nb0EntryHeader 实例。</para>
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
    /// Writes the current entry header to the specified binary writer.
    /// <para>将当前条目头写入指定的二进制写入器。</para>
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
    /// Writes the entry header to a pre-allocated byte buffer, avoiding intermediate allocation.
    /// <para>将条目头写入预分配的字节缓冲区,避免中间分配。</para>
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
    /// Reads the entry header from a byte buffer.
    /// <para>从字节缓冲区读取条目头。</para>
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
    /// Serializes the entry header to a byte array.
    /// <para>将条目头序列化为字节数组。</para>
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