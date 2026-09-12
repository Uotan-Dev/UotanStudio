using System.Buffers;
using UotanStudio.Firmware.SharedKernel.Streams;
using UotanStudio.Firmware.CrossCutting.Compress;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Zucchini;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Core;

internal static class OperationProcessor
{
    internal static void ProcessDeltaOperation(InstallOperation op, Stream source, FileStream target,
        Stream payload, ulong baseOffset, uint blockSize, CancellationToken ct, PartitionByteProgress? byteProgress = null)
    {
        using var oldData = new ExtentReadStream(source, op.SrcExtents, blockSize);
        using (var hash = new VerifyingReadStream(oldData, op.SrcSha256Hash.ToByteArray()))
        {
            UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.BinaryDeltaPatch.CopyExactly(hash, Stream.Null, oldData.Length, ct);
            hash.Complete();
        }
        oldData.Position = 0;
        using var output = new ExtentWriteStream(target, op.DstExtents, blockSize, requireExactLength: true, byteProgress: byteProgress);
        if (op.Type is InstallOperation.Types.Type.SourceCopy or InstallOperation.Types.Type.Move)
        {
            UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.BinaryDeltaPatch.CopyExactly(oldData, output, oldData.Length, ct);
            output.Complete();
            return;
        }
        using var patch = new BoundedReadStream(payload, checked((long)checked(baseOffset + op.DataOffset)), checked((long)op.DataLength), leaveOpen: true);
        using (var hash = new VerifyingReadStream(patch, op.DataSha256Hash.ToByteArray()))
        {
            UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.BinaryDeltaPatch.CopyExactly(hash, Stream.Null, patch.Length, ct);
            hash.Complete();
        }
        patch.Position = 0;
        if (op.Type is InstallOperation.Types.Type.SourceBsdiff or InstallOperation.Types.Type.Bsdiff or InstallOperation.Types.Type.BrotliBsdiff)
            UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.BinaryDeltaPatch.Apply(oldData, patch, output, output.Length, ct);
        else
        {
            // These codecs need random access to bounded operation buffers.
            const int maximumOperationBytes = 256 * 1024 * 1024;
            if (oldData.Length > maximumOperationBytes || patch.Length > maximumOperationBytes || output.Length > maximumOperationBytes)
                throw new InvalidDataException("This delta codec exceeds the supported 256 MiB operation limit.");
            if (op.Type == InstallOperation.Types.Type.Puffdiff)
            {
                // PuffDiff 支持 Span 输入：old/patch 缓冲租借自 ArrayPool（finally 归还），
                // 消除最高 2×256 MiB 的 LOH 分配。
                // PuffDiff accepts Span inputs: old/patch buffers are rented from ArrayPool
                // (returned in finally), removing up to 2x256 MiB LOH allocations.
                var oldBytes = ArrayPool<byte>.Shared.Rent(checked((int)oldData.Length));
                var patchBytes = ArrayPool<byte>.Shared.Rent(checked((int)patch.Length));
                try
                {
                    oldData.ReadExactly(oldBytes.AsSpan(0, (int)oldData.Length));
                    patch.ReadExactly(patchBytes.AsSpan(0, (int)patch.Length));
                    output.Write(PuffDiffApplier.Apply(
                        patchBytes.AsSpan(0, (int)patch.Length),
                        oldBytes.AsSpan(0, (int)oldData.Length)));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(oldBytes);
                    ArrayPool<byte>.Shared.Return(patchBytes);
                }
            }
            else if (op.Type == InstallOperation.Types.Type.Zucchini)
            {
                // old/result 缓冲池化；解码数据长度不等于输出长度，保留可扩展
                // MemoryStream 并用 GetBuffer() 零复制取出（patch 流直接喂解码器，免物化）。
                // old/result buffers are pooled; the decoded size differs from the output size,
                // so keep an expandable MemoryStream and extract via GetBuffer() (zero copy).
                // The patch stream feeds the decoder directly, avoiding full materialization.
                var oldBytes = ArrayPool<byte>.Shared.Rent(checked((int)oldData.Length));
                var result = ArrayPool<byte>.Shared.Rent(checked((int)output.Length));
                try
                {
                    oldData.ReadExactly(oldBytes.AsSpan(0, (int)oldData.Length));
                    using var decoded = new MemoryStream();
                    using (var decoder = new UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.StrictBrotliReadStream(patch))
                    using (var bounded = new CountingWriteStream(decoded, maximumOperationBytes, leaveOpen: true, cancellationToken: ct))
                        decoder.CopyTo(bounded);
                    ZucchiniPatchApplier.Apply(
                        oldBytes.AsSpan(0, (int)oldData.Length),
                        decoded.GetBuffer().AsSpan(0, checked((int)decoded.Length)),
                        result.AsSpan(0, (int)output.Length));
                    output.Write(result.AsSpan(0, (int)output.Length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(oldBytes);
                    ArrayPool<byte>.Shared.Return(result);
                }
            }
            else if (op.Type is InstallOperation.Types.Type.Lz4diffBsdiff or InstallOperation.Types.Type.Lz4diffPuffdiff)
            {
                // Lz4DiffApplier 仅提供 byte[] API，保持精确长度分配（原行为）。
                // Lz4DiffApplier only exposes a byte[] API; keep exact-length allocations (original behavior).
                var oldBytes = new byte[checked((int)oldData.Length)];
                var patchBytes = new byte[checked((int)patch.Length)];
                oldData.ReadExactly(oldBytes);
                patch.ReadExactly(patchBytes);
                output.Write(Lz4DiffApplier.Apply(oldBytes, patchBytes, checked((int)output.Length)));
            }
            else throw new PayloadUnsupportedOperationException("Unsupported delta operation.", op.Type.ToString());
        }
        output.Complete();
    }
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
        CancellationToken cancellationToken = default,
        PartitionByteProgress? byteProgress = null)
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
            or InstallOperation.Types.Type.ReplaceXz;
        if (isReplace)
        {


            lock (writeLock)
            {
                using var bounded = new SharedKernel.Streams.BoundedReadStream(stream, dataPos, dataLength, leaveOpen: true, syncRoot: streamLock);
                using var hashing = new VerifyingReadStream(bounded, op.DataSha256Hash?.ToByteArray());
                cancellationToken.ThrowIfCancellationRequested();
                ProcessReplaceStream(op, hashing, outFile, blockSize, writeLock, cancellationToken, byteProgress);
                hashing.Complete();
            }
            return;
        }

        if (op.Type is not (InstallOperation.Types.Type.Zero or InstallOperation.Types.Type.Discard))
            throw new PayloadUnsupportedOperationException($"Source-dependent operation {op.Type} cannot run on the parallel path.", op.Type.ToString());
        if (dataLength != 0) throw new PartitionExtractionException("ZERO and DISCARD operations cannot carry a data blob.", string.Empty);
        using var dataRange = new BoundedReadStream(stream, dataPos, dataLength, leaveOpen: true, syncRoot: streamLock);
        using var dataHash = new VerifyingReadStream(dataRange, op.DataSha256Hash.ToByteArray());
        UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.BinaryDeltaPatch.CopyExactly(dataHash, Stream.Null, dataLength, cancellationToken);
        dataHash.Complete();
        lock (writeLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (op.Type == InstallOperation.Types.Type.Zero) ProcessZero(op, outFile, blockSize, cancellationToken, byteProgress);
            else if (op.Type != InstallOperation.Types.Type.Discard)
                throw new PayloadUnsupportedOperationException($"Unsupported parallel operation type: {op.Type}", op.Type.ToString());
        }
    }

    private static void ProcessReplaceStream(InstallOperation op, Stream input, FileStream outFile, uint blockSize, object writeLock, CancellationToken cancellationToken, PartitionByteProgress? byteProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new ExtentWriteStream(outFile, op.DstExtents, blockSize, requireExactLength: true, syncRoot: writeLock, byteProgress: byteProgress);
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
            default:
                throw new PayloadUnsupportedOperationException($"Unsupported replace operation type: {op.Type}", op.Type.ToString());
        }
        output.Complete();
    }

    private static readonly byte[] ZeroBuffer = new byte[65536];

    private static void ProcessZero(InstallOperation op, FileStream outFile, uint blockSize, CancellationToken cancellationToken, PartitionByteProgress? byteProgress = null)
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
            byteProgress?.Add(remaining);
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toWrite = (int)Math.Min(remaining, ZeroBuffer.Length);
                outFile.Write(ZeroBuffer, 0, toWrite);
                remaining -= toWrite;
            }
        }
    }

}