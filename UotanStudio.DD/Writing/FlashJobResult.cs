namespace UotanStudio.DD.Writing;

/// <summary>
/// Outcome of a completed flash job in the form the host process reports to the user.
/// <para>宿主进程向用户报告的已完成烧录任务结果。</para>
/// </summary>
public sealed record FlashJobResult
{
    /// <summary>
    /// Gets the number of image bytes written to the target.
    /// <para>获取写入目标的镜像字节数。</para>
    /// </summary>
    public required long ImageBytes { get; init; }

    /// <summary>
    /// Gets the total bytes written across every target.
    /// <para>获取跨全部目标写入的字节总数。</para>
    /// </summary>
    public required long BytesWritten { get; init; }

    /// <summary>
    /// Gets the wall-clock duration of the operation.
    /// <para>获取操作的实际耗时。</para>
    /// </summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Gets the lowercase hexadecimal SHA-256 digest of the decompressed source image.
    /// <para>获取解压后源镜像的小写十六进制 SHA-256 摘要。</para>
    /// </summary>
    public required string SourceChecksum { get; init; }

    /// <summary>
    /// Converts a flasher result into the job outcome.
    /// <para>把烧录器结果转换为任务结果。</para>
    /// </summary>
    /// <param name="result">Result returned by <see cref="ImageFlasher"/>.<para>由 <see cref="ImageFlasher"/> 返回的结果。</para></param>
    /// <returns>The job outcome.<para>任务结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.
    /// <para><paramref name="result"/> 为 <see langword="null"/>。</para></exception>
    public static FlashJobResult FromFlashResult(FlashResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new FlashJobResult
        {
            ImageBytes = result.ImageBytes,
            BytesWritten = result.BytesWritten,
            Elapsed = result.Elapsed,
            SourceChecksum = result.SourceChecksum,
        };
    }
}