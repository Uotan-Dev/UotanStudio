using UotanStudio.DD.Drives;
using UotanStudio.DD.Writing;

namespace UotanStudio.DD;

/// <summary>
/// Orchestrates validation and recording of one ISO image to an optical disc writer.
/// <para>编排校验并把单个 ISO 镜像写入光盘写入器。</para>
/// </summary>
public sealed class OpticalDiscBurner
{
    private readonly IDiscWriterFactory _writerFactory;

    /// <summary>
    /// Creates a burner.
    /// <para>创建刻录器。</para>
    /// </summary>
    /// <param name="writerFactory">Factory that creates platform optical writers; <see langword="null"/> uses
    /// <see cref="Writing.PlatformDiscWriterFactory.Instance"/>.
    /// <para>创建平台光盘写入器的工厂；<see langword="null"/> 时使用 <see cref="Writing.PlatformDiscWriterFactory.Instance"/>。</para></param>
    public OpticalDiscBurner(Writing.IDiscWriterFactory? writerFactory = null)
    {
        _writerFactory = writerFactory ?? Writing.PlatformDiscWriterFactory.Instance;
    }

    /// <summary>
    /// Validates the drive and image, then records the disc session.
    /// <para>校验驱动器与镜像，然后写入光盘会话。</para>
    /// </summary>
    /// <param name="image">Probed raw ISO image.<para>已探测的 raw ISO 镜像。</para></param>
    /// <param name="drive">Selected optical recorder.<para>选中的光驱。</para></param>
    /// <param name="session">Recording settings.<para>刻录设置。</para></param>
    /// <param name="progress">Optional progress sink.<para>可选的进度接收器。</para></param>
    /// <param name="cancellationToken">Cancels the recording.<para>取消刻录。</para></param>
    /// <returns>A task that completes when the disc session finished.<para>光盘会话完成时完成的任务。</para></returns>
    /// <exception cref="FlashValidationException">The drive or image failed validation.
    /// <para>驱动器或镜像未通过校验。</para></exception>
    /// <exception cref="FlashException">The recorder failed.<para>刻录机写入失败。</para></exception>
    public async Task BurnAsync(
        Images.FlashImage image,
        FlashDrive drive,
        OpticalDiscSession session,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();

        var issues = DriveCompatibilityEvaluator.Evaluate(drive, image, writing: true);
        var blocking = issues
            .Where(issue => DriveCompatibilityEvaluator.IsBlocking(issue, allowLargeDrive: false))
            .ToArray();
        if (blocking.Length > 0)
        {
            throw new FlashValidationException([new DriveValidationFailure(drive, blocking)]);
        }

        var writer = _writerFactory.Create(drive);
        await writer.WriteAsync(session, progress, cancellationToken).ConfigureAwait(false);
    }
}