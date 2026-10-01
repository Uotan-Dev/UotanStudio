using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Tests.Firmware.Fat;

internal sealed partial class FatImageBuilder
{
    private static readonly char[] ShortNameAllowed =
    [
        'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T',
        'U', 'V', 'W', 'X', 'Y', 'Z', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '_', '$', '!', '#',
        '%', '&', '\'', '(', ')', '-', '@', '^', '~', '`',
    ];

    private byte[] Render(Node node)
    {
        var buffer = new MemoryStream();
        if (node != _root)
        {
            WriteEntry(buffer, DotEntry(".", node.Clusters.Count > 0 ? node.Clusters[0] : 0));
            WriteEntry(buffer, DotEntry("..", node.Parent?.Clusters.Count > 0 ? node.Parent.Clusters[0] : 0));
        }

        foreach (var child in node.Children)
        {
            var cluster = child.Clusters.Count > 0 ? child.Clusters[0] : 0;
            var shortName = ShortNameEntry(child.Name, child.IsDirectory ? (byte)0x10 : (byte)0x20, cluster,
                child.IsDirectory ? 0 : child.Content.Length);
            if (!FitsShortName(child.Name))
            {
                foreach (var longEntry in LongNameEntries(child.Name, shortName))
                {
                    WriteEntry(buffer, longEntry);
                }
            }

            WriteEntry(buffer, shortName);
        }

        return buffer.ToArray();
    }

    private static void WriteEntry(Stream buffer, byte[] entry) => buffer.Write(entry, 0, entry.Length);

    private static byte[] DotEntry(string label, int cluster)
    {
        var entry = new byte[32];
        entry.AsSpan(0, 11).Fill((byte)' ');
        Encoding.ASCII.GetBytes(label).CopyTo(entry, 0);
        entry[11] = 0x10;
        WriteCluster(entry, cluster);
        return entry;
    }

    private static byte[] ShortNameEntry(string name, byte attributes, int cluster, long size)
    {
        var entry = new byte[32];
        var dot = name.LastIndexOf('.');
        var stem = dot < 0 ? name : name[..dot];
        var extension = dot < 0 ? string.Empty : name[(dot + 1)..].ToUpperInvariant();
        Encoding.ASCII.GetBytes(Collapse(stem)).CopyTo(entry, 0);
        Encoding.ASCII.GetBytes(extension.Length == 0 ? "   " : extension.PadRight(3)[..3]).CopyTo(entry, 8);
        entry[11] = attributes;
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(14), EncodeTime(8, 0, 0));
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(16), EncodeDate(2026, 1, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(18), EncodeDate(2026, 1, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(22), EncodeTime(9, 15, 30));
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(24), EncodeDate(2026, 1, 2));
        WriteCluster(entry, cluster);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(28), (uint)Math.Min(size, uint.MaxValue));
        return entry;
    }

    private static void WriteCluster(byte[] entry, int cluster)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(20), (ushort)(cluster >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(26), (ushort)(cluster & 0xFFFF));
    }

    private static string Collapse(string stem)
    {
        var filtered = new string(stem.Select(char.ToUpperInvariant).Where(ShortNameAllowed.Contains).ToArray());
        if (filtered.Length == 0)
        {
            filtered = "FILE";
        }

        return filtered.Length <= 8 ? filtered.PadRight(8) : (filtered[..7] + "1").PadRight(8);
    }

    private static bool FitsShortName(string name)
    {
        var dot = name.LastIndexOf('.');
        var stem = dot < 0 ? name : name[..dot];
        var extension = dot < 0 ? string.Empty : name[(dot + 1)..];
        return stem.Length is > 0 and <= 8
            && extension.Length <= 3
            && stem.Concat(extension).Select(char.ToUpperInvariant).All(ShortNameAllowed.Contains);
    }
}