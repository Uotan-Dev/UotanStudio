using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Compress.Codecs.Lz4;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.OtaDelta.Diff;

internal static class Lz4DiffApplier
{
    private const int MaximumBytes = 256 * 1024 * 1024;
    private sealed record Block(int Offset, int Length, int StoredLength, byte[] Hash, byte[] Patch);
    private sealed record Info(int Algorithm, int Level, bool ZeroPadding, List<Block> Blocks);

    internal static byte[] Apply(ReadOnlySpan<byte> source, ReadOnlySpan<byte> patch, int expectedLength)
    {
        var output = new byte[expectedLength];
        Apply(source, patch, output);
        return output;
    }

    internal static void Apply(ReadOnlySpan<byte> source, ReadOnlySpan<byte> patch, Span<byte> destination)
    {
        if (patch.Length < 16 || !patch[0..7].SequenceEqual("LZ4DIFF"u8) || BinaryPrimitives.ReadUInt32BigEndian(patch[7..11]) != 1)
            throw new InvalidDataException("Invalid LZ4DIFF header.");
        var headerLength = BinaryPrimitives.ReadUInt32BigEndian(patch[11..15]);
        if (headerLength > 16 * 1024 * 1024 || headerLength > patch.Length - 16) throw new InvalidDataException("Invalid LZ4DIFF protobuf length.");
        using var proto = new CodedInputStream(patch.Slice(16, (int)headerLength).ToArray());
        Info? sourceInfo = null, targetInfo = null; var innerType = 0; uint tag;
        while ((tag = proto.ReadTag()) != 0)
        {
            if (tag == 10) sourceInfo = ReadInfo(proto.ReadBytes().ToByteArray());
            else if (tag == 18) targetInfo = ReadInfo(proto.ReadBytes().ToByteArray());
            else if (tag == 24) innerType = proto.ReadInt32(); else proto.SkipLastField();
        }
        if (sourceInfo is null || targetInfo is null) throw new InvalidDataException("Missing LZ4DIFF compression info.");
        var sourceSize = ValidateBlocks(sourceInfo); var targetSize = ValidateBlocks(targetInfo);
        var expectedLength = destination.Length;
        var plainSource = ArrayPool<byte>.Shared.Rent(Math.Max(sourceSize, 1));
        var plainTarget = ArrayPool<byte>.Shared.Rent(Math.Max(targetSize, 1));
        try
        {
            var sourcePosition = 0;
            foreach (var block in sourceInfo.Blocks)
            {
                if (sourcePosition > source.Length || block.StoredLength > source.Length - sourcePosition) throw new InvalidDataException("LZ4DIFF source block exceeds input.");
                var stored = source.Slice(sourcePosition, block.StoredLength); var plain = plainSource.AsSpan(block.Offset, block.Length);
                if (block.StoredLength >= block.Length) stored[..block.Length].CopyTo(plain);
                else
                {
                    var padding = 0;
                    if (sourceInfo.ZeroPadding) while (padding < Math.Min(4096, stored.Length) && stored[padding] == 0) padding++;
                    if (Lz4BlockCodec.PartialDecode(stored[padding..], plain) != plain.Length) throw new InvalidDataException("LZ4DIFF source decompression length mismatch.");
                }
                sourcePosition += block.StoredLength;
            }
            if (sourcePosition != source.Length) throw new InvalidDataException("Unexpected LZ4DIFF source tail.");
            var innerPatch = patch[checked(16 + (int)headerLength)..];
            if (innerType == 0)
            {
                using var result = new MemoryStream(plainTarget, 0, targetSize, writable: true);
                BinaryDeltaPatch.Apply(new MemoryStream(plainSource, 0, sourceSize, writable: false), new MemoryStream(innerPatch.ToArray(), writable: false), result, targetSize);
                if (result.Length != targetSize) throw new InvalidDataException("LZ4DIFF inner target length mismatch.");
            }
            else if (innerType == 1)
            {
                PuffDiffApplier.Apply(innerPatch, plainSource.AsSpan(0, sourceSize), plainTarget, targetSize);
            }
            else throw new InvalidDataException("Unknown LZ4DIFF inner patch type.");
            var storedTotal = targetInfo.Blocks.Aggregate(0L, (length, block) => checked(length + block.StoredLength));
            if (storedTotal != expectedLength) throw new InvalidDataException("LZ4DIFF stored output does not match destination extents.");
            destination.Clear();
            var output = destination; var position = 0;
            foreach (var block in targetInfo.Blocks)
            {
                var stored = output.Slice(position, block.StoredLength);
                if (block.StoredLength >= block.Length) plainTarget.AsSpan(block.Offset, block.Length).CopyTo(stored);
                else
                {
                    var encoded = Lz4DestinationCodec.Encode(plainTarget.AsSpan(block.Offset), stored, targetInfo.Algorithm, targetInfo.Level, out _);
                    if (targetInfo.ZeroPadding)
                    { stored[..encoded].CopyTo(stored[(stored.Length - encoded)..]); stored[..(stored.Length - encoded)].Clear(); }
                    else stored[encoded..].Clear();
                }
                if (block.Hash.Length > 0 && (block.Hash.Length != 32 || !CryptographicOperations.FixedTimeEquals(Sha256.ComputeHash(stored), block.Hash)))
                    throw new InvalidDataException("LZ4DIFF recompression SHA-256 mismatch; the compressor version differs from the patch generator.");
                if (block.Patch.Length > 0)
                {
                    using var result = new MemoryStream();
                    BinaryDeltaPatch.Apply(new MemoryStream(stored.ToArray(), writable: false), new MemoryStream(block.Patch, writable: false), result, stored.Length);
                    if (result.Length != stored.Length) throw new InvalidDataException("LZ4DIFF postfix length mismatch.");
                    result.GetBuffer().AsSpan(0, stored.Length).CopyTo(stored);
                }
                position += block.StoredLength;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plainSource);
            ArrayPool<byte>.Shared.Return(plainTarget);
        }
    }

    private static int ValidateBlocks(Info info)
    {
        long end = 0;
        foreach (var block in info.Blocks)
        {
            if (block.Offset != end || block.Length <= 0 || block.StoredLength <= 0) throw new InvalidDataException("Invalid LZ4DIFF block layout.");
            end = checked(end + block.Length); if (end > MaximumBytes || block.StoredLength > MaximumBytes) throw new InvalidDataException("LZ4DIFF operation limit exceeded.");
        }
        if (info.Blocks.Count == 0) throw new InvalidDataException("Missing LZ4DIFF block metadata.");
        return (int)end;
    }
    private static Info ReadInfo(byte[] bytes)
    {
        using var input = new CodedInputStream(bytes); int algorithm = 0, level = 0; var padding = false; var blocks = new List<Block>(); uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag == 10)
            {
                using var algo = new CodedInputStream(input.ReadBytes().ToByteArray()); uint a;
                while ((a = algo.ReadTag()) != 0) { if (a == 8) algorithm = algo.ReadInt32(); else if (a == 16) level = algo.ReadInt32(); else algo.SkipLastField(); }
            }
            else if (tag == 18)
            {
                using var block = new CodedInputStream(input.ReadBytes().ToByteArray()); int offset = 0, length = 0, stored = 0; byte[] hash = [], patch = []; uint b;
                while ((b = block.ReadTag()) != 0)
                { switch (b) { case 8: offset = checked((int)block.ReadUInt64()); break; case 16: length = checked((int)block.ReadUInt64()); break; case 24: stored = checked((int)block.ReadUInt64()); break; case 34: hash = block.ReadBytes().ToByteArray(); break; case 42: patch = block.ReadBytes().ToByteArray(); break; default: block.SkipLastField(); break; } }
                blocks.Add(new(offset, length, stored, hash, patch));
            }
            else if (tag == 24) padding = input.ReadBool(); else input.SkipLastField();
        }
        return new(algorithm, level, padding, blocks);
    }
}