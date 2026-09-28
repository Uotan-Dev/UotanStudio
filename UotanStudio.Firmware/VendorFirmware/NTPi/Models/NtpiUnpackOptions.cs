namespace UotanStudio.Firmware.VendorFirmware.NTPi.Models;

/// <summary>
/// Trusted output-size limits enforced while unpacking an NTPI package, so untrusted file entries cannot drive unbounded writes.
/// <para>解包 NTPI 包时强制执行的受信输出大小上限，防止不可信文件条目造成无界写入。</para>
/// </summary>
public sealed class NtpiUnpackOptions
{
    /// <summary>Default per-file output limit of 32 GiB. <para>默认单文件输出上限：32 GiB。</para></summary>
    public const long DefaultMaxFileOutputBytes = 32L * 1024 * 1024 * 1024;

    /// <summary>Default total output limit of 128 GiB. <para>默认总输出上限：128 GiB。</para></summary>
    public const long DefaultMaxTotalOutputBytes = 128L * 1024 * 1024 * 1024;

    /// <summary>Maximum output bytes a single file entry may declare; exceeding it fails the unpack before any write.
    /// <para>单个文件条目可声明的最大输出字节数；超出时在写入前即解包失败。</para></summary>
    /// <value>The per-file limit, initialized to <see cref="DefaultMaxFileOutputBytes"/>. <para>单文件上限，初始为 <see cref="DefaultMaxFileOutputBytes"/>。</para></value>
    public long MaxFileOutputBytes { get; init; } = DefaultMaxFileOutputBytes;

    /// <summary>Maximum summed output bytes of all file entries; the declared total is checked for Int64 overflow against this cap.
    /// <para>所有文件条目输出字节总和的上限；声明总量会按此上限检查并防止 Int64 溢出。</para></summary>
    /// <value>The total limit, initialized to <see cref="DefaultMaxTotalOutputBytes"/>. <para>总量上限，初始为 <see cref="DefaultMaxTotalOutputBytes"/>。</para></value>
    public long MaxTotalOutputBytes { get; init; } = DefaultMaxTotalOutputBytes;

    /// <summary>When <see langword="true"/>, both limits are treated as <see cref="long.MaxValue"/> and negative limit values are accepted;
    /// unpacking an untrusted package with this set is unsafe.
    /// <para>为 <see langword="true"/> 时，两项上限均按 <see cref="long.MaxValue"/> 处理，且允许负数上限；对不可信包开启此项是不安全的。</para></summary>
    /// <value>Whether output limits are disabled, <see langword="false"/> by default. <para>是否禁用输出上限，默认 <see langword="false"/>。</para></value>
    public bool DisableOutputLimits { get; init; }

    /// <summary>Shared instance with all output limits disabled. <para>已关闭全部输出上限的共享实例。</para></summary>
    /// <value>An options object whose <see cref="DisableOutputLimits"/> is <see langword="true"/>.
    /// <para><see cref="DisableOutputLimits"/> 为 <see langword="true"/> 的选项对象。</para></value>
    public static NtpiUnpackOptions Unlimited { get; } = new() { DisableOutputLimits = true };
}