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

        cancellationToken.ThrowIfCancellationRequested();
        await ConfirmWritableMediaAsync(cancellationToken).ConfigureAwait(false);

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

        await WriteCoreAsync(session, progress, cancellationToken).ConfigureAwait(false);
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
    /// Re-reads the recorder media state immediately before recording and throws when the media
    /// became unwritable since the scan.
    /// <para>在刻录前重读刻录机介质状态；自扫描以来介质变为不可写时抛出异常。</para>
    /// </summary>
    /// <remarks>
    /// The scan result can be stale: a user may swap discs between the scan and the recording request.
    /// The default implementation accepts the scanned state; platforms with a cheap fresh query override
    /// this method and fail fast instead of surfacing the error mid-burn.
    /// <para>
    /// 扫描结果可能过期：用户可能在扫描与刻录请求之间换盘。默认实现接受扫描时的状态；具备低成本即时查询的
    /// 平台重写此方法并快速失败，而不是把错误留到刻录中段。
    /// </para>
    /// </remarks>
    /// <exception cref="FlashException">The recorder no longer has writable media loaded.
    /// <para>刻录机已没有可写介质。</para></exception>
    protected virtual Task ConfirmWritableMediaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Performs the actual platform recording after validation succeeds.
    /// <para>校验成功后执行平台相关的实际刻录。</para>
    /// </summary>
    /// <param name="session">Validated session.<para>已校验的会话。</para></param>
    /// <param name="progress">Optional progress sink for platform fine-grained reports.
    /// <para>可选的进度接收器，用于平台细粒度上报。</para></param>
    /// <param name="cancellationToken">Cancels the recording.<para>取消刻录。</para></param>
    /// <returns>A task that completes when recording finishes.<para>刻录完成时完成的任务。</para></returns>
    protected abstract Task WriteCoreAsync(
        OpticalDiscSession session,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken);
}