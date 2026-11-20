using UotanStudio.DD.Drives;
using UotanStudio.DD.Drives.Windows;

namespace UotanStudio.DD.Writing.Windows;

/// <summary>
/// Records an ISO image to a Windows optical recorder through the in-process IMAPI2 interop.
/// <para>通过进程内 IMAPI2 互操作把 ISO 镜像刻录到 Windows 光驱。</para>
/// </summary>
/// <remarks>
/// The interop layer reports engine updates with the written logical block, so progress maps the
/// block position onto image bytes and derives throughput and remaining time; verification reports
/// use the dedicated <see cref="FlashStage.Verifying"/> stage. Cancellation calls
/// <c>IDiscFormat2Data.CancelWrite</c> on the engine instead of killing a process, so the recorder
/// finishes its current buffer and leaves a continuable session.
/// <para>
/// 互操作层上报已写入的逻辑块，进度把块位置映射为镜像字节并推导吞吐量与剩余时间；校验上报使用
/// 专属的 <see cref="FlashStage.Verifying"/> 阶段。取消在引擎上调用 <c>IDiscFormat2Data.CancelWrite</c>
/// 而不是杀进程，刻录机会完成当前缓冲并留下可继续的会话。
/// </para>
/// </remarks>
internal sealed class WindowsOpticalDiscWriter(FlashDrive drive) : OpticalDiscWriterBase(drive)
{
    /// <inheritdoc />
    protected override Task ConfirmWritableMediaAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = Imapi2Interop.GetRecorderInfo(Drive.DevicePath);
        if (!WindowsOpticalDriveScanner.IsWritableMedia(info.MediaType)
            || !WindowsOpticalDriveScanner.IsWritableState(info.MediaState))
        {
            throw new FlashException(
                "The optical recorder no longer has writable media loaded. Insert a blank or rewritable disc.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task WriteCoreAsync(
        OpticalDiscSession session,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var imageBytes = new FileInfo(session.ImagePath).Length;
        var reporter = new OpticalProgressReporter(imageBytes) { Progress = progress };
        try
        {
            Imapi2Interop.BurnImage(
                Drive.DevicePath,
                session.ImagePath,
                session.CloseSession,
                reporter.OnEngineUpdate,
                cancellationToken);
            return Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not FlashException and not OperationCanceledException)
        {
            throw new FlashException("The Windows optical recording failed: " + exception.Message, exception);
        }
    }

    /// <summary>
    /// Maps IMAPI2 engine updates onto byte progress with throughput estimation.
    /// <para>把 IMAPI2 引擎更新映射为字节进度并估算吞吐量。</para>
    /// </summary>
    internal sealed class OpticalProgressReporter
    {
        private const int IsoSectorBytes = Images.IsoImageValidator.SectorSize;
        private readonly long _imageBytes;
        private readonly System.Diagnostics.Stopwatch _writeWatch = new();
        private readonly System.Diagnostics.Stopwatch _verifyWatch = new();
        private long _lastWrittenLba = -1;
        private bool _writeStarted;
        private bool _verifyStarted;

        public OpticalProgressReporter(long imageBytes)
        {
            _imageBytes = imageBytes;
        }

        /// <summary>
        /// The progress sink; assigned by the writer before recording starts.
        /// <para>进度接收器；写入器在刻录开始前赋值。</para>
        /// </summary>
        public IProgress<FlashProgress>? Progress { get; set; }

        /// <summary>
        /// Called by the interop layer on every engine update.
        /// <para>互操作层在每次引擎更新时调用。</para>
        /// </summary>
        /// <param name="action">IMAPI_FORMAT2_DATA_WRITE_ACTION value.<para>IMAPI_FORMAT2_DATA_WRITE_ACTION 值。</para></param>
        /// <param name="startLba">First logical block of the write.<para>写入的起始逻辑块。</para></param>
        /// <param name="sectorCount">Planned sector count.<para>规划扇区数。</para></param>
        /// <param name="lastWrittenLba">Last completed logical block.<para>最近完成的逻辑块。</para></param>
        /// <param name="remainingSeconds">Engine-estimated remaining seconds.<para>引擎估算的剩余秒数。</para></param>
        public void OnEngineUpdate(int action, int startLba, int sectorCount, int lastWrittenLba, int remainingSeconds)
        {
            var progress = Progress;
            if (progress is null)
            {
                return;
            }

            if (action == Imapi2Interop.WriteActionVerifying)
            {
                ReportVerify(progress, lastWrittenLba);
                return;
            }

            if (action != Imapi2Interop.WriteActionWritingData)
            {
                return;
            }

            ReportWrite(progress, lastWrittenLba, remainingSeconds);
        }

        private void ReportWrite(IProgress<FlashProgress> progress, int lastWrittenLba, int remainingSeconds)
        {
            if (!_writeStarted)
            {
                _writeStarted = true;
                _writeWatch.Start();
            }

            if (lastWrittenLba < 0)
            {
                return;
            }

            var writtenBytes = Math.Min(_imageBytes, (long)(lastWrittenLba + 1) * IsoSectorBytes);
            if (writtenBytes <= _lastWrittenLba * IsoSectorBytes && _lastWrittenLba >= 0)
            {
                return;
            }

            _lastWrittenLba = lastWrittenLba;
            var elapsed = _writeWatch.Elapsed.TotalSeconds;
            var speed = elapsed > 0.5 ? writtenBytes / elapsed : 0;
            var percentage = _imageBytes > 0 ? Math.Min(100.0, writtenBytes * 100.0 / _imageBytes) : 0;
            TimeSpan? remaining = remainingSeconds > 0
                ? TimeSpan.FromSeconds(remainingSeconds)
                : speed > 0 && writtenBytes < _imageBytes
                    ? TimeSpan.FromSeconds((_imageBytes - writtenBytes) / speed)
                    : null;
            progress.Report(new FlashProgress
            {
                Stage = FlashStage.Writing,
                BytesProcessed = writtenBytes,
                TotalBytes = _imageBytes,
                Percentage = percentage,
                BytesPerSecond = speed,
                RemainingTime = remaining,
                TargetCount = 1,
            });
        }

        private void ReportVerify(IProgress<FlashProgress> progress, int lastWrittenLba)
        {
            if (!_verifyStarted)
            {
                _verifyStarted = true;
                _verifyWatch.Start();
            }

            if (lastWrittenLba < 0)
            {
                return;
            }

            var verifiedBytes = Math.Min(_imageBytes, (long)(lastWrittenLba + 1) * IsoSectorBytes);
            var elapsed = _verifyWatch.Elapsed.TotalSeconds;
            var speed = elapsed > 0.5 ? verifiedBytes / elapsed : 0;
            var percentage = _imageBytes > 0 ? Math.Min(100.0, verifiedBytes * 100.0 / _imageBytes) : 0;
            TimeSpan? remaining = speed > 0 && verifiedBytes < _imageBytes
                ? TimeSpan.FromSeconds((_imageBytes - verifiedBytes) / speed)
                : null;
            progress.Report(new FlashProgress
            {
                Stage = FlashStage.Verifying,
                BytesProcessed = verifiedBytes,
                TotalBytes = _imageBytes,
                Percentage = percentage,
                BytesPerSecond = speed,
                RemainingTime = remaining,
                TargetCount = 1,
            });
        }
    }
}