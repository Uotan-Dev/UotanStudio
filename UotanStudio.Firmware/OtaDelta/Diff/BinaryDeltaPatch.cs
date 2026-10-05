using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.OtaDelta.Diff;

internal static class BinaryDeltaPatch
{
    internal static long ReadOutputLength(Stream patch)
    {
        var position = patch.Position;
        Span<byte> header = stackalloc byte[32];
        patch.ReadExactly(header);
        patch.Position = position;
        ValidateMagic(header);
        var length = ReadNumber(header[24..]);
        if (length < 0) throw new InvalidDataException("Negative binary patch output length.");
        return length;
    }

    internal static void Apply(Stream source, Stream patch, Stream output, long maximumOutputBytes, CancellationToken ct = default)
    {
        var start = patch.Position;
        Span<byte> header = stackalloc byte[32];
        patch.ReadExactly(header);
        ValidateMagic(header);
        var controlLength = ReadNumber(header[8..16]);
        var diffLength = ReadNumber(header[16..24]);
        var outputLength = ReadNumber(header[24..32]);
        if (controlLength < 0 || diffLength < 0 || outputLength < 0 || outputLength > maximumOutputBytes)
            throw new InvalidDataException("Invalid binary patch lengths or output limit exceeded.");
        var available = patch.Length - patch.Position;
        if (controlLength > available || diffLength > available - controlLength)
            throw new InvalidDataException("Binary patch streams exceed the patch length.");
        using var controlRange = new BoundedReadStream(patch, start + 32, controlLength, leaveOpen: true);
        using var diffRange = new BoundedReadStream(patch, start + 32 + controlLength, diffLength, leaveOpen: true);
        using var extraRange = new BoundedReadStream(patch, start + 32 + controlLength + diffLength, available - controlLength - diffLength, leaveOpen: true);
        var extended = header[..5].SequenceEqual("BSDF2"u8);
        using var control = Decoder(controlRange, extended ? header[5] : (byte)1);
        using var diff = Decoder(diffRange, extended ? header[6] : (byte)1);
        using var extra = Decoder(extraRange, extended ? header[7] : (byte)1);
        using var boundedOutput = new CountingWriteStream(output, outputLength, leaveOpen: true, cancellationToken: ct);
        var deltaBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        var oldBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(deltaBuffer.Length);
        try
        {
            Span<byte> tuple = stackalloc byte[24];
            long oldPosition = 0;
            long newPosition = 0;
            long controlEntries = 0;
            while (newPosition < outputLength)
            {
                ct.ThrowIfCancellationRequested();
                if (++controlEntries > Math.Max(outputLength, 1024 * 1024)) throw new InvalidDataException("Binary patch control-entry limit exceeded.");
                control.ReadExactly(tuple);
                var addLength = ReadNumber(tuple[..8]);
                var extraLength = ReadNumber(tuple[8..16]);
                var adjustment = ReadNumber(tuple[16..24]);
                if (addLength < 0 || extraLength < 0 || addLength > outputLength - newPosition ||
                    extraLength > outputLength - newPosition - addLength)
                    throw new InvalidDataException("Invalid binary patch control tuple.");
                long remaining = addLength;
                while (remaining > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(remaining, deltaBuffer.Length);
                    diff.ReadExactly(deltaBuffer.AsSpan(0, count));
                    Array.Clear(oldBuffer, 0, count);
                    var end = checked(oldPosition + count);
                    var validStart = Math.Max(0, oldPosition);
                    var validEnd = Math.Min(source.Length, end);
                    if (validStart < validEnd)
                    {
                        source.Position = validStart;
                        source.ReadExactly(oldBuffer.AsSpan(checked((int)(validStart - oldPosition)), checked((int)(validEnd - validStart))));
                    }
                    AddBytesUnchecked(deltaBuffer.AsSpan(0, count), oldBuffer.AsSpan(0, count));
                    boundedOutput.Write(deltaBuffer, 0, count);
                    oldPosition = end;
                    remaining -= count;
                }
                remaining = extraLength;
                while (remaining > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(remaining, deltaBuffer.Length);
                    extra.ReadExactly(deltaBuffer.AsSpan(0, count));
                    boundedOutput.Write(deltaBuffer, 0, count);
                    remaining -= count;
                }
                oldPosition = checked(oldPosition + adjustment);
                newPosition += addLength + extraLength;
            }
            if (control.ReadByte() != -1 || diff.ReadByte() != -1 || extra.ReadByte() != -1)
                throw new InvalidDataException("Binary patch contains unused decompressed data.");
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(deltaBuffer);
            System.Buffers.ArrayPool<byte>.Shared.Return(oldBuffer);
        }
    }

    /// <summary>
    /// Adds two byte spans with wraparound semantics, vectorized via <see cref="System.Numerics.Vector{T}"/>.
    /// <para>按字节回绕语义相加两个 Span，经 <see cref="System.Numerics.Vector{T}"/> 向量化。</para>
    /// </summary>
    private static void AddBytesUnchecked(Span<byte> target, ReadOnlySpan<byte> addend)
    {
        if (System.Numerics.Vector.IsHardwareAccelerated && target.Length >= System.Numerics.Vector<byte>.Count)
        {
            var width = System.Numerics.Vector<byte>.Count;
            var i = 0;
            for (; i <= target.Length - width; i += width)
            {
                System.Numerics.Vector.Add(
                    new System.Numerics.Vector<byte>(target.Slice(i, width)),
                    new System.Numerics.Vector<byte>(addend.Slice(i, width)))
                    .CopyTo(target.Slice(i, width));
            }
            for (; i < target.Length; i++)
            {
                target[i] = unchecked((byte)(target[i] + addend[i]));
            }
            return;
        }

        for (var i = 0; i < target.Length; i++)
        {
            target[i] = unchecked((byte)(target[i] + addend[i]));
        }
    }

    internal static void Create(Stream source, Stream target, Stream output, CancellationToken ct = default)
    {
        using var control = CreateTemporaryStream();
        using var diff = CreateTemporaryStream();
        using var extra = CreateTemporaryStream();
        var commonLength = Math.Min(source.Length, target.Length);
        using (var encoder = BZip2Stream.Create(control, CompressionMode.Compress, decompressConcatenated: false, leaveOpen: true))
        {
            Span<byte> tuple = stackalloc byte[24];
            WriteNumber(tuple[..8], commonLength);
            WriteNumber(tuple[8..16], target.Length - commonLength);
            WriteNumber(tuple[16..24], 0);
            if (target.Length > 0) encoder.Write(tuple);
        }
        source.Position = 0;
        target.Position = 0;
        var oldBuffer = new byte[64 * 1024];
        var newBuffer = new byte[oldBuffer.Length];
        using (var encoder = BZip2Stream.Create(diff, CompressionMode.Compress, decompressConcatenated: false, leaveOpen: true))
        {
            long remaining = commonLength;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(remaining, newBuffer.Length);
                source.ReadExactly(oldBuffer.AsSpan(0, count));
                target.ReadExactly(newBuffer.AsSpan(0, count));
                for (var i = 0; i < count; i++) newBuffer[i] = unchecked((byte)(newBuffer[i] - oldBuffer[i]));
                encoder.Write(newBuffer, 0, count);
                remaining -= count;
            }
        }
        using (var encoder = BZip2Stream.Create(extra, CompressionMode.Compress, decompressConcatenated: false, leaveOpen: true))
            CopyExactly(target, encoder, target.Length - commonLength, ct);
        Span<byte> header = stackalloc byte[32];
        "BSDIFF40"u8.CopyTo(header);
        WriteNumber(header[8..16], control.Length);
        WriteNumber(header[16..24], diff.Length);
        WriteNumber(header[24..32], target.Length);
        output.Write(header);
        foreach (var stream in new[] { control, diff, extra })
        {
            stream.Position = 0;
            CopyExactly(stream, output, stream.Length, ct);
        }
    }

    internal static FileStream CreateTemporaryStream() => new(
        Path.Combine(Path.GetTempPath(), $"uotan-delta-{Guid.NewGuid():N}.tmp"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);

    internal static void CopyExactly(Stream source, Stream target, long length, CancellationToken ct = default)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (length > 0)
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(length, buffer.Length);
                source.ReadExactly(buffer.AsSpan(0, count));
                target.Write(buffer, 0, count);
                length -= count;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static long ReadNumber(ReadOnlySpan<byte> bytes)
    {
        ulong value = (uint)(bytes[7] & 0x7f);
        for (var i = 6; i >= 0; i--) value = (value << 8) | bytes[i];
        return (bytes[7] & 0x80) != 0 ? -(long)value : (long)value;
    }

    private static void ValidateMagic(ReadOnlySpan<byte> header)
    {
        if (!header[..8].SequenceEqual("BSDIFF40"u8) && !header[..5].SequenceEqual("BSDF2"u8))
            throw new InvalidDataException("Unsupported binary patch header; expected BSDIFF40 or BSDF2.");
    }

    private static Stream Decoder(Stream stream, byte codec) => codec switch
    {
        1 => BZip2Stream.Create(stream, CompressionMode.Decompress, decompressConcatenated: false, leaveOpen: true),
        2 => new StrictBrotliReadStream(stream),
        _ => throw new InvalidDataException($"Unsupported BSDF2 stream codec: {codec}")
    };

    private static void WriteNumber(Span<byte> bytes, long value)
    {
        var magnitude = (ulong)Math.Abs(value);
        for (var i = 0; i < 8; i++) { bytes[i] = (byte)magnitude; magnitude >>= 8; }
        if (value < 0) bytes[7] |= 0x80;
    }
}