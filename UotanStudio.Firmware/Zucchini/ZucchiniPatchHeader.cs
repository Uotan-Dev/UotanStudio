namespace UotanStudio.Firmware.Zucchini;

internal readonly struct ZucchiniPatchHeader
{
    internal ZucchiniPatchHeader(
        uint magic,
        uint version,
        uint oldSize,
        uint oldCrc,
        uint newSize,
        uint newCrc,
        uint numElements)
    {
        Magic = magic;
        Version = version;
        OldSize = oldSize;
        OldCrc = oldCrc;
        NewSize = newSize;
        NewCrc = newCrc;
        NumElements = numElements;
    }

    internal uint Magic { get; }

    internal uint Version { get; }

    internal uint OldSize { get; }

    internal uint OldCrc { get; }

    internal uint NewSize { get; }

    internal uint NewCrc { get; }

    internal uint NumElements { get; }
}
