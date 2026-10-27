using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace UotanStudio.Firmware.FileSystem.Fat;

public static partial class FatBuilder
{
    private static readonly SearchValues<char> InvalidNameChars =
        SearchValues.Create(['"', '*', '/', ':', '<', '>', '?', '\\', '|']);

    private static readonly SearchValues<char> ShortNameChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789$%'-_@~\u0060!(){}^#&");

    /// <summary>
    /// Assigns one short (8.3) name per entry and, when the short name cannot carry the original spelling, the
    /// long-name entries that precede it. Names are unique in their directory both as long names and as short
    /// names, which is what every FAT reader expects.
    /// <para>为每个条目分配一个 8.3 短名，并在短名无法承载原始写法时生成排在其前的长名条目。同一目录内的长名与
    /// 短名都保持唯一，符合所有 FAT 读取方的预期。</para>
    /// </summary>
    private static void AssignNames(FatNode root, FatBuildOptions options)
    {
        var labelBytes = options.VolumeLabel.Length == 0 ? 0 : DirectoryEntryBytes;
        var queue = new Queue<FatNode>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var directory = queue.Dequeue();
            var usedShortNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedLongNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bytes = directory.Parent is null ? labelBytes : 2 * DirectoryEntryBytes;
            foreach (var child in directory.Children)
            {
                ValidateName(child.Name);
                if (!usedLongNames.Add(child.Name))
                {
                    throw new InvalidDataException(
                        $"The source directory '{directory.SourcePath}' stores two entries named '{child.Name}'; FAT names are case-insensitive.");
                }

                child.ShortName = CreateShortName(child.Name, usedShortNames);
                child.LongNameEntries = BuildLongNameEntries(child.Name, child.ShortName);
                bytes += child.EntryBytes;
                if (bytes > FatBuilder.MaximumDirectoryBytes)
                {
                    throw new FileSystemCorruptedException(
                        $"The directory '{directory.SourcePath}' needs more than {FatBuilder.MaximumDirectoryBytes} bytes of entries.");
                }

                if (child.IsDirectory)
                {
                    queue.Enqueue(child);
                }
            }

            directory.DirectoryBytes = bytes;
        }
    }

    private static void ValidateName(string name)
    {
        if (name.Length == 0 || name is "." or "..")
        {
            throw new InvalidDataException($"'{name}' is not a storable FAT entry name.");
        }

        if (name.Length > 255)
        {
            throw new InvalidDataException($"The FAT entry name '{name}' exceeds the 255-character long-name limit.");
        }

        if (name[0] == ' ' || name[^1] is ' ' or '.')
        {
            throw new InvalidDataException($"The FAT entry name '{name}' cannot start with a space or end with a space or dot.");
        }

        foreach (var character in name)
        {
            if (character < 0x20 || character == 0x7F || InvalidNameChars.Contains(character))
            {
                throw new InvalidDataException($"The FAT entry name '{name}' contains '{character}', which FAT cannot store.");
            }
        }
    }

    private static byte[] CreateShortName(string name, HashSet<string> usedShortNames)
    {
        SplitName(name, out var rawBase, out var rawExtension);
        var basePart = Sanitize(rawBase, 8);
        if (basePart.Length == 0)
        {
            basePart = "FILE";
        }

        var extension = Sanitize(rawExtension, 3);
        var canonical = FormatShortName(basePart, extension);
        if (usedShortNames.Add(Encoding.ASCII.GetString(canonical)))
        {
            return canonical;
        }

        for (var index = 1; index <= 999_999; index++)
        {
            var tail = "~" + index.ToString(CultureInfo.InvariantCulture);
            var keep = Math.Min(basePart.Length, 8 - tail.Length);
            var candidate = FormatShortName(basePart[..keep] + tail, extension);
            if (usedShortNames.Add(Encoding.ASCII.GetString(candidate)))
            {
                return candidate;
            }
        }

        throw new InvalidDataException($"The directory stores too many names that shorten to the same 8.3 form for '{name}'.");
    }

    private static void SplitName(string name, out string basePart, out string extension)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
        {
            basePart = name;
            extension = string.Empty;
            return;
        }

        basePart = name[..dot];
        extension = name[(dot + 1)..];
    }

    private static string Sanitize(string value, int maximumLength)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        foreach (var character in value)
        {
            if (builder.Length == maximumLength)
            {
                break;
            }

            if (character is >= 'a' and <= 'z')
            {
                builder.Append((char)(character - 'a' + 'A'));
            }
            else if (character is >= 'A' and <= 'Z' or >= '0' and <= '9' || ShortNameChars.Contains(character))
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('_');
            }
        }

        return builder.ToString().TrimEnd(' ');
    }

    private static byte[] FormatShortName(string basePart, string extension)
    {
        var name = new byte[11];
        Array.Fill(name, (byte)' ');
        Encoding.ASCII.GetBytes(basePart).CopyTo(name, 0);
        Encoding.ASCII.GetBytes(extension).CopyTo(name, 8);
        return name;
    }

    private static string CanonicalShortName(byte[] shortName)
    {
        var basePart = Encoding.ASCII.GetString(shortName, 0, 8).TrimEnd(' ');
        var extension = Encoding.ASCII.GetString(shortName, 8, 3).TrimEnd(' ');
        return extension.Length == 0 ? basePart : basePart + "." + extension;
    }

    private static byte[][] BuildLongNameEntries(string name, byte[] shortName)
    {
        if (string.Equals(name, CanonicalShortName(shortName), StringComparison.Ordinal))
        {
            return [];
        }

        var parts = (name.Length + 12) / 13;
        var slots = parts * 13;
        var characters = new char[slots];
        for (var index = 0; index < slots; index++)
        {
            characters[index] = index < name.Length ? name[index] : index == name.Length ? '\0' : '\uFFFF';
        }

        var checksum = ShortNameChecksum(shortName);
        var entries = new byte[parts][];
        for (var slot = 0; slot < parts; slot++)
        {
            var source = parts - 1 - slot;
            var entry = new byte[DirectoryEntryBytes];
            entry[0] = (byte)(source + 1 | (source == parts - 1 ? 0x40 : 0));
            entry[11] = 0x0F;
            entry[13] = checksum;
            WriteLongNameCharacters(entry, 1, characters, source * 13, 5);
            WriteLongNameCharacters(entry, 14, characters, source * 13 + 5, 6);
            WriteLongNameCharacters(entry, 28, characters, source * 13 + 11, 2);
            entries[slot] = entry;
        }

        return entries;
    }

    private static void WriteLongNameCharacters(byte[] entry, int offset, char[] characters, int start, int count)
    {
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(offset + index * 2, 2), characters[start + index]);
        }
    }

    private static byte ShortNameChecksum(byte[] shortName)
    {
        var sum = 0;
        foreach (var value in shortName)
        {
            sum = ((sum & 1) != 0 ? 0x80 : 0) + (sum >> 1) + value & 0xFF;
        }

        return (byte)sum;
    }

    private static byte AttributeByte(FileAttributes attributes, bool isDirectory)
    {
        byte value = isDirectory ? (byte)0x10 : (byte)0x20;
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            value |= 0x01;
        }

        if ((attributes & FileAttributes.Hidden) != 0)
        {
            value |= 0x02;
        }

        if ((attributes & FileAttributes.System) != 0)
        {
            value |= 0x04;
        }

        return value;
    }

    private static (ushort Date, ushort Time) DosTimestamp(DateTime timestampUtc)
    {
        var local = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc.ToLocalTime() : timestampUtc;
        var year = Math.Clamp(local.Year, 1980, 2107);
        var date = (ushort)((year - 1980) << 9 | local.Month << 5 | local.Day);
        var time = (ushort)(local.Hour << 11 | local.Minute << 5 | local.Second / 2);
        return (date, time);
    }
}