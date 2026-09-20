using System.Buffers.Binary;
using System.IO.Hashing;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.Zucchini;

/// <summary>Applies Chromium Zucchini ensemble patches containing NoOp elements. <para>应用含 NoOp 元素的 Chromium Zucchini 集合补丁。</para></summary>
public static class ZucchiniPatchApplier
{
    /// <summary>Applies a patch to a bounded output buffer. <para>将补丁应用到有界输出缓冲区。</para></summary>
    public static byte[] Apply(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> patch, int newSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(newSize);
        if (newSize > 256 * 1024 * 1024) throw new ZucchiniFormatException("Zucchini buffer API is limited to 256 MiB.");
        var result = new byte[newSize]; Apply(oldData, patch, result); return result;
    }

    /// <summary>Applies a standard Zucc patch and verifies both CRCs. <para>应用标准 Zucc 补丁并验证源和目标 CRC。</para></summary>
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
            long previousSource = 0; int previousDestination = 0;
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
            long copyOffset = 0; long precedingCopyLength = 0; int copyIndex = 0;
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
        var actual = Crc32.HashToUInt32(data);
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