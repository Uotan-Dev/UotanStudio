using System.Buffers.Binary;
using System.Text;
using UotanStudio.Models;

namespace UotanStudio.Tests.TestInfrastructure;

internal readonly record struct GptFixtureEntry(Guid TypeGuid, Guid UniqueGuid, ulong FirstLba, ulong LastLba, string Name);

internal static class GptFixtureImage
{
    internal static void Write(string path, ulong diskSizeBytes, IReadOnlyList<GptFixtureEntry> entries)
        => File.WriteAllBytes(path, Build(diskSizeBytes, Guid.NewGuid(), entries));

    internal static byte[] Build(ulong diskSizeBytes, Guid diskGuid, IReadOnlyList<GptFixtureEntry> entries)
    {
        var diskSectors = checked(diskSizeBytes / GptTable.SectorSize);
        var lastUsableLba = checked(diskSectors - GptTable.FirstUsableLba - 1);
        var entryBytes = checked(entries.Count * GptTable.EntrySize);
        var entrySectors = checked((entryBytes + GptTable.SectorSize - 1) / GptTable.SectorSize);
        var imageLength = checked((long)((GptTable.EntriesStartLba + (ulong)entrySectors) * GptTable.SectorSize));
        var image = new byte[imageLength];

        var mbrSlot = image.AsSpan(446, 16);
        mbrSlot[4] = GptTable.ProtectiveMbrType;
        BinaryPrimitives.WriteUInt32LittleEndian(mbrSlot[8..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(mbrSlot[12..], (uint)Math.Min(diskSectors, uint.MaxValue));
        image[510] = 0x55;
        image[511] = 0xAA;

        var header = image.AsSpan(GptTable.HeaderOffset, GptTable.HeaderSize);
        GptTable.Signature.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], GptTable.Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], GptTable.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], GptTable.PrimaryHeaderLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], checked(diskSectors - 1));
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], GptTable.FirstUsableLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], lastUsableLba);
        WriteGuidBigEndian(header[56..], diskGuid);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], GptTable.EntriesStartLba);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], (uint)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[84..], GptTable.EntrySize);

        var array = image.AsSpan((int)checked(GptTable.EntriesStartLba * GptTable.SectorSize), entryBytes);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var slot = array.Slice(i * GptTable.EntrySize, GptTable.EntrySize);
            WriteGuidBigEndian(slot, entry.TypeGuid);
            WriteGuidBigEndian(slot[16..], entry.UniqueGuid);
            BinaryPrimitives.WriteUInt64LittleEndian(slot[32..], entry.FirstLba);
            BinaryPrimitives.WriteUInt64LittleEndian(slot[40..], entry.LastLba);
            Encoding.Unicode.GetBytes(entry.Name.AsSpan(0, Math.Min(entry.Name.Length, GptTable.MaxNameUnits)),
                slot[56..(56 + Math.Min(entry.Name.Length, GptTable.MaxNameUnits) * 2)]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(header[GptTable.EntriesCrcOffset..], GptTable.Crc32(array));
        BinaryPrimitives.WriteUInt32LittleEndian(header[GptTable.HeaderCrcOffset..], GptTable.Crc32(header));
        return image;
    }

    private static void WriteGuidBigEndian(Span<byte> target, Guid guid)
    {
        var bytes = guid.ToByteArray();
        BinaryPrimitives.WriteUInt32BigEndian(target, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        BinaryPrimitives.WriteUInt16BigEndian(target[4..], BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]));
        BinaryPrimitives.WriteUInt16BigEndian(target[6..], BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]));
        bytes.AsSpan(8, 8).CopyTo(target[8..]);
    }
}