using UotanStudio.DD.Drives;

namespace UotanStudio.DD.Writing;

/// <summary>
/// One optical recording request: the image to burn and whether to close the disc session.
/// <para>一次光盘刻录请求：要刻录的镜像以及是否关闭光盘会话。</para>
/// </summary>
/// <param name="ImagePath">Absolute path of the source image (a raw ISO or a supported container).
/// <para>源镜像的绝对路径（raw ISO 或受支持的容器）。</para></param>
/// <param name="CloseSession">Whether to finalise the session so the disc can be read in other drives.
/// <para>是否结束会话，使光盘可在其它光驱中读取。</para></param>
public sealed record OpticalDiscSession(string ImagePath, bool CloseSession = true);

/// <summary>
/// Burns an image to an optical recorder using the platform's session-based recording API.
/// <para>使用平台基于会话的刻录 API 把镜像写入光驱。</para>
/// </summary>
/// <remarks>
/// Unlike <see cref="IDiskTarget"/>, optical media cannot be exposed as a seekable raw stream: recording is
/// a one-shot, forward-only operation driven by the recorder. Implementations stream from the image file
/// directly and never touch a raw block device.
/// <para>
/// 与 <see cref="IDiskTarget"/> 不同，光盘不能暴露为可寻址裸流：刻录是由刻录机驱动的一次性只进操作。
/// 实现直接从镜像文件流式读取，绝不触碰裸块设备。
/// </para>
/// </remarks>
public interface IDiscWriter
{
    /// <summary>
    /// Gets the optical drive metadata this writer was created for.
    /// <para>获取创建该写入器时使用的光驱元数据。</para>
    /// </summary>
    FlashDrive Drive { get; }

    /// <summary>
    /// Records one session to the optical drive.
    /// <para>向光驱写入一个会话。</para>
    /// </summary>
    /// <param name="session">Image and finalisation settings.<para>镜像与结束会话设置。</para></param>
    /// <param name="progress">Optional progress sink.<para>可选的进度接收器。</para></param>
    /// <param name="cancellationToken">Cancels the recording.<para>取消刻录。</para></param>
    /// <returns>A task that completes when the recorder finished the session.<para>刻录机完成会话时完成的任务。</para></returns>
    /// <exception cref="FlashException">The recorder rejected the media, the image or the write.
    /// <para>刻录机拒绝介质、镜像或写入。</para></exception>
    Task WriteAsync(
        OpticalDiscSession session,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates <see cref="IDiscWriter"/> instances for optical drives on the current platform.
/// <para>为当前平台的光驱创建 <see cref="IDiscWriter"/> 实例。</para>
/// </summary>
public interface IDiscWriterFactory
{
    /// <summary>
    /// Creates a writer for one optical drive.
    /// <para>为一个光驱创建写入器。</para>
    /// </summary>
    /// <param name="drive">Optical drive selected for recording.<para>选中用于刻录的光驱。</para></param>
    /// <returns>An optical writer.<para>光盘写入器。</para></returns>
    IDiscWriter Create(FlashDrive drive);
}

/// <summary>
/// Factory that selects the optical recorder implementation for the current operating system.
/// <para>为当前操作系统选择光驱刻录实现的目标工厂。</para>
/// </summary>
public static class PlatformDiscWriterFactory
{
    /// <summary>
    /// Gets the shared factory instance.
    /// <para>获取共享的工厂实例。</para>
    /// </summary>
    public static IDiscWriterFactory Instance { get; } = new DefaultFactory();

    private sealed class DefaultFactory : IDiscWriterFactory
    {
        public IDiscWriter Create(FlashDrive drive)
        {
            ArgumentNullException.ThrowIfNull(drive);
            if (drive.MediaKind != FlashMediaKind.OpticalDisc)
            {
                throw new ArgumentException("The drive is not an optical recorder.", nameof(drive));
            }

            if (OperatingSystem.IsWindows())
            {
                return new Windows.WindowsOpticalDiscWriter(drive);
            }

            if (OperatingSystem.IsLinux())
            {
                return new Linux.LinuxOpticalDiscWriter(drive);
            }

            if (OperatingSystem.IsMacOS())
            {
                return new MacOS.MacOpticalDiscWriter(drive);
            }

            throw new PlatformNotSupportedException("Optical disc writing is supported on Windows, Linux and macOS only.");
        }
    }
}