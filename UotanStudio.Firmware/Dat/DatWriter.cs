using System.Text;

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
    /// <param name="outputTransferListPath">The output <c>.transfer.list</c> file path. <para>输出 <c>.transfer.list</c> 文件路径。</para>
    public static void Write(string inputImagePath, string outputDatPath, string outputTransferListPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDatPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputTransferListPath);

        var totalBytes = new FileInfo(inputImagePath).Length;
        var totalBlocks = (totalBytes + BlockSize - 1) / BlockSize;

        var newRanges = new List<BlockRange>();
        var zeroRanges = new List<BlockRange>();

        // 单遍扫描：分类块、合并相邻同类型范围，并按序把数据块写入 .new.dat。
        // Single pass: classify blocks, merge adjacent same-type ranges, and write data blocks to .new.dat in order.
        using var input = new FileStream(inputImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.SequentialScan);
        using var output = new FileStream(outputDatPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize);
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

        input.Dispose();
        output.Dispose();
        using var writer = new StreamWriter(outputTransferListPath);
        writer.WriteLine("4");
        writer.WriteLine(totalBlocks);
        writer.WriteLine("0");
        writer.WriteLine("0");
        if (newRanges.Count > 0)
        {
            writer.WriteLine($"new {FormatRanges(newRanges)}");
        }

        if (zeroRanges.Count > 0)
        {
            writer.WriteLine($"zero {FormatRanges(zeroRanges)}");
        }
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