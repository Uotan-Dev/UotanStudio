using UotanStudio.DD.Drives;

namespace UotanStudio.DD;

/// <summary>
/// Summary of a successful flash operation.
/// <para>成功烧录操作的汇总结果。</para>
/// </summary>
public sealed record FlashResult
{
    /// <summary>
    /// Gets the targets that were written and verified.
    /// <para>获取已写入并完成校验的目标。</para>
    /// </summary>
    public required IReadOnlyList<FlashDrive> Targets { get; init; }

    /// <summary>
    /// Gets the number of image bytes written to each target.
    /// <para>获取写入每个目标的镜像字节数。</para>
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
    /// Gets the average total throughput in bytes per second across every target.
    /// <para>获取跨全部目标的平均吞吐量，单位为字节每秒。</para>
    /// </summary>
    /// <value>Zero when <see cref="Elapsed"/> is too small to measure.
    /// <para><see cref="Elapsed"/> 太短无法测量时为零。</para></value>
    public double AverageBytesPerSecond => Elapsed.TotalSeconds > 0 ? BytesWritten / Elapsed.TotalSeconds : 0;
}