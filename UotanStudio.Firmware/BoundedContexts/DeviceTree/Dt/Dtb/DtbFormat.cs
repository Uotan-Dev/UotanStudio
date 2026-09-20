using DtTree = UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Models.DeviceTree;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Models;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Dtb;

public static class DtbFormat
{
    /// <summary>
    /// Parses a device tree from a byte array in DTB format.
    /// <para>从 DTB 格式的字节数组解析设备树。</para>
    /// </summary>
    public static DtTree Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Read(data.AsSpan());
    }

    /// <summary>
    /// Parses a device tree from a span in DTB format.
    /// <para>从 DTB 格式的字节跨度解析设备树。</para>
    /// </summary>
    public static DtTree Read(ReadOnlySpan<byte> data)
    {
        var header = FdtHeader.ReadFrom(data);
        if (!header.IsValidMagic)
        {
            throw new InvalidDataException($"Invalid FDT magic: 0x{header.Magic:X8} (expected 0x{FdtConstants.FdtMagic:X8})");
        }
        if (header.TotalSize > data.Length)
        {
            throw new InvalidDataException($"Data size ({data.Length}) is smaller than declared total size ({header.TotalSize})");
        }

        var tree = new DtTree
        {
            BootCpuId = header.BootCpuidPhys,
            Version = header.Version
        };


        if (header.OffsetMemRsvmap > 0 && header.OffsetMemRsvmap < data.Length)
        {
            var cursor = (int)header.OffsetMemRsvmap;
            while (cursor + FdtConstants.MemoryReservationEntrySize <= data.Length)
            {
                var address = ReadU64BE(data, cursor);
                var size = ReadU64BE(data, cursor + 8);
                if (address == 0 && size == 0)
                {
                    break;
                }
                tree.MemoryReservations.Add(new MemoryReservation { Address = address, Size = size });
                cursor += FdtConstants.MemoryReservationEntrySize;
            }
        }

        var stringsBlock = data.Slice((int)header.OffsetDtStrings, (int)header.SizeDtStrings);
        var structBlock = data.Slice((int)header.OffsetDtStruct, (int)header.SizeDtStruct);

        ParseStructure(structBlock, stringsBlock, tree);
        return tree;
    }

    /// <summary>
    /// Serializes a device tree to binary DTB format.
    /// <para>将设备树序列化为二进制 DTB 格式。</para>
    /// </summary>
    public static byte[] Write(DtTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);


        var strings = new List<string>();
        var stringOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        var stringsBytes = new List<byte> { 0 };

        int GetStringOffset(string name)
        {
            if (stringOffsets.TryGetValue(name, out var offset))
            {
                return offset;
            }
            offset = stringsBytes.Count;
            stringsBytes.AddRange(Encoding.UTF8.GetBytes(name));
            stringsBytes.Add(0);
            strings.Add(name);
            stringOffsets[name] = offset;
            return offset;
        }

        var structBytes = new List<byte>();

        void Align(List<byte> buffer, int alignment)
        {
            while (buffer.Count % alignment != 0)
            {
                buffer.Add(0);
            }
        }

        void WriteU32(List<byte> buffer, uint value)
        {
            buffer.Add((byte)(value >> 24));
            buffer.Add((byte)(value >> 16));
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }

        void WriteNode(DeviceTreeNode node)
        {
            WriteU32(structBytes, FdtConstants.FdtBeginNode);
            var nameBytes = Encoding.UTF8.GetBytes(node.Name);
            structBytes.AddRange(nameBytes);
            structBytes.Add(0);
            Align(structBytes, FdtConstants.Alignment);

            foreach (var property in node.Properties)
            {
                if (property.IsNop)
                {
                    WriteU32(structBytes, FdtConstants.FdtNop);
                    continue;
                }
                WriteU32(structBytes, FdtConstants.FdtProp);
                WriteU32(structBytes, (uint)property.Length);
                WriteU32(structBytes, (uint)GetStringOffset(property.Name));
                structBytes.AddRange(property.ValueBytes);
                Align(structBytes, FdtConstants.Alignment);
            }

            foreach (var child in node.Children)
            {
                WriteNode(child);
            }

            WriteU32(structBytes, FdtConstants.FdtEndNode);
        }


        var rsvBytes = new List<byte>();
        foreach (var rsv in tree.MemoryReservations)
        {
            var addrBytes = new byte[8];
            WriteU64BE(addrBytes, 0, rsv.Address);
            rsvBytes.AddRange(addrBytes);
            var sizeBytes = new byte[8];
            WriteU64BE(sizeBytes, 0, rsv.Size);
            rsvBytes.AddRange(sizeBytes);
        }
        rsvBytes.AddRange(new byte[16]);
        Align(rsvBytes, 8);

        WriteNode(tree.Root);
        WriteU32(structBytes, FdtConstants.FdtEnd);

        var header = new FdtHeader
        {
            Magic = FdtConstants.FdtMagic,
            Version = tree.Version,
            LastCompVersion = FdtConstants.FdtLastCompVersion,
            BootCpuidPhys = tree.BootCpuId,
            OffsetMemRsvmap = FdtConstants.HeaderSize,
            SizeDtStrings = (uint)stringsBytes.Count,
            SizeDtStruct = (uint)structBytes.Count
        };

        Align(structBytes, FdtConstants.Alignment);
        Align(stringsBytes, FdtConstants.Alignment);

        header.OffsetDtStruct = header.OffsetMemRsvmap + (uint)rsvBytes.Count;
        header.OffsetDtStrings = header.OffsetDtStruct + (uint)structBytes.Count;
        header.TotalSize = header.OffsetDtStrings + (uint)stringsBytes.Count;

        var result = new byte[header.TotalSize];
        header.ToBytes().CopyTo(result, 0);
        rsvBytes.CopyTo(result, (int)header.OffsetMemRsvmap);
        structBytes.CopyTo(result, (int)header.OffsetDtStruct);
        stringsBytes.CopyTo(result, (int)header.OffsetDtStrings);

        return result;
    }

    private static void ParseStructure(ReadOnlySpan<byte> structBlock, ReadOnlySpan<byte> stringsBlock, DtTree tree)
    {
        var cursor = 0;


        if (cursor + 4 > structBlock.Length)
        {
            throw new InvalidDataException("Empty structure block in DTB.");
        }

        var rootToken = FdtHeader.ReadU32BE(structBlock[cursor..]);
        cursor += 4;
        if (rootToken != FdtConstants.FdtBeginNode)
        {
            throw new InvalidDataException($"Expected root FDT_BEGIN_NODE, got 0x{rootToken:X8}");
        }

        var nameStart = cursor;
        var end = structBlock[cursor..].IndexOf((byte)0);
        if (end < 0)
        {
            throw new InvalidDataException("Unterminated root node name in DTB structure block.");
        }

        tree.Root.Name = Encoding.UTF8.GetString(structBlock.Slice(nameStart, end));
        cursor = nameStart + end + 1;
        while (cursor % FdtConstants.Alignment != 0)
        {
            cursor++;
        }

        ParseNode(structBlock, stringsBlock, ref cursor, tree.Root);
    }

    private static void ParseNode(ReadOnlySpan<byte> structBlock, ReadOnlySpan<byte> stringsBlock, ref int cursor, DeviceTreeNode parent)
    {
        DeviceTreeNode? current;

        while (cursor + 4 <= structBlock.Length)
        {
            var token = FdtHeader.ReadU32BE(structBlock[cursor..]);
            cursor += 4;

            switch (token)
            {
                case FdtConstants.FdtBeginNode:
                    {
                        var nameStart = cursor;
                        var end = structBlock[cursor..].IndexOf((byte)0);
                        if (end < 0)
                        {
                            throw new InvalidDataException("Unterminated node name in DTB structure block.");
                        }
                        var name = Encoding.UTF8.GetString(structBlock.Slice(nameStart, end));
                        cursor = nameStart + end + 1;
                        while (cursor % FdtConstants.Alignment != 0)
                        {
                            cursor++;
                        }

                        current = parent.AddChild(name);
                        ParseNode(structBlock, stringsBlock, ref cursor, current);
                        break;
                    }
                case FdtConstants.FdtEndNode:
                    return;
                case FdtConstants.FdtProp:
                    {
                        if (cursor + 8 > structBlock.Length)
                        {
                            throw new InvalidDataException("Truncated property header in DTB structure block.");
                        }
                        var len = (int)FdtHeader.ReadU32BE(structBlock[cursor..]);
                        var nameOff = (int)FdtHeader.ReadU32BE(structBlock[(cursor + 4)..]);
                        cursor += 8;

                        var nameEnd = stringsBlock[nameOff..].IndexOf((byte)0);
                        var propName = nameEnd >= 0
                            ? Encoding.UTF8.GetString(stringsBlock.Slice(nameOff, nameEnd))
                            : Encoding.UTF8.GetString(stringsBlock[nameOff..]);

                        var value = structBlock.Slice(cursor, len).ToArray();
                        cursor += len;
                        while (cursor % FdtConstants.Alignment != 0)
                        {
                            cursor++;
                        }

                        parent.AddProperty(propName, value);
                        break;
                    }
                case FdtConstants.FdtNop:
                    break;
                case FdtConstants.FdtEnd:
                    return;
                default:
                    throw new InvalidDataException($"Unknown FDT token: 0x{token:X8}");
            }
        }
    }

    internal static ulong ReadU64BE(ReadOnlySpan<byte> data, int offset)
    {
        return ((ulong)FdtHeader.ReadU32BE(data[offset..]) << 32)
               | FdtHeader.ReadU32BE(data[(offset + 4)..]);
    }

    internal static void WriteU64BE(Span<byte> data, int offset, ulong value)
    {
        FdtHeader.WriteU32BE(data, offset, (uint)(value >> 32));
        FdtHeader.WriteU32BE(data, offset + 4, (uint)value);
    }
}