using UotanStudio.DD.Drives;

namespace UotanStudio.DD.Writing;

/// <summary>
/// Shared validation and progress reporting for optical disc writers.
/// <para>光盘写入器共用的校验与进度上报逻辑。</para>
/// </summary>
internal abstract class OpticalDiscWriterBase(FlashDrive drive) : IDiscWriter
{
    /// <inheritdoc />
    public FlashDrive Drive { get; } = drive;

    /// <inheritdoc />
    public async Task WriteAsync(
        OpticalDiscSession session,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        if (Drive.MediaKind != FlashMediaKind.OpticalDisc)
        {
            throw new FlashException("The selected drive is not an optical recorder.");
        }

        if (Drive.IsDiscWritable == false || Drive.IsReadOnly)
        {
            throw new FlashException(
                "The optical recorder does not have writable media loaded. Insert a blank or rewritable disc.");
        }

        if (string.IsNullOrWhiteSpace(session.ImagePath) || !File.Exists(session.ImagePath))
        {
            throw new FlashException("The image file for optical recording does not exist.");
        }

        progress?.Report(new FlashProgress
        {
            Stage = FlashStage.Preparing,
            BytesProcessed = 0,
            TargetCount = 1,
        });
        cancellationToken.ThrowIfCancellationRequested();

        var imageBytes = new FileInfo(session.ImagePath).Length;
        progress?.Report(new FlashProgress
        {
            Stage = FlashStage.Writing,
            BytesProcessed = 0,
            TotalBytes = imageBytes,
            TargetCount = 1,
        });

        await WriteCoreAsync(session, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new FlashProgress
        {
            Stage = FlashStage.Completed,
            BytesProcessed = imageBytes,
            TotalBytes = imageBytes,
            Percentage = 100,
            TargetCount = 1,
        });
    }

    /// <summary>
    /// Performs the actual platform recording after validation succeeds.
    /// <para>校验成功后执行平台相关的实际刻录。</para>
    /// </summary>
    /// <param name="session">Validated session.<para>已校验的会话。</para></param>
    /// <param name="cancellationToken">Cancels the recording.<para>取消刻录。</para></param>
    /// <returns>A task that completes when recording finishes.<para>刻录完成时完成的任务。</para></returns>
    protected abstract Task WriteCoreAsync(OpticalDiscSession session, CancellationToken cancellationToken);
}