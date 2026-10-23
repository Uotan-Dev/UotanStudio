using System.Buffers;
using UotanStudio.Compress;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.OtaDelta.Diff;
using UotanStudio.Firmware.OtaDelta.Diff.Zucchini;
using UotanStudio.Firmware.OtaDelta.Payload.IO;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;

namespace UotanStudio.Firmware.OtaDelta.Payload;

internal static class OperationProcessor
{
    /// <summary>
    /// Applies one source-dependent delta operation: it maps the operation's source extents over
    /// <paramref name="source"/>, verifies the operation's data blob, decodes the codec named by
    /// <paramref name="op"/> and writes the result into the destination extents of <paramref name="target"/>.
    /// <para>应用一个源依赖的差分操作：把操作的源 extent 映射到 <paramref name="source"/> 上，校验操作的数据块，
    /// 按 <paramref name="op"/> 指定的编解码器解码，并把结果写入 <paramref name="target"/> 的目标 extent。</para>
    /// </summary>
    /// <param name="op">Manifest entry naming the codec, the source and destination extents and both SHA-256 digests.
    /// <para>清单项，指明编解码器、源与目标 extent 以及两个 SHA-256 摘要。</para></param>
    /// <param name="source">Base image the source extents address; it is read, never written.
    /// <para>源 extent 所指向的基座镜像；只读取，不写入。</para></param>
    /// <param name="target">Temporary output file the destination extents are written into; the caller publishes it.
    /// <para>写入目标 extent 的临时输出文件；由调用方负责发布。</para></param>
    /// <param name="payload">Payload stream holding the operation's compressed blob at
    /// <c><paramref name="baseOffset"/> + <see cref="InstallOperation.DataOffset"/></c>.
    /// <para>载荷流，其压缩数据块位于 <c><paramref name="baseOffset"/> + <see cref="InstallOperation.DataOffset"/></c>。</para></param>
    /// <param name="baseOffset">Absolute byte offset in <paramref name="payload"/> where operation data begins.
    /// <para><paramref name="payload"/> 中操作数据起始的绝对字节偏移。</para></param>
    /// <param name="blockSize">Manifest block size converting extent blocks to bytes; it must be nonzero.
    /// <para>清单块大小，用于把 extent 块数换算为字节；必须非零。</para></param>
    /// <param name="ct">Checked between codec chunks and inside the copy loops.
    /// <para>在编解码块之间与复制循环内部检查。</para></param>
    /// <param name="sourceAlreadyVerified">
    /// <see langword="true"/> when the caller has already proven every byte of <paramref name="source"/> against
    /// <see cref="PartitionInfo.Hash"/>, so the per-operation source digest pass is redundant and is skipped; the
    /// operation's own data-blob digest and the partition-level destination digest are enforced either way.
    /// <see langword="false"/> keeps that pass, which then reads each source extent an extra time. When both source
    /// digests are present and disagree, this flag makes the whole-partition digest authoritative.
    /// <para>当调用方已用 <see cref="PartitionInfo.Hash"/> 证明 <paramref name="source"/> 的每个字节时为
    /// <see langword="true"/>，此时逐操作的源摘要趟是冗余的，会被跳过；无论取值如何，操作自身的数据块摘要与分区级
    /// 目标摘要仍会被强制校验。为 <see langword="false"/> 时保留该趟，于是每个源 extent 会多读一遍。
    /// 两个源摘要同时存在且不一致时，该标记使整分区摘要具有权威性。</para>
    /// </param>
    /// <param name="byteProgress">Optional per-partition byte progress sink fed by the destination extent writer.
    /// <para>可选的分区级字节进度回调，由目标 extent 写入器投喂。</para></param>
    /// <exception cref="InvalidDataException">A source extent leaves the base image, a binary patch declares invalid
    /// lengths, or a non-bsdiff codec exceeds its 256 MiB operation ceiling.
    /// <para>某个源 extent 越出基座镜像、二进制补丁声明的长度非法，或非 bsdiff 编解码器超出其 256 MiB 操作上限。</para></exception>
    /// <exception cref="PartitionExtractionException">The operation's data blob is truncated or its SHA-256 digest
    /// disagrees with the manifest. <para>操作的数据块被截断，或其 SHA-256 摘要与清单不一致。</para></exception>
    /// <exception cref="PayloadUnsupportedOperationException">The operation names a codec this processor cannot apply.
    /// <para>操作指定了本处理器无法应用的编解码器。</para></exception>
    /// <exception cref="ZucchiniException">A Zucchini container is malformed, fails either CRC check, or declares an
    /// executable type other than NoOp. <para>Zucchini 容器畸形、任一 CRC 校验失败，或声明了 NoOp 之外的可执行类型。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.
    /// <para><paramref name="ct"/> 已被取消。</para></exception>
    internal static void ProcessDeltaOperation(InstallOperation op, Stream source, FileStream target,
        Stream payload, ulong baseOffset, uint blockSize, CancellationToken ct,
        bool sourceAlreadyVerified = false, PartitionByteProgress? byteProgress = null)
    {
        using var oldData = new ExtentReadStream(source, op.SrcExtents, blockSize);
        if (!sourceAlreadyVerified)
        {
            using var sourceHash = new VerifyingReadStream(oldData, op.SrcSha256Hash.ToByteArray());
            BinaryDeltaPatch.CopyExactly(sourceHash, Stream.Null, oldData.Length, ct);
            sourceHash.Complete();
            oldData.Position = 0;
        }

        using var output = new ExtentWriteStream(target, op.DstExtents, blockSize, requireExactLength: true, byteProgress: byteProgress);
        if (op.Type is InstallOperation.Types.Type.SourceCopy or InstallOperation.Types.Type.Move)
        {
            BinaryDeltaPatch.CopyExactly(oldData, output, oldData.Length, ct);
            output.Complete();
            return;
        }
        using var patch = new BoundedReadStream(payload, checked((long)checked(baseOffset + op.DataOffset)), checked((long)op.DataLength), leaveOpen: true);
        if (op.Type is InstallOperation.Types.Type.SourceBsdiff or InstallOperation.Types.Type.Bsdiff or InstallOperation.Types.Type.BrotliBsdiff)
            ApplyBinaryDelta(op, oldData, patch, output, ct);
        else
        {
            const int maximumOperationBytes = 256 * 1024 * 1024;
            if (oldData.Length > maximumOperationBytes || patch.Length > maximumOperationBytes || output.Length > maximumOperationBytes)
                throw new InvalidDataException("This delta codec exceeds the supported 256 MiB operation limit.");
            var patchBytes = ArrayPool<byte>.Shared.Rent(checked((int)patch.Length));
            try
            {
                using (var hash = new VerifyingReadStream(patch, op.DataSha256Hash.ToByteArray()))
                {
                    hash.ReadExactly(patchBytes.AsSpan(0, (int)patch.Length));
                    hash.Complete();
                }
                ApplyBufferedDelta(op, oldData, patchBytes, (int)patch.Length, output, maximumOperationBytes, ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(patchBytes);
            }
        }
        output.Complete();
    }

    /// <summary>
    /// Applies a binary delta after proving the patch blob with a single sequential read.
    /// <para>用单次顺序读取证明补丁数据块后应用二进制差分。</para>
    /// </summary>
    /// <remarks>
    /// Patches within the 256 MiB buffer ceiling are read once through a verifying wrapper into pooled
    /// memory and applied from that buffer; larger patches keep the streaming verify-then-rewind path
    /// because the binary delta control, diff and extra sections interleave seeks on the patch stream.
    /// <para>不超过 256 MiB 缓冲上限的补丁经校验包装器一次读入池化内存并从缓冲应用；更大的补丁保留流式
    /// "先校验再回退"路径，因为二进制差分的 control、diff 与 extra 三段会在补丁流上交错 seek。</para>
    /// </remarks>
    private static void ApplyBinaryDelta(InstallOperation op, Stream source, Stream patch, ExtentWriteStream output, CancellationToken ct)
    {
        const int maximumPatchBytes = 256 * 1024 * 1024;
        if (patch.Length <= maximumPatchBytes)
        {
            var patchBytes = ArrayPool<byte>.Shared.Rent(checked((int)patch.Length));
            try
            {
                using (var hash = new VerifyingReadStream(patch, op.DataSha256Hash.ToByteArray()))
                {
                    hash.ReadExactly(patchBytes.AsSpan(0, (int)patch.Length));
                    hash.Complete();
                }
                using var buffered = new MemoryStream(patchBytes, 0, (int)patch.Length, writable: false);
                BinaryDeltaPatch.Apply(source, buffered, output, output.Length, ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(patchBytes);
            }
        }
        else
        {
            using var hash = new VerifyingReadStream(patch, op.DataSha256Hash.ToByteArray());
            BinaryDeltaPatch.CopyExactly(hash, Stream.Null, patch.Length, ct);
            hash.Complete();
            patch.Position = 0;
            BinaryDeltaPatch.Apply(source, patch, output, output.Length, ct);
        }
    }

    /// <summary>
    /// Applies a bounded delta codec over an already verified patch buffer.
    /// <para>在已通过校验的补丁缓冲上应用有界差分编解码器。</para>
    /// </summary>
    private static void ApplyBufferedDelta(InstallOperation op, Stream oldData, byte[] patchBytes, int patchLength, ExtentWriteStream output, int maximumOperationBytes, CancellationToken ct)
    {
        var oldLength = checked((int)oldData.Length);
        var outputLength = checked((int)output.Length);
        if (op.Type == InstallOperation.Types.Type.Puffdiff)
        {
            var oldBytes = ArrayPool<byte>.Shared.Rent(oldLength);
            var result = ArrayPool<byte>.Shared.Rent(outputLength);
            try
            {
                oldData.ReadExactly(oldBytes.AsSpan(0, oldLength));
                PuffDiffApplier.Apply(
                    patchBytes.AsSpan(0, patchLength),
                    oldBytes.AsSpan(0, oldLength),
                    result,
                    outputLength);
                output.Write(result.AsSpan(0, outputLength));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(oldBytes);
                ArrayPool<byte>.Shared.Return(result);
            }
        }
        else if (op.Type == InstallOperation.Types.Type.Zucchini)
        {
            var oldBytes = ArrayPool<byte>.Shared.Rent(oldLength);
            var result = ArrayPool<byte>.Shared.Rent(outputLength);
            try
            {
                oldData.ReadExactly(oldBytes.AsSpan(0, oldLength));
                using var decoded = new PooledBufferStream(64 * 1024, maximumOperationBytes);
                using (var decoder = new StrictBrotliReadStream(new MemoryStream(patchBytes, 0, patchLength, writable: false)))
                using (var bounded = new CountingWriteStream(decoded, maximumOperationBytes, leaveOpen: true, cancellationToken: ct))
                {
                    decoder.CopyTo(bounded);
                }

                ZucchiniPatchApplier.Apply(
                    oldBytes.AsSpan(0, oldLength),
                    decoded.WrittenSpan,
                    result.AsSpan(0, outputLength));
                output.Write(result.AsSpan(0, outputLength));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(oldBytes);
                ArrayPool<byte>.Shared.Return(result);
            }
        }
        else if (op.Type is InstallOperation.Types.Type.Lz4diffBsdiff or InstallOperation.Types.Type.Lz4diffPuffdiff)
        {
            var oldBytes = ArrayPool<byte>.Shared.Rent(oldLength);
            var result = ArrayPool<byte>.Shared.Rent(outputLength);
            try
            {
                oldData.ReadExactly(oldBytes.AsSpan(0, oldLength));
                Lz4DiffApplier.Apply(
                    oldBytes.AsSpan(0, oldLength),
                    patchBytes.AsSpan(0, patchLength),
                    result.AsSpan(0, outputLength));
                output.Write(result.AsSpan(0, outputLength));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(oldBytes);
                ArrayPool<byte>.Shared.Return(result);
            }
        }
        else throw new PayloadUnsupportedOperationException("Unsupported delta operation.", op.Type.ToString());
    }
    /// <summary>
    /// Processes a parallelizable install operation by reading data from the payload stream and writing the result to the output file.
    /// Replace-family operations hash-verify while decoding (single pass, no outer write lock: decompression runs outside
    /// <paramref name="writeLock"/>; only the underlying extent Seek+Write is serialized via <see cref="ExtentWriteStream"/>'s sync root).
    /// ZERO writes zeros through an extent-mapped write stream; DISCARD is a no-op.
    /// <para>通过从载荷流读取数据并将结果写入输出文件来处理可并行化的安装操作。
    /// Replace 系操作边解码边校验哈希（单次读取，无外层写锁：解压在 <paramref name="writeLock"/> 之外自由运行，
    /// 仅底层 extent 的 Seek+Write 通过 <see cref="ExtentWriteStream"/> 的 syncRoot 串行化）。
    /// ZERO 通过按区间映射的写入流写零；DISCARD 为空操作。</para>
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
        PartitionByteProgress? byteProgress = null,
        ZstdReplaceDecoder.ZstdDecodeContext? zstdContext = null)
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
            or InstallOperation.Types.Type.ReplaceZstd;
        if (isReplace)
        {
            using var cursor = new IndependentReadStream(stream, streamLock);
            using var bounded = new BoundedReadStream(cursor, dataPos, dataLength, leaveOpen: true);
            using var hashing = new VerifyingReadStream(bounded, op.DataSha256Hash?.ToByteArray());
            cancellationToken.ThrowIfCancellationRequested();
            ProcessReplaceStream(op, hashing, outFile, blockSize, writeLock, cancellationToken, byteProgress, zstdContext);
            hashing.Complete();
            return;
        }

        if (op.Type is not (InstallOperation.Types.Type.Zero or InstallOperation.Types.Type.Discard))
            throw new PayloadUnsupportedOperationException($"Source-dependent operation {op.Type} cannot run on the parallel path.", op.Type.ToString());
        if (dataLength != 0) throw new PartitionExtractionException("ZERO and DISCARD operations cannot carry a data blob.", string.Empty);
        if (op.Type == InstallOperation.Types.Type.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var zeros = new ExtentWriteStream(outFile, op.DstExtents, blockSize, requireExactLength: false, syncRoot: writeLock, byteProgress: byteProgress);
            ProcessZero(zeros, cancellationToken);
        }
        else if (op.Type != InstallOperation.Types.Type.Discard)
            throw new PayloadUnsupportedOperationException($"Unsupported parallel operation type: {op.Type}", op.Type.ToString());
    }

    private static void ProcessReplaceStream(InstallOperation op, Stream input, FileStream outFile, uint blockSize, object writeLock, CancellationToken cancellationToken, PartitionByteProgress? byteProgress = null, ZstdReplaceDecoder.ZstdDecodeContext? zstdContext = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new ExtentWriteStream(outFile, op.DstExtents, blockSize, requireExactLength: true, syncRoot: writeLock, byteProgress: byteProgress);
        switch (op.Type)
        {
            case InstallOperation.Types.Type.Replace:
                var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
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
            case InstallOperation.Types.Type.ReplaceZstd:
                if (zstdContext is null)
                {
                    ZstdReplaceDecoder.Decode(input, output, ReadOnlyMemory<byte>.Empty, cancellationToken);
                }
                else
                {
                    ZstdReplaceDecoder.Decode(input, output, zstdContext, cancellationToken);
                }

                break;
            default:
                throw new PayloadUnsupportedOperationException($"Unsupported replace operation type: {op.Type}", op.Type.ToString());
        }
        output.Complete();
    }

    private static readonly byte[] ZeroBuffer = new byte[65536];

    /// <summary>
    /// Writes zero bytes into the destination extents through the given extent-mapped write stream.
    /// Extent boundary crossing is handled by the stream; byte progress is tracked by the stream itself.
    /// <para>通过给定的按区间映射写入流向目标 extent 写入零字节。extent 跨界由流处理；
    /// 字节进度由流自身统计。</para>
    /// </summary>
    private static void ProcessZero(ExtentWriteStream zeros, CancellationToken cancellationToken)
    {
        var remaining = zeros.Length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toWrite = (int)Math.Min(remaining, ZeroBuffer.Length);
            zeros.Write(ZeroBuffer, 0, toWrite);
            remaining -= toWrite;
        }
    }
}