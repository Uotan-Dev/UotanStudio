using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.Bsdiff;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.ImgDiff;

/// <summary>
/// Raised when the IMGDIFF2 container a build produced fails its own apply-side
/// self-verification, so an internally inconsistent patch is never published. Mirrors the
/// <see cref="InvalidDataException"/> semantics of the imgdiff applier without inheriting its sealed type.
/// <para>当构建生成的 IMGDIFF2 容器未通过其自身的 apply 端自校验时抛出，确保内部不一致的补丁不会被发布。
/// 在语义上对应 imgdiff 应用器的 <see cref="InvalidDataException"/>，但不继承该密封类型。</para>
/// </summary>
public sealed class ImgDiffSelfVerificationException : Exception
{
    /// <summary>Initializes the exception with the default message. <para>使用默认消息初始化异常。</para></summary>
    public ImgDiffSelfVerificationException()
        : base("IMGDIFF2 self-verification failed: applying the generated container to the source image does not reproduce the target image.")
    {
    }

    /// <summary>Initializes the exception with an inner cause. <para>使用内部原因初始化异常。</para></summary>
    /// <param name="innerException">Underlying failure observed while replaying the container. <para>回放容器时观察到的底层失败。</para></param>
    public ImgDiffSelfVerificationException(Exception innerException)
        : base("IMGDIFF2 self-verification failed: applying the generated container to the source image does not reproduce the target image.", innerException)
    {
    }
}

/// <summary>
/// Options for <see cref="ImageDeltaBuilder"/>: after-generation self-verification and an input size ceiling.
/// <para><see cref="ImageDeltaBuilder"/> 的选项：生成后自校验与输入大小上限。</para>
/// </summary>
public sealed class ImgDiffBuilderOptions
{
    /// <summary>Applies the freshly built container back against the source and compares it with the target.
    /// <para>将新生成的容器对源镜像回放并与目标比较。</para></summary>
    /// <value>
    /// <see langword="true"/> (the default) makes <see cref="ImageDeltaBuilder.Build"/> throw
    /// <see cref="ImgDiffSelfVerificationException"/> instead of publishing a patch that cannot rebuild its own
    /// target; <see langword="false"/> skips the extra pass.
    /// <para>默认 <see langword="true"/>：<see cref="ImageDeltaBuilder.Build"/> 会在补丁无法重建自身目标时抛出
    /// <see cref="ImgDiffSelfVerificationException"/> 而非发布；<see langword="false"/> 跳过该额外回放。</para>
    /// </value>
    public bool SelfVerify { get; set; } = true;

    /// <summary>Ceiling for each input image length, guarding the in-memory chunking the container requires.
    /// <para>单侧输入镜像长度上限，用于保护容器所需的内存分块。</para></summary>
    /// <value>
    /// Bytes; the default is 512 MiB. Inputs longer than this are rejected before any read happens because both
    /// images are buffered to lay out the NORMAL/RAW chunks.
    /// <para>字节数；默认 512 MiB。两侧镜像都会被完整缓冲以排布 NORMAL/RAW 块，因此超过该上限的输入在读取前即被拒绝。</para>
    /// </value>
    public long MaximumImageBytes { get; set; } = 512L * 1024 * 1024;
}

/// <summary>
/// Produces IMGDIFF2 containers (VERSION2 chunk layout as parsed by <see cref="ImageDeltaPatch.Apply"/>,
/// which round-trips AOSP recovery imgdiff output) from a raw source and target image: one NORMAL chunk whose
/// payload is a BSDIFF1 stream covering the block-aligned common prefix, plus one RAW chunk carrying the
/// block-misaligned target tail verbatim.
/// <para>由原始源镜像与目标镜像生成 IMGDIFF2 容器（即 <see cref="ImageDeltaPatch.Apply"/> 解析、可往返 AOSP
/// recovery imgdiff 输出的 VERSION2 块布局）：一个 NORMAL 块，其载荷为覆盖块对齐公共前缀的 BSDIFF1 流；
/// 外加一个 RAW 块，原样携带未块对齐的目标尾部。</para>
/// </summary>
/// <remarks>
/// The builder always self-checks by default (see <see cref="ImgDiffBuilderOptions.SelfVerify"/>): the generated
/// container is applied back to the source and compared with the target before the caller can publish it.
/// Correctness is preferred over patch size; unlike AOSP imgdiff this builder never emits DEFLATE-aware chunks,
/// so callers must not claim compression-aware diffs.
/// <para>构建器默认自校验（见 <see cref="ImgDiffBuilderOptions.SelfVerify"/>）：生成的容器会先回放到源镜像并与
/// 目标比较，之后才交由调用方发布。正确性优先于补丁大小；与 AOSP imgdiff 不同，本构建器不发射 DEFLATE 感知块，
/// 调用方不得声称具备压缩感知差分。</para>
/// </remarks>
public static class ImageDeltaBuilder
{
    /// <summary>The Android recovery block size the container layout assumes by default. <para>容器布局默认采用的 Android recovery 块大小。</para></summary>
    public const int DefaultBlockSize = 4096;

    /// <summary>Builds an IMGDIFF2 patch container from raw source and target images. <para>由原始源镜像与目标镜像构建 IMGDIFF2 补丁容器。</para></summary>
    /// <param name="sourceImage">
    /// Base image; read from position <c>0</c> and required to be readable and seekable because self-verification
    /// replays against it. <para>基座镜像；需从位置 <c>0</c> 读取，且因自校验需回放而必须可读、可定位。</para>
    /// </param>
    /// <param name="targetImage">
    /// Desired image; read from position <c>0</c>. Its length minus the block-aligned common prefix becomes the
    /// RAW chunk. <para>目标镜像；从位置 <c>0</c> 读取。其长度减去块对齐公共前缀后成为 RAW 块。</para>
    /// </param>
    /// <param name="patchOutput">
    /// Sink receiving the container bytes from its current position; left open. <para>从其当前位置接收容器字节的输出流；保持打开。</para>
    /// </param>
    /// <param name="blockSize">
    /// Block alignment for the NORMAL/RAW split; must be a positive power of two no larger than 1 MiB. The
    /// recovery updater vocabulary assumes <see cref="DefaultBlockSize"/>. <para>NORMAL/RAW 切分的块对齐；必须是不大于
    /// 1 MiB 的正 2 的幂。recovery 更新器词汇表假定 <see cref="DefaultBlockSize"/>。</para>
    /// </param>
    /// <param name="options">
    /// Self-verification and size-ceiling settings; <see langword="null"/> uses <see cref="ImgDiffBuilderOptions"/>
    /// defaults. Ownership stays with the caller. <para>自校验与大小上限设置；为 <see langword="null"/> 时使用
    /// <see cref="ImgDiffBuilderOptions"/> 默认值。所有权归调用方。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels before reads, during the BSDIFF1 computation and between self-verification passes; a cancelled
    /// build may leave partial bytes in <paramref name="patchOutput"/> and publishes nothing further.
    /// <para>在读取前、BSDIFF1 计算期间及自校验各趟之间响应取消；被取消的构建可能在 <paramref name="patchOutput"/>
    /// 中留下部分字节且不再写入。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// A stream is unusable (not readable/seekable/writable), <paramref name="blockSize"/> is not a positive
    /// power of two within 1 MiB, or an image exceeds <see cref="ImgDiffBuilderOptions.MaximumImageBytes"/>.
    /// <para>流不可用（不可读/不可定位/不可写）、<paramref name="blockSize"/> 不是不大于 1 MiB 的正 2 的幂，
    /// 或镜像超过 <see cref="ImgDiffBuilderOptions.MaximumImageBytes"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="sourceImage"/>, <paramref name="targetImage"/> or <paramref name="patchOutput"/> is
    /// <see langword="null"/>. <para><paramref name="sourceImage"/>、<paramref name="targetImage"/> 或
    /// <paramref name="patchOutput"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Cancellation was requested. <para>已请求取消。</para>
    /// </exception>
    /// <exception cref="ImgDiffSelfVerificationException">
    /// <see cref="ImgDiffBuilderOptions.SelfVerify"/> is set and the generated container does not rebuild the
    /// target byte-for-byte. <para>设置了 <see cref="ImgDiffBuilderOptions.SelfVerify"/> 且生成的容器未能逐字节重建目标。</para>
    /// </exception>
    public static void Build(
        Stream sourceImage,
        Stream targetImage,
        Stream patchOutput,
        int blockSize = DefaultBlockSize,
        ImgDiffBuilderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceImage);
        ArgumentNullException.ThrowIfNull(targetImage);
        ArgumentNullException.ThrowIfNull(patchOutput);
        if (blockSize <= 0 || blockSize > 1024 * 1024 || (blockSize & (blockSize - 1)) != 0)
        {
            throw new ArgumentException("Block size must be a positive power of two no larger than 1 MiB.", nameof(blockSize));
        }

        if (!sourceImage.CanRead || !sourceImage.CanSeek)
        {
            throw new ArgumentException("IMGDIFF2 building requires a readable, seekable source image.", nameof(sourceImage));
        }

        if (!targetImage.CanRead || !targetImage.CanSeek)
        {
            throw new ArgumentException("IMGDIFF2 building requires a readable, seekable target image.", nameof(targetImage));
        }

        if (!patchOutput.CanWrite)
        {
            throw new ArgumentException("The patch output stream must be writable.", nameof(patchOutput));
        }

        options ??= new ImgDiffBuilderOptions();
        if (options.SelfVerify && !patchOutput.CanSeek)
        {
            throw new ArgumentException("IMGDIFF2 self-verification requires a seekable patch output stream.", nameof(patchOutput));
        }

        var maximum = options.MaximumImageBytes;
        if (maximum < 0 || maximum > int.MaxValue)
        {
            maximum = int.MaxValue;
        }

        sourceImage.Position = 0;
        targetImage.Position = 0;
        if (sourceImage.Length > maximum || targetImage.Length > maximum)
        {
            throw new ArgumentException(
                $"IMGDIFF2 inputs exceed the configured {nameof(ImgDiffBuilderOptions.MaximumImageBytes)} ceiling.",
                nameof(sourceImage));
        }

        var containerStart = patchOutput.Position;
        var sourceBytes = new byte[sourceImage.Length];
        sourceImage.ReadExactly(sourceBytes);
        var targetBytes = new byte[targetImage.Length];
        targetImage.ReadExactly(targetBytes);
        BuildFromBuffers(sourceBytes, targetBytes, patchOutput, containerStart, blockSize, options.SelfVerify, cancellationToken);
    }

    private static void BuildFromBuffers(
        byte[] sourceBytes,
        byte[] targetBytes,
        Stream patchOutput,
        long containerStart,
        int blockSize,
        bool selfVerify,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var covered = Math.Min((long)sourceBytes.Length, (long)targetBytes.Length);
        var normalLength = covered == 0 ? 0L : (covered - 1) / blockSize * blockSize + blockSize;
        var rawLength = targetBytes.Length - normalLength;
        var hasNormal = normalLength > 0;
        var hasRaw = rawLength > 0;
        var chunkCount = (hasNormal ? 1 : 0) + (hasRaw ? 1 : 0);
        var normalSourceLength = Math.Min(normalLength, (long)sourceBytes.Length);
        var headerSize = 12 + (hasNormal ? 28 : 0) + (hasRaw ? 8 : 0);
        var normalPatchOffset = headerSize + (hasRaw ? (int)rawLength : 0);

        byte[] normalPatch;
        if (hasNormal)
        {
            using var patchBuffer = new MemoryStream();
            using (var sourceSlice = new MemoryStream(sourceBytes, 0, (int)normalSourceLength, writable: false))
            using (var targetSlice = new MemoryStream(targetBytes, 0, (int)normalLength, writable: false))
            {
                BinaryDeltaPatch.Create(sourceSlice, targetSlice, patchBuffer, ct);
            }

            normalPatch = patchBuffer.ToArray();
        }
        else
        {
            normalPatch = [];
        }

        using (var writer = new BinaryWriter(patchOutput, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("IMGDIFF2"u8.ToArray());
            writer.Write(chunkCount);
            if (hasNormal)
            {
                writer.Write(0);
                writer.Write((long)0);
                writer.Write(normalSourceLength);
                writer.Write((long)normalPatchOffset);
            }

            if (hasRaw)
            {
                writer.Write(3);
                writer.Write((int)rawLength);
            }

            writer.Flush();
        }

        if (hasRaw)
        {
            ct.ThrowIfCancellationRequested();
            patchOutput.Write(targetBytes, (int)normalLength, (int)rawLength);
        }

        if (hasNormal)
        {
            ct.ThrowIfCancellationRequested();
            patchOutput.Write(normalPatch);
        }

        patchOutput.Flush();
        if (!selfVerify)
        {
            return;
        }

        Verify(sourceBytes, targetBytes, patchOutput, containerStart);
    }

    private static void Verify(byte[] sourceBytes, byte[] targetBytes, Stream patchStream, long containerStart)
    {
        var containerLength = patchStream.Position - containerStart;
        try
        {
            using var container = new BoundedReadStream(patchStream, containerStart, containerLength, leaveOpen: true);
            using var rebuilt = BinaryDeltaPatch.CreateTemporaryStream();
            using (var source = new MemoryStream(sourceBytes, writable: false))
            {
                ImageDeltaPatch.Apply(source, container, rebuilt, targetBytes.LongLength);
            }

            if (rebuilt.Length != targetBytes.LongLength)
            {
                throw new ImgDiffSelfVerificationException();
            }

            rebuilt.Position = 0;
            var buffer = new byte[64 * 1024];
            var offset = 0;
            while (offset < targetBytes.Length)
            {
                var read = rebuilt.Read(buffer, 0, Math.Min(buffer.Length, targetBytes.Length - offset));
                if (read == 0)
                {
                    throw new ImgDiffSelfVerificationException();
                }

                if (!targetBytes.AsSpan(offset, read).SequenceEqual(buffer.AsSpan(0, read)))
                {
                    throw new ImgDiffSelfVerificationException();
                }

                offset += read;
            }
        }
        finally
        {
            patchStream.Position = containerStart + containerLength;
        }
    }
}