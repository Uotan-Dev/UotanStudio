using UotanStudio.DD.Drives;

namespace UotanStudio.DD;

/// <summary>
/// Raised when the read-back comparison finds bytes that differ from the source image.
/// <para>回读比较发现与源镜像不一致的字节时抛出。</para>
/// </summary>
public sealed class FlashVerificationException : FlashException
{
    /// <summary>
    /// Creates an exception for a specific target and offset.
    /// <para>为特定目标与偏移创建异常。</para>
    /// </summary>
    /// <param name="drive">Target whose read-back differed.<para>回读不一致的目标。</para></param>
    /// <param name="offset">Zero-based byte offset of the first mismatch.
    /// <para>首个不一致字节的零基偏移。</para></param>
    public FlashVerificationException(FlashDrive drive, long offset)
        : base($"Verification failed for {drive.DisplayName} ({drive.DevicePath}) at byte offset {offset}.")
    {
        Drive = drive;
        Offset = offset;
    }

    /// <summary>
    /// Target whose read-back differed from the source image.
    /// <para>回读与源镜像不一致的目标。</para>
    /// </summary>
    public FlashDrive Drive { get; }

    /// <summary>
    /// Zero-based byte offset of the first mismatch.
    /// <para>首个不一致字节的零基偏移。</para>
    /// </summary>
    public long Offset { get; }
}