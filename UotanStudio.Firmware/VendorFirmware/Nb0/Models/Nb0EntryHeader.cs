using System.Text;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.VendorFirmware.Nb0.Models;

/// <summary>
/// Fixed 64-byte directory entry header of the Nb0 container: four little-endian 32-bit offset/size
/// halves followed by a 48-byte ASCII name.
/// <para>Nb0 容器的 64 字节定长目录条目头：四个小端 32 位偏移/大小编辑对，随后是 48 字节 ASCII 名称。</para>
/// </summary>
public sealed class Nb0EntryHeader
{
    /// <summary>Serialized header size in bytes. <para>序列化后的头部字节大小。</para></summary>
    public const int StructSize = 64;

    /// <summary>Width of the fixed name field in bytes. <para>定长名称字段的字节宽度。</para></summary>
    public const int NameLength = 48;

    /// <summary>Low 32 bits of the data offset as stored on disk. <para>磁盘上存储的数据偏移低 32 位。</para></summary>
    /// <value>The raw low half. <para>原始低半部值。</para></value>
    public uint LoDataOffset { get; set; }

    /// <summary>Low 32 bits of the file size as stored on disk. <para>磁盘上存储的文件大小低 32 位。</para></summary>
    /// <value>The raw low half. <para>原始低半部值。</para></value>
    public uint LoFileSize { get; set; }

    /// <summary>High 32 bits of the data offset as stored on disk. <para>磁盘上存储的数据偏移高 32 位。</para></summary>
    /// <value>The raw high half. <para>原始高半部值。</para></value>
    public uint HiDataOffset { get; set; }

    /// <summary>High 32 bits of the file size as stored on disk. <para>磁盘上存储的文件大小高 32 位。</para></summary>
    /// <value>The raw high half. <para>原始高半部值。</para></value>
    public uint HiFileSize { get; set; }

    /// <summary>Entry name, trimmed of trailing NUL and space bytes. <para>条目名称，会去除结尾的 NUL 与空格字节。</para></summary>
    /// <value>The name string; empty when the field holds only padding. <para>名称字符串；字段仅含填充时为空。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Combined 64-bit data offset built from <see cref="LoDataOffset"/> and <see cref="HiDataOffset"/>;
    /// setting it rewrites both halves.
    /// <para>由 <see cref="LoDataOffset"/> 与 <see cref="HiDataOffset"/> 合成的 64 位数据偏移；赋值时会同时改写两半。</para>
    /// </summary>
    /// <value>The data offset relative to the start of the data section.
    /// <para>相对于数据段起始处的数据偏移。</para></value>
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
    /// Combined 64-bit file size built from <see cref="LoFileSize"/> and <see cref="HiFileSize"/>;
    /// setting it rewrites both halves.
    /// <para>由 <see cref="LoFileSize"/> 与 <see cref="HiFileSize"/> 合成的 64 位文件大小；赋值时会同时改写两半。</para>
    /// </summary>
    /// <value>The file size in bytes. <para>以字节计的文件大小。</para></value>
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
    /// <param name="reader">
    /// Reader positioned at the start of a 64-byte header record.
    /// <para>定位到 64 字节头记录起始处的读取器。</para>
    /// </param>
    /// <returns>The parsed header. <para>解析后的头部。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is <see langword="null"/>.
    /// <para><paramref name="reader"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="EndOfStreamException">The stream has fewer than 64 bytes remaining.
    /// <para>流剩余字节不足 64。</para></exception>
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
    /// <param name="writer">
    /// Destination writer. The name is ASCII-encoded and truncated to 47 bytes; remaining bytes are zero.
    /// <para>目标写入器。名称按 ASCII 编码并截断为 47 字节，其余字节补零。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.
    /// <para><paramref name="writer"/> 为 <see langword="null"/>。</para></exception>
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
    /// <param name="buffer">
    /// Destination buffer with at least <see cref="StructSize"/> bytes; integers are written little-endian
    /// at offsets 0, 4, 8, 12 and the 48-byte name at offset 16.
    /// <para>至少含 <see cref="StructSize"/> 字节的目标缓冲区；整数以小端写入偏移 0、4、8、12，48 字节名称写入偏移 16。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.
    /// <para><paramref name="buffer"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="buffer"/> is shorter than <see cref="StructSize"/> bytes.
    /// <para><paramref name="buffer"/> 长度小于 <see cref="StructSize"/> 字节。</para></exception>
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
    /// <param name="buffer">
    /// Source buffer holding a header record whose integers are little-endian.
    /// <para>持有头记录的源缓冲区，其中整数为小端。</para>
    /// </param>
    /// <param name="offset">
    /// Byte offset in <paramref name="buffer"/> where the header starts. Defaults to 0.
    /// <para><paramref name="buffer"/> 中头记录起始的字节偏移，默认为 0。</para>
    /// </param>
    /// <returns>The parsed header. <para>解析后的头部。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.
    /// <para><paramref name="buffer"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="buffer"/> does not contain <see cref="StructSize"/> bytes at <paramref name="offset"/>.
    /// <para><paramref name="buffer"/> 在 <paramref name="offset"/> 处不足 <see cref="StructSize"/> 字节。</para>
    /// </exception>
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
    /// <returns>
    /// A new <see cref="StructSize"/>-byte array in little-endian field order.
    /// <para>按小端字段顺序生成的新的 <see cref="StructSize"/> 字节数组。</para>
    /// </returns>
    public byte[] ToBytes()
    {
        var buffer = new byte[StructSize];
        WriteToBuffer(buffer);
        return buffer;
    }

    private static void WriteUInt32LE(byte[] buffer, int offset, uint value)
        => ByteWriter.WriteUInt32LE(buffer, offset, value);

    private static uint ReadUInt32LE(byte[] buffer, int offset)
    {
        return (uint)(buffer[offset]
            | (buffer[offset + 1] << 8)
            | (buffer[offset + 2] << 16)
            | (buffer[offset + 3] << 24));
    }
}