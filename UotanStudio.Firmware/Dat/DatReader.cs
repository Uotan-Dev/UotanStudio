using System.Buffers.Binary;
using UotanStudio.Firmware.Sparse;

namespace UotanStudio.Firmware.Dat;

/// <summary>
/// Unpacks an Android OTA v4 DAT image pair (<c>.transfer.list</c> + <c>.new.dat</c>) back into a raw
/// image file, supporting the full-image <c>new</c>/<c>zero</c>/<c>erase</c>/<c>free</c> command path.
/// <para>将 Android OTA v4 DAT 镜像对（<c>.transfer.list</c> + <c>.new.dat</c>）解包回原始镜像文件，
/// 支持全量镜像的 <c>new</c>/<c>zero</c>/<c>erase</c>/<c>free</c> 命令路径。</para>
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
    /// sparse input is transparently converted. <c>diff</c>/<c>stash</c> commands throw
    /// <see cref="NotSupportedException"/> (incremental OTA is out of scope).
    /// <para>将 DAT 镜像对解包为原始镜像。<c>.new.dat</c> 可为原始或 Android 稀疏格式，稀疏输入会被
    /// 透明转换。<c>diff</c>/<c>stash</c> 命令抛出 <see cref="NotSupportedException"/>（增量 OTA 不在范围内）。</para>
    /// </summary>
    /// <param name="transferListPath">The <c>.transfer.list</c> file path. <para><c>.transfer.list</c> 文件路径。</para></param>
    /// <param name="newDataPath">The <c>.new.dat</c> file path. <para><c>.new.dat</c> 文件路径。</para></param>
    /// <param name="outputImagePath">The output raw image file path. <para>输出原始镜像文件路径。</para></param>
    /// <param name="progress">Optional progress reporter (0..1). <para>可选进度回调（0..1）。</para></param>
    /// <exception cref="NotSupportedException">Thrown when a <c>diff</c> or <c>stash</c> command is encountered. <para>遇到 <c>diff</c> 或 <c>stash</c> 命令时抛出。</para></exception>
    public static void Unpack(string transferListPath, string newDataPath, string outputImagePath, IProgress<double>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferListPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newDataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputImagePath);

        var transferList = TransferListParser.Parse(transferListPath);
        var totalBytes = checked(transferList.TotalBlocks * BlockSize);

        using var newData = OpenNewData(newDataPath);
        using var output = new FileStream(outputImagePath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize);
        output.SetLength(totalBytes);

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
                case TransferListCommandKind.Diff:
                case TransferListCommandKind.Stash:
                    throw new NotSupportedException(
                        $"Transfer list command '{command.Kind}' requires incremental OTA data (patch/stash), which is not supported.  transfer list 命令 '{command.Kind}' 需要增量 OTA 数据（patch/stash），暂不支持。");
            }

            foreach (var range in command.Ranges)
            {
                written += range.BlockCount * BlockSize;
            }

            progress?.Report(totalBytes == 0 ? 1.0 : Math.Min(1.0, (double)written / totalBytes));
        }
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