using UotanStudio.Firmware.BoundedContexts.Executable.Elf;

namespace UotanStudio.Firmware.BoundedContexts.Executable.Qualcomm;

internal static class QualcommMelfComponents
{
    private const int MaximumComponents = 256;
    private const int ScanChunkBytes = 64 * 1024;

    internal static IReadOnlyList<QualcommMelfComponent> Scan(Stream stream, CancellationToken token)
    {
        var offsets = new List<long>();
        var buffer = new byte[ScanChunkBytes + 3];
        long position = 0;
        while (position < stream.Length)
        {
            token.ThrowIfCancellationRequested();
            stream.Position = position;
            var count = stream.Read(buffer, 0, (int)Math.Min(ScanChunkBytes, stream.Length - position));
            if (count == 0)
                break;
            for (var index = 0; index <= count - 4; index++)
            {
                if (!buffer.AsSpan(index, 4).SequenceEqual("\u007fELF"u8))
                    continue;
                var offset = position + index;
                if (offsets.Count == 0 || offsets[^1] != offset)
                    offsets.Add(offset);
                if (offsets.Count > MaximumComponents)
                    throw new InvalidDataException("Qualcomm multi-ELF component count exceeds the supported limit.");
            }

            if (count < ScanChunkBytes)
                break;
            position += count - 3;
        }

        var components = new List<QualcommMelfComponent>();
        for (var index = 0; index < offsets.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var offset = offsets[index];
            var next = index + 1 < offsets.Count ? offsets[index + 1] : stream.Length;
            if (next <= offset || next - offset < 52)
            {
                continue;
            }
            stream.Position = offset;
            var headerBytes = new byte[64];
            var read = stream.Read(headerBytes, 0, Math.Min(headerBytes.Length, checked((int)Math.Min(64, next - offset))));
            if (!ElfReader.TryReadHeader(headerBytes.AsSpan(0, read), out var header)
                || header is null
                || header.ProgramHeaderOffset > (ulong)(next - offset)
                || header.ProgramHeaderCount > 0
                    && checked(header.ProgramHeaderOffset + (ulong)header.ProgramHeaderEntrySize * header.ProgramHeaderCount)
                        > (ulong)(next - offset))
            {
                continue;
            }

            components.Add(new(offset, next - offset, header.Is64Bit, header.Machine));
        }

        return components.Count > 1 ? components : [];
    }
}