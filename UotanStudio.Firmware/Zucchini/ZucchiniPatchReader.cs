using System.Buffers.Binary;

namespace UotanStudio.Firmware.Zucchini;

/// <summary>
/// Parses and validates the fixed header and the element list of a Zucchini patch. The applier is
/// the public entry point; this reader performs all defensive bounds checks so malformed input fails
/// fast with a <see cref="ZucchiniFormatException"/> (or <see cref="ZucchiniUnsupportedExecutableTypeException"/>
/// for non-RAW elements).
/// <para>解析并校验 Zucchini 补丁的定长头部与元素列表。应用器是公开入口；本读取器执行全部
/// 防御性边界校验，使畸形输入快速失败并抛出 <see cref="ZucchiniFormatException"/>
/// （非 RAW 元素则抛出 <see cref="ZucchiniUnsupportedExecutableTypeException"/>）。</para>
/// </summary>
internal static class ZucchiniPatchReader
{
    /// <summary>
    /// Reads and validates the fixed patch header.
    /// <para>读取并校验定长补丁头部。</para>
    /// </summary>
    /// <param name="patch">The patch buffer. <para>补丁缓冲区。</para></param>
    /// <returns>The validated header. <para>校验通过的头部。</para></returns>
    /// <exception cref="ZucchiniFormatException">The patch is shorter than the header, the magic does
    /// not match, or the version is unsupported.
    /// <para>补丁短于头部、魔数不匹配或版本不受支持时抛出。</para></exception>
    public static ZucchiniPatchHeader ReadHeader(ReadOnlySpan<byte> patch)
    {
        if (patch.Length < ZucchiniConstants.HeaderByteSize)
        {
            throw new ZucchiniFormatException(
                $"Zucchini patch is too short: {patch.Length} bytes; the fixed header requires {ZucchiniConstants.HeaderByteSize} bytes. " +
                $"Zucchini 补丁过短：{patch.Length} 字节；定长头部需要 {ZucchiniConstants.HeaderByteSize} 字节。");
        }

        var header = new ZucchiniPatchHeader(
            magic: ReadUInt32(patch, 0),
            version: ReadUInt32(patch, 4),
            oldSize: ReadUInt32(patch, 8),
            oldCrc: ReadUInt32(patch, 12),
            newSize: ReadUInt32(patch, 16),
            newCrc: ReadUInt32(patch, 20),
            numElements: ReadUInt32(patch, 24));

        if (header.Magic != ZucchiniConstants.Magic)
        {
            throw new ZucchiniFormatException(
                $"Zucchini magic mismatch: expected 0x{ZucchiniConstants.Magic:X8} (\"ZcP\\x03\" little-endian), found 0x{header.Magic:X8}. " +
                $"Zucchini 魔数不匹配：期望 0x{ZucchiniConstants.Magic:X8}，实际 0x{header.Magic:X8}。");
        }

        if (header.Version != ZucchiniConstants.Version)
        {
            throw new ZucchiniFormatException(
                $"Zucchini format version {header.Version} is not supported; expected {ZucchiniConstants.Version}. " +
                $"Zucchini 格式版本 {header.Version} 不受支持；期望 {ZucchiniConstants.Version}。");
        }

        return header;
    }

    /// <summary>
    /// Reads and validates the element list. Non-RAW elements are rejected with
    /// <see cref="ZucchiniUnsupportedExecutableTypeException"/> as soon as their fixed header is read,
    /// because the reference-bytes region that follows is only meaningful for disassembly-aware types
    /// and cannot be located reliably without full reference support.
    /// <para>读取并校验元素列表。非 RAW 元素在读取其定长头部后立即以
    /// <see cref="ZucchiniUnsupportedExecutableTypeException"/> 拒绝，因为其后的引用字节区仅对
    /// 反汇编感知类型有意义，在缺乏完整引用支持时无法可靠定位。</para>
    /// </summary>
    /// <param name="patch">The patch buffer. <para>补丁缓冲区。</para></param>
    /// <param name="header">The validated header. <para>已校验的头部。</para></param>
    /// <returns>The parsed elements. <para>解析出的元素列表。</para></returns>
    /// <exception cref="ZucchiniFormatException">An element is truncated or has out-of-bounds ranges.
    /// <para>元素被截断或范围越界时抛出。</para></exception>
    /// <exception cref="ZucchiniUnsupportedExecutableTypeException">An element is not RAW.
    /// <para>元素不是 RAW 时抛出。</para></exception>
    public static ZucchiniPatchElement[] ReadElements(ReadOnlySpan<byte> patch, in ZucchiniPatchHeader header)
    {
        if (header.NumElements == 0)
        {
            return [];
        }

        if (header.NumElements > (ulong)patch.Length / ZucchiniConstants.ElementHeaderByteSize)
        {
            throw new ZucchiniFormatException(
                $"Zucchini patch declares {header.NumElements} elements but is only {patch.Length} bytes. " +
                $"Zucchini 补丁声明 {header.NumElements} 个元素，但仅有 {patch.Length} 字节。");
        }

        var elements = new ZucchiniPatchElement[header.NumElements];
        int pos = ZucchiniConstants.HeaderByteSize;
        for (uint i = 0; i < header.NumElements; i++)
        {
            elements[i] = ReadElement(patch, in header, ref pos);
        }

        return elements;
    }

    private static ZucchiniPatchElement ReadElement(ReadOnlySpan<byte> patch, in ZucchiniPatchHeader header, ref int pos)
    {
        if (pos < 0 || pos > patch.Length - ZucchiniConstants.ElementHeaderByteSize)
        {
            throw new ZucchiniFormatException(
                $"Zucchini element at patch offset {pos} exceeds the patch length {patch.Length}. " +
                $"位于补丁偏移 {pos} 的 Zucchini 元素超出补丁长度 {patch.Length}。");
        }

        uint oldOffset = ReadUInt32(patch, pos + 0);
        uint oldSize = ReadUInt32(patch, pos + 4);
        uint oldExeType = ReadUInt32(patch, pos + 8);
        uint newOffset = ReadUInt32(patch, pos + 12);
        uint newSize = ReadUInt32(patch, pos + 16);
        uint newExeType = ReadUInt32(patch, pos + 20);
        uint equivalenceSourceLength = ReadUInt32(patch, pos + 24);
        uint extraDataLength = ReadUInt32(patch, pos + 28);
        uint rawDeltaLength = ReadUInt32(patch, pos + 32);
        uint referenceDeltaLength = ReadUInt32(patch, pos + 36);

        if (oldOffset > header.OldSize || oldSize > header.OldSize - oldOffset)
        {
            throw new ZucchiniFormatException(
                $"Zucchini element old range [{oldOffset}, {oldOffset + oldSize}) exceeds the old image size {header.OldSize}. " +
                $"Zucchini 元素旧范围 [{oldOffset}, {oldOffset + oldSize}) 超出旧镜像大小 {header.OldSize}。");
        }

        if (newOffset > header.NewSize || newSize > header.NewSize - newOffset)
        {
            throw new ZucchiniFormatException(
                $"Zucchini element new range [{newOffset}, {newOffset + newSize}) exceeds the new image size {header.NewSize}. " +
                $"Zucchini 元素新范围 [{newOffset}, {newOffset + newSize}) 超出新镜像大小 {header.NewSize}。");
        }

        if (oldExeType != newExeType)
        {
            throw new ZucchiniFormatException(
                $"Zucchini element old executable type {oldExeType} differs from its new executable type {newExeType} at new offset {newOffset}. " +
                $"位于新偏移 {newOffset} 的 Zucchini 元素旧可执行类型 {oldExeType} 与新可执行类型 {newExeType} 不一致。");
        }

        if ((ZucchiniExecutableType)newExeType != ZucchiniExecutableType.Raw)
        {
            throw new ZucchiniUnsupportedExecutableTypeException(newExeType, newOffset);
        }

        if (referenceDeltaLength != 0)
        {
            throw new ZucchiniFormatException(
                $"RAW Zucchini element at new offset {newOffset} declares a {referenceDeltaLength}-byte reference delta stream; RAW elements must not carry reference data. " +
                $"位于新偏移 {newOffset} 的 RAW Zucchini 元素声明了 {referenceDeltaLength} 字节引用增量流；RAW 元素不得携带引用数据。");
        }

        // The reference-bytes region is always empty for RAW elements (no disassembly, no references),
        // so the four data streams begin immediately after the fixed element header.
        ulong total;
        try
        {
            total = checked(
                (ulong)ZucchiniConstants.ElementHeaderByteSize +
                equivalenceSourceLength +
                extraDataLength +
                rawDeltaLength +
                referenceDeltaLength);
        }
        catch (OverflowException ex)
        {
            throw new ZucchiniFormatException("Zucchini element data-stream lengths overflow.", ex);
        }

        if (total > (ulong)(patch.Length - pos))
        {
            throw new ZucchiniFormatException(
                $"Zucchini element at patch offset {pos} needs {total} bytes but only {patch.Length - pos} remain in the patch. " +
                $"位于补丁偏移 {pos} 的 Zucchini 元素需要 {total} 字节，但补丁仅剩 {patch.Length - pos} 字节。");
        }

        int dataStart = pos + ZucchiniConstants.ElementHeaderByteSize;
        int equivalenceSourceOffset = dataStart;
        int extraDataOffset = checked(dataStart + (int)equivalenceSourceLength);
        int rawDeltaOffset = checked(extraDataOffset + (int)extraDataLength);
        int referenceDeltaOffset = checked(rawDeltaOffset + (int)rawDeltaLength);

        pos += (int)total;

        return new ZucchiniPatchElement(
            oldOffset,
            oldSize,
            (ZucchiniExecutableType)oldExeType,
            newOffset,
            newSize,
            (ZucchiniExecutableType)newExeType,
            equivalenceSourceLength,
            extraDataLength,
            rawDeltaLength,
            referenceDeltaLength,
            equivalenceSourceOffset,
            extraDataOffset,
            rawDeltaOffset,
            referenceDeltaOffset);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> patch, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(patch.Slice(offset));
}