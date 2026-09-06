using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.OnePlusOps;

public class OpsHeader
{
    public string Magic { get; set; } = string.Empty;

    public uint Version { get; set; }

    public uint HeaderSize { get; set; }

    public uint FileSize { get; set; }

    public uint EntryCount { get; set; }

    public uint Flags { get; set; }

    public bool IsValid => Magic == OpsConstants.OPS_MAGIC;

    /// <summary>
    /// Parses an OpsHeader from a read-only byte span
    /// <para>从只读字节跨度解析OpsHeader</para>
    /// </summary>
    public static OpsHeader FromBytes(ReadOnlySpan<byte> data)
    {
        return data.Length < OpsConstants.HEADER_SIZE
            ? throw new ArgumentException("Insufficient data to parse OPS header", nameof(data))
            : new OpsHeader
            {
                Magic = Encoding.ASCII.GetString(data[..4].ToArray()),
                Version = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)),
                HeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4)),
                FileSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12, 4)),
                EntryCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16, 4)),
                Flags = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20, 4))
            };
    }
}

public class OpsEntry
{
    public uint Id { get; set; }

    public uint Offset { get; set; }

    public uint Size { get; set; }

    public string Name { get; set; } = string.Empty;

    public byte[] Data { get; set; } = [];

    /// <summary>
    /// Parses an OpsEntry from a read-only byte span
    /// <para>从只读字节跨度解析OpsEntry</para>
    /// </summary>
    public static OpsEntry FromBytes(ReadOnlySpan<byte> data)
    {
        return data.Length < OpsConstants.ENTRY_HEADER_SIZE
            ? throw new ArgumentException("Insufficient data to parse OPS entry", nameof(data))
            : new OpsEntry
            {
                Id = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]),
                Offset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)),
                Size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4)),
                Name = Encoding.ASCII.GetString(data.Slice(12, OpsConstants.ENTRY_NAME_SIZE)).TrimEnd('\0')
            };
    }
}
