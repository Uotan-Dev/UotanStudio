using System.Buffers.Binary;

namespace UotanStudio.Services.Projects;

/// <summary>Bounds ZIP directory allocation before ZipArchive materializes its entry list.</summary>
internal static class ProjectPackageIndex
{
    internal static void Validate(FileStream stream)
    {
        var length = stream.Length;
        var tail = new byte[(int)Math.Min(length, 65557)];
        stream.Position = length - tail.Length;
        stream.ReadExactly(tail);
        Span<byte> locator = stackalloc byte[20];
        Span<byte> zip64 = stackalloc byte[56];
        for (var offset = tail.Length - 22; offset >= 0; offset--)
        {
            var end = tail.AsSpan(offset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(end) != 0x06054b50
                || BinaryPrimitives.ReadUInt16LittleEndian(end[20..]) != end.Length - 22) continue;
            if (BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0)
                throw new InvalidDataException("Split ZIP packages are not supported.");
            ulong count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
            ulong size = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
            if (count == ushort.MaxValue || size == uint.MaxValue || BinaryPrimitives.ReadUInt32LittleEndian(end[16..]) == uint.MaxValue)
            {
                var endPosition = length - tail.Length + offset;
                if (endPosition < 20) throw new InvalidDataException("Missing ZIP64 locator.");
                stream.Position = endPosition - 20;
                stream.ReadExactly(locator);
                if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50
                    || BinaryPrimitives.ReadUInt32LittleEndian(locator[4..]) != 0
                    || BinaryPrimitives.ReadUInt32LittleEndian(locator[16..]) != 1)
                    throw new InvalidDataException("Invalid ZIP64 locator.");
                var position = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
                if (position > (ulong)Math.Max(0, length - 56)) throw new InvalidDataException("ZIP64 index is outside the package.");
                stream.Position = (long)position;
                stream.ReadExactly(zip64);
                if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != 0x06064b50
                    || BinaryPrimitives.ReadUInt32LittleEndian(zip64[16..]) != 0
                    || BinaryPrimitives.ReadUInt32LittleEndian(zip64[20..]) != 0)
                    throw new InvalidDataException("Invalid ZIP64 index.");
                count = BinaryPrimitives.ReadUInt64LittleEndian(zip64[32..]);
                size = BinaryPrimitives.ReadUInt64LittleEndian(zip64[40..]);
            }
            if (count > 100000 || size > 32 * 1024 * 1024)
                throw new InvalidDataException("Package directory exceeds its metadata budget.");
            stream.Position = 0;
            return;
        }
        throw new InvalidDataException("Missing ZIP end record.");
    }
}