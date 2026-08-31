namespace UotanStudio.Firmware.Zucchini;

/// <summary>
/// A parsed Zucchini patch element: an old/new element pair, four data-stream lengths, and the
/// absolute offsets of the four data streams inside the patch buffer. Offsets are filled in by
/// <see cref="ZucchiniPatchReader"/> after bounds validation, so the applier can slice the streams
/// without re-parsing.
/// <para>已解析的 Zucchini 补丁元素：一对旧/新元素、四个数据流长度，以及四个数据流在补丁
/// 缓冲区内的绝对偏移。偏移由 <see cref="ZucchiniPatchReader"/> 完成边界校验后填入，
/// 应用器可直接切片数据流而无需重新解析。</para>
/// </summary>
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

    /// <summary>The offset of the element in the old image. <para>元素在旧镜像中的偏移。</para></summary>
    internal uint OldOffset { get; }

    /// <summary>The size in bytes of the element in the old image. <para>元素在旧镜像中的字节数。</para></summary>
    internal uint OldSize { get; }

    /// <summary>The executable type of the old element. <para>旧元素的可执行类型。</para></summary>
    internal ZucchiniExecutableType OldExeType { get; }

    /// <summary>The offset of the element in the new image. <para>元素在新镜像中的偏移。</para></summary>
    internal uint NewOffset { get; }

    /// <summary>The size in bytes of the element in the new image. <para>元素在新镜像中的字节数。</para></summary>
    internal uint NewSize { get; }

    /// <summary>The executable type of the new element. <para>新元素的可执行类型。</para></summary>
    internal ZucchiniExecutableType NewExeType { get; }

    /// <summary>The length in bytes of the equivalence-source stream. <para>等价源数据流长度。</para></summary>
    internal uint EquivalenceSourceLength { get; }

    /// <summary>The length in bytes of the extra-data stream. <para>附加数据流长度。</para></summary>
    internal uint ExtraDataLength { get; }

    /// <summary>The length in bytes of the raw-delta stream. <para>原始差分流长度。</para></summary>
    internal uint RawDeltaLength { get; }

    /// <summary>The length in bytes of the reference-delta stream (always zero for RAW). <para>引用增量流长度（RAW 恒为零）。</para></summary>
    internal uint ReferenceDeltaLength { get; }

    /// <summary>The absolute offset of the equivalence-source stream in the patch buffer. <para>等价源数据流在补丁缓冲区中的绝对偏移。</para></summary>
    internal int EquivalenceSourceOffset { get; }

    /// <summary>The absolute offset of the extra-data stream in the patch buffer. <para>附加数据流在补丁缓冲区中的绝对偏移。</para></summary>
    internal int ExtraDataOffset { get; }

    /// <summary>The absolute offset of the raw-delta stream in the patch buffer. <para>原始差分流在补丁缓冲区中的绝对偏移。</para></summary>
    internal int RawDeltaOffset { get; }

    /// <summary>The absolute offset of the reference-delta stream in the patch buffer. <para>引用增量流在补丁缓冲区中的绝对偏移。</para></summary>
    internal int ReferenceDeltaOffset { get; }
}
