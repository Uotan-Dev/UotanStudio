using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Models;

/// <summary>Reads and writes the Linux F2FS aligned xattr header and entry layout.</summary>
internal static class F2fsXattrs
{
    private static readonly Dictionary<byte, string> Prefixes = new()
    {
        [1] = "user.",
        [2] = "system.posix_acl_access",
        [3] = "system.posix_acl_default",
        [4] = "trusted.",
        [6] = "security.",
        [9] = "encryption."
    };

    internal static Dictionary<string, byte[]> Decode(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (data.IsEmpty || data[..Math.Min(4, data.Length)].IndexOfAnyExcept((byte)0) < 0) return result;
        if (data.Length < 24 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0xF2F52011)
            throw new InvalidDataException("Invalid F2FS xattr header.");
        var offset = 24;
        while (offset + 4 <= data.Length && BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) != 0)
        {
            var index = data[offset];
            var nameLength = data[offset + 1];
            var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
            var length = checked(4 + nameLength + valueLength);
            if (length > data.Length - offset) throw new InvalidDataException("Truncated F2FS xattr.");
            var prefix = Prefixes.GetValueOrDefault(index, $"f2fs.index{index}.");
            var name = prefix + Encoding.UTF8.GetString(data.Slice(offset + 4, nameLength));
            result.Add(name, data.Slice(offset + 4 + nameLength, valueLength).ToArray());
            offset += (length + 3) & ~3;
        }
        return result;
    }

    internal static byte[] Encode(Dictionary<string, byte[]>? values)
    {
        if (values is not { Count: > 0 }) return [];
        using var output = new MemoryStream();
        output.Write(new byte[24]);
        foreach (var pair in values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var match = Prefixes.FirstOrDefault(p => pair.Key.StartsWith(p.Value, StringComparison.Ordinal));
            if (match.Key == 0 && pair.Key.StartsWith("f2fs.index", StringComparison.Ordinal))
            {
                var end = pair.Key.IndexOf('.', "f2fs.index".Length);
                if (end > 0 && byte.TryParse(pair.Key.AsSpan("f2fs.index".Length, end - "f2fs.index".Length), out var index) && index != 0)
                    match = new(index, pair.Key[..(end + 1)]);
            }
            if (match.Key == 0) throw new InvalidDataException($"Unsupported F2FS xattr namespace: {pair.Key}");
            var name = Encoding.UTF8.GetBytes(pair.Key[match.Value.Length..]);
            if (name.Length > 255 || pair.Value.Length > ushort.MaxValue) throw new InvalidDataException("F2FS xattr exceeds its entry limit.");
            output.WriteByte(match.Key);
            output.WriteByte((byte)name.Length);
            output.WriteByte((byte)pair.Value.Length);
            output.WriteByte((byte)(pair.Value.Length >> 8));
            output.Write(name);
            output.Write(pair.Value);
            while (output.Position % 4 != 0) output.WriteByte(0);
        }
        output.Write(new byte[4]);
        if (output.Length > 512 + 4072) throw new InvalidDataException("F2FS xattrs exceed the inline and external node capacity (4584 bytes); no attributes were discarded.");
        var result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0xF2F52011);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 1);
        return result;
    }
}