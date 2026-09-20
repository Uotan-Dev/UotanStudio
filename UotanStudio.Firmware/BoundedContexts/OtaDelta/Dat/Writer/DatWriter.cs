using UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat.Models;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat.Serialization;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.ImgDiff;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.Compression.Compressors;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat.Writer;

public static class DatWriter
{
    public const int BlockSize = 4096;

    private const int StreamBufferSize = 1024 * 1024;

    /// <summary>
    /// Writes a raw image as a DAT image pair.
    /// <para>将原始镜像写出为 DAT 镜像对。</para>
    /// </summary>
    public static void Write(string inputImagePath, string outputDatPath, string outputTransferListPath)
        => Write(inputImagePath, outputDatPath, outputTransferListPath, new DatWriteOptions());

    /// <summary>
    /// Writes a raw image as a DAT image pair with the given options.
    /// <para>使用给定选项将原始镜像写出为 DAT 镜像对。</para>
    /// </summary>
    /// <param name="progress">Optional byte progress, fed with (processed bytes read from the raw image, total raw bytes); <c>null</c> to disable reporting. <para>可选的按字节进度回调（读入原始镜像的字节数、原始镜像总字节数）；<c>null</c> 表示不报告。</para></param>
    public static void Write(string inputImagePath, string outputDatPath, string outputTransferListPath, DatWriteOptions options, IProgress<(long Processed, long Total)>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDatPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputTransferListPath);
        ArgumentNullException.ThrowIfNull(options);

        var totalBytes = new FileInfo(inputImagePath).Length;
        var totalBlocks = (totalBytes + BlockSize - 1) / BlockSize;

        var newRanges = new List<BlockRange>();
        var zeroRanges = new List<BlockRange>();


        using (var input = new FileStream(inputImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.SequentialScan))
        using (var output = new FileStream(outputDatPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize))
        {
            var buffer = new byte[BlockSize];
            long block = 0;
            long? newStart = null;
            long? zeroStart = null;
            while (block < totalBlocks)
            {
                var read = ReadBlock(input, buffer);
                var isZero = IsZeroBlock(buffer, read);
                if (isZero)
                {
                    if (newStart is not null)
                    {
                        newRanges.Add(new BlockRange(newStart.Value, block));
                        newStart = null;
                    }

                    zeroStart ??= block;
                }
                else
                {
                    if (zeroStart is not null)
                    {
                        zeroRanges.Add(new BlockRange(zeroStart.Value, block));
                        zeroStart = null;
                    }

                    newStart ??= block;
                    if (read < BlockSize)
                    {
                        Array.Clear(buffer, read, BlockSize - read);
                    }

                    output.Write(buffer, 0, BlockSize);
                }

                block++;
                progress?.Report((Math.Min(checked(block * BlockSize), totalBytes), totalBytes));
            }
            progress?.Report((totalBytes, totalBytes));

            if (newStart is not null)
            {
                newRanges.Add(new BlockRange(newStart.Value, block));
            }

            if (zeroStart is not null)
            {
                zeroRanges.Add(new BlockRange(zeroStart.Value, block));
            }
        }

        using (var writer = new StreamWriter(outputTransferListPath))
        {
            writer.WriteLine("4");
            writer.WriteLine(totalBlocks);
            writer.WriteLine("0");
            writer.WriteLine("0");
            WriteNewCommands(writer, newRanges, options.MaxCommandRangeBlocks);
            if (zeroRanges.Count > 0)
            {
                writer.WriteLine($"zero {FormatRanges(zeroRanges)}");
            }
        }

        if (options.CompressWithBrotli)
        {
            CompressDatWithBrotli(outputDatPath);
        }
    }

    /// <summary>
    /// Writes an incremental classic block OTA from a source and target image. Runs of differing non-zero
    /// blocks become positional <c>imgdiff</c> commands whose bsdiff bytes land in <c>.patch.dat</c>; blocks
    /// that become zero become <c>zero</c> commands; unchanged blocks are omitted and inherited from the
    /// source copy the updater seeds. Runs are compared positionally, so blocks that move without editing
    /// are re-emitted rather than referenced with <c>move</c>/<c>stash</c>. If the target has no differing
    /// non-zero block (only zeroing or identical content), which the recovery reader could not seed from a
    /// source it never consumes, the writer falls back to a full <c>new</c> image.
    /// <para>由源镜像与目标镜像写出增量经典块 OTA。差异非零块区段成为位置式 <c>imgdiff</c> 命令，其 bsdiff
    /// 字节落入 <c>.patch.dat</c>；归零块成为 <c>zero</c> 命令；未改动块被省略并由 updater 预置的源拷贝继承。
    /// 区段按位置比较，故移动的块被重新发出而非以 <c>move</c>/<c>stash</c> 引用。若目标无差异非零块（仅归零或完全
    /// 相同），而 recovery 读取器无法从不消费的源预置，则回退为全量 <c>new</c> 镜像。</para>
    /// </summary>
    /// <param name="progress">Optional byte progress over the target image. <para>可选的按目标镜像字节进度。</para></param>
    public static void WriteIncremental(string sourceImagePath, string targetImagePath, string outputDatPath, string outputPatchDatPath,
        string outputTransferListPath, DatWriteOptions options, IProgress<(long Processed, long Total)>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDatPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPatchDatPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputTransferListPath);
        ArgumentNullException.ThrowIfNull(options);

        var sourceLength = new FileInfo(sourceImagePath).Length;
        var totalBytes = new FileInfo(targetImagePath).Length;
        var totalBlocks = (totalBytes + BlockSize - 1) / BlockSize;

        // Pass one: classify blocks into coalesced positional runs (source is streamed, target seeked).
        var runs = new List<(long Start, long End, bool Imgdiff)>();
        using (var source = new FileStream(sourceImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.RandomAccess))
        using (var target = new FileStream(targetImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.RandomAccess))
        {
            var targetBlock = new byte[BlockSize];
            var sourceBlock = new byte[BlockSize];
            var zeroBlock = new byte[BlockSize];
            long block = 0;
            while (block < totalBlocks)
            {
                ReadAt(target, block * BlockSize, targetBlock);
                if (block * BlockSize < sourceLength)
                {
                    ReadAt(source, block * BlockSize, sourceBlock);
                }
                else
                {
                    Array.Clear(sourceBlock);
                }

                bool imgdiff;
                bool emit;
                if (targetBlock.AsSpan().SequenceEqual(sourceBlock))
                {
                    emit = false; imgdiff = false;
                }
                else if (targetBlock.AsSpan().SequenceEqual(zeroBlock))
                {
                    emit = true; imgdiff = false;
                }
                else
                {
                    emit = true; imgdiff = true;
                }

                if (emit && runs.Count > 0 && runs[^1].End == block && runs[^1].Imgdiff == imgdiff)
                {
                    runs[^1] = (runs[^1].Start, block + 1, imgdiff);
                }
                else
                {
                    if (emit) runs.Add((block, block + 1, imgdiff));
                }

                block++;
                progress?.Report((Math.Min(checked(block * BlockSize), totalBytes), totalBytes));
            }
        }

        // Without an imgdiff run there is nothing to make the reader consume (and seed from) the source,
        // so fall back to a full image that never relies on inheritance.
        if (!runs.Any(run => run.Imgdiff))
        {
            File.WriteAllBytes(outputPatchDatPath, []);
            Write(targetImagePath, outputDatPath, outputTransferListPath, options);
            return;
        }

        var commands = new List<TransferListCommand>();
        long patchOffset = 0;
        using (var source = new FileStream(sourceImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.RandomAccess))
        using (var target = new FileStream(targetImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.RandomAccess))
        using (var patch = new FileStream(outputPatchDatPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize))
        using (new FileStream(outputDatPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            foreach (var (start, end, isImgdiff) in runs)
            {
                var count = end - start;
                if (!isImgdiff)
                {
                    commands.Add(new TransferListCommand(TransferListCommandKind.Zero, [new BlockRange(start, end)]));
                    continue;
                }

                var sourceRegion = ReadRegion(source, start, count);
                var targetRegion = ReadRegion(target, start, count);
                using var patchStream = new MemoryStream();
                ImageDeltaPatch.Create(new MemoryStream(sourceRegion, writable: false), new MemoryStream(targetRegion, writable: false), patchStream);
                var patchBytes = patchStream.ToArray();
                patch.Write(patchBytes);
                patchOffset += patchBytes.Length;
                commands.Add(new TransferListCommand(TransferListCommandKind.Imgdiff, [new BlockRange(start, end)], sourceRanges: [new BlockRange(start, end)])
                {
                    SourceBlockCount = count,
                    SourceLocations = [new BlockRange(0, count)],
                    PatchOffset = patchOffset - patchBytes.Length,
                    PatchLength = patchBytes.Length,
                    SourceHash = Sha1(sourceRegion),
                    TargetHash = Sha1(targetRegion),
                });
            }
        }

        var list = new TransferList(4, totalBlocks, 0, 0, commands);
        TransferListSerializer.Write(outputTransferListPath, list);
    }

    private static void ReadAt(FileStream stream, long offset, byte[] buffer)
    {
        stream.Position = offset;
        var read = ReadBlock(stream, buffer);
        if (read < buffer.Length) Array.Clear(buffer, read, buffer.Length - read);
    }

    private static byte[] ReadRegion(FileStream stream, long startBlock, long count)
    {
        var buffer = new byte[checked((int)(count * BlockSize))];
        stream.Position = startBlock * BlockSize;
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        return buffer;
    }

    private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data));

    /// <summary>
    /// Writes the new command lines, splitting the merged ranges into multiple commands when maxBlocksPerCommand is greater than zero.
    /// <para>写出 new 命令行；当 maxBlocksPerCommand 大于 0 时将合并后的范围拆分为 多条命令。</para>
    /// </summary>
    private static void WriteNewCommands(TextWriter writer, List<BlockRange> ranges, int maxBlocksPerCommand)
    {
        if (ranges.Count == 0)
        {
            return;
        }

        if (maxBlocksPerCommand <= 0)
        {
            writer.WriteLine($"new {FormatRanges(ranges)}");
            return;
        }

        var commandRanges = new List<BlockRange>();
        long commandBlocks = 0;
        foreach (var range in ranges)
        {
            var start = range.Start;
            while (start < range.End)
            {
                var capacity = maxBlocksPerCommand - commandBlocks;
                if (capacity <= 0)
                {
                    writer.WriteLine($"new {FormatRanges(commandRanges)}");
                    commandRanges.Clear();
                    commandBlocks = 0;
                    continue;
                }

                var take = (int)Math.Min(range.End - start, capacity);
                commandRanges.Add(new BlockRange(start, start + take));
                commandBlocks += take;
                start += take;
            }
        }

        if (commandRanges.Count > 0)
        {
            writer.WriteLine($"new {FormatRanges(commandRanges)}");
        }
    }

    /// <summary>
    /// Brotli-compresses the .new.dat file into .new.dat.br (streaming, with the compressor's default options) and deletes the uncompressed file.
    /// <para>将 .new.dat 文件流式 Brotli 压缩为 .new.dat.br（使用压缩器默认选项）并删除未压缩 的文件。</para>
    /// </summary>
    private static void CompressDatWithBrotli(string datPath)
    {
        var brPath = datPath + ".br";
        using (var input = new FileStream(datPath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.SequentialScan))
        using (var output = new FileStream(brPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize))
        {
            BrotliCompressor.Compress(input, output);
        }

        File.Delete(datPath);
    }

    /// <summary>
    /// Formats block ranges as a comma-separated count,start,end,... range set.
    /// <para>将块范围格式化为逗号分隔的 count,start,end,... 范围集。</para>
    /// </summary>
    private static string FormatRanges(List<BlockRange> ranges)
    {
        var builder = new StringBuilder();
        builder.Append(checked(ranges.Count * 2));
        foreach (var range in ranges)
        {
            builder.Append(',');
            builder.Append(range.Start);
            builder.Append(',');
            builder.Append(range.End);
        }

        return builder.ToString();
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
    /// Determines whether the first length bytes of the buffer are all zero.
    /// <para>判断缓冲区前 length 个字节是否全为零。</para>
    /// </summary>
    private static bool IsZeroBlock(byte[] buffer, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (buffer[i] != 0)
            {
                return false;
            }
        }

        return true;
    }
}