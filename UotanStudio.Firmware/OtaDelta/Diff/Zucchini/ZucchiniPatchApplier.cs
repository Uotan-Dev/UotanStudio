using System.Buffers.Binary;
using UotanStudio.Hash.Crc32Ieee;

namespace UotanStudio.Firmware.OtaDelta.Diff.Zucchini;

/// <summary>Applies Chromium Zucchini ensemble patches containing NoOp elements. <para>应用含 NoOp 元素的 Chromium Zucchini 集合补丁。</para></summary>
internal static class ZucchiniPatchApplier
{
    /// <summary>Describes a caller-confirmed raw equivalence used by the limited Raw/NoOp writer.
    /// <para>描述受限 Raw/NoOp 写入器使用的调用方确认的原始等价区间。</para></summary>
    /// <param name="SourceOffset">Start offset of the run inside the old image. <para>该区段在旧镜像中的起始偏移。</para></param>
    /// <param name="DestinationOffset">Start offset of the run inside the new image; the writer requires it to be at or
    /// after the end of the previous run. <para>该区段在新镜像中的起始偏移；写入器要求它不小于前一段的结束位置。</para></param>
    /// <param name="Length">Byte length of the identical run, which must be non-zero and fit in both images.
    /// <para>相同字节段的长度，必须非零且在两个镜像内都不越界。</para></param>
    public readonly record struct Equivalence(uint SourceOffset, uint DestinationOffset, uint Length);

    /// <summary>
    /// Creates a standard Zucc patch from caller-confirmed raw equivalences. The writer emits only Raw/NoOp elements,
    /// never executable references or pools; executable-aware Chromium Zucchini generation is not provided.
    /// <para>根据调用方确认的原始等价区间创建标准 Zucc 补丁。写入器仅生成 Raw/NoOp 元素，绝不生成可执行引用或池；
    /// 不提供面向可执行文件的 Chromium Zucchini 生成。</para>
    /// </summary>
    /// <param name="oldData">Source image the patch is built against; its length and CRC32 are recorded in the header.
    /// <para>补丁所依据的源镜像；其长度与 CRC32 会记录在头部。</para></param>
    /// <param name="newData">Target image the patch must reproduce; bytes not covered by an equivalence are stored literally.
    /// <para>补丁需要重现的目标镜像；未被任何等价区间覆盖的字节按原样存入。</para></param>
    /// <param name="equivalences">Runs in list order, each with a non-zero length that fits both images and a destination start at or after the previous run's end; gaps between runs contribute literal bytes. <see langword="null"/> is rejected and an empty list turns the whole target into literal bytes.
    /// <para>按列表顺序处理的区段，每段长度非零、在两个镜像内都不越界，且起始偏移不早于前一段的结束位置；区段之间的空隙按字面字节写入。<see langword="null"/> 会被拒绝，空列表则把整个目标镜像写成字面字节。</para></param>
    /// <param name="cancellationToken">Checked before every run and before each copied or differenced byte; cancellation leaves no partial patch because the result is returned only at the end.
    /// <para>在处理每段之前以及每次复制或求差字节之前检查；由于结果只在末尾返回，取消不会留下半成品补丁。</para></param>
    /// <returns>Complete <c>Zucc</c> container: header with magic, version, both sizes and both CRC32 values, the Raw executable type, then the length-prefixed source-skip, destination-skip, length, extra-byte, delta-skip and delta-byte streams.
    /// <para>完整的 <c>Zucc</c> 容器：含魔数、版本、两个长度与两个 CRC32 的头部，Raw 可执行类型，随后是带长度前缀的 source-skip、destination-skip、length、extra、delta-skip 与 delta-byte 流。</para></returns>
    public static byte[] CreateRawPatch(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData,
        IReadOnlyList<Equivalence> equivalences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(equivalences);
        var sourceSkips = new List<byte>();
        var destinationSkips = new List<byte>();
        var lengths = new List<byte>();
        var extra = new List<byte>();
        var deltaSkips = new List<byte>();
        var deltaBytes = new List<byte>();
        uint sourceEnd = 0;
        uint destinationEnd = 0;
        uint copied = 0;
        uint previousDelta = 0;
        foreach (var equivalence in equivalences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (equivalence.Length == 0
                || equivalence.SourceOffset > (uint)oldData.Length
                || equivalence.Length > oldData.Length - equivalence.SourceOffset
                || equivalence.DestinationOffset < destinationEnd
                || equivalence.Length > newData.Length - equivalence.DestinationOffset)
            {
                throw new InvalidDataException("Zucchini equivalences must be ordered, contiguous and in range.");
            }

            var sourceDelta = checked((long)equivalence.SourceOffset - sourceEnd);
            ZucchiniLeb128.Write(EncodeSigned(sourceDelta), sourceSkips);
            ZucchiniLeb128.Write(equivalence.DestinationOffset - destinationEnd, destinationSkips);
            ZucchiniLeb128.Write(equivalence.Length, lengths);
            for (var index = destinationEnd; index < equivalence.DestinationOffset; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                extra.Add(newData[checked((int)index)]);
            }

            for (uint index = 0; index < equivalence.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = oldData[checked((int)(equivalence.SourceOffset + index))];
                var target = newData[checked((int)(equivalence.DestinationOffset + index))];
                var difference = unchecked((byte)(target - source));
                if (difference != 0)
                {
                    var absolute = checked(copied + index);
                    ZucchiniLeb128.Write(absolute - previousDelta, deltaSkips);
                    deltaBytes.Add(difference);
                    previousDelta = checked(absolute + 1);
                }
            }

            copied = checked(copied + equivalence.Length);
            sourceEnd = checked(equivalence.SourceOffset + equivalence.Length);
            destinationEnd = checked(equivalence.DestinationOffset + equivalence.Length);
        }

        for (var index = destinationEnd; index < newData.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            extra.Add(newData[checked((int)index)]);
        }

        var patch = new List<byte>(checked(32 + 54 + sourceSkips.Count + destinationSkips.Count
            + lengths.Count + extra.Count + deltaSkips.Count + deltaBytes.Count));
        WriteUInt32(ZucchiniConstants.Magic, patch);
        WriteUInt32(ZucchiniConstants.Version, patch);
        WriteUInt32(checked((uint)oldData.Length), patch);
        WriteUInt32(Crc32Ieee.Compute(oldData), patch);
        WriteUInt32(checked((uint)newData.Length), patch);
        WriteUInt32(Crc32Ieee.Compute(newData), patch);
        WriteUInt32(1, patch);
        WriteUInt32(0, patch);
        WriteUInt32(checked((uint)oldData.Length), patch);
        WriteUInt32(0, patch);
        WriteUInt32(checked((uint)newData.Length), patch);
        WriteUInt32((uint)ZucchiniExecutableType.Raw, patch);
        patch.Add(1);
        patch.Add(0);
        foreach (var stream in new[] { sourceSkips, destinationSkips, lengths, extra, deltaSkips, deltaBytes, new List<byte>() })
        {
            WriteUInt32(checked((uint)stream.Count), patch);
            patch.AddRange(stream);
        }
        WriteUInt32(0, patch);
        return [.. patch];
    }

    private static uint EncodeSigned(long value)
    {
        var encoded = checked((ulong)(value < 0 ? -value : value)) << 1;
        return checked((uint)(value < 0 ? encoded - 1 : encoded));
    }

    private static void WriteUInt32(uint value, List<byte> output)
    {
        output.Add((byte)value);
        output.Add((byte)(value >> 8));
        output.Add((byte)(value >> 16));
        output.Add((byte)(value >> 24));
    }

    /// <summary>Applies a patch to a bounded output buffer. <para>将补丁应用到有界输出缓冲区。</para></summary>
    /// <param name="oldData">
    /// Source image the patch was created against; its length must equal the size recorded in the patch header.
    /// <para>补丁创建时所依据的源镜像；其长度必须与补丁头部记录的大小一致。</para>
    /// </param>
    /// <param name="patch">
    /// Complete <c>Zucc</c> container, read from its first byte. <para>完整的 <c>Zucc</c> 容器，从首字节开始读取。</para>
    /// </param>
    /// <param name="newSize">
    /// Size in bytes of the image to produce, up to the <c>256</c> MiB buffer ceiling of this API; it must match the
    /// new size recorded in the patch.
    /// <para>要生成镜像的字节大小，不得超过本 API 的 <c>256</c> MiB 缓冲区上限；必须与补丁记录的新大小一致。</para>
    /// </param>
    /// <returns>
    /// A new array of exactly <paramref name="newSize"/> bytes holding the reconstructed image after both CRC
    /// checks passed. <para>长度恰为 <paramref name="newSize"/> 字节的新数组，保存两项 CRC 校验通过后的还原镜像。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="newSize"/> is negative. <para><paramref name="newSize"/> 为负数。</para>
    /// </exception>
    /// <exception cref="ZucchiniFormatException">
    /// <paramref name="newSize"/> exceeds <c>256</c> MiB, or the container is malformed, truncated, or does not
    /// cover the destination exactly. <para><paramref name="newSize"/> 超过 <c>256</c> MiB，或容器畸形、被截断，未能精确覆盖目标。</para>
    /// </exception>
    /// <exception cref="ZucchiniChecksumException">
    /// The CRC-32 of <paramref name="oldData"/> or of the result disagrees with the patch header.
    /// <para><paramref name="oldData"/> 或结果的 CRC-32 与补丁头部不一致。</para>
    /// </exception>
    /// <exception cref="ZucchiniUnsupportedExecutableTypeException">
    /// An element declares an executable type other than NoOp. <para>某个元素声明了 NoOp 之外的可执行类型。</para>
    /// </exception>
    public static byte[] Apply(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> patch, int newSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(newSize);
        if (newSize > 256 * 1024 * 1024) throw new ZucchiniFormatException("Zucchini buffer API is limited to 256 MiB.");
        var result = new byte[newSize]; Apply(oldData, patch, result); return result;
    }

    /// <summary>Applies a standard Zucc patch and verifies both CRCs. <para>应用标准 Zucc 补丁并验证源和目标 CRC。</para></summary>
    /// <param name="oldData">
    /// Source image whose length must equal the old size recorded in the patch; it is read, never modified.
    /// <para>长度必须与补丁记录的旧大小一致的源镜像；只被读取，不会被修改。</para>
    /// </param>
    /// <param name="patch">
    /// Complete <c>Zucc</c> container: magic, version, image sizes and CRCs, then the element table.
    /// <para>完整的 <c>Zucc</c> 容器：魔数、版本、镜像大小与 CRC，随后是元素表。</para>
    /// </param>
    /// <param name="newData">
    /// Destination buffer, whose length must equal the new size recorded in the patch and which the elements fill
    /// in strictly increasing destination order.
    /// <para>目标缓冲区，其长度必须与补丁记录的新大小一致，元素按目标偏移严格递增地将其填满。</para>
    /// </param>
    /// <exception cref="ZucchiniFormatException">
    /// The magic or major version is unsupported, an image size disagrees with the header, an element range or
    /// destination coverage is invalid, an element carries a reference pool, its version is not <c>1</c>, patch
    /// sub-streams are left over, or the container has trailing data.
    /// <para>魔数或主版本不受支持、镜像大小与头部不符、元素范围或目标覆盖非法、元素含引用池、元素版本不是 <c>1</c>、
    /// 补丁子流有残留，或容器尾部有多余数据。</para>
    /// </exception>
    /// <exception cref="ZucchiniChecksumException">
    /// The CRC-32 computed over <paramref name="oldData"/> or over the finished <paramref name="newData"/> disagrees
    /// with the header. <para>对 <paramref name="oldData"/> 或完成的 <paramref name="newData"/> 计算的 CRC-32 与头部不一致。</para>
    /// </exception>
    /// <exception cref="ZucchiniUnsupportedExecutableTypeException">
    /// An element declares an executable type other than NoOp; the offset of that element is reported.
    /// <para>某个元素声明了 NoOp 之外的可执行类型；会上报该元素的偏移。</para>
    /// </exception>
    public static void Apply(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> patch, Span<byte> newData)
    {
        var reader = new Reader(patch);
        if (reader.UInt32() != ZucchiniConstants.Magic) throw new ZucchiniFormatException("Invalid Zucchini Zucc magic.");
        var version = reader.UInt32();
        if ((version & 0xffff) != ZucchiniConstants.Version) throw new ZucchiniFormatException("Unsupported Zucchini major version.");
        var oldSize = reader.UInt32(); var oldCrc = reader.UInt32();
        var newSize = reader.UInt32(); var newCrc = reader.UInt32();
        if (oldSize != oldData.Length || newSize != newData.Length) throw new ZucchiniFormatException("Zucchini image sizes do not match the patch.");
        VerifyCrc(oldData, oldCrc, true);
        var elements = reader.UInt32();
        if (elements > reader.Remaining / 54) throw new ZucchiniFormatException("Zucchini element count exceeds patch bounds.");
        uint nextDestination = 0;
        for (uint i = 0; i < elements; i++)
        {
            var oldOffset = reader.UInt32(); var oldLength = reader.UInt32();
            var newOffset = reader.UInt32(); var newLength = reader.UInt32();
            var executable = reader.UInt32(); var elementVersion = reader.UInt16();
            if (oldLength == 0 || newLength == 0 || oldOffset > oldSize || oldLength > oldSize - oldOffset ||
                newOffset != nextDestination || newOffset > newSize || newLength > newSize - newOffset)
                throw new ZucchiniFormatException("Invalid Zucchini element range or destination coverage.");
            if (executable != (uint)ZucchiniExecutableType.Raw)
                throw new ZucchiniUnsupportedExecutableTypeException(executable, newOffset);
            if (elementVersion != 1) throw new ZucchiniFormatException("Unsupported NoOp element version.");
            var srcSkips = new Reader(reader.Buffer());
            var dstSkips = new Reader(reader.Buffer());
            var lengths = new Reader(reader.Buffer());
            var extra = new Reader(reader.Buffer());
            var deltaSkips = new Reader(reader.Buffer());
            var deltaDiffs = new Reader(reader.Buffer());
            var referenceDeltas = reader.Buffer();
            var pools = reader.UInt32();
            if (!referenceDeltas.IsEmpty || pools != 0) throw new ZucchiniFormatException("NoOp elements cannot contain reference pools.");
            var oldElement = oldData.Slice(checked((int)oldOffset), checked((int)oldLength));
            var newElement = newData.Slice(checked((int)newOffset), checked((int)newLength));
            var copies = new List<(int Destination, int Length)>();
            long previousSource = 0; var previousDestination = 0;
            while (lengths.Remaining > 0)
            {
                var encodedLength = lengths.VarUInt();
                if (encodedLength > newElement.Length) throw new ZucchiniFormatException("Zucchini copy length exceeds the element.");
                var length = (int)encodedLength;
                var signed = srcSkips.VarUInt();
                var sourceSkip = (long)(signed >> 1) ^ -(long)(signed & 1);
                var sourceOffset = checked(previousSource + sourceSkip);
                var destinationPosition = (long)previousDestination + dstSkips.VarUInt();
                if (destinationPosition > newElement.Length) throw new ZucchiniFormatException("Zucchini destination skip exceeds the element.");
                var destinationOffset = (int)destinationPosition;
                if (length <= 0 || sourceOffset < 0 || sourceOffset > oldElement.Length || length > oldElement.Length - sourceOffset ||
                    destinationOffset > newElement.Length || length > newElement.Length - destinationOffset)
                    throw new ZucchiniFormatException("Invalid Zucchini equivalence range.");
                extra.Take(destinationOffset - previousDestination).CopyTo(newElement[previousDestination..]);
                oldElement.Slice((int)sourceOffset, length).CopyTo(newElement[destinationOffset..]);
                copies.Add((destinationOffset, length));
                previousSource = sourceOffset + length; previousDestination = destinationOffset + length;
            }
            extra.Take(newElement.Length - previousDestination).CopyTo(newElement[previousDestination..]);
            if (srcSkips.Remaining != 0 || dstSkips.Remaining != 0 || extra.Remaining != 0)
                throw new ZucchiniFormatException("Unused Zucchini equivalence or extra data.");
            long copyOffset = 0; long precedingCopyLength = 0; var copyIndex = 0;
            while (deltaSkips.Remaining > 0)
            {
                copyOffset = checked(copyOffset + deltaSkips.VarUInt());
                var difference = deltaDiffs.Take(1)[0];
                if (difference == 0) throw new ZucchiniFormatException("Zero Zucchini raw delta.");
                while (copyIndex < copies.Count && copyOffset - precedingCopyLength >= copies[copyIndex].Length)
                    precedingCopyLength += copies[copyIndex++].Length;
                if (copyIndex == copies.Count) throw new ZucchiniFormatException("Raw delta exceeds copied bytes.");
                var offset = checked(copies[copyIndex].Destination + (int)(copyOffset - precedingCopyLength));
                newElement[offset] = unchecked((byte)(newElement[offset] + difference));
                copyOffset++;
            }
            if (deltaDiffs.Remaining != 0) throw new ZucchiniFormatException("Unused Zucchini raw deltas.");
            nextDestination = checked(newOffset + newLength);
        }
        if (reader.Remaining != 0 || nextDestination != newSize) throw new ZucchiniFormatException("Zucchini patch has trailing data or incomplete destination coverage.");
        VerifyCrc(newData, newCrc, false);
    }

    private static void VerifyCrc(ReadOnlySpan<byte> data, uint expected, bool oldImage)
    {
        var actual = Crc32Ieee.Compute(data);
        if (actual != expected) throw new ZucchiniChecksumException($"Zucchini {(oldImage ? "old" : "new")} image CRC mismatch: expected {expected:X8}, found {actual:X8}.");
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _data = data;
        internal int Remaining => _data.Length;
        internal ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _data.Length) throw new ZucchiniFormatException("Truncated Zucchini data.");
            var result = _data[..count]; _data = _data[count..]; return result;
        }
        internal uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        internal ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        internal ReadOnlySpan<byte> Buffer()
        {
            var length = UInt32();
            if (length > Remaining) throw new ZucchiniFormatException("Zucchini stream length exceeds the patch.");
            return Take((int)length);
        }
        internal uint VarUInt()
        {
            uint result = 0;
            for (var shift = 0; shift <= 28; shift += 7)
            {
                var value = Take(1)[0];
                if (shift == 28 && value > 15) throw new ZucchiniFormatException("Zucchini varint overflow.");
                result |= (uint)(value & 127) << shift;
                if ((value & 128) == 0) return result;
            }
            throw new ZucchiniFormatException("Invalid Zucchini varint.");
        }
    }
}