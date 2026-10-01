namespace UotanStudio.Tests.Firmware.Fat;

internal sealed partial class FatImageBuilder
{
    private static IEnumerable<byte[]> LongNameEntries(string name, byte[] shortEntry)
    {
        var characters = (name + '\0').ToCharArray();
        var filler = (char)0xFFFF;
        var chunks = (characters.Length + 12) / 13;
        var padded = new char[chunks * 13];
        Array.Fill(padded, filler);
        characters.CopyTo(padded, 0);
        var checksum = ShortNameChecksum(shortEntry);
        for (var sequence = chunks; sequence >= 1; sequence--)
        {
            var entry = new byte[32];
            entry[0] = sequence == chunks ? (byte)(sequence | 0x40) : (byte)sequence;
            entry[11] = 0x0F;
            entry[13] = checksum;
            var start = (sequence - 1) * 13;
            for (var index = 0; index < 5; index++)
            {
                WriteCharacter(entry, 1 + index * 2, padded[start + index]);
            }

            for (var index = 0; index < 6; index++)
            {
                WriteCharacter(entry, 14 + index * 2, padded[start + 5 + index]);
            }

            for (var index = 0; index < 2; index++)
            {
                WriteCharacter(entry, 28 + index * 2, padded[start + 11 + index]);
            }

            yield return entry;
        }
    }

    private static void WriteCharacter(byte[] entry, int offset, char value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(offset, 2), value);

    private static byte ShortNameChecksum(byte[] entry)
    {
        byte sum = 0;
        for (var index = 0; index < 11; index++)
        {
            sum = (byte)((RotateRight(sum) + entry[index]) & 0xFF);
        }

        return sum;
    }

    private static byte RotateRight(byte value) => (byte)(((value & 1) == 1 ? 0x80 : 0) | (value >> 1));

    private static ushort EncodeTime(int hour, int minute, int second) =>
        (ushort)((hour << 11) | (minute << 5) | second / 2);

    private static ushort EncodeDate(int year, int month, int day) =>
        (ushort)(((year - 1980) << 9) | (month << 5) | day);
}