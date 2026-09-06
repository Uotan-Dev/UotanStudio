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

internal static class OperationProcessor
{
    /// <summary>
    /// Processes a parallelizable install operation by reading data from the payload stream and writing the result to the output file.
    /// <para>通过从载荷流读取数据并将结果写入输出文件来处理可并行化的安装操作。</para>
    /// </summary>
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
    /// Processes a SourceCopy operation by reading source data from the output file and writing it to the destination extents.
    /// <para>通过从输出文件读取源数据并将其写入目标区间来处理 SourceCopy 操作。</para>
    /// </summary>
    internal static void ProcessSourceCopy(InstallOperation op, FileStream outFile, uint blockSize)
    {


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
    /// Processes a MOVE operation by reading source data from the output file into a buffer and writing it to the destination extents.
    /// <para>通过从输出文件读取源数据到缓冲区并将其写入目标区间来处理 MOVE 操作。</para>
    /// </summary>
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
    /// Processes a SourceBsdiff operation by reading source data from the output file, applying a bsdiff patch, and writing the result to the destination extents.
    /// <para>通过从输出文件读取源数据，应用 bsdiff 补丁，并将结果写入目标区间来处理 SourceBsdiff 操作。</para>
    /// </summary>
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
    /// Processes a PUFFDIFF operation by puff-decompressing the attached patch data, reading the source partition base data at the source extents, applying the bsdiff patch and writing the result to the destination extents.
    /// <para>处理 PUFFDIFF 操作：对附加的补丁数据做 puff 解压，在源区间处读取源分区基准数据， 应用 bsdiff 补丁并将结果写入目标区间。</para>
    /// </summary>
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
    /// Processes a BROTLI_BSDIFF operation by decompressing the Brotli-compressed extra data and applying a bsdiff patch against the existing source data.
    /// <para>通过解压 Brotli 压缩的附加数据并对现有源数据应用 bsdiff 补丁来处理 BROTLI_BSDIFF 操作。</para>
    /// </summary>
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
    /// Processes a BSDIFF operation by reading source data from the output file (the base image when one was provided) and applying a bsdiff patch against it, writing the result to the destination extents.
    /// <para>通过从输出文件（提供基座时即基座镜像）读取源数据、对其应用 bsdiff 补丁并将结果写入 目标区间来处理 BSDIFF 操作。</para>
    /// </summary>
    internal static void ProcessBsdiff(InstallOperation op, byte[] data, FileStream outFile, uint blockSize)
        => ProcessSourceBsdiff(op, data, outFile, blockSize);

    /// <summary>
    /// Processes a ZUCCHINI operation by reading the source base data at the source extents, applying the Zucchini binary delta patch (RAW element subset) and writing the result to the destination extents.
    /// <para>处理 ZUCCHINI 操作：在源区间处读取源基准数据，应用 Zucchini 二进制差分补丁 （RAW 元素子集），并将结果写入目标区间。</para>
    /// </summary>
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
    /// Processes an LZ4DIFF_BSDIFF operation: the attached data blob is an LZ4-compressed bsdiff patch.
    /// <para>处理 LZ4DIFF_BSDIFF 操作：附加的数据块是 LZ4 压缩的 bsdiff 补丁。</para>
    /// </summary>
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
    /// Processes an LZ4DIFF_PUFFDIFF operation: the attached data blob is an LZ4-compressed puffdiff patch.
    /// <para>处理 LZ4DIFF_PUFFDIFF 操作：附加的数据块是 LZ4 压缩的 puffdiff 补丁。</para>
    /// </summary>
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
    /// Decompresses an LZ4 frame whose decompressed size is bounded by maxOutputBytes.
    /// <para>解压 LZ4 帧，解压后大小以 maxOutputBytes 为上限。</para>
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

    private const int MinimumLz4PatchBound = 1 << 20;

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
    /// Writes a fully-produced data stream sequentially across ALL destination extents of an operation, rejecting a length that does not exactly match the destination extent total.
    /// <para>把已生成的数据流按顺序写入操作的全部目标区间，长度与目标区间总长不一致时拒绝。</para>
    /// </summary>
    private static void WriteExactlyToDstExtents(Stream stream, InstallOperation op, FileStream outFile, uint blockSize)
    {
        long dstLength = 0;
        foreach (var ext in op.DstExtents)
        {
            dstLength += (long)ext.NumBlocks * blockSize;
        }

        using var output = new ExtentWriteStream(outFile, op.DstExtents, blockSize, requireExactLength: true);


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
