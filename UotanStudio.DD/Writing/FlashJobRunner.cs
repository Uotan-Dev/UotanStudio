using UotanStudio.DD.Images;

namespace UotanStudio.DD.Writing;

/// <summary>
/// Runs one flash job inside the process that holds the write privilege.
/// <para>在持有写权限的进程内运行一个烧录任务。</para>
/// </summary>
public static class FlashJobRunner
{
    /// <summary>
    /// Probes the image, writes it to every target of the job and optionally verifies the result.
    /// <para>探测镜像，写入任务的全部目标，并可选地校验结果。</para>
    /// </summary>
    /// <param name="job">Job to run.<para>待运行的任务。</para></param>
    /// <param name="onProgress">Optional synchronous progress callback.<para>可选的同步进度回调。</para></param>
    /// <param name="cancellationToken">Cancels the write.<para>取消写入。</para></param>
    /// <returns>The job outcome.<para>任务结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="job"/> is <see langword="null"/>.
    /// <para><paramref name="job"/> 为 <see langword="null"/>。</para></exception>
    public static async Task<FlashJobResult> RunAsync(
        FlashJob job,
        Action<FlashProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var image = ImageProbe.Probe(job.ImagePath);
        var flasher = new ImageFlasher();
        IProgress<FlashProgress>? progress = onProgress is null ? null : new RelayProgress(onProgress);
        var result = await flasher
            .FlashAsync(image, job.Drives, job.Options.ToFlashOptions(), progress, cancellationToken)
            .ConfigureAwait(false);
        return FlashJobResult.FromFlashResult(result);
    }

    private sealed class RelayProgress(Action<FlashProgress> callback) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => callback(value);
    }
}