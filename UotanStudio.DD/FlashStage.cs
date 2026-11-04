namespace UotanStudio.DD;

/// <summary>
/// Phase of a flash operation reported through <see cref="FlashProgress"/>.
/// <para>通过 <see cref="FlashProgress"/> 上报的烧录阶段。</para>
/// </summary>
public enum FlashStage
{
    /// <summary>
    /// Targets are being unmounted, locked and opened.
    /// <para>正在卸载、锁定并打开目标。</para>
    /// </summary>
    Preparing,

    /// <summary>
    /// Image bytes are being written to every target.
    /// <para>正在向全部目标写入镜像字节。</para>
    /// </summary>
    Writing,

    /// <summary>
    /// Written bytes are being read back and compared with the source.
    /// <para>正在回读写入字节并与源比较。</para>
    /// </summary>
    Verifying,

    /// <summary>
    /// Every target finished successfully.
    /// <para>全部目标成功完成。</para>
    /// </summary>
    Completed,
}