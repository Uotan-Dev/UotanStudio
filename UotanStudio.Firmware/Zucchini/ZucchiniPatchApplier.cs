using System.IO.Hashing;

namespace UotanStudio.Firmware.Zucchini;

public static class ZucchiniPatchApplier
{
    /// <summary>
    /// Applies patch to oldData and returns a new byte array of exactly newSize bytes.
    /// <para>将 patch 应用到 oldData，返回恰好 newSize 字节的新字节数组。</para>
    /// </summary>
    public static byte[] Apply(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> patch, int newSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(newSize);
        var newData = new byte[newSize];
        Apply(oldData, patch, newData);
        return newData;
    }

    /// <summary>
    /// Applies patch to oldData, writing the new image into newData.
    /// <para>将 patch 应用到 oldData，把新镜像写入 newData。</para>
    /// </summary>
    public static void Apply(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> patch, Span<byte> newData)
    {
        var header = ZucchiniPatchReader.ReadHeader(patch);

        if ((uint)oldData.Length != header.OldSize)
        {
            throw new ZucchiniFormatException(
                $"Old image length {oldData.Length} does not match the patch old_size {header.OldSize}. " +
                $"旧镜像长度 {oldData.Length} 与补丁 old_size {header.OldSize} 不一致。");
        }

        if ((uint)newData.Length != header.NewSize)
        {
            throw new ZucchiniFormatException(
                $"New image length {newData.Length} does not match the patch new_size {header.NewSize}. " +
                $"新镜像长度 {newData.Length} 与补丁 new_size {header.NewSize} 不一致。");
        }

        VerifyCrc(oldData, header.OldCrc, oldImage: true);
        newData.Clear();

        var elements = ZucchiniPatchReader.ReadElements(patch, in header);
        foreach (var element in elements)
        {
            ApplyElement(in element, patch, oldData, newData);
        }

        VerifyCrc(newData, header.NewCrc, oldImage: false);
    }

    private static void ApplyElement(
        in ZucchiniPatchElement element,
        ReadOnlySpan<byte> patch,
        ReadOnlySpan<byte> oldData,
        Span<byte> newData)
    {
        if (element.NewExeType != ZucchiniExecutableType.Raw)
        {
            throw new ZucchiniUnsupportedExecutableTypeException((uint)element.NewExeType, element.NewOffset);
        }

        if (element.ReferenceDeltaLength != 0)
        {
            throw new ZucchiniFormatException(
                $"RAW Zucchini element at new offset {element.NewOffset} carries {element.ReferenceDeltaLength} reference-delta bytes; RAW elements must have no reference data. " +
                $"位于新偏移 {element.NewOffset} 的 RAW Zucchini 元素携带 {element.ReferenceDeltaLength} 字节引用增量；RAW 元素不得包含引用数据。");
        }

        ApplyEquivalenceAndExtra(in element, patch, oldData, newData);
        ApplyRawDelta(in element, patch, newData);
    }

    private static void ApplyEquivalenceAndExtra(
        in ZucchiniPatchElement element,
        ReadOnlySpan<byte> patch,
        ReadOnlySpan<byte> oldData,
        Span<byte> newData)
    {
        var equivalenceStream = patch.Slice(element.EquivalenceSourceOffset, checked((int)element.EquivalenceSourceLength));
        var extraStream = patch.Slice(element.ExtraDataOffset, checked((int)element.ExtraDataLength));

        long elemOldEnd = checked((long)element.OldOffset + element.OldSize);
        long elemNewEnd = checked((long)element.NewOffset + element.NewSize);
        if (elemOldEnd > oldData.Length || elemNewEnd > newData.Length)
        {
            throw new ZucchiniFormatException(
                $"Zucchini element at new offset {element.NewOffset} is outside the provided image buffers. " +
                $"位于新偏移 {element.NewOffset} 的 Zucchini 元素超出所提供的镜像缓冲区。");
        }


        long srcPos = element.OldOffset;
        long newPos = element.NewOffset;
        int extraPos = 0;
        int pos = 0;

        while (pos < equivalenceStream.Length)
        {
            int consumed = ZucchiniLeb128.TryRead(equivalenceStream, pos, out uint skip);
            if (consumed < 0)
            {
                throw new ZucchiniFormatException(
                    $"Invalid equivalence skip varint in the Zucchini element at new offset {element.NewOffset}. " +
                    $"位于新偏移 {element.NewOffset} 的 Zucchini 元素等价跳过 varint 无效。");
            }
            pos += consumed;

            consumed = ZucchiniLeb128.TryRead(equivalenceStream, pos, out uint copy);
            if (consumed < 0)
            {
                throw new ZucchiniFormatException(
                    $"Invalid equivalence copy varint in the Zucchini element at new offset {element.NewOffset}. " +
                    $"位于新偏移 {element.NewOffset} 的 Zucchini 元素等价拷贝 varint 无效。");
            }
            pos += consumed;

            if (copy == 0)
            {
                throw new ZucchiniFormatException(
                    $"Zucchini element at new offset {element.NewOffset} contains a zero-length equivalence copy. " +
                    $"位于新偏移 {element.NewOffset} 的 Zucchini 元素包含零长度等价拷贝。");
            }

            srcPos += skip;
            long dstPos = newPos + skip;
            if (srcPos > elemOldEnd || copy > elemOldEnd - srcPos)
            {
                throw new ZucchiniFormatException(
                    $"Zucchini equivalence source range [{srcPos}, {srcPos + copy}) exceeds the element old range [{element.OldOffset}, {elemOldEnd}). " +
                    $"Zucchini 等价源范围 [{srcPos}, {srcPos + copy}) 超出元素旧范围 [{element.OldOffset}, {elemOldEnd})。");
            }

            if (dstPos > elemNewEnd || copy > elemNewEnd - dstPos)
            {
                throw new ZucchiniFormatException(
                    $"Zucchini equivalence destination range [{dstPos}, {dstPos + copy}) exceeds the element new range [{element.NewOffset}, {elemNewEnd}). " +
                    $"Zucchini 等价目标范围 [{dstPos}, {dstPos + copy}) 超出元素新范围 [{element.NewOffset}, {elemNewEnd})。");
            }

            long gap = dstPos - newPos;
            if (gap < 0 || gap > extraStream.Length - extraPos)
            {
                throw new ZucchiniFormatException(
                    $"Zucchini element at new offset {element.NewOffset} has an invalid destination gap {gap} or insufficient extra data. " +
                    $"位于新偏移 {element.NewOffset} 的 Zucchini 元素存在非法目标空隙 {gap} 或附加数据不足。");
            }

            if (gap > 0)
            {
                extraStream.Slice(extraPos, (int)gap).CopyTo(newData.Slice((int)newPos, (int)gap));
                extraPos += (int)gap;
            }

            oldData.Slice((int)srcPos, (int)copy).CopyTo(newData.Slice((int)dstPos, (int)copy));


            srcPos += copy;
            newPos = dstPos + copy;
        }

        long remaining = elemNewEnd - newPos;
        if (remaining < 0)
        {
            throw new ZucchiniFormatException(
                $"Zucchini element at new offset {element.NewOffset} overran its new range. " +
                $"位于新偏移 {element.NewOffset} 的 Zucchini 元素超出其新范围。");
        }

        int remainingExtra = extraStream.Length - extraPos;
        if (remaining != remainingExtra)
        {
            throw new ZucchiniFormatException(
                $"Zucchini extra-data length {extraStream.Length} does not match the destination gaps ({remaining} bytes remain after the last equivalence) for the element at new offset {element.NewOffset}. " +
                $"位于新偏移 {element.NewOffset} 的 Zucchini 元素附加数据长度 {extraStream.Length} 与目标空隙（最后一个等价后剩余 {remaining} 字节）不一致。");
        }

        if (remaining > 0)
        {
            extraStream.Slice(extraPos, (int)remaining).CopyTo(newData.Slice((int)newPos, (int)remaining));
            newPos += remaining;
        }

        if (newPos != elemNewEnd)
        {
            throw new ZucchiniFormatException(
                $"Zucchini element at new offset {element.NewOffset} destination coverage ends at {newPos} instead of {elemNewEnd}. " +
                $"位于新偏移 {element.NewOffset} 的 Zucchini 元素目标覆盖结束于 {newPos}，应为 {elemNewEnd}。");
        }
    }

    private static void ApplyRawDelta(
        in ZucchiniPatchElement element,
        ReadOnlySpan<byte> patch,
        Span<byte> newData)
    {
        var rawStream = patch.Slice(element.RawDeltaOffset, checked((int)element.RawDeltaLength));


        long current = 0;
        int pos = 0;
        while (pos < rawStream.Length)
        {
            int consumed = ZucchiniLeb128.TryRead(rawStream, pos, out uint deltaDiff);
            if (consumed < 0)
            {
                throw new ZucchiniFormatException(
                    $"Invalid raw-delta offset varint in the Zucchini element at new offset {element.NewOffset}. " +
                    $"位于新偏移 {element.NewOffset} 的 Zucchini 元素原始差分流偏移 varint 无效。");
            }
            pos += consumed;

            if (pos >= rawStream.Length)
            {
                throw new ZucchiniFormatException(
                    $"Zucchini raw-delta stream ends before a correction byte for the element at new offset {element.NewOffset}. " +
                    $"位于新偏移 {element.NewOffset} 的 Zucchini 元素原始差分流在修正字节前结束。");
            }

            byte correction = rawStream[pos];
            pos += 1;

            current += deltaDiff;
            if (current < 0 || current >= newData.Length)
            {
                throw new ZucchiniFormatException(
                    $"Zucchini raw-delta offset {current} is outside the new image ({newData.Length} bytes) for the element at new offset {element.NewOffset}. " +
                    $"位于新偏移 {element.NewOffset} 的 Zucchini 元素原始差分流偏移 {current} 超出新镜像（{newData.Length} 字节）。");
            }

            newData[(int)current] ^= correction;
            current += 1;
        }
    }

    private static void VerifyCrc(ReadOnlySpan<byte> data, uint expected, bool oldImage)
    {
        var crc = new Crc32();
        crc.Append(data);
        uint actual = crc.GetCurrentHashAsUInt32();
        if (actual != expected)
        {
            throw new ZucchiniChecksumException(
                $"Zucchini {(oldImage ? "old" : "new")} image CRC-32 mismatch: expected 0x{expected:X8}, computed 0x{actual:X8}. " +
                $"Zucchini {(oldImage ? "旧" : "新")} 镜像 CRC-32 不匹配：期望 0x{expected:X8}，实际 0x{actual:X8}。");
        }
    }
}
