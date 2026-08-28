using System.Text;
using UotanStudio.Firmware.Compress.Compressors;

namespace UotanStudio.Firmware.Dat;

/// <summary>
/// Writes an Android OTA v4 DAT image pair (<c>.new.dat</c> data file plus <c>.transfer.list</c>) from a raw
/// image file. All-zero blocks are emitted as <c>zero</c> commands and non-zero blocks as <c>new</c> commands,
/// with adjacent same-type blocks merged into shared range groups.
/// <para>从原始镜像文件写出 Android OTA v4 DAT 镜像对（<c>.new.dat</c> 数据文件与 <c>.transfer.list</c>）。
/// 全零块输出为 <c>zero</c> 命令，非零块输出为 <c>new</c> 命令，相邻同类型块合并为共享范围组。</para>
/// </summary>
public static class DatWriter
{
    /// <summary>
    /// Block size in bytes (4096).
    /// <para>块大小（字节，4096）。</para>
    /// </summary>
    public const int BlockSize = 4096;

    private const int StreamBufferSize = 1024 * 1024;

    /// <summary>
    /// Writes a raw image as a DAT image pair. The <c>.new.dat</c> holds the non-zero blocks (final partial
    /// block zero-padded to <see cref="BlockSize"/>); the <c>.transfer.list</c> is a valid v4 file with the
    /// total block count, zero stash counts, and <c>new</c>/<c>zero</c> commands whose range sets use
    /// comma-separated <c>count,start,end,...</c> syntax.
    /// <para>将原始镜像写出为 DAT 镜像对。<c>.new.dat</c> 承载非零块（末块不足 <see cref="BlockSize"/> 时以零
    /// 填充）；<c>.transfer.list</c> 为合法 v4 文件，含总块数、零暂存计数与 <c>new</c>/<c>zero</c> 命令，
    /// 范围集使用逗号分隔的 <c>count,start,end,...</c> 语法。</para>
    /// </summary>
    /// <param name="inputImagePath">The input raw image file path. <para>输入原始镜像文件路径。</para></param>
    /// <param name="outputDatPath">The output <c>.new.dat</c> file path. <para>输出 <c>.new.dat</c> 文件路径。</para></param>
    /// <param name="outputTransferListPath">The output <c>.transfer.list</c> file path. <para>输出 <c>.transfer.list</c> 文件路径。</para></param>
    public static void Write(string inputImagePath, string outputDatPath, string outputTransferListPath)
        => Write(inputImagePath, outputDatPath, outputTransferListPath, new DatWriteOptions());

    /// <summary>
    /// Writes a raw image as a DAT image pair with the given options. With
    /// <see cref="DatWriteOptions.CompressWithBrotli"/> the <c>.new.dat</c> is replaced by a Brotli-compressed
    /// <c>.new.dat.br</c> (the transfer list stays unchanged); with
    /// <see cref="DatWriteOptions.MaxCommandRangeBlocks"/> &gt; 0 the merged <c>new</c> ranges are split into
    /// multiple <c>new</c> commands whose range sets each stay within the block limit. In both cases the
    /// <c>.new.dat</c> byte layout is identical to the default overload.
    /// <para>使用给定选项将原始镜像写出为 DAT 镜像对。设置 <see cref="DatWriteOptions.CompressWithBrotli"/> 时
    /// <c>.new.dat</c> 会被替换为 Brotli 压缩的 <c>.new.dat.br</c>（transfer list 保持不变）；设置
    /// <see cref="DatWriteOptions.MaxCommandRangeBlocks"/> &gt; 0 时合并后的 <c>new</c> 范围会被拆分为多条
    /// <c>new</c> 命令，每条命令的范围集都不超过该块数上限。两种情况下 <c>.new.dat</c> 的字节布局都与
    /// 默认重载一致。</para>
    /// </summary>
    /// <param name="inputImagePath">The input raw image file path. <para>输入原始镜像文件路径。</para></param>
    /// <param name="outputDatPath">The output <c>.new.dat</c> file path. <para>输出 <c>.new.dat</c> 文件路径。</para></param>
    /// <param name="outputTransferListPath">The output <c>.transfer.list</c> file path. <para>输出 <c>.transfer.list</c> 文件路径。</para></param>
    /// <param name="options">The writer options. <para>写入器选项。</para></param>
    public static void Write(string inputImagePath, string outputDatPath, string outputTransferListPath, DatWriteOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDatPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputTransferListPath);
        ArgumentNullException.ThrowIfNull(options);

        var totalBytes = new FileInfo(inputImagePath).Length;
        var totalBlocks = (totalBytes + BlockSize - 1) / BlockSize;

        var newRanges = new List<BlockRange>();
        var zeroRanges = new List<BlockRange>();

        // 单遍扫描：分类块、合并相邻同类型范围，并按序把数据块写入 .new.dat。
        // Single pass: classify blocks, merge adjacent same-type ranges, and write data blocks to .new.dat in order.
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
            }

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
    /// Writes the <c>new</c> command lines, splitting the merged ranges into multiple commands when
    /// <paramref name="maxBlocksPerCommand"/> is greater than zero. Range splitting never changes the order of
    /// the non-zero blocks, so the <c>.new.dat</c> byte layout stays identical to the unsplit output.
    /// <para>写出 <c>new</c> 命令行；当 <paramref name="maxBlocksPerCommand"/> 大于 0 时将合并后的范围拆分为
    /// 多条命令。范围拆分不会改变非零块的顺序，因此 <c>.new.dat</c> 的字节布局与未拆分时完全一致。</para>
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
    /// Brotli-compresses the <c>.new.dat</c> file into <c>.new.dat.br</c> (streaming, with the compressor's
    /// default options) and deletes the uncompressed file.
    /// <para>将 <c>.new.dat</c> 文件流式 Brotli 压缩为 <c>.new.dat.br</c>（使用压缩器默认选项）并删除未压缩
    /// 的文件。</para>
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
    /// Formats block ranges as a comma-separated <c>count,start,end,...</c> range set.
    /// <para>将块范围格式化为逗号分隔的 <c>count,start,end,...</c> 范围集。</para>
    /// </summary>
    private static string FormatRanges(List<BlockRange> ranges)
    {
        var builder = new StringBuilder();
        builder.Append(ranges.Count);
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
    /// Determines whether the first <paramref name="length"/> bytes of the buffer are all zero.
    /// <para>判断缓冲区前 <paramref name="length"/> 个字节是否全为零。</para>
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