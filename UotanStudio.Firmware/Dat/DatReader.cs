using System.Buffers.Binary;
using BsDiff;
using ICSharpCode.SharpZipLib.BZip2;
using UotanStudio.Firmware.Compress.Compressors;
using UotanStudio.Firmware.Sparse;

namespace UotanStudio.Firmware.Dat;

/// <summary>
/// Unpacks an Android OTA v4 DAT image pair (<c>.transfer.list</c> + <c>.new.dat</c>) back into a raw
/// image file. Full-image commands (<c>new</c>/<c>zero</c>/<c>erase</c>/<c>free</c>) are always supported;
/// incremental commands (<c>stash</c>/<c>move</c>/<c>diff</c>) are supported by the overloads that also take
/// the <c>.patch.dat</c> data stream and the source image, where block data is read on demand from the
/// source image and <c>diff</c> patches are applied per source/target range pair.
/// <para>将 Android OTA v4 DAT 镜像对（<c>.transfer.list</c> + <c>.new.dat</c>）解包回原始镜像文件。
/// 全量命令（<c>new</c>/<c>zero</c>/<c>erase</c>/<c>free</c>）始终支持；增量命令
/// （<c>stash</c>/<c>move</c>/<c>diff</c>）由同时接收 <c>.patch.dat</c> 数据流与源镜像的重载支持，
/// 块数据按需从源镜像读取，<c>diff</c> 补丁按源/目标范围对逐一应用。</para>
/// </summary>
public static class DatReader
{
    /// <summary>
    /// Block size in bytes (4096).
    /// <para>块大小（字节，4096）。</para>
    /// </summary>
    public const int BlockSize = 4096;

    private const int StreamBufferSize = 1024 * 1024;

    /// <summary>
    /// Unpacks a DAT image pair into a raw image. The <c>.new.dat</c> may be raw or Android-sparse;
    /// sparse input is transparently converted. This overload supports only the full-image command path;
    /// when the transfer list contains <c>stash</c>/<c>move</c>/<c>diff</c> commands use the incremental
    /// overload that also takes <paramref name="patchDataPath"/> and <paramref name="sourceImagePath"/>.
    /// <para>将 DAT 镜像对解包为原始镜像。<c>.new.dat</c> 可为原始或 Android 稀疏格式，稀疏输入会被
    /// 透明转换。本重载仅支持全量命令路径；当 transfer list 包含 <c>stash</c>/<c>move</c>/<c>diff</c>
    /// 命令时，请使用同时接收 <paramref name="patchDataPath"/> 与 <paramref name="sourceImagePath"/>
    /// 的增量重载。</para>
    /// </summary>
    /// <param name="transferListPath">The <c>.transfer.list</c> file path. <para><c>.transfer.list</c> 文件路径。</para></param>
    /// <param name="newDataPath">The <c>.new.dat</c> file path. <para><c>.new.dat</c> 文件路径。</para></param>
    /// <param name="outputImagePath">The output raw image file path. <para>输出原始镜像文件路径。</para></param>
    /// <param name="progress">Optional progress reporter (0..1). <para>可选进度回调（0..1）。</para></param>
    /// <exception cref="ArgumentException">Thrown when the transfer list contains incremental commands but no source image is supplied. <para>transfer list 包含增量命令但未提供源镜像时抛出。</para></exception>
    public static void Unpack(string transferListPath, string newDataPath, string outputImagePath, IProgress<double>? progress = null)
        => Unpack(transferListPath, newDataPath, null, null, outputImagePath, progress);

    /// <summary>
    /// Unpacks a DAT image pair into a raw image, additionally supporting a Brotli-compressed data file
    /// (<c>.new.dat.br</c>). When <paramref name="brotliCompressed"/> is <c>true</c> the input is decompressed
    /// to a temporary raw file before unpacking; when <c>false</c> the input is auto-detected: a <c>.br</c>
    /// suffix, or a non-<c>.dat</c> extension whose first byte looks like the Brotli magic, also triggers
    /// decompression; otherwise raw/sparse input is handled exactly like the base overload. The temporary file
    /// is always deleted afterwards. This overload supports only the full-image command path.
    /// <para>将 DAT 镜像对解包为原始镜像，额外支持 Brotli 压缩的数据文件（<c>.new.dat.br</c>）。当
    /// <paramref name="brotliCompressed"/> 为 <c>true</c> 时先解压到临时原始文件再解包；为 <c>false</c> 时自动
    /// 检测：<c>.br</c> 后缀，或非 <c>.dat</c> 扩展名且首字节形如 Brotli 魔数，也会触发解压；否则与基础重载
    /// 一致处理原始/稀疏输入。临时文件之后总会删除。本重载仅支持全量命令路径。</para>
    /// </summary>
    /// <param name="transferListPath">The <c>.transfer.list</c> file path. <para><c>.transfer.list</c> 文件路径。</para></param>
    /// <param name="newDataPath">The <c>.new.dat</c> or <c>.new.dat.br</c> file path. <para><c>.new.dat</c> 或 <c>.new.dat.br</c> 文件路径。</para></param>
    /// <param name="outputImagePath">The output raw image file path. <para>输出原始镜像文件路径。</para></param>
    /// <param name="brotliCompressed">Whether the input is assumed to be Brotli-compressed. <para>输入是否假定为 Brotli 压缩。</para></param>
    /// <param name="progress">Optional progress reporter (0..1). <para>可选进度回调（0..1）。</para></param>
    /// <exception cref="ArgumentException">Thrown when the transfer list contains incremental commands but no source image is supplied. <para>transfer list 包含增量命令但未提供源镜像时抛出。</para></exception>
    public static void Unpack(string transferListPath, string newDataPath, string outputImagePath, bool brotliCompressed, IProgress<double>? progress = null)
        => Unpack(transferListPath, newDataPath, null, null, outputImagePath, brotliCompressed, progress);

    /// <summary>
    /// Unpacks a DAT image pair into a raw image, additionally supporting incremental OTA commands
    /// (<c>stash</c>/<c>move</c>/<c>diff</c>). Source block data for <c>stash</c>/<c>move</c>/<c>diff</c> is
    /// read on demand from <paramref name="sourceImagePath"/> (a raw image); <c>diff</c> patches are read
    /// sequentially from <paramref name="patchDataPath"/>, one bsdiff (<c>BSDIFF40</c>) patch entry per
    /// source/target range pair. When the transfer list contains no incremental commands,
    /// <paramref name="patchDataPath"/> and <paramref name="sourceImagePath"/> may be <c>null</c>.
    /// <para>将 DAT 镜像对解包为原始镜像，额外支持增量 OTA 命令（<c>stash</c>/<c>move</c>/<c>diff</c>）。
    /// <c>stash</c>/<c>move</c>/<c>diff</c> 的源块数据按需从 <paramref name="sourceImagePath"/>（原始镜像）读取；
    /// <c>diff</c> 补丁按序从 <paramref name="patchDataPath"/> 读取，每个源/目标范围对对应一个 bsdiff
    /// （<c>BSDIFF40</c>）补丁条目。当 transfer list 不包含增量命令时，
    /// <paramref name="patchDataPath"/> 与 <paramref name="sourceImagePath"/> 可为 <c>null</c>。</para>
    /// </summary>
    /// <param name="transferListPath">The <c>.transfer.list</c> file path. <para><c>.transfer.list</c> 文件路径。</para></param>
    /// <param name="newDataPath">The <c>.new.dat</c> file path. <para><c>.new.dat</c> 文件路径。</para></param>
    /// <param name="patchDataPath">The <c>.patch.dat</c> file path, or <c>null</c> when the transfer list has no <c>diff</c> commands. <para><c>.patch.dat</c> 文件路径；transfer list 无 <c>diff</c> 命令时为 <c>null</c>。</para></param>
    /// <param name="sourceImagePath">The source (old) image file path, or <c>null</c> when the transfer list has no incremental commands. <para>源（旧）镜像文件路径；transfer list 无增量命令时为 <c>null</c>。</para></param>
    /// <param name="outputImagePath">The output raw image file path. <para>输出原始镜像文件路径。</para></param>
    /// <param name="progress">Optional progress reporter (0..1). <para>可选进度回调（0..1）。</para></param>
    /// <exception cref="ArgumentException">Thrown when an incremental command is present but <paramref name="patchDataPath"/>/<paramref name="sourceImagePath"/> is missing. <para>存在增量命令但缺少 <paramref name="patchDataPath"/>/<paramref name="sourceImagePath"/> 时抛出。</para></exception>
    /// <exception cref="FileNotFoundException">Thrown when a required input file does not exist. <para>必需输入文件不存在时抛出。</para></exception>
    /// <exception cref="InvalidDataException">Thrown on malformed transfer lists, patch entries, or range/stream overruns. <para>transfer list、补丁条目非法或范围/流越界时抛出。</para></exception>
    public static void Unpack(string transferListPath, string newDataPath, string? patchDataPath, string? sourceImagePath, string outputImagePath, IProgress<double>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferListPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newDataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputImagePath);

        var transferList = TransferListParser.Parse(transferListPath);
        using var newData = OpenNewData(newDataPath);
        using var patchData = OpenOptionalPatchData(patchDataPath, transferList.Commands.Any(c => c.Kind == TransferListCommandKind.Diff));
        using var sourceImage = OpenOptionalSourceImage(sourceImagePath, transferList);
        UnpackCore(transferList, newData, patchData, sourceImage, outputImagePath, progress);
    }

    /// <summary>
    /// Unpacks a DAT image pair into a raw image with incremental OTA support and optional Brotli
    /// decompression of both the <c>.new.dat</c> and the <c>.patch.dat</c> data files. When
    /// <paramref name="brotliCompressed"/> is <c>true</c> both inputs are assumed Brotli-compressed; when
    /// <c>false</c> each is auto-detected (see the base Brotli overload). Decompressed inputs are staged in
    /// temporary raw files that are always deleted afterwards.
    /// <para>将 DAT 镜像对解包为原始镜像，支持增量 OTA 并对 <c>.new.dat</c> 与 <c>.patch.dat</c> 两个数据文件
    /// 提供可选的 Brotli 解压。当 <paramref name="brotliCompressed"/> 为 <c>true</c> 时假定两个输入均为 Brotli
    /// 压缩；为 <c>false</c> 时各自自动检测（见基础 Brotli 重载）。解压后的输入先暂存到临时原始文件，之后总会删除。</para>
    /// </summary>
    /// <param name="transferListPath">The <c>.transfer.list</c> file path. <para><c>.transfer.list</c> 文件路径。</para></param>
    /// <param name="newDataPath">The <c>.new.dat</c> or <c>.new.dat.br</c> file path. <para><c>.new.dat</c> 或 <c>.new.dat.br</c> 文件路径。</para></param>
    /// <param name="patchDataPath">The <c>.patch.dat</c> or <c>.patch.dat.br</c> file path, or <c>null</c> when the transfer list has no <c>diff</c> commands. <para><c>.patch.dat</c> 或 <c>.patch.dat.br</c> 文件路径；transfer list 无 <c>diff</c> 命令时为 <c>null</c>。</para></param>
    /// <param name="sourceImagePath">The source (old) image file path, or <c>null</c> when the transfer list has no incremental commands. <para>源（旧）镜像文件路径；transfer list 无增量命令时为 <c>null</c>。</para></param>
    /// <param name="outputImagePath">The output raw image file path. <para>输出原始镜像文件路径。</para></param>
    /// <param name="brotliCompressed">Whether the inputs are assumed to be Brotli-compressed. <para>输入是否假定为 Brotli 压缩。</para></param>
    /// <param name="progress">Optional progress reporter (0..1). <para>可选进度回调（0..1）。</para></param>
    /// <exception cref="ArgumentException">Thrown when an incremental command is present but <paramref name="patchDataPath"/>/<paramref name="sourceImagePath"/> is missing. <para>存在增量命令但缺少 <paramref name="patchDataPath"/>/<paramref name="sourceImagePath"/> 时抛出。</para></exception>
    /// <exception cref="FileNotFoundException">Thrown when a required input file does not exist. <para>必需输入文件不存在时抛出。</para></exception>
    /// <exception cref="InvalidDataException">Thrown on malformed transfer lists, patch entries, or range/stream overruns. <para>transfer list、补丁条目非法或范围/流越界时抛出。</para></exception>
    public static void Unpack(string transferListPath, string newDataPath, string? patchDataPath, string? sourceImagePath, string outputImagePath, bool brotliCompressed, IProgress<double>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferListPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newDataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputImagePath);

        var transferList = TransferListParser.Parse(transferListPath);
        var needsPatchData = transferList.Commands.Any(c => c.Kind == TransferListCommandKind.Diff);

        var newTemp = NeedsDecompression(newDataPath, brotliCompressed) ? CreateBrotliTemp(newDataPath) : null;
        var patchTemp = patchDataPath is not null && needsPatchData && NeedsDecompression(patchDataPath, brotliCompressed)
            ? CreateBrotliTemp(patchDataPath)
            : null;
        try
        {
            Unpack(transferListPath, newTemp ?? newDataPath, patchTemp ?? patchDataPath, sourceImagePath, outputImagePath, progress);
        }
        finally
        {
            if (newTemp is not null)
            {
                TryDeleteTempFile(newTemp);
            }

            if (patchTemp is not null)
            {
                TryDeleteTempFile(patchTemp);
            }
        }
    }

    /// <summary>
    /// Executes the actual unpack pass against already-resolved data streams, committing the output image
    /// atomically through a sibling temporary file that replaces the target on success.
    /// <para>对已解析的数据流执行实际的解包过程，输出镜像经同级临时文件原子提交，成功后替换目标文件。</para>
    /// </summary>
    private static void UnpackCore(TransferList transferList, Stream newData, Stream? patchData, Stream? sourceImage, string outputImagePath, IProgress<double>? progress)
    {
        var fullOutputPath = Path.GetFullPath(outputImagePath);
        var directory = Path.GetDirectoryName(fullOutputPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");

        var totalBytes = checked(transferList.TotalBlocks * BlockSize);
        try
        {
            using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, StreamBufferSize))
            {
                output.SetLength(totalBytes);
                ExecuteCommands(transferList, newData, patchData, sourceImage, output, progress, totalBytes);
            }

            File.Move(tempPath, fullOutputPath, overwrite: true);
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    /// <summary>
    /// Executes every transfer-list command in order against the output stream.
    /// <para>按顺序对输出流执行全部 transfer list 命令。</para>
    /// </summary>
    private static void ExecuteCommands(TransferList transferList, Stream newData, Stream? patchData, Stream? sourceImage, Stream output, IProgress<double>? progress, long totalBytes)
    {
        // stash 只记账不预读：记录 id → 区间，数据在需要时直接从源镜像读取，避免把整个暂存块集读入内存。
        // AOSP v4 的 stash id 就是源范围集的字符串形式；后续 move/diff 的源 token 与之相同时即引用该暂存，
        // 由于源镜像只读且从不被修改，直接读取源镜像得到的数据与暂存内容完全一致。
        // Stashes are pure bookkeeping (id -> ranges); the data itself is read from the source image on
        // demand so the whole stash never has to be held in memory. In AOSP v4 the stash id is the source
        // range-set string; a later move/diff whose source token matches references it, and because the
        // source image is never mutated, reading it yields exactly the stashed data.
        var stashes = new Dictionary<string, List<BlockRange>>(StringComparer.Ordinal);
        var buffer = new byte[BlockSize];
        long written = 0;
        foreach (var command in transferList.Commands)
        {
            switch (command.Kind)
            {
                case TransferListCommandKind.New:
                    ReadNew(command, newData, output, buffer);
                    break;
                case TransferListCommandKind.Zero:
                case TransferListCommandKind.Erase:
                    WriteZeros(command, output);
                    break;
                case TransferListCommandKind.Free:
                    // 释放的块保持为零（输出文件已预置零）。
                    // Freed blocks stay zero (the output file is pre-sized with zeros).
                    break;
                case TransferListCommandKind.Stash:
                    if (command.StashId is null)
                    {
                        throw new InvalidDataException("A 'stash' command requires a stash id.  'stash' 命令需要暂存 id。");
                    }

                    stashes[command.StashId] = [.. command.Ranges];
                    break;
                case TransferListCommandKind.Move:
                    MoveRanges(command, sourceImage, output, buffer);
                    break;
                case TransferListCommandKind.Diff:
                    ApplyDiff(command, patchData, sourceImage, output);
                    break;
            }

            foreach (var range in command.Ranges)
            {
                written += range.BlockCount * BlockSize;
            }

            progress?.Report(totalBytes == 0 ? 1.0 : Math.Min(1.0, (double)written / totalBytes));
        }
    }

    /// <summary>
    /// Copies each (source, target) range pair of a <c>move</c> command block-by-block from the source
    /// image into the output image.
    /// <para>将 <c>move</c> 命令的每一对（源，目标）范围按块从源镜像拷贝到输出镜像。</para>
    /// </summary>
    private static void MoveRanges(TransferListCommand command, Stream? sourceImage, Stream output, byte[] buffer)
    {
        if (sourceImage is null)
        {
            throw new InvalidDataException("A 'move' command requires a source image.  'move' 命令需要源镜像。");
        }

        ValidateSourceRangeCounts(command);
        for (var i = 0; i < command.Ranges.Count; i++)
        {
            var src = command.SourceRanges[i];
            var dst = command.Ranges[i];
            if (src.BlockCount != dst.BlockCount)
            {
                throw new InvalidDataException(
                    $"Move range pair {i} has {src.BlockCount} source blocks but {dst.BlockCount} target blocks.  第 {i} 对 move 范围有 {src.BlockCount} 个源块但 {dst.BlockCount} 个目标块。");
            }

            var offset = checked(src.Start * BlockSize);
            var size = checked(src.BlockCount * BlockSize);
            ValidateReadRange(sourceImage, offset, size);
            sourceImage.Position = offset;
            output.Position = checked(dst.Start * BlockSize);
            for (var b = 0; b < src.BlockCount; b++)
            {
                ReadExactlyOrThrow(sourceImage, buffer);
                output.Write(buffer, 0, BlockSize);
            }
        }
    }

    /// <summary>
    /// Applies a <c>diff</c> command: for every (source, target) range pair one patch entry is read from
    /// the sequential patch data stream and applied to the source blocks to reconstruct the target blocks.
    /// Only bsdiff (<c>BSDIFF40</c>) patch entries are supported.
    /// <para>应用 <c>diff</c> 命令：对每一对（源，目标）范围从顺序补丁数据流读取一个补丁条目并应用到源块，
    /// 还原出目标块。仅支持 bsdiff（<c>BSDIFF40</c>）补丁条目。</para>
    /// </summary>
    private static void ApplyDiff(TransferListCommand command, Stream? patchData, Stream? sourceImage, Stream output)
    {
        if (patchData is null || sourceImage is null)
        {
            throw new InvalidDataException("A 'diff' command requires both the patch data file (.patch.dat) and the source image.  'diff' 命令需要补丁数据文件（.patch.dat）与源镜像。");
        }

        ValidateSourceRangeCounts(command);
        for (var i = 0; i < command.Ranges.Count; i++)
        {
            var src = command.SourceRanges[i];
            var dst = command.Ranges[i];
            var expectedSize = checked(dst.BlockCount * BlockSize);

            var srcData = ReadSourceBlocks(sourceImage, src);
            var patch = ReadPatchEntry(patchData);
            if (patch.NewSize != expectedSize)
            {
                throw new InvalidDataException(
                    $"Diff range pair {i} patch reconstructs {patch.NewSize} bytes but the target range expects {expectedSize} bytes.  第 {i} 对 diff 范围的补丁还原 {patch.NewSize} 字节，而目标范围期望 {expectedSize} 字节。");
            }

            output.Position = checked(dst.Start * BlockSize);
            using var sourceStream = new MemoryStream(srcData, writable: false);
            try
            {
                BinaryPatch.Apply(sourceStream, () => new MemoryStream(patch.Data, writable: false), output);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidDataException($"Failed to apply the bsdiff patch for diff range pair {i}: {ex.Message}  第 {i} 对 diff 范围应用 bsdiff 补丁失败。", ex);
            }
        }
    }

    /// <summary>
    /// Reads the source-image blocks of a range into a contiguous buffer for bsdiff application.
    /// <para>把某个范围的源镜像块读取到连续缓冲区，供 bsdiff 应用。</para>
    /// </summary>
    private static byte[] ReadSourceBlocks(Stream sourceImage, BlockRange src)
    {
        var offset = checked(src.Start * BlockSize);
        var size = checked(src.BlockCount * BlockSize);
        ValidateReadRange(sourceImage, offset, size);
        if (size > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Diff source range [{src.Start},{src.End}) is too large ({size} bytes) for a single patch entry.  diff 源范围 [{src.Start},{src.End}) 过大（{size} 字节），无法作为单个补丁处理。");
        }

        var data = new byte[(int)size];
        sourceImage.Position = offset;
        ReadExactlyOrThrow(sourceImage, data);
        return data;
    }

    /// <summary>
    /// The ASCII magic of a bsdiff patch entry.
    /// <para>bsdiff 补丁条目的 ASCII 魔数。</para>
    /// </summary>
    private static ReadOnlySpan<byte> BsdiffMagic => "BSDIFF40"u8;

    /// <summary>
    /// A single patch entry read from the sequential <c>.patch.dat</c> stream, with the reconstructable
    /// target size from its bsdiff header.
    /// <para>从顺序 <c>.patch.dat</c> 流读取的单个补丁条目，附带其 bsdiff 头部声明的可还原目标大小。</para>
    /// </summary>
    private readonly record struct PatchEntry(byte[] Data, long NewSize);

    /// <summary>
    /// Reads one patch entry (a complete bsdiff file, including its 32-byte header) from the sequential
    /// patch data stream. <c>imgdiff</c> magic or any unknown magic throws a clear error.
    /// <para>从顺序补丁数据流读取一个补丁条目（完整的 bsdiff 文件，含 32 字节头部）。<c>imgdiff</c> 魔数或
    /// 未知魔数会抛出明确异常。</para>
    /// </summary>
    /// <remarks>
    /// The bsdiff header declares only the compressed lengths of the control and diff blocks plus the
    /// reconstructed new-file size; the trailing extra block is a bzip2 stream of unknown compressed
    /// length, so it is located by draining that stream (SharpZipLib does not read past the block
    /// boundary) and then re-read into the entry buffer.
    /// <para>bsdiff 头部只声明控制块与差异块的压缩长度以及还原出的新文件大小；末尾的 extra 块是压缩长度未知的
    /// bzip2 流，因此先排空该流定位其边界（SharpZipLib 不会越过块边界预读），再重新读入条目缓冲区。</para>
    /// </remarks>
    private static PatchEntry ReadPatchEntry(Stream patchData)
    {
        if (!patchData.CanSeek)
        {
            throw new InvalidDataException("The patch data stream must be seekable.  补丁数据流必须是可 seek 的。");
        }

        Span<byte> magic = stackalloc byte[8];
        ReadExactlyOrThrow(patchData, magic);

        if (!magic.SequenceEqual(BsdiffMagic))
        {
            if (magic.SequenceEqual("IMGDIFF1"u8) || magic.SequenceEqual("IMGDIFF2"u8))
            {
                throw new InvalidDataException("imgdiff (IMGDIFF1/IMGDIFF2) patches are not supported.  imgdiff（IMGDIFF1/IMGDIFF2）补丁暂不支持。");
            }

            throw new InvalidDataException(
                $"Unsupported patch magic. Only bsdiff (BSDIFF40) is supported; the leading 8 bytes were {Convert.ToHexString(magic)}.  不支持的补丁魔数：仅支持 bsdiff（BSDIFF40），前 8 字节为 {Convert.ToHexString(magic)}。");
        }

        Span<byte> headerRest = stackalloc byte[24];
        ReadExactlyOrThrow(patchData, headerRest);
        var controlLength = BinaryPrimitives.ReadInt64LittleEndian(headerRest[..8]);
        var diffLength = BinaryPrimitives.ReadInt64LittleEndian(headerRest.Slice(8, 8));
        var newSize = BinaryPrimitives.ReadInt64LittleEndian(headerRest.Slice(16, 8));
        if (controlLength < 0 || diffLength < 0 || newSize < 0)
        {
            throw new InvalidDataException("Invalid negative length in the bsdiff patch header.  bsdiff 补丁头部存在非法负长度。");
        }

        if (controlLength > int.MaxValue || diffLength > int.MaxValue)
        {
            throw new InvalidDataException(
                $"The bsdiff patch block lengths are too large (control {controlLength}, diff {diffLength}).  bsdiff 补丁块长度过大（control {controlLength}，diff {diffLength}）。");
        }

        // 头部 + 控制块 + 差异块的长度已知，先读入条目缓冲区。
        // The header, control block and diff block have known lengths; read them first.
        var fixedLength = checked(32 + controlLength + diffLength);
        if (fixedLength > int.MaxValue)
        {
            throw new InvalidDataException($"The bsdiff patch entry header is too large ({fixedLength} bytes).  bsdiff 补丁条目头部过大（{fixedLength} 字节）。");
        }

        var data = new byte[(int)fixedLength];
        magic.CopyTo(data);
        headerRest.CopyTo(data.AsSpan(8));
        ReadExactlyOrThrow(patchData, data.AsSpan(32, (int)controlLength));
        ReadExactlyOrThrow(patchData, data.AsSpan(checked(32 + (int)controlLength), (int)diffLength));

        // 通过排空 extra 块的 bzip2 流定位条目的真实结束位置。
        // Drain the extra block's bzip2 stream to locate the entry's true end.
        var extraStart = patchData.Position;
        try
        {
            using var extra = new BZip2InputStream(patchData) { IsStreamOwner = false };
            var discard = new byte[BlockSize];
            while (extra.Read(discard, 0, discard.Length) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException($"Failed to decode the bsdiff patch extra block: {ex.Message}  bsdiff 补丁 extra 块解码失败。", ex);
        }

        var extraLength = checked(patchData.Position - extraStart);
        if (extraLength < 0 || extraLength > int.MaxValue)
        {
            throw new InvalidDataException($"The bsdiff patch extra block length is invalid ({extraLength} bytes).  bsdiff 补丁 extra 块长度非法（{extraLength} 字节）。");
        }

        // 重新读取压缩的 extra 块，补全补丁条目字节，供 BinaryPatch.Apply 使用。
        // Re-read the compressed extra block to complete the patch entry for BinaryPatch.Apply.
        var entryLength = checked(fixedLength + extraLength);
        if (entryLength > int.MaxValue)
        {
            throw new InvalidDataException($"The bsdiff patch entry is too large ({entryLength} bytes).  bsdiff 补丁条目过大（{entryLength} 字节）。");
        }

        Array.Resize(ref data, (int)entryLength);
        patchData.Position = extraStart;
        ReadExactlyOrThrow(patchData, data.AsSpan((int)fixedLength, (int)extraLength));
        return new PatchEntry(data, newSize);
    }

    /// <summary>
    /// Verifies that a move/diff command's source range count matches its target range count.
    /// <para>校验 move/diff 命令的源范围数与目标范围数一致。</para>
    /// </summary>
    private static void ValidateSourceRangeCounts(TransferListCommand command)
    {
        if (command.SourceRanges.Count != command.Ranges.Count)
        {
            throw new InvalidDataException(
                $"The '{command.Kind}' command has {command.SourceRanges.Count} source ranges but {command.Ranges.Count} target ranges.  '{command.Kind}' 命令有 {command.SourceRanges.Count} 个源范围但 {command.Ranges.Count} 个目标范围。");
        }
    }

    /// <summary>
    /// Validates a byte range against the stream length using the <c>offset &lt;= length &amp;&amp;
    /// size &lt;= length - offset</c> invariant.
    /// <para>用 <c>offset &lt;= length &amp;&amp; size &lt;= length - offset</c> 不变量校验字节范围是否落在流长度内。</para>
    /// </summary>
    private static void ValidateReadRange(Stream stream, long offset, long size)
    {
        var length = stream.Length;
        if (offset < 0 || size < 0 || offset > length || size > length - offset)
        {
            throw new InvalidDataException($"Read range [{offset}, {offset + size}) exceeds the available data length {length}.  读取范围 [{offset}, {offset + size}) 超出可用数据长度 {length}。");
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes or throws, so patch/source reads never silently
    /// truncate.
    /// <para>精确读取 <paramref name="buffer"/>.Length 字节，否则抛出，确保补丁/源数据读取不会静默截断。</para>
    /// </summary>
    private static void ReadExactlyOrThrow(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                throw new InvalidDataException($"Unexpected end of data stream after {total} of {buffer.Length} bytes.  数据流提前结束（已读取 {total}/{buffer.Length} 字节）。");
            }

            total += read;
        }
    }

    /// <summary>
    /// Decompresses a Brotli stream into a raw temporary file.
    /// <para>将 Brotli 流解压为原始临时文件。</para>
    /// </summary>
    private static void DecompressBrotliToFile(string inputPath, string outputPath)
    {
        using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.SequentialScan);
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize);
        BrotliCompressor.Decompress(input, output);
    }

    /// <summary>
    /// Best-effort deletion of a temporary file.
    /// <para>尽力删除临时文件。</para>
    /// </summary>
    private static void TryDeleteTempFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 临时文件可能被残留句柄占用，忽略。
            // Ignore: the temp file may be locked by a lingering handle.
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
            // Same as above.
        }
    }

    /// <summary>
    /// Detects whether <paramref name="path"/> refers to a Brotli-compressed input: a <c>.br</c> suffix, or a
    /// non-<c>.dat</c> extension whose first byte has the Brotli magic low nibble (<c>0x0b</c>).
    /// <para>检测 <paramref name="path"/> 是否指向 Brotli 压缩输入：<c>.br</c> 后缀，或非 <c>.dat</c> 扩展名且
    /// 首字节具有 Brotli 的魔数低半字节（<c>0x0b</c>）。</para>
    /// </summary>
    private static bool IsBrotliInput(string path)
    {
        if (path.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(Path.GetExtension(path), ".dat", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var firstByte = stream.ReadByte();
            return (firstByte & 0x0F) == 0x0B;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Determines whether an input must be decompressed: forced by <paramref name="brotliCompressed"/>, or
    /// auto-detected by <see cref="IsBrotliInput"/>.
    /// <para>判断输入是否需要解压：由 <paramref name="brotliCompressed"/> 强制，或经 <see cref="IsBrotliInput"/> 自动检测。</para>
    /// </summary>
    private static bool NeedsDecompression(string path, bool brotliCompressed) => brotliCompressed || IsBrotliInput(path);

    /// <summary>
    /// Decompresses a Brotli input file into a fresh raw temporary file and returns its path.
    /// <para>将 Brotli 输入文件解压到新的原始临时文件并返回其路径。</para>
    /// </summary>
    private static string CreateBrotliTemp(string inputPath)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"uotan_dat_{Guid.NewGuid():N}.raw");
        DecompressBrotliToFile(inputPath, tempPath);
        return tempPath;
    }

    /// <summary>
    /// Reads the blocks for a <c>new</c> command sequentially from the data stream and writes them to the
    /// target ranges; a short final block is zero-padded.
    /// <para>为 <c>new</c> 命令从数据流顺序读取数据块并写入目标范围；不足的末块以零填充。</para>
    /// </summary>
    private static void ReadNew(TransferListCommand command, Stream newData, Stream output, byte[] buffer)
    {
        foreach (var range in command.Ranges)
        {
            output.Position = checked(range.Start * BlockSize);
            for (var b = 0; b < range.BlockCount; b++)
            {
                var read = ReadBlock(newData, buffer);
                if (read < BlockSize)
                {
                    Array.Clear(buffer, read, BlockSize - read);
                }

                output.Write(buffer, 0, BlockSize);
            }
        }
    }

    /// <summary>
    /// Writes zero blocks to the target ranges of a <c>zero</c>/<c>erase</c> command.
    /// <para>为 <c>zero</c>/<c>erase</c> 命令向目标范围写入零块。</para>
    /// </summary>
    private static void WriteZeros(TransferListCommand command, Stream output)
    {
        var zeros = new byte[BlockSize];
        foreach (var range in command.Ranges)
        {
            output.Position = checked(range.Start * BlockSize);
            for (var b = 0; b < range.BlockCount; b++)
            {
                output.Write(zeros);
            }
        }
    }

    /// <summary>
    /// Reads up to <see cref="BlockSize"/> bytes into the buffer, returning the number actually read.
    /// <para>向缓冲区读取最多 <see cref="BlockSize"/> 字节，返回实际读取的字节数。</para>
    /// </summary>
    private static int ReadBlock(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < BlockSize)
        {
            var read = stream.Read(buffer, total, BlockSize - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>
    /// Opens the <c>.patch.dat</c> file for sequential patch-entry reads when the transfer list contains
    /// <c>diff</c> commands; returns <c>null</c> otherwise.
    /// <para>当 transfer list 包含 <c>diff</c> 命令时打开 <c>.patch.dat</c> 文件用于顺序补丁条目读取；否则返回 <c>null</c>。</para>
    /// </summary>
    private static Stream? OpenOptionalPatchData(string? path, bool needsPatchData)
    {
        if (!needsPatchData)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "The transfer list contains 'diff' commands, so the patch data file (.patch.dat) is required.  transfer list 包含 'diff' 命令，因此需要补丁数据文件（.patch.dat）。",
                nameof(path));
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.SequentialScan);
    }

    /// <summary>
    /// Opens the source image file when the transfer list contains incremental commands
    /// (<c>stash</c>/<c>move</c>/<c>diff</c>); returns <c>null</c> otherwise.
    /// <para>当 transfer list 包含增量命令（<c>stash</c>/<c>move</c>/<c>diff</c>）时打开源镜像文件；否则返回 <c>null</c>。</para>
    /// </summary>
    private static Stream? OpenOptionalSourceImage(string? path, TransferList transferList)
    {
        if (!transferList.Commands.Any(c => c.Kind is TransferListCommandKind.Stash or TransferListCommandKind.Move or TransferListCommandKind.Diff))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "The transfer list contains incremental commands (stash/move/diff), so the source image is required.  transfer list 包含增量命令（stash/move/diff），因此需要源镜像。",
                nameof(path));
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.SequentialScan);
    }

    /// <summary>
    /// Opens the <c>.new.dat</c> file for sequential block reads, transparently converting Android-sparse
    /// input to raw via a temporary file (deleted on close).
    /// <para>打开 <c>.new.dat</c> 文件用于顺序块读取，Android 稀疏输入会经临时文件（关闭时删除）透明转换为原始格式。</para>
    /// </summary>
    private static Stream OpenNewData(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.SequentialScan);
        Span<byte> magic = stackalloc byte[4];
        var read = stream.Read(magic);
        if (read == 4 && BinaryPrimitives.ReadUInt32LittleEndian(magic) == SparseFormat.SparseHeaderMagic)
        {
            stream.Dispose();
            var tempPath = Path.Combine(Path.GetTempPath(), $"uotan_dat_{Guid.NewGuid():N}.raw");
            SparseImageConverter.ConvertSparseToRaw([path], tempPath);
            return new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize,
                FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        }

        stream.Position = 0;
        return stream;
    }
}
