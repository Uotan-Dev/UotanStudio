namespace UotanStudio.Firmware.Zucchini;

internal readonly struct ZucchiniPatchElement
{
    internal ZucchiniPatchElement(
        uint oldOffset,
        uint oldSize,
        ZucchiniExecutableType oldExeType,
        uint newOffset,
        uint newSize,
        ZucchiniExecutableType newExeType,
        uint equivalenceSourceLength,
        uint extraDataLength,
        uint rawDeltaLength,
        uint referenceDeltaLength,
        int equivalenceSourceOffset,
        int extraDataOffset,
        int rawDeltaOffset,
        int referenceDeltaOffset)
    {
        OldOffset = oldOffset;
        OldSize = oldSize;
        OldExeType = oldExeType;
        NewOffset = newOffset;
        NewSize = newSize;
        NewExeType = newExeType;
        EquivalenceSourceLength = equivalenceSourceLength;
        ExtraDataLength = extraDataLength;
        RawDeltaLength = rawDeltaLength;
        ReferenceDeltaLength = referenceDeltaLength;
        EquivalenceSourceOffset = equivalenceSourceOffset;
        ExtraDataOffset = extraDataOffset;
        RawDeltaOffset = rawDeltaOffset;
        ReferenceDeltaOffset = referenceDeltaOffset;
    }

    internal uint OldOffset { get; }

    internal uint OldSize { get; }

    internal ZucchiniExecutableType OldExeType { get; }

    internal uint NewOffset { get; }

    internal uint NewSize { get; }

    internal ZucchiniExecutableType NewExeType { get; }

    internal uint EquivalenceSourceLength { get; }

    internal uint ExtraDataLength { get; }

    internal uint RawDeltaLength { get; }

    internal uint ReferenceDeltaLength { get; }

    internal int EquivalenceSourceOffset { get; }

    internal int ExtraDataOffset { get; }

    internal int RawDeltaOffset { get; }

    internal int ReferenceDeltaOffset { get; }
}
