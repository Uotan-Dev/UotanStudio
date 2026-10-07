using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Text;
using UotanStudio.Firmware.Executable.Elf;

namespace UotanStudio.Firmware.Executable.Qualcomm;

internal static class QualcommProgrammerCommands
{
    private static readonly FrozenSet<string> Known = new[]
    {
        "program", "read", "nop", "patch", "configure", "erase", "power",
        "getstorageinfo", "getsha256digest", "setbootablestoragedrive",
        "firmwarewrite", "peek", "poke", "fixgpt", "benchmark", "ufs", "emmc"
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static IReadOnlyList<QualcommCommandEvidence> Find(Stream stream, ElfInfo elf, CancellationToken token)
    {
        if (!elf.Header.IsLittleEndian || elf.Header.Machine is not (40 or 183))
        {
            return [];
        }

        var mapped = elf.Segments.Where(segment => segment.Type == 1 && segment.FileSize > 0).ToArray();
        var strings = new Dictionary<ulong, (string Name, long Offset)>();
        var weak = new Dictionary<string, QualcommCommandEvidence>(StringComparer.Ordinal);
        var remaining = 16L * 1024 * 1024;
        foreach (var segment in mapped)
        {
            token.ThrowIfCancellationRequested();
            if (segment.FileSize > (ulong)remaining)
            {
                continue;
            }

            remaining -= (long)segment.FileSize;
            var data = new byte[(int)segment.FileSize];
            QualcommMbnLayout.ReadAt(stream, (long)segment.Offset, data, token);
            for (var i = 0; i < data.Length;)
            {
                if (data[i] is < 0x20 or > 0x7e)
                {
                    i++;
                    continue;
                }

                var start = i;
                while (i < data.Length && data[i] is >= 0x20 and <= 0x7e && i - start <= 128)
                {
                    i++;
                }

                if (i >= data.Length || data[i] != 0 || i - start is < 2 or > 128)
                {
                    i++;
                    continue;
                }

                var name = Encoding.ASCII.GetString(data, start, i - start);
                if (Known.Contains(name))
                {
                    strings.TryAdd(segment.VirtualAddress + (ulong)start,
                        (name, checked((long)(segment.Offset + (ulong)start))));
                    weak.TryAdd(name, new(name, "StringLiteral", checked((long)(segment.Offset + (ulong)start))));
                }

                i++;
            }
        }

        if (strings.Count < 3)
        {
            return [.. weak.Values.OrderBy(value => value.FileOffset)];
        }

        var pointerSize = elf.Header.Is64Bit ? 8 : 4;
        foreach (var segment in mapped)
        {
            token.ThrowIfCancellationRequested();
            if (segment.FileSize > 8UL * 1024 * 1024)
            {
                continue;
            }

            var data = new byte[(int)segment.FileSize];
            QualcommMbnLayout.ReadAt(stream, (long)segment.Offset, data, token);
            for (var offset = 0; offset + pointerSize * 6 <= data.Length; offset += pointerSize)
            {
                var entries = new List<QualcommCommandEvidence>();
                for (var cursor = offset; cursor + pointerSize * 2 <= data.Length && entries.Count < 128;
                    cursor += pointerSize * 2)
                {
                    var nameAddress = ReadPointer(data.AsSpan(cursor), pointerSize);
                    var handlerAddress = ReadPointer(data.AsSpan(cursor + pointerSize), pointerSize);
                    if (!strings.TryGetValue(nameAddress, out var name)
                        || !mapped.Any(code => code.Flags is var flags && (flags & 1) != 0
                            && handlerAddress >= code.VirtualAddress
                            && handlerAddress - code.VirtualAddress < code.FileSize)
                        || entries.Any(entry => entry.Name == name.Name))
                    {
                        break;
                    }

                    entries.Add(new(name.Name, "MappedHandlerTable", (long)segment.Offset + cursor));
                }

                var names = entries.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
                if (names.Count >= 3 && names.Contains("program")
                    && (names.Contains("read") || names.Contains("nop"))
                    && names.Contains("configure"))
                {
                    foreach (var entry in entries)
                    {
                        weak[entry.Name] = entry;
                    }

                    return [.. weak.Values.OrderBy(value => value.FileOffset)];
                }
            }
        }

        return [.. weak.Values.OrderBy(value => value.FileOffset)];
    }

    internal static IReadOnlyList<QualcommCommandEvidence> FindUnmapped(
        Stream stream, long offset, long length, CancellationToken token)
    {
        if (length > 16 * 1024 * 1024 || length < 0)
        {
            return [];
        }

        var data = new byte[(int)length];
        QualcommMbnLayout.ReadAt(stream, offset, data, token);
        var found = new Dictionary<string, QualcommCommandEvidence>(StringComparer.Ordinal);
        for (var i = 0; i < data.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (data[i] is < 0x20 or > 0x7e)
            {
                i++;
                continue;
            }

            var start = i;
            while (i < data.Length && data[i] is >= 0x20 and <= 0x7e && i - start <= 128)
            {
                i++;
            }

            if (i < data.Length && data[i] == 0 && i - start is >= 2 and <= 128)
            {
                var name = Encoding.ASCII.GetString(data, start, i - start);
                if (Known.Contains(name))
                {
                    found.TryAdd(name, new(name, "UnmappedStringLiteral", offset + start));
                }
            }

            i++;
        }

        return [.. found.Values.OrderBy(value => value.FileOffset)];
    }

    private static ulong ReadPointer(ReadOnlySpan<byte> bytes, int size)
        => size == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) :
            BinaryPrimitives.ReadUInt32LittleEndian(bytes);
}