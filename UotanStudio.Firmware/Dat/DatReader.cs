using System.Buffers.Binary;
using UotanStudio.Firmware.Compress.Compressors;
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
        using var newData = OpenNewData(newDataPath);
        UnpackCore(transferList, newData, outputImagePath, progress);
    }

    /// <summary>
    /// Unpacks a DAT image pair into a raw image, additionally supporting a Brotli-compressed data file
    /// (<c>.new.dat.br</c>). When <paramref name="brotliCompressed"/> is <c>true</c> the input is decompressed
    /// to a temporary raw file before unpacking; when <c>false</c> the input is auto-detected: a <c>.br</c>
    /// suffix, or a non-<c>.dat</c> extension whose first byte looks like the Brotli magic, also triggers
    /// decompression; otherwise raw/sparse input is handled exactly like the base overload. The temporary file
    /// is always deleted afterwards.
    /// <para>将 DAT 镜像对解包为原始镜像，额外支持 Brotli 压缩的数据文件（<c>.new.dat.br</c>）。当
    /// <paramref name="brotliCompressed"/> 为 <c>true</c> 时先解压到临时原始文件再解包；为 <c>false</c> 时自动
    /// 检测：<c>.br</c> 后缀，或非 <c>.dat</c> 扩展名且首字节形如 Brotli 魔数，也会触发解压；否则与基础重载
    /// 一致处理原始/稀疏输入。临时文件之后总会删除。</para>
    /// </summary>
    /// <param name="transferListPath">The <c>.transfer.list</c> file path. <para><c>.transfer.list</c> 文件路径。</para></param>
    /// <param name="newDataPath">The <c>.new.dat</c> or <c>.new.dat.br</c> file path. <para><c>.new.dat</c> 或 <c>.new.dat.br</c> 文件路径。</para></param>
    /// <param name="outputImagePath">The output raw image file path. <para>输出原始镜像文件路径。</para></param>
    /// <param name="brotliCompressed">Whether the input is assumed to be Brotli-compressed. <para>输入是否假定为 Brotli 压缩。</para></param>
    /// <param name="progress">Optional progress reporter (0..1). <para>可选进度回调（0..1）。</para></param>
    /// <exception cref="NotSupportedException">Thrown when a <c>diff</c> or <c>stash</c> command is encountered. <para>遇到 <c>diff</c> 或 <c>stash</c> 命令时抛出。</para></exception>
    public static void Unpack(string transferListPath, string newDataPath, string outputImagePath, bool brotliCompressed, IProgress<double>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferListPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newDataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputImagePath);

        if (brotliCompressed || IsBrotliInput(newDataPath))
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"uotan_dat_{Guid.NewGuid():N}.raw");
            try
            {
                DecompressBrotliToFile(newDataPath, tempPath);
                Unpack(transferListPath, tempPath, outputImagePath, progress);
            }
            finally
            {
                TryDeleteTempFile(tempPath);
            }

            return;
        }

        Unpack(transferListPath, newDataPath, outputImagePath, progress);
    }

    /// <summary>
    /// Executes the actual unpack pass against an already-resolved data stream.
    /// <para>对已解析的数据流执行实际的解包过程。</para>
    /// </summary>
    private static void UnpackCore(TransferList transferList, Stream newData, string outputImagePath, IProgress<double>? progress)
    {
        var totalBytes = checked(transferList.TotalBlocks * BlockSize);

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