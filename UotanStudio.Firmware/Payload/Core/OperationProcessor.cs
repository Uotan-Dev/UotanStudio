using System.Buffers;
using BrotliSharpLib;
using BsDiff;
using K4os.Compression.LZ4.Streams;
using UotanStudio.Firmware.Common.Streams;
using UotanStudio.Firmware.Compress;
using UotanStudio.Firmware.Payload.Exceptions;
using UotanStudio.Firmware.Payload.Proto;
using UotanStudio.Firmware.Zucchini;

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
                using var bounded = new Common.Streams.BoundedReadStream(stream, dataPos, dataLength, leaveOpen: true, syncRoot: streamLock);
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
            using var bounded = new Common.Streams.BoundedReadStream(stream, dataPos, dataLength, leaveOpen: true, syncRoot: streamLock);
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
                        // Source-dependent ops must run on the sequential path (they read the output
                        // file). Landing here means a misclassification; silently skipping would
                        // produce a corrupted partition.
                        // 源依赖操作必须走顺序路径（需读取输出文件）。落入这里说明分类错误，
                        // 静默跳过会产生损坏的分区镜像。
                        throw new PayloadUnsupportedOperationException(
                            $"Source-dependent operation {op.Type} was scheduled on the parallel path", op.Type.ToString());
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
        // Upstream delta_performer semantics: source extents are concatenated in order into one
        // data stream, verified against op.src_sha256_hash as a whole, then written sequentially
        // across ALL destination extents. Writing each source extent at DstExtents[0] would
        // overwrite earlier copies for multi-extent operations.
        // 上游 delta_performer 语义：源区间按顺序拼接为一条数据流，整体校验 op.src_sha256_hash，
        // 再按顺序写入全部目标区间。若把每个源区间都写到 DstExtents[0]，多区间操作会互相覆盖。
        long srcLength = 0;
        foreach (var ext in op.SrcExtents)
        {
            srcLength += (long)ext.NumBlocks * blockSize;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(CheckedInt32BlockLengthCeil(srcLength));
        try
        {
            ReadSourceExtents(op, buffer, (int)srcLength, outFile, blockSize);

            HashVerificationHelper.VerifySourceHash(buffer, (int)srcLength, op);

            using var output = new ExtentWriteStream(outFile, op.DstExtents, blockSize, requireExactLength: true);
            output.Write(buffer, 0, (int)srcLength);
            output.Complete();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int CheckedInt32BlockLengthCeil(long length)
    {
        if (length > int.MaxValue)
        {
            throw new PartitionExtractionException(
                $"Extent byte length {length} exceeds the supported byte-array limit {int.MaxValue}",
                string.Empty);
        }

        return checked((int)length);
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
            ReadSourceExtents(op, oldData, (int)srcLength, outFile, blockSize);

            HashVerificationHelper.VerifySourceHash(oldData, (int)srcLength, op);

            using var oldDataStream = new MemoryStream(oldData, 0, (int)srcLength);
            using var newData = new MemoryStream();
            BinaryPatch.Apply(oldDataStream, () => new MemoryStream(data), newData);

            WriteExactlyToDstExtents(newData, op, outFile, blockSize);
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
            ReadSourceExtents(op, oldData, (int)srcLength, outFile, blockSize);

            HashVerificationHelper.VerifySourceHash(oldData, (int)srcLength, op);

            var newData = PuffDiffApplier.Apply(data.AsSpan(0, dataLength), oldData.AsSpan(0, (int)srcLength));

            WriteExactlyToDstExtents(new MemoryStream(newData, writable: false), op, outFile, blockSize);
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
            ReadSourceExtents(op, oldData, (int)srcLength, outFile, blockSize);

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

            WriteExactlyToDstExtents(newData, op, outFile, blockSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(oldData);
        }
    }

    /// <summary>
    /// Processes a BSDIFF operation by reading source data from the output file (the base image
    /// when one was provided) and applying a bsdiff patch against it, writing the result to the
    /// destination extents. This mirrors <see cref="ProcessSourceBsdiff"/>; BSDIFF is the deprecated
    /// predecessor of SOURCE_BSDIFF and per AOSP semantics always reads its source extents.
    /// <para>通过从输出文件（提供基座时即基座镜像）读取源数据、对其应用 bsdiff 补丁并将结果写入
    /// 目标区间来处理 BSDIFF 操作。该实现与 <see cref="ProcessSourceBsdiff"/> 一致；BSDIFF 是
    /// SOURCE_BSDIFF 的已弃用前身，按 AOSP 语义始终从源区间读取数据。</para>
    /// </summary>
    /// <param name="op">The BSDIFF install operation to process.
    /// <para>要处理的 BSDIFF 安装操作。</para>
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
    internal static void ProcessBsdiff(InstallOperation op, byte[] data, FileStream outFile, uint blockSize)
        => ProcessSourceBsdiff(op, data, outFile, blockSize);

    /// <summary>
    /// Processes a ZUCCHINI operation by reading the source base data at the source extents, applying
    /// the Zucchini binary delta patch (RAW element subset) and writing the result to the destination
    /// extents. Source and destination buffers are rented from the shared array pool, so peak memory
    /// stays bounded by the source plus destination extent length and the patch data blob.
    /// <para>处理 ZUCCHINI 操作：在源区间处读取源基准数据，应用 Zucchini 二进制差分补丁
    /// （RAW 元素子集），并将结果写入目标区间。源与目标缓冲区均取自共享数组池，峰值内存
    /// 由源与目标区间长度及补丁数据块之和限定。</para>
    /// </summary>
    /// <param name="op">The Zucchini install operation.
    /// <para>ZUCCHINI 安装操作。</para>
    /// </param>
    /// <param name="data">The Zucchini patch data blob read from the payload stream.
    /// <para>从载荷流读取的 Zucchini 补丁数据块。</para>
    /// </param>
    /// <param name="outFile">The output file stream to read source data from and write patched data to.
    /// <para>用于读取源数据和写入补丁数据的输出文件流。</para>
    /// </param>
    /// <param name="blockSize">The block size used for extent calculations.
    /// <para>用于区间计算的块大小。</para>
    /// </param>
    /// <exception cref="PartitionExtractionException">Thrown when source data cannot be read, the patch
    /// is invalid or its CRC-32 checks fail, or the destination extents cannot be written.
    /// <para>当无法读取源数据、补丁非法或其 CRC-32 校验失败、或无法写入目标区间时抛出。</para>
    /// </exception>
    internal static void ProcessZucchini(InstallOperation op, byte[] data, FileStream outFile, uint blockSize)
    {
        long srcLength = 0;
        foreach (var ext in op.SrcExtents)
        {
            srcLength += (long)ext.NumBlocks * blockSize;
        }

        long dstLength = 0;
        foreach (var ext in op.DstExtents)
        {
            dstLength += (long)ext.NumBlocks * blockSize;
        }

        if (srcLength > int.MaxValue)
        {
            throw new PartitionExtractionException(
                $"ZUCCHINI source length {srcLength} exceeds the supported byte-array limit {int.MaxValue}. " +
                $"ZUCCHINI 源长度 {srcLength} 超过支持的字节数组上限 {int.MaxValue}。",
                string.Empty);
        }
        if (dstLength > int.MaxValue)
        {
            throw new PartitionExtractionException(
                $"ZUCCHINI destination length {dstLength} exceeds the supported byte-array limit {int.MaxValue}. " +
                $"ZUCCHINI 目标长度 {dstLength} 超过支持的字节数组上限 {int.MaxValue}。",
                string.Empty);
        }

        var oldData = ArrayPool<byte>.Shared.Rent((int)srcLength);
        var newData = ArrayPool<byte>.Shared.Rent((int)dstLength);
        try
        {
            ReadSourceExtents(op, oldData, (int)srcLength, outFile, blockSize);

            HashVerificationHelper.VerifySourceHash(oldData, (int)srcLength, op);

            try
            {
                ZucchiniPatchApplier.Apply(oldData.AsSpan(0, (int)srcLength), data, newData.AsSpan(0, (int)dstLength));
            }
            catch (ZucchiniException ex)
            {
                throw new PartitionExtractionException(
                    $"ZUCCHINI patch application failed for operation at data offset {op.DataOffset}: {ex.Message} " +
                    $"ZUCCHINI 补丁应用失败（数据偏移 {op.DataOffset}）。",
                    string.Empty,
                    ex);
            }

            using var output = new ExtentWriteStream(outFile, op.DstExtents, blockSize, requireExactLength: true);
            output.Write(newData, 0, (int)dstLength);
            output.Complete();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(oldData);
            ArrayPool<byte>.Shared.Return(newData);
        }
    }

    /// <summary>
    /// Processes an LZ4DIFF_BSDIFF operation: the attached data blob is an LZ4-compressed bsdiff
    /// patch. The patch is decompressed under a strict output bound (source plus destination extent
    /// length) to guard against decompression bombs, then applied against the source data read from
    /// the output file (the base image when one was provided).
    /// <para>处理 LZ4DIFF_BSDIFF 操作：附加的数据块是 LZ4 压缩的 bsdiff 补丁。补丁在严格的输出上限
    /// （源区间与目标区间长度之和）下解压以防解压炸弹，随后对从输出文件（提供基座时即基座镜像）
    /// 读取的源数据应用补丁。</para>
    /// </summary>
    /// <param name="op">The Lz4diffBsdiff install operation.
    /// <para>LZ4DIFF_BSDIFF 安装操作。</para>
    /// </param>
    /// <param name="data">The LZ4-compressed patch data blob read from the payload stream.
    /// <para>从载荷流读取的 LZ4 压缩补丁数据块。</para>
    /// </param>
    /// <param name="outFile">The output file stream to read source data from and write patched data to.
    /// <para>用于读取源数据和写入补丁数据的输出文件流。</para>
    /// </param>
    /// <param name="blockSize">The block size used for extent calculations.
    /// <para>用于区间计算的块大小。</para>
    /// </param>
    /// <exception cref="PartitionExtractionException">Thrown when source data cannot be read, the patch
    /// exceeds the decompression bound, or the patch fails.
    /// <para>当无法读取源数据、补丁超过解压上限或补丁失败时抛出。</para>
    /// </exception>
    internal static void ProcessLz4diffBsdiff(InstallOperation op, byte[] data, FileStream outFile, uint blockSize)
    {
        long srcLength = 0;
        foreach (var ext in op.SrcExtents)
        {
            srcLength += (long)ext.NumBlocks * blockSize;
        }

        var oldData = ArrayPool<byte>.Shared.Rent((int)srcLength);
        try
        {
            ReadSourceExtents(op, oldData, (int)srcLength, outFile, blockSize);

            HashVerificationHelper.VerifySourceHash(oldData, (int)srcLength, op);

            var maxPatchBytes = CheckedLz4PatchBound(srcLength, op, blockSize);
            var patch = DecompressLz4Patch(data, maxPatchBytes);

            using var oldDataStream = new MemoryStream(oldData, 0, (int)srcLength);
            using var newData = new MemoryStream();
            BinaryPatch.Apply(oldDataStream, () => new MemoryStream(patch, writable: false), newData);

            WriteExactlyToDstExtents(newData, op, outFile, blockSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(oldData);
        }
    }

    /// <summary>
    /// Processes an LZ4DIFF_PUFFDIFF operation: the attached data blob is an LZ4-compressed puffdiff
    /// patch. The patch is decompressed under a strict output bound (source plus destination extent
    /// length) to guard against decompression bombs, then applied against the source data read from
    /// the output file (the base image when one was provided).
    /// <para>处理 LZ4DIFF_PUFFDIFF 操作：附加的数据块是 LZ4 压缩的 puffdiff 补丁。补丁在严格的输出
    /// 上限（源区间与目标区间长度之和）下解压以防解压炸弹，随后对从输出文件（提供基座时即基座
    /// 镜像）读取的源数据应用补丁。</para>
    /// </summary>
    /// <param name="op">The Lz4diffPuffdiff install operation.
    /// <para>LZ4DIFF_PUFFDIFF 安装操作。</para>
    /// </param>
    /// <param name="data">The LZ4-compressed patch data blob read from the payload stream.
    /// <para>从载荷流读取的 LZ4 压缩补丁数据块。</para>
    /// </param>
    /// <param name="outFile">The output file stream to read source data from and write patched data to.
    /// <para>用于读取源数据和写入补丁数据的输出文件流。</para>
    /// </param>
    /// <param name="blockSize">The block size used for extent calculations.
    /// <para>用于区间计算的块大小。</para>
    /// </param>
    /// <exception cref="PartitionExtractionException">Thrown when source data cannot be read, the patch
    /// exceeds the decompression bound, or the patch fails.
    /// <para>当无法读取源数据、补丁超过解压上限或补丁失败时抛出。</para>
    /// </exception>
    internal static void ProcessLz4diffPuffdiff(InstallOperation op, byte[] data, FileStream outFile, uint blockSize)
    {
        long srcLength = 0;
        foreach (var ext in op.SrcExtents)
        {
            srcLength += (long)ext.NumBlocks * blockSize;
        }

        var oldData = ArrayPool<byte>.Shared.Rent((int)srcLength);
        try
        {
            ReadSourceExtents(op, oldData, (int)srcLength, outFile, blockSize);

            HashVerificationHelper.VerifySourceHash(oldData, (int)srcLength, op);

            var maxPatchBytes = CheckedLz4PatchBound(srcLength, op, blockSize);
            var patch = DecompressLz4Patch(data, maxPatchBytes);

            var newData = PuffDiffApplier.Apply(patch, oldData.AsSpan(0, (int)srcLength));

            WriteExactlyToDstExtents(new MemoryStream(newData, writable: false), op, outFile, blockSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(oldData);
        }
    }

    private static void ReadSourceExtents(InstallOperation op, byte[] buffer, int srcLength, FileStream outFile, uint blockSize)
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
                    $"Failed to read {chunkLen} bytes of source data for {op.Type} at offset {srcPos}",
                    string.Empty);
            }
            offset += bytesRead;
        }

        if (offset != srcLength)
        {
            throw new PartitionExtractionException(
                $"Source extents produced {offset} bytes but {srcLength} were expected for {op.Type}",
                string.Empty);
        }
    }

    /// <summary>
    /// Decompresses an LZ4 frame whose decompressed size is bounded by <paramref name="maxOutputBytes"/>.
    /// <para>解压 LZ4 帧，解压后大小以 <paramref name="maxOutputBytes"/> 为上限。</para>
    /// </summary>
    private static byte[] DecompressLz4Patch(byte[] data, int maxOutputBytes)
    {
        if (data.Length == 0)
        {
            throw new PartitionExtractionException("LZ4 diff patch data is empty", string.Empty);
        }

        try
        {
            using var input = new MemoryStream(data, writable: false);
            using var decoder = LZ4Stream.Decode(input, leaveOpen: true);
            using var output = new MemoryStream();
            using (var bounded = new CountingWriteStream(output, maxOutputBytes, leaveOpen: true))
            {
                decoder.CopyTo(bounded);
            }

            return output.ToArray();
        }
        catch (IOException ex)
        {
            throw new PartitionExtractionException(
                $"LZ4 diff patch decompression exceeded the output bound of {maxOutputBytes} bytes. " +
                $"LZ4 差分补丁解压超过输出上限 {maxOutputBytes} 字节。",
                string.Empty,
                ex);
        }
        catch (Exception ex)
        {
            throw new PartitionExtractionException($"LZ4 diff patch decompression failed. LZ4 差分补丁解压失败。", string.Empty, ex);
        }
    }

    private const int MinimumLz4PatchBound = 1 << 20; // 1 MiB floor so small valid patches (32-byte bsdiff header + control overhead) are not rejected.

    private static int CheckedLz4PatchBound(long srcLength, InstallOperation op, uint blockSize)
    {
        long dstLength = 0;
        foreach (var ext in op.DstExtents)
        {
            dstLength += (long)ext.NumBlocks * blockSize;
        }

        long bound;
        try
        {
            bound = checked(srcLength + dstLength);
        }
        catch (OverflowException ex)
        {
            throw new PartitionExtractionException("LZ4 diff patch output bound overflow", string.Empty, ex);
        }

        if (bound > int.MaxValue)
        {
            throw new PartitionExtractionException(
                $"LZ4 diff patch output bound {bound} exceeds the supported byte-array limit {int.MaxValue}. " +
                $"LZ4 差分补丁输出上限 {bound} 超过支持的字节数组上限 {int.MaxValue}。",
                string.Empty);
        }
        return Math.Max((int)bound, MinimumLz4PatchBound);
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
                pos = checked((long)ext.StartBlock * blockSize);
                remaining = checked((long)ext.NumBlocks * blockSize);
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

    /// <summary>
    /// Writes a fully-produced data stream sequentially across ALL destination extents of an
    /// operation, rejecting a length that does not exactly match the destination extent total.
    /// This mirrors upstream delta_performer, whose ExtentWriter stack maps the output across
    /// every destination extent rather than only the first one.
    /// <para>把已生成的数据流按顺序写入操作的全部目标区间，长度与目标区间总长不一致时拒绝。
    /// 与上游 delta_performer 的 ExtentWriter 栈一致：输出映射到每一个目标区间，
    /// 而不是只写第一个。</para>
    /// </summary>
    /// <param name="stream">The data stream to write; must contain exactly the destination length.
    /// <para>待写入的数据流；长度必须恰好等于目标区间总长。</para></param>
    /// <param name="op">The install operation providing the destination extents.
    /// <para>提供目标区间的安装操作。</para></param>
    /// <param name="outFile">The output file stream. <para>输出文件流。</para></param>
    /// <param name="blockSize">The block size used for extent calculations.
    /// <para>用于区间计算的块大小。</para></param>
    private static void WriteExactlyToDstExtents(Stream stream, InstallOperation op, FileStream outFile, uint blockSize)
    {
        long dstLength = 0;
        foreach (var ext in op.DstExtents)
        {
            dstLength += (long)ext.NumBlocks * blockSize;
        }

        using var output = new ExtentWriteStream(outFile, op.DstExtents, blockSize, requireExactLength: true);
        // BinaryPatch.Apply / 解压器可能把流位置留在末尾；写入前必须回到起点，
        // 否则读到的长度为 0。
        // BinaryPatch.Apply / decompressors may leave the stream position at the end; rewind
        // before writing, otherwise the read length is 0.
        if (stream.CanSeek)
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        output.Complete();
    }
}