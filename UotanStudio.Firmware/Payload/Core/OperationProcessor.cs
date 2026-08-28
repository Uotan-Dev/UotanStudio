using System.Buffers;
using BrotliSharpLib;
using BsDiff;
using UotanStudio.Firmware.Compress;
using UotanStudio.Firmware.Payload.Exceptions;
using UotanStudio.Firmware.Payload.Proto;

namespace UotanStudio.Firmware.Payload.Core;

/// <summary>
/// Processes individual install operations during partition extraction.
/// <para>在分区提取期间处理单个安装操作。</para>
/// </summary>
internal static class OperationProcessor
{
    /// <summary>
    /// Processes a parallelizable install operation by reading data from the payload stream
    /// and writing the result to the output file.
    /// <para>通过从载荷流读取数据并将结果写入输出文件来处理可并行化的安装操作。</para>
    /// </summary>
    /// <param name="op">The install operation to process.
    /// <para>要处理的安装操作。</para>
    /// </param>
    /// <param name="outFile">The output file stream to write decompressed data to.
    /// <para>用于写入解压数据的输出文件流。</para>
    /// </param>
    /// <param name="stream">The payload stream to read operation data from.
    /// <para>用于读取操作数据的载荷流。</para>
    /// </param>
    /// <param name="baseOffset">The base offset in the stream where data blobs begin.
    /// <para>流中数据块开始的基偏移量。</para>
    /// </param>
    /// <param name="blockSize">The block size used for extent calculations.
    /// <para>用于区间计算的块大小。</para>
    /// </param>
    /// <param name="writeLock">An object used to synchronize file write operations across threads.
    /// <para>用于跨线程同步文件写入操作的对象。</para>
    /// </param>
    /// <param name="streamLock">An object used to synchronize stream read operations across threads.
    /// <para>用于跨线程同步流读取操作的对象。</para>
    /// </param>
    /// <exception cref="PartitionExtractionException">Thrown when the operation type is not supported or data read fails.
    /// <para>当操作类型不受支持或数据读取失败时抛出。</para>
    /// </exception>
    internal static void ProcessOperation(
        InstallOperation op,
        FileStream outFile,
        Stream stream,
        ulong baseOffset,
        uint blockSize,
        object writeLock,
        object streamLock,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long dataPos;
        long dataLength;
        try
        {
            dataPos = checked((long)checked(baseOffset + op.DataOffset));
            dataLength = checked((long)op.DataLength);
        }
        catch (OverflowException ex)
        {
            throw new PartitionExtractionException("Payload operation data offset or length exceeds Int64 range", string.Empty, ex);
        }

        if (!stream.CanSeek)
        {
            throw new PartitionExtractionException("Payload stream must be seekable", string.Empty);
        }
        if (dataPos > stream.Length || dataLength > stream.Length - dataPos)
        {
            throw new PartitionExtractionException(
                $"Operation data range [{dataPos}, {dataPos + dataLength}) exceeds payload stream length {stream.Length}",
                string.Empty);
        }

        var isReplace = op.Type is InstallOperation.Types.Type.Replace
            or InstallOperation.Types.Type.ReplaceBz
            or InstallOperation.Types.Type.ReplaceXz
            || op.Type == (InstallOperation.Types.Type)14;
        if (isReplace)
        {
            // A streamed replacement writes its destination incrementally. Keep the output lock for
            // the entire operation (including draining/verifying the input hash) so MOVE and other
            // output-dependent operations cannot observe a partially replaced destination.
            lock (writeLock)
            {
                using var bounded = new LockedBoundedReadStream(stream, streamLock, dataPos, dataLength);
                using var hashing = new HashingReadStream(bounded, op.DataSha256Hash?.ToByteArray());
                cancellationToken.ThrowIfCancellationRequested();
                ProcessReplaceStream(op, hashing, outFile, blockSize, writeLock, cancellationToken);
                hashing.Complete();
            }
            return;
        }

        if (op.DataLength > int.MaxValue)
        {
            throw new PartitionExtractionException(
                $"Operation {op.Type} data length {op.DataLength} exceeds the supported byte-array limit {int.MaxValue}",
                string.Empty);
        }

        var bufferLength = checked((int)op.DataLength);
        var data = ArrayPool<byte>.Shared.Rent(bufferLength);
        try
        {
            using var bounded = new LockedBoundedReadStream(stream, streamLock, dataPos, dataLength);
            var bytesRead = 0;
            while (bytesRead < bufferLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = bounded.Read(data, bytesRead, bufferLength - bytesRead);
                if (read == 0) break;
                bytesRead += read;
            }
            if (bytesRead != bufferLength)
            {
                throw new PartitionExtractionException($"Failed to read {bufferLength} operation data bytes; read {bytesRead}", string.Empty);
            }
            HashVerificationHelper.VerifyDataHash(data, bufferLength, op);

            lock (writeLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (op.Type)
                {
                    case InstallOperation.Types.Type.Zero:
                        ProcessZero(op, outFile, blockSize);
                        break;
                    case InstallOperation.Types.Type.Discard:
                        break;
                    case InstallOperation.Types.Type.Move:
                        ProcessMove(op, outFile, blockSize);
                        break;
                    case InstallOperation.Types.Type.Bsdiff:
                    case InstallOperation.Types.Type.SourceCopy:
                    case InstallOperation.Types.Type.SourceBsdiff:
                    case InstallOperation.Types.Type.BrotliBsdiff:
                    case InstallOperation.Types.Type.Zucchini:
                    case InstallOperation.Types.Type.Lz4diffBsdiff:
                    case InstallOperation.Types.Type.Lz4diffPuffdiff:
                        break;
                    case InstallOperation.Types.Type.Puffdiff:
                        ProcessPuffdiff(op, data, bufferLength, outFile, blockSize);
                        break;
                    default:
                        throw new PayloadUnsupportedOperationException($"Unsupported parallel operation type: {op.Type}", op.Type.ToString());
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    /// <summary>
    /// Processes a SourceCopy operation by reading source data from the output file
    /// and writing it to the destination extents.
    /// <para>通过从输出文件读取源数据并将其写入目标区间来处理 SourceCopy 操作。</para>
    /// </summary>
    /// <param name="op">The SourceCopy install operation to process.
    /// <para>要处理的 SourceCopy 安装操作。</para>
    /// </param>
    /// <param name="outFile">The output file stream to read source data from and write destination data to.
    /// <para>用于读取源数据和写入目标数据的输出文件流。</para>
    /// </param>
    /// <param name="blockSize">The block size used for extent calculations.
    /// <para>用于区间计算的块大小。</para>
    /// </param>
    /// <exception cref="PartitionExtractionException">Thrown when source data cannot be read from the output file.
    /// <para>当无法从输出文件读取源数据时抛出。</para>
    /// </exception>
    internal static void ProcessSourceCopy(InstallOperation op, FileStream outFile, uint blockSize)
    {
        foreach (var srcExt in op.SrcExtents)
        {
            var srcPos = (long)(srcExt.StartBlock * blockSize);
            var length = CheckedInt32BlockLength(srcExt.NumBlocks, blockSize);
            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                outFile.Seek(srcPos, SeekOrigin.Begin);
                var bytesRead = 0;
                while (bytesRead < length)
                {
                    var read = outFile.Read(buffer, bytesRead, length - bytesRead);
                    if (read == 0)
                    {
                        break;
                    }
                    bytesRead += read;
                }
                if (bytesRead != length)
                {
                    throw new PartitionExtractionException(
                        $"Failed to read {length} bytes of source data for SOURCE_COPY at offset {srcPos}, only {bytesRead} bytes read",
                        string.Empty);
                }

                HashVerificationHelper.VerifySourceHash(buffer, length, op);

                var dstPos = (long)(op.DstExtents[0].StartBlock * blockSize);
                outFile.Seek(dstPos, SeekOrigin.Begin);
                outFile.Write(buffer, 0, bytesRead);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Processes a MOVE operation by reading source data from the output file into a buffer
    /// and writing it to the destination extents. A buffer is used because source and destination
    /// extents may overlap.
    /// <para>通过从输出文件读取源数据到缓冲区并将其写入目标区间来处理 MOVE 操作。使用缓冲区是因为源区间和目标区间可能重叠。</para>
    /// </summary>
    /// <param name="op">The MOVE install operation to process.
    /// <para>要处理的 MOVE 安装操作。</para>
    /// </param>
    /// <param name="outFile">The output file stream to read source data from and write destination data to.
    /// <para>用于读取源数据和写入目标数据的输出文件流。</para>
    /// </param>
    /// <param name="blockSize">The block size used for extent calculations.
    /// <para>用于区间计算的块大小。</para>
    /// </param>
    /// <exception cref="PartitionExtractionException">Thrown when source data cannot be read from the output file.
    /// <para>当无法从输出文件读取源数据时抛出。</para>
    /// </exception>
    internal static void ProcessMove(InstallOperation op, FileStream outFile, uint blockSize)
    {
        long srcLength = 0;
        foreach (var ext in op.SrcExtents)
        {
            srcLength += (long)ext.NumBlocks * blockSize;
        }

        var buffer = ArrayPool<byte>.Shared.Rent((int)srcLength);
        try
        {
            var offset = 0;
            foreach (var ext in op.SrcExtents)
            {
                var srcPos = (long)(ext.StartBlock * blockSize);
                var chunkLen = CheckedInt32BlockLength(ext.NumBlocks, blockSize);
                outFile.Seek(srcPos, SeekOrigin.Begin);
                var bytesRead = 0;
                while (bytesRead < chunkLen)
                {
                    var read = outFile.Read(buffer, offset + bytesRead, chunkLen - bytesRead);
                    if (read == 0)
                    {
                        break;
                    }
                    bytesRead += read;
                }
                if (bytesRead != chunkLen)
                {
                    throw new PartitionExtractionException(
                        $"Failed to read {chunkLen} bytes of source data for MOVE at offset {srcPos}",
                        string.Empty);
                }
                offset += bytesRead;
            }

            offset = 0;
            foreach (var ext in op.DstExtents)
            {
                var dstPos = (long)(ext.StartBlock * blockSize);
                var chunkLen = CheckedInt32BlockLength(ext.NumBlocks, blockSize);
                outFile.Seek(dstPos, SeekOrigin.Begin);
                outFile.Write(buffer, offset, chunkLen);
                offset += chunkLen;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Processes a SourceBsdiff operation by reading source data from the output file,
    /// applying a bsdiff patch, and writing the result to the destination extents.
    /// <para>通过从输出文件读取源数据，应用 bsdiff 补丁，并将结果写入目标区间来处理 SourceBsdiff 操作。</para>
    /// </summary>
    /// <param name="op">The SourceBsdiff install operation to process.
    /// <para>要处理的 SourceBsdiff 安装操作。</para>
    /// </param>
    /// <param name="data">The patch data blob read from the payload stream.
    /// <para>从载荷流读取的补丁数据块。</para>
    /// </param>
    /// <param name="outFile">The output file stream to read source data from and write patched data to.
    /// <para>用于读取源数据和写入补丁数据的输出文件流。</para>
    /// </param>
    /// <param name="blockSize">The block size used for extent calculations.
    /// <para>用于区间计算的块大小。</para>
    /// </param>
    /// <exception cref="PartitionExtractionException">Thrown when source data cannot be read or the patch fails.
    /// <para>当无法读取源数据或补丁失败时抛出。</para>
    /// </exception>
    internal static void ProcessSourceBsdiff(InstallOperation op, byte[] data, FileStream outFile, uint blockSize)
    {
        long srcLength = 0;
        foreach (var ext in op.SrcExtents)
        {
            srcLength += (long)ext.NumBlocks * blockSize;
        }

        var oldData = ArrayPool<byte>.Shared.Rent((int)srcLength);
        try
        {
            var offset = 0;
            foreach (var ext in op.SrcExtents)
            {
                var srcPos = (long)(ext.StartBlock * blockSize);
                var chunkLen = CheckedInt32BlockLength(ext.NumBlocks, blockSize);
                outFile.Seek(srcPos, SeekOrigin.Begin);
                var bytesRead = 0;
                while (bytesRead < chunkLen)
                {
                    var read = outFile.Read(oldData, offset + bytesRead, chunkLen - bytesRead);
                    if (read == 0)
                    {
                        break;
                    }
                    bytesRead += read;
                }
                if (bytesRead != chunkLen)
                {
                    throw new PartitionExtractionException(
                        $"Failed to read {chunkLen} bytes of source data for SOURCE_BSDIFF at offset {srcPos}",
                        string.Empty);
                }
                offset += bytesRead;
            }

            HashVerificationHelper.VerifySourceHash(oldData, (int)srcLength, op);

            using var oldDataStream = new MemoryStream(oldData, 0, (int)srcLength);
            using var newData = new MemoryStream();
            BinaryPatch.Apply(oldDataStream, () => new MemoryStream(data), newData);

            var dstPos = (long)(op.DstExtents[0].StartBlock * blockSize);
            outFile.Seek(dstPos, SeekOrigin.Begin);
            WriteStreamToFile(newData, outFile);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(oldData);
        }
    }

    /// <summary>
    /// Processes a PUFFDIFF operation by puff-decompressing the attached patch data, reading the
    /// source partition base data at the source extents, applying the bsdiff patch and writing the
    /// result to the destination extents.
    /// <para>处理 PUFFDIFF 操作：对附加的补丁数据做 puff 解压，在源区间处读取源分区基准数据，
    /// 应用 bsdiff 补丁并将结果写入目标区间。</para>
    /// </summary>
    /// <param name="op">The Puffdiff install operation.
    /// <para>PUFFDIFF 安装操作。</para>
    /// </param>
    /// <param name="data">The puff-compressed patch data blob read from the payload stream.
    /// <para>从载荷流读取的 puff 压缩补丁数据块。</para>
    /// </param>
    /// <param name="dataLength">The number of valid bytes in <paramref name="data"/>.
    /// <para><paramref name="data"/> 中的有效字节数。</para>
    /// </param>
    /// <param name="outFile">The output file stream to read source data from and write patched data to.
    /// <para>用于读取源数据和写入补丁数据的输出文件流。</para>
    /// </param>
    /// <param name="blockSize">The block size used for extent calculations.
    /// <para>用于区间计算的块大小。</para>
    /// </param>
    /// <exception cref="PartitionExtractionException">Thrown when source data cannot be read or the patch fails.
    /// <para>当无法读取源数据或补丁失败时抛出。</para>
    /// </exception>
    internal static void ProcessPuffdiff(InstallOperation op, byte[] data, int dataLength, FileStream outFile, uint blockSize)
    {
        long srcLength = 0;
        foreach (var ext in op.SrcExtents)
        {
            srcLength += (long)ext.NumBlocks * blockSize;
        }

        var oldData = ArrayPool<byte>.Shared.Rent((int)srcLength);
        try
        {
            var offset = 0;
            foreach (var ext in op.SrcExtents)
            {
                var srcPos = (long)(ext.StartBlock * blockSize);
                var chunkLen = CheckedInt32BlockLength(ext.NumBlocks, blockSize);
                outFile.Seek(srcPos, SeekOrigin.Begin);
                var bytesRead = 0;
                while (bytesRead < chunkLen)
                {
                    var read = outFile.Read(oldData, offset + bytesRead, chunkLen - bytesRead);
                    if (read == 0)
                    {
                        break;
                    }
                    bytesRead += read;
                }
                if (bytesRead != chunkLen)
                {
                    throw new PartitionExtractionException(
                        $"Failed to read {chunkLen} bytes of source data for PUFFDIFF at offset {srcPos}",
                        string.Empty);
                }
                offset += bytesRead;
            }

            HashVerificationHelper.VerifySourceHash(oldData, (int)srcLength, op);

            var newData = PuffDiffApplier.Apply(data.AsSpan(0, dataLength), oldData.AsSpan(0, (int)srcLength));

            var dstPos = (long)(op.DstExtents[0].StartBlock * blockSize);
            outFile.Seek(dstPos, SeekOrigin.Begin);
            outFile.Write(newData, 0, newData.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(oldData);
        }
    }

    /// <summary>
    /// Processes a BROTLI_BSDIFF operation by decompressing the Brotli-compressed extra data
    /// and applying a bsdiff patch against the existing source data.
    /// <para>通过解压 Brotli 压缩的附加数据并对现有源数据应用 bsdiff 补丁来处理 BROTLI_BSDIFF 操作。</para>
    /// </summary>
    /// <param name="op">The BrotliBsdiff install operation.
    /// <para>BROTLI_BSDIFF 安装操作。</para>
    /// </param>
    /// <param name="data">The patch data blob read from the payload stream.
    /// <para>从载荷流读取的补丁数据块。</para>
    /// </param>
    /// <param name="outFile">The output file stream.
    /// <para>输出文件流。</para>
    /// </param>
    /// <param name="blockSize">The block size used for extent calculations.
    /// <para>用于区间计算的块大小。</para>
    /// </param>
    /// <exception cref="PartitionExtractionException">Thrown when the Brotli decompression or bsdiff patch fails.
    /// <para>当 Brotli 解压或 bsdiff 补丁失败时抛出。</para>
    /// </exception>
    internal static void ProcessBrotliBsdiff(InstallOperation op, byte[] data, FileStream outFile, uint blockSize)
    {
        long srcLength = 0;
        foreach (var ext in op.SrcExtents)
        {
            srcLength += (long)ext.NumBlocks * blockSize;
        }

        var oldData = ArrayPool<byte>.Shared.Rent((int)srcLength);
        try
        {
            var readOffset = 0;
            foreach (var ext in op.SrcExtents)
            {
                var srcPos = (long)(ext.StartBlock * blockSize);
                var chunkLen = CheckedInt32BlockLength(ext.NumBlocks, blockSize);
                outFile.Seek(srcPos, SeekOrigin.Begin);
                var bytesRead = 0;
                while (bytesRead < chunkLen)
                {
                    var read = outFile.Read(oldData, readOffset + bytesRead, chunkLen - bytesRead);
                    if (read == 0)
                    {
                        break;
                    }
                    bytesRead += read;
                }
                if (bytesRead != chunkLen)
                {
                    throw new PartitionExtractionException(
                        $"Failed to read {chunkLen} bytes of source data for BROTLI_BSDIFF at offset {srcPos}",
                        string.Empty);
                }
                readOffset += bytesRead;
            }

            HashVerificationHelper.VerifySourceHash(oldData, (int)srcLength, op);

            using var compressedStream = new MemoryStream(data);
            var decompressedStream = new MemoryStream();
            using (var brotliStream = new BrotliStream(compressedStream, System.IO.Compression.CompressionMode.Decompress))
            {
                brotliStream.CopyTo(decompressedStream);
            }

            using var oldDataStream = new MemoryStream(oldData, 0, (int)srcLength);
            using var newData = new MemoryStream();
            using (decompressedStream)
            {
                BinaryPatch.Apply(oldDataStream, () => new MemoryStream(decompressedStream.GetBuffer(), 0, (int)decompressedStream.Length), newData);
            }

            var dstPos = (long)(op.DstExtents[0].StartBlock * blockSize);
            outFile.Seek(dstPos, SeekOrigin.Begin);
            WriteStreamToFile(newData, outFile);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(oldData);
        }
    }

    /// <summary>
    /// Processes a BSDIFF operation by applying a bsdiff patch against zero-filled source data
    /// and writing the result to the destination extents.
    /// <para>通过对零填充的源数据应用 bsdiff 补丁并将结果写入目标区间来处理 BSDIFF 操作。</para>
    /// </summary>
    /// <param name="op">The BSDIFF install operation to process.
    /// <para>要处理的 BSDIFF 安装操作。</para>
    /// </param>
    /// <param name="data">The patch data blob read from the payload stream.
    /// <para>从载荷流读取的补丁数据块。</para>
    /// </param>
    /// <param name="outFile">The output file stream to write patched data to.
    /// <para>用于写入补丁数据的输出文件流。</para>
    /// </param>
    /// <param name="blockSize">The block size used for extent calculations.
    /// <para>用于区间计算的块大小。</para>
    /// </param>
    internal static void ProcessBsdiff(InstallOperation op, byte[] data, FileStream outFile, uint blockSize)
    {
        long dstLength = 0;
        foreach (var ext in op.DstExtents)
        {
            dstLength += (long)ext.NumBlocks * blockSize;
        }

        var zeroData = ArrayPool<byte>.Shared.Rent((int)dstLength);
        try
        {
            Array.Clear(zeroData, 0, (int)dstLength);

            using var oldDataStream = new MemoryStream(zeroData, 0, (int)dstLength);
            using var newData = new MemoryStream();
            BinaryPatch.Apply(oldDataStream, () => new MemoryStream(data), newData);

            var dstPos = (long)(op.DstExtents[0].StartBlock * blockSize);
            outFile.Seek(dstPos, SeekOrigin.Begin);
            WriteStreamToFile(newData, outFile);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeroData);
        }
    }

    private static void ProcessReplaceStream(InstallOperation op, Stream input, FileStream outFile, uint blockSize, object writeLock, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new ExtentWriteStream(outFile, op.DstExtents, blockSize, requireExactLength: true, syncRoot: writeLock);
        switch (op.Type)
        {
            case InstallOperation.Types.Type.Replace:
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = input.Read(buffer, 0, buffer.Length);
                        if (read == 0) break;
                        cancellationToken.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                break;
            case InstallOperation.Types.Type.ReplaceBz:
                CompressionService.Decompress(input, output, CompressionFormat.Bzip2, output.Length, cancellationToken);
                break;
            case InstallOperation.Types.Type.ReplaceXz:
                CompressionService.Decompress(input, output, CompressionFormat.Xz, output.Length, cancellationToken);
                break;
            case (InstallOperation.Types.Type)14:
                CompressionService.Decompress(input, output, CompressionFormat.Zstd, output.Length, cancellationToken);
                break;
            default:
                throw new PayloadUnsupportedOperationException($"Unsupported replace operation type: {op.Type}", op.Type.ToString());
        }
        output.Complete();
    }

    private static readonly byte[] ZeroBuffer = new byte[65536];

    private static void ProcessZero(InstallOperation op, FileStream outFile, uint blockSize)
    {
        foreach (var ext in op.DstExtents)
        {
            long pos;
            long remaining;
            try
            {
                pos = checked((long)ext.StartBlock * (long)blockSize);
                remaining = checked((long)ext.NumBlocks * (long)blockSize);
            }
            catch (OverflowException ex)
            {
                throw new PartitionExtractionException("ZERO extent size or offset exceeds Int64 range", string.Empty, ex);
            }
            outFile.Seek(pos, SeekOrigin.Begin);
            while (remaining > 0)
            {
                var toWrite = (int)Math.Min(remaining, ZeroBuffer.Length);
                outFile.Write(ZeroBuffer, 0, toWrite);
                remaining -= toWrite;
            }
        }
    }

    private static int CheckedInt32BlockLength(ulong numBlocks, uint blockSize)
    {
        try
        {
            var length = checked(numBlocks * blockSize);
            if (length > int.MaxValue)
            {
                throw new PartitionExtractionException(
                    $"Extent byte length {length} exceeds the supported byte-array limit {int.MaxValue}",
                    string.Empty);
            }
            return (int)length;
        }
        catch (OverflowException ex)
        {
            throw new PartitionExtractionException("Extent byte length overflow", string.Empty, ex);
        }
    }

    private static void WriteStreamToFile(Stream stream, FileStream outFile)
    {
        if (stream is MemoryStream ms && ms.TryGetBuffer(out var buffer))
        {
            outFile.Write(buffer.Array!, buffer.Offset, buffer.Count);
        }
        else
        {
            stream.CopyTo(outFile);
        }
    }
}