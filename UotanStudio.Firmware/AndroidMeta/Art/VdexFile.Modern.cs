namespace UotanStudio.Firmware.AndroidMeta.Art;

internal static partial class VdexFile
{
    private static (List<VdexEntry> Entries, long QuickOffset, uint QuickSize, int Version) ParseModern(
        Stream input, int version, CancellationToken token)
    {
        var data = new ArtData(input, 0, input.Length, token);
        var headerSize = version == 19 ? 20 : 28;
        data.Check(0, headerSize);
        var count = data.U32(12);
        if (count is 0 or > 4096)
        {
            throw new InvalidDataException("VDEX DEX count exceeds the resource limit.");
        }
        Span<byte> sectionVersion = stackalloc byte[4];
        data.Read(8, sectionVersion);
        if (sectionVersion.SequenceEqual("000\0"u8))
        {
            var verifierStart = headerSize + count * 4L;
            var verifierEnd = verifierStart + data.U32(16);
            var extraSize = version == 21
                ? checked((long)data.U32(20) + data.U32(24))
                : 0;
            if (verifierEnd > input.Length || verifierEnd + extraSize != input.Length)
            {
                throw new InvalidDataException("VDEX verifier-only sections do not match the input length.");
            }
            return ([], verifierEnd + extraSize, 0, version);
        }
        if (!sectionVersion.SequenceEqual("002\0"u8))
        {
            throw new NotSupportedException("Unsupported VDEX DEX section version.");
        }
        var section = headerSize + count * 4L;
        data.Check(section, 12);
        var start = section + 12;
        var dexEnd = start + data.U32(section);
        var sharedEnd = dexEnd + data.U32(section + 4);
        var verifierSize = data.U32(16);
        var quickStart = sharedEnd + verifierSize;
        var quickSize = data.U32(section + 8);
        var tailSize = version == 21 ? (long)data.U32(20) + data.U32(24) : 0;
        if (quickStart + quickSize + tailSize != input.Length)
        {
            throw new InvalidDataException("VDEX 019/021 sections do not match the input length.");
        }
        var result = new List<VdexEntry>((int)count);
        Span<byte> magic = stackalloc byte[8];
        for (var i = 0; i < count; i++)
        {
            if ((start & 3) != 0 || start > dexEnd - 116)
            {
                throw new InvalidDataException("Invalid VDEX DEX preheader or alignment.");
            }
            var table = data.U32(start);
            start += 4;
            data.Read(start, magic);
            var compact = magic.SequenceEqual("cdex001\0"u8);
            var size = data.U32(start + 32);
            if (size < (compact ? 136 : 112) || size > dexEnd - start)
            {
                throw new InvalidDataException("DEX main section exceeds its VDEX section.");
            }
            long sharedOffset = 0;
            uint sharedSize = 0;
            if (compact)
            {
                sharedOffset = start + data.U32(start + 108);
                sharedSize = data.U32(start + 104);
                if (data.U32(start + 36) != 136 || data.U32(start + 40) != 0x12345678 ||
                    sharedOffset < dexEnd || sharedOffset > sharedEnd || sharedSize > sharedEnd - sharedOffset ||
                    data.U32(start + 128) > data.U32(start + 132) || data.U32(start + 132) > sharedSize ||
                    (data.U32(start + 112) & ~1u) != 0)
                {
                    throw new InvalidDataException("Invalid CompactDex header or shared data range.");
                }
            }
            else
            {
                new ArtData(input, start, size, token).ValidateDex(modern: true);
            }
            if (quickSize != 0)
            {
                _ = new CompactOffsetTable(new ArtData(input, quickStart, quickSize, token), table, data.U32(start + 88));
            }
            result.Add(new VdexEntry(start, size, data.U32(headerSize + 4L * i))
            {
                Compact = compact,
                SharedOffset = sharedOffset,
                SharedSize = sharedSize,
                QuickTable = table
            });
            start += size;
        }
        if (start != dexEnd)
        {
            throw new InvalidDataException("VDEX DEX size and count disagree.");
        }
        return (result, quickStart, quickSize, version);
    }

    private static (long Start, long End) ModernQuickBounds(ArtData quick, uint offset)
    {
        if (offset == 0)
        {
            return (0, 0);
        }
        long cursor = offset - 1L;
        var count = quick.Leb(ref cursor);
        quick.Check(cursor, count * 2L);
        return (cursor, cursor + count * 2L);
    }
}