using UotanStudio.DD.Drives;

namespace UotanStudio.DD.Writing;

/// <summary>
/// A prepared destination that exposes a seekable, writable stream over the raw image area.
/// <para>已准备好的目标，暴露覆盖裸镜像区域的可寻址可写流。</para>
/// </summary>
public interface IDiskTarget : IAsyncDisposable
{
    /// <summary>
    /// Gets the drive metadata this target was created for.
    /// <para>获取创建此目标所用的驱动器元数据。</para>
    /// </summary>
    FlashDrive Drive { get; }

    /// <summary>
    /// Gets the raw stream positioned at byte zero.
    /// <para>获取定位在字节零的裸流。</para>
    /// </summary>
    /// <value>Only valid after <see cref="PrepareAsync"/> completed successfully.
    /// <para>仅当 <see cref="PrepareAsync"/> 成功完成后有效。</para></value>
    Stream Stream { get; }

    /// <summary>
    /// Unmounts volumes and opens the raw device for read/write access.
    /// <para>卸载卷并以读写方式打开裸设备。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the preparation.<para>取消准备过程。</para></param>
    /// <returns>A task that completes when the stream is ready.<para>流就绪时完成的任务。</para></returns>
    /// <exception cref="FlashException">A volume could not be unmounted or the device could not be opened.
    /// <para>卷无法卸载或设备无法打开。</para></exception>
    Task PrepareAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushes written bytes to stable storage before verification or disposal.
    /// <para>在校验或释放之前把已写字节刷新到稳定存储。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the flush.<para>取消刷新。</para></param>
    /// <returns>A task that completes when the flush finished.<para>刷新完成时完成的任务。</para></returns>
    Task CompleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates <see cref="IDiskTarget"/> instances for scanned drives.
/// <para>为已扫描驱动器创建 <see cref="IDiskTarget"/> 实例。</para>
/// </summary>
public interface IDiskTargetFactory
{
    /// <summary>
    /// Creates a target for one drive.
    /// <para>为一个驱动器创建目标。</para>
    /// </summary>
    /// <param name="drive">Drive selected for writing.<para>选中用于写入的驱动器。</para></param>
    /// <returns>An unprepared target.<para>尚未准备的目标。</para></returns>
    IDiskTarget Create(FlashDrive drive);
}