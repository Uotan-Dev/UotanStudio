using System.IO.Compression;
using UotanStudio.Compress.Internal;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.OtaDelta.Diff;

internal static class ImageDeltaPatch
{
    private sealed record Chunk(int Type, long SourceOffset, long SourceLength, long PatchOffset,
        long ExpandedLength, long TargetLength, int Level, int Strategy, long RawOffset, int RawLength);

    private const int BlockSize = 4096;

    /// <summary>
    /// Builds an IMGDIFF2 patch from a source slice to a target using NORMAL (bsdiff4) and RAW chunks.
    /// DEFLATE chunks require the caller to know the source region is stored deflate-compressed and are
    /// emitted by the compressor-aware block differ, not this primitive.
    /// <para>由源区间与目标构建 IMGDIFF2 补丁，使用 NORMAL（bsdiff4）与 RAW 块。DEFLATE 块要求调用方
    /// 已知源区间以 deflate 压缩存储，故由具备压缩感知的块差分器发射，不由该原语发射。</para>
    /// </summary>
    internal static void Create(Stream source, Stream target, Stream output, CancellationToken ct = default)
    {
        if (!source.CanSeek)
        {
            throw new ArgumentException("IMGDIFF2 encoder requires a seekable source stream.", nameof(source));
        }

        if (!target.CanSeek)
        {
            throw new ArgumentException("IMGDIFF2 encoder requires a seekable target stream.", nameof(target));
        }

        source.Position = 0;
        target.Position = 0;
        var sourceBytes = new byte[source.Length];
        source.ReadExactly(sourceBytes);
        var targetBytes = new byte[target.Length];
        target.ReadExactly(targetBytes);

        var covered = (long)Math.Min(sourceBytes.Length, targetBytes.Length);
        var normalLength = covered == 0 ? 0 : (covered - 1) / BlockSize * BlockSize + BlockSize;
        var rawLength = targetBytes.Length - normalLength;
        var hasNormal = normalLength > 0;
        var hasRaw = rawLength > 0;
        var chunkCount = (hasNormal ? 1 : 0) + (hasRaw ? 1 : 0);
        var normalSourceLength = Math.Min(normalLength, (long)sourceBytes.Length);
        var headerSize = 12 + (hasNormal ? 28 : 0) + (hasRaw ? 8 : 0);
        var normalPatchOffset = headerSize + (hasRaw ? (int)rawLength : 0);

        byte[] normalPatch;
        if (hasNormal)
        {
            using var patchBuffer = new MemoryStream();
            using (var sourceSlice = new MemoryStream(sourceBytes, 0, (int)normalSourceLength, writable: false))
            using (var targetSlice = new MemoryStream(targetBytes, 0, (int)normalLength, writable: false))
            {
                BinaryDeltaPatch.Create(sourceSlice, targetSlice, patchBuffer, ct);
            }

            normalPatch = patchBuffer.ToArray();
        }
        else
        {
            normalPatch = [];
        }

        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("IMGDIFF2"u8.ToArray());
            writer.Write(chunkCount);
            if (hasNormal)
            {
                writer.Write(0);
                writer.Write((long)0);
                writer.Write(normalSourceLength);
                writer.Write((long)normalPatchOffset);
            }

            if (hasRaw)
            {
                writer.Write(3);
                writer.Write((int)rawLength);
            }

            writer.Flush();
        }

        if (hasRaw)
        {
            output.Write(targetBytes, (int)normalLength, (int)rawLength);
        }

        if (hasNormal)
        {
            output.Write(normalPatch);
        }
    }

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
                using var decoder = new DeflateStream(sourceRange, CompressionMode.Decompress, leaveOpen: true);
                BinaryDeltaPatch.CopyExactly(decoder, expanded, chunk.ExpandedLength);
                if (decoder.ReadByte() != -1) throw new InvalidDataException("IMGDIFF2 source inflation length mismatch.");
            }
            expanded.Position = 0;
            if (BinaryDeltaPatch.ReadOutputLength(patchRange) != chunk.TargetLength) throw new InvalidDataException("IMGDIFF2 target expansion length mismatch.");
            using (var encoder = CreateDeflateEncoder(bounded, chunk.Level, chunk.Strategy))
                BinaryDeltaPatch.Apply(expanded, patchRange, encoder, chunk.TargetLength);
        }
        if (bounded.BytesWritten != expectedLength) throw new InvalidDataException("IMGDIFF2 output does not match destination ranges.");
    }

    /// <summary>
    /// Creates the raw DEFLATE encoder that reproduces the compressed bytes recorded in an IMGDIFF2 DEFLATE chunk.
    /// <para>创建可复现 IMGDIFF2 DEFLATE 数据块中记录的压缩字节的原始 DEFLATE 编码器。</para>
    /// </summary>
    /// <remarks>
    /// A DEFLATE chunk stores its compressor parameters next to the patch, and the applier has to reproduce the very
    /// same compressed bytes because the assembled image is checked against the recorded target hash. The in-box
    /// <see cref="System.IO.Compression.DeflateStream"/> cannot honour that contract: on the recorded fixtures it emits
    /// 160 bytes at the chunk's level 6 where the patch records 87, so this single site stays on
    /// <see cref="SharpCompress.Compressors.Deflate.DeflateStream"/>, which the assembly already depends on for bzip2.
    /// <para>DEFLATE 数据块把压缩参数与补丁一并存储，应用端必须复现完全相同的压缩字节，因为组装后的镜像要与记录的目标
    /// 哈希校验。内置的 <see cref="System.IO.Compression.DeflateStream"/> 无法满足该契约：在记录的夹具上，数据块等级 6
    /// 会输出 160 字节，而补丁记录的是 87 字节；因此此唯一位点保留
    /// <see cref="SharpCompress.Compressors.Deflate.DeflateStream"/>，程序集本就为 bzip2 依赖它。</para>
    /// </remarks>
    /// <param name="output">Destination the compressed bytes are written to. <para>压缩字节写入的目标。</para></param>
    /// <param name="level">Zlib level recorded in the chunk, or <c>-1</c> for the encoder default.
    /// <para>数据块记录的 zlib 等级，<c>-1</c> 表示编码器默认值。</para></param>
    /// <param name="strategy">Zlib strategy recorded in the chunk. <para>数据块记录的 zlib 策略。</para></param>
    /// <returns>A raw DEFLATE encoder that leaves <paramref name="output"/> open when it is disposed.
    /// <para>释放时保持 <paramref name="output"/> 打开的原始 DEFLATE 编码器。</para></returns>
    private static Stream CreateDeflateEncoder(Stream output, int level, int strategy) =>
        new SharpCompress.Compressors.Deflate.DeflateStream(
            new NonDisposingStream(output),
            SharpCompress.Compressors.CompressionMode.Compress,
            (SharpCompress.Compressors.Deflate.CompressionLevel)(level == -1 ? 6 : level))
        {
            Strategy = (SharpCompress.Compressors.Deflate.CompressionStrategy)strategy
        };
}