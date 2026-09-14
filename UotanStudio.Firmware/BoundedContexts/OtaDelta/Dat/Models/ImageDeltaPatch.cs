using System.IO.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff;
using UotanStudio.Firmware.CrossCutting.Compress.Internal;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat;

// Android recovery IMGDIFF2 contains NORMAL/BSDIFF, DEFLATE/BSDIFF and inline RAW chunks.
internal static class ImageDeltaPatch
{
    private sealed record Chunk(int Type, long SourceOffset, long SourceLength, long PatchOffset,
        long ExpandedLength, long TargetLength, int Level, int Strategy, long RawOffset, int RawLength);

    internal static void Apply(Stream source, Stream patch, Stream output, long expectedLength)
    {
        using var reader = new BinaryReader(patch, System.Text.Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual("IMGDIFF2"u8)) throw new InvalidDataException("Invalid IMGDIFF2 magic.");
        var count = reader.ReadInt32();
        if (count < 0 || count > 1024 * 1024 || count > (patch.Length - 12) / 8) throw new InvalidDataException("Invalid IMGDIFF2 chunk count.");
        var chunks = new List<Chunk>(count);
        for (var i = 0; i < count; i++)
        {
            var type = reader.ReadInt32();
            if (type == 3)
            {
                var length = reader.ReadInt32(); var offset = patch.Position;
                if (length < 0 || length > patch.Length - offset) throw new InvalidDataException("IMGDIFF2 RAW chunk exceeds patch.");
                chunks.Add(new(type, 0, 0, 0, 0, 0, 0, 0, offset, length)); patch.Position += length;
                continue;
            }
            if (type is not (0 or 2)) throw new InvalidDataException("Unknown IMGDIFF2 chunk type.");
            var sourceOffset = reader.ReadInt64(); var sourceLength = reader.ReadInt64(); var patchOffset = reader.ReadInt64();
            if (sourceOffset < 0 || sourceLength < 0 || sourceOffset > source.Length || sourceLength > source.Length - sourceOffset)
                throw new InvalidDataException("IMGDIFF2 source range exceeds source image.");
            long expanded = 0, targetLength = 0; int level = 0, strategy = 0;
            if (type == 2)
            {
                expanded = reader.ReadInt64(); targetLength = reader.ReadInt64(); level = reader.ReadInt32();
                var method = reader.ReadInt32(); var windowBits = reader.ReadInt32(); var memoryLevel = reader.ReadInt32(); strategy = reader.ReadInt32();
                if (expanded < 0 || targetLength <= 0 || level is < -1 or > 9 || strategy is < 0 or > 2)
                    throw new InvalidDataException("Invalid IMGDIFF2 DEFLATE parameters.");
                // These are the fixed settings emitted by Android imgdiff's ImageChunk.
                if (method != 8 || windowBits != -15 || memoryLevel != 8)
                    throw new InvalidDataException("IMGDIFF2 requires Android raw DEFLATE settings (method 8, window -15, memory 8).");
            }
            chunks.Add(new(type, sourceOffset, sourceLength, patchOffset, expanded, targetLength, level, strategy, 0, 0));
        }
        var headerEnd = patch.Position;
        if (chunks.Any(chunk => chunk.Type != 3 && (chunk.PatchOffset < headerEnd || chunk.PatchOffset >= patch.Length)))
            throw new InvalidDataException("IMGDIFF2 patch offset overlaps the header or exceeds patch.");
        var patchOffsets = chunks.Where(chunk => chunk.Type != 3).Select(chunk => chunk.PatchOffset).Distinct().Order().Append(patch.Length).ToArray();
        if (patchOffsets.Any(offset => offset < headerEnd || offset > patch.Length)) throw new InvalidDataException("IMGDIFF2 patch offset overlaps the header or exceeds patch.");
        using var bounded = new CountingWriteStream(output, expectedLength, leaveOpen: true);
        foreach (var chunk in chunks)
        {
            if (chunk.Type == 3)
            {
                using var raw = new BoundedReadStream(patch, chunk.RawOffset, chunk.RawLength, leaveOpen: true);
                BinaryDeltaPatch.CopyExactly(raw, bounded, raw.Length); continue;
            }
            var nextOffset = patchOffsets[Array.BinarySearch(patchOffsets, chunk.PatchOffset) + 1];
            using var patchRange = new BoundedReadStream(patch, chunk.PatchOffset, nextOffset - chunk.PatchOffset, leaveOpen: true);
            using var sourceRange = new BoundedReadStream(source, chunk.SourceOffset, chunk.SourceLength, leaveOpen: true);
            if (chunk.Type == 0)
            {
                BinaryDeltaPatch.Apply(sourceRange, patchRange, bounded, expectedLength - bounded.BytesWritten); continue;
            }
            using var expanded = BinaryDeltaPatch.CreateTemporaryStream();
            if (chunk.ExpandedLength > 0)
            {
                var inflater = new Inflater(noHeader: true);
                using var decoder = new InflaterInputStream(sourceRange, inflater) { IsStreamOwner = false };
                BinaryDeltaPatch.CopyExactly(decoder, expanded, chunk.ExpandedLength);
                if (decoder.ReadByte() != -1 || !inflater.IsFinished) throw new InvalidDataException("IMGDIFF2 source inflation length mismatch.");
            }
            expanded.Position = 0;
            if (BinaryDeltaPatch.ReadOutputLength(patchRange) != chunk.TargetLength) throw new InvalidDataException("IMGDIFF2 target expansion length mismatch.");
            using (var encoder = new SharpCompress.Compressors.Deflate.DeflateStream(new NonDisposingStream(bounded),
                   SharpCompress.Compressors.CompressionMode.Compress,
                   (SharpCompress.Compressors.Deflate.CompressionLevel)(chunk.Level == -1 ? 6 : chunk.Level))
            { Strategy = (SharpCompress.Compressors.Deflate.CompressionStrategy)chunk.Strategy })
                BinaryDeltaPatch.Apply(expanded, patchRange, encoder, chunk.TargetLength);
        }
        if (bounded.BytesWritten != expectedLength) throw new InvalidDataException("IMGDIFF2 output does not match destination ranges.");
    }
}