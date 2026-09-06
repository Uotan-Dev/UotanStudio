using System.Buffers.Binary;
using BsDiff;
using ICSharpCode.SharpZipLib.BZip2;
using UotanStudio.Firmware.Compress.Compressors;
using UotanStudio.Firmware.Sparse;

namespace UotanStudio.Firmware.Dat;

public static class DatReader
{
    public const int BlockSize = 4096;

    private const int StreamBufferSize = 1024 * 1024;

    /// <summary>
    /// Unpacks a DAT image pair into a raw image.
    /// <para>将 DAT 镜像对解包为原始镜像。</para>
    /// </summary>
    public static void Unpack(string transferListPath, string newDataPath, string outputImagePath, IProgress<double>? progress = null)
        => Unpack(transferListPath, newDataPath, null, null, outputImagePath, progress);

    /// <summary>
    /// Unpacks a DAT image pair into a raw image, additionally supporting a Brotli-compressed data file (.new.dat.br).
    /// <para>将 DAT 镜像对解包为原始镜像，额外支持 Brotli 压缩的数据文件（.new.dat.br）。</para>
    /// </summary>
    public static void Unpack(string transferListPath, string newDataPath, string outputImagePath, bool brotliCompressed, IProgress<double>? progress = null)
        => Unpack(transferListPath, newDataPath, null, null, outputImagePath, brotliCompressed, progress);

    /// <summary>
    /// Unpacks a DAT image pair into a raw image, additionally supporting incremental OTA commands (stash/move/diff).
    /// <para>将 DAT 镜像对解包为原始镜像，额外支持增量 OTA 命令（stash/move/diff）。</para>
    /// </summary>
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
    /// Unpacks a DAT image pair into a raw image with incremental OTA support and optional Brotli decompression of both the .new.dat and the .patch.dat data files.
    /// <para>将 DAT 镜像对解包为原始镜像，支持增量 OTA 并对 .new.dat 与 .patch.dat 两个数据文件 提供可选的 Brotli 解压。</para>
    /// </summary>
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
    /// Executes the actual unpack pass against already-resolved data streams, committing the output image atomically through a sibling temporary file that replaces the target on success.
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
    /// Copies each (source, target) range pair of a move command block-by-block from the source image into the output image.
    /// <para>将 move 命令的每一对（源，目标）范围按块从源镜像拷贝到输出镜像。</para>
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
    /// Applies a diff command: for every (source, target) range pair one patch entry is read from the sequential patch data stream and applied to the source blocks to reconstruct the target blocks.
    /// <para>应用 diff 命令：对每一对（源，目标）范围从顺序补丁数据流读取一个补丁条目并应用到源块， 还原出目标块。</para>
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

    private static ReadOnlySpan<byte> BsdiffMagic => "BSDIFF40"u8;

    private readonly record struct PatchEntry(byte[] Data, long NewSize);

    /// <summary>
    /// Reads one patch entry (a complete bsdiff file, including its 32-byte header) from the sequential patch data stream. imgdiff magic or any unknown magic throws a clear error.
    /// <para>从顺序补丁数据流读取一个补丁条目（完整的 bsdiff 文件，含 32 字节头部）。</para>
    /// </summary>
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
    /// Validates a byte range against the stream length using the offset &lt;= length &amp;&amp; size &lt;= length - offset invariant.
    /// <para>用 offset &lt;= length &amp;&amp; size &lt;= length - offset 不变量校验字节范围是否落在流长度内。</para>
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
    /// Reads exactly buffer.Length bytes or throws, so patch/source reads never silently truncate.
    /// <para>精确读取 buffer.Length 字节，否则抛出，确保补丁/源数据读取不会静默截断。</para>
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


        }
        catch (UnauthorizedAccessException)
        {


        }
    }

    /// <summary>
    /// Detects whether path refers to a Brotli-compressed input: a .br suffix, or a non-.dat extension whose first byte has the Brotli magic low nibble (0x0b).
    /// <para>检测 path 是否指向 Brotli 压缩输入：.br 后缀，或非 .dat 扩展名且 首字节具有 Brotli 的魔数低半字节（0x0b）。</para>
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
    /// Determines whether an input must be decompressed: forced by brotliCompressed, or auto-detected by IsBrotliInput.
    /// <para>判断输入是否需要解压：由 brotliCompressed 强制，或经 IsBrotliInput 自动检测。</para>
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
    /// Reads the blocks for a new command sequentially from the data stream and writes them to the target ranges; a short final block is zero-padded.
    /// <para>为 new 命令从数据流顺序读取数据块并写入目标范围；不足的末块以零填充。</para>
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
    /// Writes zero blocks to the target ranges of a zero/erase command.
    /// <para>为 zero/erase 命令向目标范围写入零块。</para>
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
    /// Reads up to BlockSize bytes into the buffer, returning the number actually read.
    /// <para>向缓冲区读取最多 BlockSize 字节，返回实际读取的字节数。</para>
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
    /// Opens the .patch.dat file for sequential patch-entry reads when the transfer list contains diff commands; returns null otherwise.
    /// <para>当 transfer list 包含 diff 命令时打开 .patch.dat 文件用于顺序补丁条目读取；否则返回 null。</para>
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
    /// Opens the source image file when the transfer list contains incremental commands (stash/move/diff); returns null otherwise.
    /// <para>当 transfer list 包含增量命令（stash/move/diff）时打开源镜像文件；否则返回 null。</para>
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
    /// Opens the .new.dat file for sequential block reads, transparently converting Android-sparse input to raw via a temporary file (deleted on close).
    /// <para>打开 .new.dat 文件用于顺序块读取，Android 稀疏输入会经临时文件（关闭时删除）透明转换为原始格式。</para>
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
