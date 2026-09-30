using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BlockImage.Sparse.Models;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.IO;

namespace UotanStudio.Firmware.BlockImage.Sparse;

/// <summary>
/// Distinguishes the two on-disk shapes of <c>*.sparsechunk.NN</c> sets.
/// <para>区分 <c>*.sparsechunk.NN</c> 集合的两种磁盘形态。</para>
/// </summary>
public enum SparseChunkVariant
{
    /// <summary>Plain byte slices of one raw image; concatenation reproduces the original bytes. <para>单个原始镜像的纯字节切片；拼接即可还原原始字节。</para></summary>
    RawStream,
    /// <summary>Each part is a self-contained sparse image that carries the full logical geometry, the AOSP absolute-address chunking shape produced by <c>ResparseImage</c>. <para>每个分片都是携带完整逻辑几何的自包含稀疏镜像，即 <c>ResparseImage</c> 生成的 AOSP 绝对寻址分片形态。</para></summary>
    SelfContainedSparse
}

/// <summary>
/// A validated, ordered set of <c>.sparsechunk</c> files.
/// <para>已校验并排好顺序的 <c>.sparsechunk</c> 文件集合。</para>
/// </summary>
/// <param name="Variant">
/// Detected disk shape of the set. <para>检测到的集合磁盘形态。</para>
/// </param>
/// <param name="Parts">
/// Input paths in merge order. <para>按合并顺序排列的输入路径。</para>
/// </param>
/// <param name="TotalInputBytes">
/// Sum of physical input lengths. <para>输入卷的物理长度总和。</para>
/// </param>
public sealed record SparseChunkSet(SparseChunkVariant Variant, IReadOnlyList<string> Parts, long TotalInputBytes);

/// <summary>
/// Merges and splits <c>super.img.sparsechunk.NN</c> flash sets, mirroring the grouping discipline of
/// <c>DatSplitImporter</c>: sibling discovery through the <c>\.sparsechunk\.(\d+)$</c> suffix, gap-free
/// contiguous numbering from zero, and atomic publication of every produced file.
/// <para>合并与拆分 <c>super.img.sparsechunk.NN</c> 刷写集合，沿用 <c>DatSplitImporter</c> 的归组纪律：
/// 通过 <c>\.sparsechunk\.(\d+)$</c> 后缀发现同级文件、编号必须从零开始连续无缺口，并对每个生成文件执行原子发布。</para>
/// </summary>
/// <remarks>
/// <para>Merge supports both shapes: the raw-slice shape is a bounded stream concatenation, and the
/// self-contained-sparse shape (every part carries the sparse magic and the same logical geometry) is
/// merged by preserving each part's absolute chunk addresses and letting <see cref="SparseWriter"/>
/// re-emit the gap don't-care chunks. Split supports the raw shape only; sparse inputs must first be
/// converted with <c>SparseImageConverter.ResparseImage</c>, because byte-slicing a sparse container
/// would destroy the self-contained geometry this merger validates. Per-part trailing CRC32 chunks and
/// header image checksums are dropped on merge: the merged body is rebuilt from chunk data, and no
/// cross-part checksum semantics exist in the format.</para>
/// <para>合并支持两种形态：原始切片形态做有界的流拼接；自包含稀疏形态（每片带稀疏魔数与相同逻辑几何）通过保留各片的
/// 绝对块地址合并，由 <see cref="SparseWriter"/> 重新发出空隙 don't-care 块。拆分仅支持原始形态；稀疏输入必须先用
/// <c>SparseImageConverter.ResparseImage</c> 生成，因为对稀疏容器做字节切片会破坏本合并器所校验的自包含几何。
/// 合并会丢弃各片末尾的 CRC32 块与头部镜像校验和：合并体由块数据重建，而格式本身没有跨片校验和语义。</para>
/// </remarks>
public static class SparseChunkSplitter
{
    private const int BufferSize = 1024 * 1024;
    private const int MaximumParts = 4096;
    private const long MaximumBytes = 1L << 40;

    /// <summary>
    /// Resolves all sibling chunks sharing the selected file's exact prefix and classifies the set.
    /// <para>解析与所选文件共享准确前缀的全部同级分片，并判定集合形态。</para>
    /// </summary>
    /// <param name="selectedPath">
    /// Any numbered member of the set.
    /// <para>集合中任意带编号的成员。</para>
    /// </param>
    /// <returns>
    /// An ordered set with the detected variant and the summed physical length.
    /// <para>带有判定形态与物理长度总和的有序集合。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="selectedPath"/> is blank.
    /// <para><paramref name="selectedPath"/> 为空白。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The name, numbering, size, or the per-part sparse geometry is invalid, the parts mix sparse and
    /// raw shapes, or the raw-slice shape has ragged piece sizes that cannot come from one fixed-size split.
    /// <para>名称、编号、大小或单片稀疏几何无效、各片混合稀疏与原始形态，或原始切片形态的尺寸不齐而无法来自同一固定大小拆分。</para>
    /// </exception>
    public static SparseChunkSet Inspect(string selectedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        var selected = Path.GetFullPath(selectedPath);
        var name = Path.GetFileName(selected);
        var match = System.Text.RegularExpressions.Regex.Match(name, @"\.sparsechunk\.(\d+)$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidDataException("Expected a .sparsechunk.NNN member of a split image set.");
        }
        var prefix = name[..match.Index];
        var width = match.Groups[1].Value.Length;

        var directory = Path.GetDirectoryName(selected)!;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var members = new SortedDictionary<int, string>();
        foreach (var candidate in Directory.EnumerateFiles(directory))
        {
            var candidateName = Path.GetFileName(candidate);
            if (!candidateName.StartsWith(prefix, comparison))
            {
                continue;
            }

            var candidateMatch = System.Text.RegularExpressions.Regex.Match(candidateName,
                @"\.sparsechunk\.(\d+)$", RegexOptions.CultureInvariant);
            if (!candidateMatch.Success)
            {
                continue;
            }

            var numberText = candidateMatch.Groups[1].Value;
            if (numberText.Length != width)
            {
                throw new InvalidDataException($"Ambiguous sparsechunk numbering width: {candidateName}");
            }

            var number = int.Parse(numberText, NumberStyles.None, CultureInfo.InvariantCulture);
            if (!members.TryAdd(number, candidate))
            {
                throw new InvalidDataException($"Duplicate sparsechunk number: {candidateName}");
            }

            if (members.Count > MaximumParts)
            {
                throw new InvalidDataException("Too many sparsechunk pieces.");
            }
        }

        if (!members.Values.Any(path => string.Equals(path, selected, comparison)))
        {
            throw new FileNotFoundException("Selected sparsechunk piece is not present.", selected);
        }

        var expected = 0;
        foreach (var number in members.Keys)
        {
            if (number != expected++)
            {
                throw new InvalidDataException($"Missing sparsechunk piece {expected - 1}.");
            }
        }

        long total = 0;
        foreach (var path in members.Values)
        {
            var length = new FileInfo(path).Length;
            if (length <= 0 || length > MaximumBytes - total)
            {
                throw new InvalidDataException($"Invalid sparsechunk length: {Path.GetFileName(path)}");
            }

            total += length;
        }

        var parts = members.Values.ToArray();
        var sparseFlags = parts.Select(HasSparseMagic).ToArray();
        var anySparse = sparseFlags.Any(flag => flag);
        if (!anySparse)
        {
            return new SparseChunkSet(SparseChunkVariant.RawStream, parts, total);
        }
        if (sparseFlags.All(flag => flag))
        {
            ValidateSparseGeometry(parts);
            return new SparseChunkSet(SparseChunkVariant.SelfContainedSparse, parts, total);
        }
        throw new InvalidDataException("Sparsechunk pieces mix self-contained sparse images and raw slices.");
    }

    /// <summary>
    /// Merges a validated set into one image, publishing through a temporary file.
    /// <para>将已校验集合合并为一个镜像，经临时文件发布。</para>
    /// </summary>
    /// <param name="selectedPath">
    /// Any member used to resolve siblings.
    /// <para>用于定位同级分片的任一成员。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination image; an existing file is replaced only after the merge completed.
    /// <para>目标镜像；已存在的文件仅在合并完成后才被替换。</para>
    /// </param>
    /// <param name="progress">
    /// Optional byte progress over the physical input; <see langword="null"/> reports nothing.
    /// <para>可选的按物理输入字节进度；<see langword="null"/> 不报告。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation checked between pieces and between buffer transfers; cancellation leaves the destination untouched.
    /// <para>在各分片之间与缓冲区传输之间检查；取消不会改动目标。</para>
    /// </param>
    /// <returns>
    /// The validated set that was merged.
    /// <para>已合并的已校验集合。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="outputPath"/> is blank or resolves to one of the inputs.
    /// <para><paramref name="outputPath"/> 为空白，或其解析结果就是某个输入。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The set fails inspection or the self-contained pieces cannot be unified into one geometry.
    /// <para>集合未通过检查，或自包含分片无法统一为一份几何。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para>
    /// </exception>
    public static async Task<SparseChunkSet> MergeAsync(string selectedPath, string outputPath,
        IProgress<(long Processed, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        var set = Inspect(selectedPath);
        var destination = Path.GetFullPath(outputPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (set.Parts.Any(path => string.Equals(path, destination, comparison)))
        {
            throw new ArgumentException("The merged output must not overwrite a sparsechunk piece.", nameof(outputPath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = TempFileHelper.CreateAdjacentPath(destination);
        try
        {
            if (set.Variant == SparseChunkVariant.RawStream)
            {
                await MergeRawAsync(set, temporary, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Run(() => MergeSparse(set, temporary, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            AtomicPublish.Publish(temporary, destination);
        }
        finally
        {
            File.Delete(temporary);
        }
        return set;
    }

    /// <summary>
    /// Splits a raw image into fixed-size <c>&lt;basePath&gt;.sparsechunk.NN</c> pieces, each published
    /// atomically. Sparse inputs are rejected: their pieces must be regenerated as self-contained
    /// sparse images through <c>SparseImageConverter.ResparseImage</c> instead of byte slicing.
    /// <para>将原始镜像拆分为固定大小的 <c>&lt;basePath&gt;.sparsechunk.NN</c> 分片，每片原子发布。
    /// 稀疏输入会被拒绝：其分片必须经 <c>SparseImageConverter.ResparseImage</c> 重新生成为自包含稀疏镜像，而不是字节切片。</para>
    /// </summary>
    /// <param name="inputPath">
    /// Raw image to slice; it must exist and must not start with the sparse header magic.
    /// <para>待切分的原始镜像；必须存在且开头不得为稀疏头部魔数。</para>
    /// </param>
    /// <param name="basePath">
    /// Piece name prefix; the zero-padded two-digit piece index is appended after <c>.sparsechunk.</c>
    /// <para>分片名前缀；<c>.sparsechunk.</c> 之后追加补零的两位分片序号。</para>
    /// </param>
    /// <param name="chunkSizeBytes">
    /// Physical bytes per piece except the final remainder; must be at least 1.
    /// <para>除最后余量外每片的物理字节数；至少为 1。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation checked between piece publications; earlier pieces remain on disk.
    /// <para>在各分片发布之间检查；取消后已发布的前序分片会留在磁盘上。</para>
    /// </param>
    /// <returns>
    /// The produced piece paths in merge order.
    /// <para>按合并顺序排列的产出分片路径。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// A path is blank or <paramref name="chunkSizeBytes"/> is below 1.
    /// <para>路径为空白，或 <paramref name="chunkSizeBytes"/> 小于 1。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The input is empty, is a sparse container, or would need more than 4096 pieces.
    /// <para>输入为空、是稀疏容器，或需要超过 4096 个分片。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// The input does not exist.
    /// <para>输入不存在。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para>
    /// </exception>
    public static Task<IReadOnlyList<string>> SplitRawAsync(string inputPath, string basePath,
        long chunkSizeBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSizeBytes, 1);
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(inputPath);
        var root = Path.GetFullPath(basePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The raw image to split was not found.", source);
        }

        var length = new FileInfo(source).Length;
        if (length == 0)
        {
            throw new InvalidDataException("A zero-length image has nothing to split.");
        }
        if (HasSparseMagic(source))
        {
            throw new InvalidDataException(
                "Sparse inputs must be split by SparseImageConverter.ResparseImage so each piece stays a self-contained sparse image.");
        }
        var pieceCount = (length - 1) / chunkSizeBytes + 1;
        if (pieceCount > MaximumParts)
        {
            throw new InvalidDataException("The split would produce more than 4096 pieces.");
        }

        return Task.Run(() => SplitPieces(source, root, chunkSizeBytes, length, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<string> SplitPieces(string source, string root, long chunkSizeBytes, long length,
        CancellationToken cancellationToken)
    {
        var pieces = new List<string>();
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.SequentialScan);
        var remaining = length;
        var index = 0;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = Math.Min(chunkSizeBytes, remaining);
            var piecePath = $"{root}.sparsechunk.{index:D2}";
            FileExtraction.WriteAtomic(piecePath, output =>
            {
                Core.Streams.StreamCopy.CopyExactly(input, output, take, cancellationToken);
                output.Flush(flushToDisk: true);
            }, cancellationToken: cancellationToken);
            pieces.Add(piecePath);
            remaining -= take;
            index++;
        }
        return pieces;
    }

    private static bool HasSparseMagic(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        return HasSparseMagicCore(stream);
    }

    private static bool HasSparseMagicCore(Stream stream)
    {
        Span<byte> magic = stackalloc byte[4];
        var read = 0;
        while (read < 4)
        {
            var count = stream.Read(magic[read..]);
            if (count == 0)
            {
                break;
            }
            read += count;
        }
        return read == 4 && BinaryPrimitives.ReadUInt32LittleEndian(magic) == SparseFormat.SparseHeaderMagic;
    }

    private static void ValidateSparseGeometry(IReadOnlyList<string> parts)
    {
        SparseHeader? first = null;
        foreach (var part in parts)
        {
            SparseHeader header;
            try
            {
                header = SparseReader.PeekHeader(part);
            }
            catch (Exception ex) when (ex is EndOfStreamException or FileNotFoundException or IOException)
            {
                throw new InvalidDataException($"Sparsechunk piece {Path.GetFileName(part)} has no readable header.", ex);
            }
            if (!header.IsValid())
            {
                throw new InvalidDataException($"Sparsechunk piece {Path.GetFileName(part)} has an invalid sparse header.");
            }
            if (checked((ulong)header.TotalBlocks * header.BlockSize) > (ulong)MaximumBytes)
            {
                throw new InvalidDataException("Sparsechunk logical image exceeds the supported merge limit.");
            }
            first ??= header;
            if (header.BlockSize != first.Value.BlockSize || header.TotalBlocks != first.Value.TotalBlocks
                || header.ChunkHeaderSize != first.Value.ChunkHeaderSize
                || header.FileHeaderSize != first.Value.FileHeaderSize)
            {
                throw new InvalidDataException("Sparsechunk pieces do not declare a common sparse geometry.");
            }
        }
    }

    private static async Task MergeRawAsync(SparseChunkSet set, string temporary,
        IProgress<(long Processed, long Total)>? progress, CancellationToken token)
    {
        var buffer = new byte[BufferSize];
        long processed = 0;
        long? referenceSize = null;
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            for (var index = 0; index < set.Parts.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var path = set.Parts[index];
                var isLast = index == set.Parts.Count - 1;
                await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                               BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var length = input.Length;
                    if (length <= 0 || length > MaximumBytes - processed)
                    {
                        throw new InvalidDataException("The sparsechunk length changed after inspection.");
                    }
                    if (set.Parts.Count > 1 && !isLast)
                    {
                        referenceSize ??= length;
                        if (length != referenceSize)
                        {
                            throw new InvalidDataException($"Sparsechunk piece {Path.GetFileName(path)} is not the fixed split size.");
                        }
                    }

                    var remaining = length;
                    while (remaining > 0)
                    {
                        var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token)
                            .ConfigureAwait(false);
                        if (count == 0)
                        {
                            throw new EndOfStreamException($"Sparsechunk piece was truncated: {Path.GetFileName(path)}");
                        }
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        remaining -= count;
                    }

                    if (input.ReadByte() != -1)
                    {
                        throw new InvalidDataException($"Sparsechunk piece grew during merge: {Path.GetFileName(path)}");
                    }

                    processed += length;
                    progress?.Report((processed, set.TotalInputBytes));
                }
            }
            await output.FlushAsync(token).ConfigureAwait(false);
        }
    }

    private static void MergeSparse(SparseChunkSet set, string temporary, CancellationToken cancellationToken)
    {
        var opened = new List<SparseFile>();
        try
        {
            var mergedHeader = default(SparseHeader);
            var initialized = false;
            foreach (var path in set.Parts)
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                    FileOptions.SequentialScan);
                SparseFile sparse;
                try
                {
                    sparse = SparseReader.FromStream(stream, validateCrc: false, verbose: false,
                        MaximumParts * 256, cancellationToken, MaximumBytes);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
                sparse.Own(stream);
                opened.Add(sparse);
                if (!initialized)
                {
                    mergedHeader = sparse.Header;
                    initialized = true;
                }
                else if (sparse.Header.BlockSize != mergedHeader.BlockSize
                         || sparse.Header.TotalBlocks != mergedHeader.TotalBlocks
                         || sparse.Header.ChunkHeaderSize != mergedHeader.ChunkHeaderSize
                         || sparse.Header.FileHeaderSize != mergedHeader.FileHeaderSize)
                {
                    throw new InvalidDataException($"Sparsechunk piece {Path.GetFileName(path)} disagrees with the merge geometry.");
                }
            }

            var merged = new SparseFile();
            merged.AssignParsedHeader(mergedHeader with { TotalChunks = 0, ImageChecksum = 0 });
            try
            {
                uint dataFrontier = 0;
                foreach (var sparse in opened)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var chunk in sparse.Chunks)
                    {
                        var blocks = chunk.Header.ChunkSize;
                        switch ((ChunkType)chunk.Header.ChunkType)
                        {
                            case ChunkType.Raw when chunk.DataProvider is not null:
                                RequireAhead(chunk.StartBlock, dataFrontier, blocks, mergedHeader.TotalBlocks);
                                merged.AddRawChunk(chunk.DataProvider.GetSubProvider(0, chunk.DataProvider.Length),
                                    blocks, chunk.StartBlock);
                                dataFrontier = checked(chunk.StartBlock + blocks);
                                break;
                            case ChunkType.Fill:
                                RequireAhead(chunk.StartBlock, dataFrontier, blocks, mergedHeader.TotalBlocks);
                                merged.AddFillChunk(chunk.FillValue, checked((long)blocks * mergedHeader.BlockSize),
                                    chunk.StartBlock);
                                dataFrontier = checked(chunk.StartBlock + blocks);
                                break;
                            case ChunkType.DontCare:
                                break;
                        }
                    }
                }
                using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                merged.WriteToStream(output, sparse: true, cancellationToken: cancellationToken);
                output.Flush(flushToDisk: true);
            }
            finally
            {
                merged.Dispose();
            }
        }
        finally
        {
            foreach (var sparse in opened)
            {
                sparse.Dispose();
            }
        }
    }

    private static void RequireAhead(uint startBlock, uint dataFrontier, uint blocks, uint totalBlocks)
    {
        if (startBlock < dataFrontier)
        {
            throw new InvalidDataException("Sparsechunk pieces address overlapping logical blocks.");
        }
        if (startBlock >= totalBlocks || blocks > totalBlocks - startBlock)
        {
            throw new InvalidDataException("Sparsechunk pieces address blocks beyond the declared logical size.");
        }
    }
}