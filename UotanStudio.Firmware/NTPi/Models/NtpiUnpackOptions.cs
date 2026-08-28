namespace UotanStudio.Firmware.NTPi.Models;

/// <summary>
/// <para>NTPI 可信解包限制，用于约束由不受信任文件索引声明的解压后输出大小。</para>
/// Trusted NTPI unpacking limits that bound decompressed output sizes declared by an untrusted file index.
/// </summary>
public sealed class NtpiUnpackOptions
{
    /// <summary>
    /// <para>默认单文件最大输出：32 GiB，足以容纳常见固件分区，同时限制解压炸弹。</para>
    /// Default maximum output per file: 32 GiB, large enough for typical firmware partitions while bounding decompression bombs.
    /// </summary>
    public const long DefaultMaxFileOutputBytes = 32L * 1024 * 1024 * 1024;

    /// <summary>
    /// <para>默认总输出最大值：128 GiB。</para>
    /// Default maximum total output: 128 GiB.
    /// </summary>
    public const long DefaultMaxTotalOutputBytes = 128L * 1024 * 1024 * 1024;

    /// <summary>
    /// <para>单个文件允许写出的最大解压后字节数。</para>
    /// Maximum decompressed bytes that may be written for one file.
    /// </summary>
    public long MaxFileOutputBytes { get; init; } = DefaultMaxFileOutputBytes;

    /// <summary>
    /// <para>一次解包允许写出的最大解压后总字节数。</para>
    /// Maximum total decompressed bytes that may be written by one unpack operation.
    /// </summary>
    public long MaxTotalOutputBytes { get; init; } = DefaultMaxTotalOutputBytes;

    /// <summary>
    /// <para>显式禁用所有输出限制。仅用于完全可信的包。</para>
    /// Explicitly disables all output limits. Use only with fully trusted packages.
    /// </summary>
    public bool DisableOutputLimits { get; init; }

    /// <summary>
    /// <para>显式禁用可信输出限制的兼容配置。仅用于完全可信的包。</para>
    /// Compatibility configuration that explicitly disables trusted output limits. Use only with fully trusted packages.
    /// </summary>
    public static NtpiUnpackOptions Unlimited { get; } = new() { DisableOutputLimits = true };
}