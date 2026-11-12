using System.Diagnostics;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Images;
using UotanStudio.DD.Writing;
using UotanStudio.Hash.Digests;

namespace UotanStudio.DD;

/// <summary>
/// Orchestrates image validation, multi-target writing, read-back verification and progress reporting.
/// <para>编排镜像校验、多目标写入、回读校验与进度上报。</para>
/// </summary>
/// <remarks>
/// Each chunk is read once, decoded once and written to every target before the next chunk is
/// read. Raw block devices receive chunks rounded up to their alignment with zero padding, while the
/// checksum, progress and verification cover only the real image bytes. Verification re-opens the
/// source and compares each target byte-for-byte.
/// <para>
/// 每个块只读取、解码一次，写入全部目标后才读取下一块。裸块设备收到的块按对齐要求向上取整并以零填充，
/// 校验和、进度与回读比较只覆盖镜像的真实字节。校验阶段重新打开源并对逐个目标逐字节比较。
/// </para>
/// </remarks>
public sealed class ImageFlasher
{
    private readonly IDiskTargetFactory _targetFactory;

    /// <summary>
    /// Creates a flasher.
    /// <para>创建烧录器。</para>
    /// </summary>
    /// <param name="targetFactory">
    /// Factory that opens raw devices; <see langword="null"/> uses <see cref="PlatformDiskTargetFactory.Instance"/>.
    /// <para>打开裸设备的工厂；<see langword="null"/> 时使用 <see cref="PlatformDiskTargetFactory.Instance"/>。</para>
    /// </param>
    public ImageFlasher(IDiskTargetFactory? targetFactory = null)
    {
        _targetFactory = targetFactory ?? PlatformDiskTargetFactory.Instance;
    }

    /// <summary>
    /// Writes one image to one or more drives and optionally verifies the result.
    /// <para>把一个镜像写入一个或多个驱动器，并可选地校验结果。</para>
    /// </summary>
    /// <param name="image">Image returned by <see cref="ImageProbe.Probe"/>.
    /// <para>由 <see cref="ImageProbe.Probe"/> 返回的镜像。</para></param>
    /// <param name="drives">Selected drives; every drive receives the full image.
    /// <para>选中的驱动器；每个驱动器都会收到完整镜像。</para></param>
    /// <param name="options">Safety and buffer settings.<para>安全与缓冲区设置。</para></param>
    /// <param name="progress">Optional progress sink.<para>可选的进度接收器。</para></param>
    /// <param name="cancellationToken">Cancels preparation, writing or verification.
    /// <para>取消准备、写入或校验。</para></param>
    /// <returns>Summary of the completed operation.<para>已完成操作的汇总。</para></returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.
    /// <para>必需参数为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">No drives were supplied or a drive appears more than once.
    /// <para>未提供驱动器，或同一驱动器出现多次。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="FlashOptions.BufferBytes"/> is outside its accepted range.
    /// <para><see cref="FlashOptions.BufferBytes"/> 超出允许范围。</para></exception>
    /// <exception cref="FlashException"><see cref="FlashOptions.ConfirmOverwrite"/> is not set.
    /// <para><see cref="FlashOptions.ConfirmOverwrite"/> 未设置。</para></exception>
    /// <exception cref="FlashValidationException">A drive failed the compatibility policy.
    /// <para>驱动器未通过兼容性策略。</para></exception>
    /// <exception cref="FlashVerificationException">Read-back bytes differ from the source.
    /// <para>回读字节与源不一致。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled the operation.
    /// <para>调用方取消了操作。</para></exception>
    public async Task<FlashResult> FlashAsync(
        FlashImage image,
        IReadOnlyList<FlashDrive> drives,
        FlashOptions options,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        if (drives.Count == 0)
        {
            throw new ArgumentException("At least one target drive is required.", nameof(drives));
        }

        EnsureUniqueDrives(drives);
        ValidateDrives(image, drives, options);

        var targets = new List<IDiskTarget>(drives.Count);
        var completed = false;
        var totalWatch = Stopwatch.StartNew();
        try
        {
            foreach (var drive in drives)
            {
                targets.Add(_targetFactory.Create(drive));
            }

            var totalBytes = MultiplyPerTarget(image.Size, targets.Count);
            var progressReporter = new FlashProgressReporter(progress, targets.Count);
            progressReporter.EnterStage(FlashStage.Preparing, totalBytes);

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await target.PrepareAsync(cancellationToken).ConfigureAwait(false);
            }

            var alignment = Math.Max(1, targets.Max(target => target.ChunkAlignmentBytes));
            if (alignment > options.BufferBytes)
            {
                throw new FlashException(
                    $"The target alignment of {alignment} bytes exceeds the {options.BufferBytes}-byte write buffer.");
            }

            var chunkBytes = (options.BufferBytes / alignment) * alignment;
            var buffer = new byte[chunkBytes];
            progressReporter.EnterStage(FlashStage.Writing, totalBytes);
            long written = 0;
            using (var digest = Sha256.CreateAccumulator())
            using (var source = ImageStreamReader.OpenRead(image))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await ReadChunkAsync(source, buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    var padded = AlignUp(read, alignment);
                    if (padded > read)
                    {
                        buffer.AsSpan(read, padded - read).Clear();
                    }

                    digest.Append(buffer.AsSpan(0, read));
                    await WriteChunkAsync(targets, buffer.AsMemory(0, padded), cancellationToken).ConfigureAwait(false);
                    written += (long)read * targets.Count;
                    progressReporter.Report(written, totalBytes);
                }

                foreach (var target in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await target.CompleteAsync(cancellationToken).ConfigureAwait(false);
                }

                if (options.VerifyAfterWrite)
                {
                    await VerifyAsync(image, targets, buffer, alignment, totalBytes, progressReporter, cancellationToken)
                        .ConfigureAwait(false);
                }

                totalWatch.Stop();
                var elapsed = totalWatch.Elapsed;
                var imageBytes = targets.Count == 0 ? 0 : written / targets.Count;
                progressReporter.Complete(written, totalBytes, elapsed);

                completed = true;
                return new FlashResult
                {
                    Targets = drives,
                    ImageBytes = imageBytes,
                    BytesWritten = written,
                    Elapsed = elapsed,
                    SourceChecksum = Convert.ToHexStringLower(digest.GetHashAndReset()),
                };
            }
        }
        catch (Exception exception) when (exception is not FlashException and not OperationCanceledException)
        {
            throw new FlashException($"The image \"{image.DisplayName}\" could not be flashed.", exception);
        }
        finally
        {
            for (var index = targets.Count - 1; index >= 0; index--)
            {
                try
                {
                    await targets[index].DisposeAsync().ConfigureAwait(false);
                }
                catch when (!completed)
                {
                }
            }
        }
    }

    internal static void ValidateOptions(FlashOptions options)
    {
        if (options.BufferBytes < FlashOptions.MinimumBufferBytes || options.BufferBytes > FlashOptions.MaximumBufferBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.BufferBytes,
                $"BufferBytes must be between {FlashOptions.MinimumBufferBytes} and {FlashOptions.MaximumBufferBytes}.");
        }

        if (!options.ConfirmOverwrite)
        {
            throw new FlashException(
                "ConfirmOverwrite must be true because flashing replaces every byte on the selected drives.");
        }
    }

    private static void EnsureUniqueDrives(IReadOnlyList<FlashDrive> drives)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in drives)
        {
            if (!seen.Add(drive.DevicePath))
            {
                throw new ArgumentException(
                    $"The drive \"{drive.DevicePath}\" was selected more than once.",
                    nameof(drives));
            }
        }
    }

    private static void ValidateDrives(FlashImage image, IReadOnlyList<FlashDrive> drives, FlashOptions options)
    {
        var failures = new List<DriveValidationFailure>();
        foreach (var drive in drives)
        {
            var issues = DriveCompatibilityEvaluator.Evaluate(drive, image, writing: true);
            var blocking = issues
                .Where(issue => DriveCompatibilityEvaluator.IsBlocking(issue, options.AllowLargeDrive))
                .ToArray();
            if (blocking.Length > 0)
            {
                failures.Add(new DriveValidationFailure(drive, blocking));
            }
        }

        if (failures.Count > 0)
        {
            throw new FlashValidationException(failures);
        }
    }

    private static async Task WriteChunkAsync(
        IReadOnlyList<IDiskTarget> targets,
        ReadOnlyMemory<byte> chunk,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 1)
        {
            await WriteToTargetAsync(targets[0], chunk, cancellationToken).ConfigureAwait(false);
            return;
        }

        var writes = new Task[targets.Count];
        for (var index = 0; index < targets.Count; index++)
        {
            writes[index] = WriteToTargetAsync(targets[index], chunk, cancellationToken);
        }

        await Task.WhenAll(writes).ConfigureAwait(false);
    }

    private static async Task WriteToTargetAsync(
        IDiskTarget target,
        ReadOnlyMemory<byte> chunk,
        CancellationToken cancellationToken)
    {
        try
        {
            await target.Stream.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FlashException(
                $"Writing to {target.Drive.DisplayName} ({target.Drive.DevicePath}) failed.",
                exception);
        }
    }

    private static async Task VerifyAsync(
        FlashImage image,
        IReadOnlyList<IDiskTarget> targets,
        byte[] sourceBuffer,
        int alignment,
        long? totalBytes,
        FlashProgressReporter progressReporter,
        CancellationToken cancellationToken)
    {
        var verifyBuffer = new byte[sourceBuffer.Length];
        long processed = 0;
        long imageOffset = 0;
        foreach (var target in targets)
        {
            target.Stream.Seek(0, SeekOrigin.Begin);
        }

        progressReporter.EnterStage(FlashStage.Verifying, totalBytes);

        using var source = ImageStreamReader.OpenRead(image);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await ReadChunkAsync(source, sourceBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var padded = AlignUp(read, alignment);
            foreach (var target in targets)
            {
                var actualRead = await ReadAtLeastAsync(target.Stream, verifyBuffer.AsMemory(0, padded), cancellationToken)
                    .ConfigureAwait(false);
                if (actualRead < read)
                {
                    throw new FlashVerificationException(target.Drive, imageOffset + actualRead);
                }

                if (!sourceBuffer.AsSpan(0, read).SequenceEqual(verifyBuffer.AsSpan(0, read)))
                {
                    throw new FlashVerificationException(target.Drive, imageOffset + FindMismatch(sourceBuffer.AsSpan(0, read), verifyBuffer.AsSpan(0, read)));
                }

                processed += read;
            }

            imageOffset += read;
            progressReporter.Report(processed, totalBytes);
        }
    }

    /// <summary>Locates the first differing byte between two equal-length spans. <para>定位两个等长跨度中首个不同的字节。</para></summary>
    /// <returns>The zero-based mismatch offset, or -1 when the spans are equal. <para>从零起算的差异偏移；两段相等时为 -1。</para></returns>
    private static int FindMismatch(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index] != actual[index])
            {
                return index;
            }
        }

        return -1;
    }

    private static async ValueTask<int> ReadChunkAsync(
        Stream source,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static int AlignUp(int value, int alignment) =>
        alignment <= 1 ? value : ((value + alignment - 1) / alignment) * alignment;

    private static async Task<int> ReadAtLeastAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static long? MultiplyPerTarget(long? perTarget, int count)
    {
        if (perTarget is null)
        {
            return null;
        }

        return perTarget.Value > long.MaxValue / count ? long.MaxValue : perTarget.Value * count;
    }
}