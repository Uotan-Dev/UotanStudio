using System.Text;
using UotanStudio.Firmware.Compress.Compressors;

namespace UotanStudio.Firmware.Dat;

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
