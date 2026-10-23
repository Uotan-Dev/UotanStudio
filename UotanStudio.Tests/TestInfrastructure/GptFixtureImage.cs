using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Tests.TestInfrastructure;

internal readonly record struct GptFixtureEntry(Guid TypeId, Guid Id, ulong FirstLba, ulong LastLba, string Name);

internal static class GptFixtureImage
{
    internal static void Write(string path, ulong diskSizeBytes, IReadOnlyList<GptFixtureEntry> entries)
        => File.WriteAllBytes(path, Build(diskSizeBytes, Guid.NewGuid(), entries));

    internal static byte[] Build(ulong diskSizeBytes, Guid diskGuid, IReadOnlyList<GptFixtureEntry> entries)
    {
        var diskSectors = checked(diskSizeBytes / GptLayout.SectorSize);
        var lastUsableLba = checked(diskSectors - GptLayout.FirstUsableLba - 1);
        var entryBytes = checked(entries.Count * GptLayout.EntrySize);
        var entrySectors = checked((entryBytes + GptLayout.SectorSize - 1) / GptLayout.SectorSize);
        var imageLength = checked((long)((GptLayout.EntriesStartLba + (ulong)entrySectors) * GptLayout.SectorSize));
        var image = new byte[imageLength];

        var mbrSlot = image.AsSpan(446, 16);
        mbrSlot[4] = GptLayout.ProtectiveMbrType;
        BinaryPrimitives.WriteUInt32LittleEndian(mbrSlot[8..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(mbrSlot[12..], (uint)Math.Min(diskSectors, uint.MaxValue));
        image[510] = 0x55;
        image[511] = 0xAA;

        var header = image.AsSpan(GptLayout.HeaderOffset, GptLayout.HeaderSize);
        GptLayout.Signature.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], GptLayout.Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], GptLayout.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], GptLayout.PrimaryHeaderLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], checked(diskSectors - 1));
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], GptLayout.FirstUsableLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], lastUsableLba);
        WriteGuidBigEndian(header[56..], diskGuid);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], GptLayout.EntriesStartLba);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], (uint)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[84..], GptLayout.EntrySize);

        var array = image.AsSpan((int)checked(GptLayout.EntriesStartLba * GptLayout.SectorSize), entryBytes);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var slot = array.Slice(i * GptLayout.EntrySize, GptLayout.EntrySize);
            WriteGuidBigEndian(slot, entry.TypeId);
            WriteGuidBigEndian(slot[16..], entry.Id);
            BinaryPrimitives.WriteUInt64LittleEndian(slot[32..], entry.FirstLba);
            BinaryPrimitives.WriteUInt64LittleEndian(slot[40..], entry.LastLba);
            Encoding.Unicode.GetBytes(entry.Name.AsSpan(0, Math.Min(entry.Name.Length, GptLayout.MaxNameUnits)),
                slot[56..(56 + Math.Min(entry.Name.Length, GptLayout.MaxNameUnits) * 2)]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(header[GptLayout.EntriesCrcOffset..], System.IO.Hashing.Crc32.HashToUInt32(array));
        BinaryPrimitives.WriteUInt32LittleEndian(header[GptLayout.HeaderCrcOffset..], System.IO.Hashing.Crc32.HashToUInt32(header));
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