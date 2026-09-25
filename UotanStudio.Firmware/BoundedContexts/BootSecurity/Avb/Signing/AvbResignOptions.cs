using Org.BouncyCastle.Crypto.Parameters;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Models;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Signing;

/// <summary>
/// Selects the AVB protection scheme applied by <see cref="AvbResigner.ResignImageAsync"/>.
/// <para>选择 <see cref="AvbResigner.ResignImageAsync"/> 应用的 AVB 保护方案。</para>
/// </summary>
public enum AvbResignMode
{
    /// <summary>Salted SHA-256 hash footer (boot, dtbo, small partitions). <para>加盐 SHA-256 哈希尾部（boot、dtbo、小分区）。</para></summary>
    Hash,

    /// <summary>dm-verity Merkle hashtree footer (system, vendor, product). <para>dm-verity Merkle 哈希树尾部（system、vendor、product）。</para></summary>
    Hashtree,
}

/// <summary>
/// Parameters for the high-level AVB re-sign workflow. Defaults match avbtool 1.3.0 for
/// Android 10+ GKI: sha256 hash, 4 KiB data/hash blocks, salt = random 32 bytes,
/// rollback index / location = 0, flags = 0 (enforcement on).
/// <para>AVB 高层重签名工作流的参数。默认值与 avbtool 1.3.0 在 Android 10+ GKI 一致：sha256、
/// 4 KiB 数据/哈希块、32 字节随机盐、rollback index / location = 0、flags = 0（强制校验）。</para>
/// </summary>
public sealed class AvbResignOptions
{
    /// <summary>Protection scheme. <para>保护方案。</para></summary>
    /// <value>Defaults to <see cref="AvbResignMode.Hash"/>. <para>默认为 <see cref="AvbResignMode.Hash"/>。</para></value>
    public AvbResignMode Mode { get; init; } = AvbResignMode.Hash;

    /// <summary>Partition name embedded in the descriptor. Required; ASCII, ≤ 255 bytes.
    /// <para>描述符中的分区名；必填，ASCII，至多 255 字节。</para></summary>
    /// <value>The target partition name; a null or whitespace value makes <see cref="AvbResigner.ResignImageAsync"/> fail.
    /// <para>目标分区名；为 null 或空白时 <see cref="AvbResigner.ResignImageAsync"/> 会返回失败。</para></value>
    public required string PartitionName { get; init; }

    /// <summary>Optional salt; <c>null</c> fills 32 random bytes.
    /// <para>可选盐；<c>null</c> 时使用 32 字节随机值。</para></summary>
    /// <value>The salt passed to hashtree signing, or <see langword="null"/> to generate one.
    /// <para>传给 hashtree 签名的盐；为 <see langword="null"/> 时自动生成。</para></value>
    public byte[]? Salt { get; init; }

    /// <summary>Rollback index stored in the VBMeta header. <para>Vbmeta 头部回滚索引。</para></summary>
    /// <value>The anti-rollback counter; defaults to 0. <para>防回滚计数器；默认为 0。</para></value>
    public ulong RollbackIndex { get; init; }

    /// <summary>Rollback index location slot (0..3). <para>回滚索引槽位（0..3）。</para></summary>
    /// <value>The rollback slot number; defaults to 0. <para>回滚槽位编号；默认为 0。</para></value>
    public uint RollbackIndexLocation { get; init; }

    /// <summary>VBMeta flags (hashtree-disabled, verification-disabled). <para>Vbmeta 标志位。</para></summary>
    /// <value>The raw 32-bit flag word; 0 means no flag bits set. <para>原始 32 位标志字；0 表示未设置任何标志位。</para></value>
    public uint Flags { get; init; }

    /// <summary>dm-verity data block size (hashtree mode only). <para>dm-verity 数据块大小（仅 hashtree 模式）。</para></summary>
    /// <value>The data block size in bytes; defaults to 4096. <para>数据块字节大小；默认为 4096。</para></value>
    public int DataBlockSize { get; init; } = 4096;

    /// <summary>dm-verity hash block size (hashtree mode only). <para>dm-verity 哈希块大小（仅 hashtree 模式）。</para></summary>
    /// <value>The hash block size in bytes; defaults to 4096. <para>哈希块字节大小；默认为 4096。</para></value>
    public int HashBlockSize { get; init; } = 4096;
}

/// <summary>
/// High-level AVB re-sign workflow: one entry point that dispatches to hash / hashtree with a
/// single options object. Both branches publish atomically and honor cancellation.
/// <para>AVB 高层重签名工作流：单一入口按模式分派到 hash / hashtree；两条路径均原子发布并响应取消。</para>
/// </summary>
public static class AvbResigner
{
    /// <summary>
    /// Re-signs <paramref name="imagePath"/> with an AVB footer chosen by <paramref name="options"/>
    /// and writes the result to <paramref name="outputPath"/>.
    /// <para>按 <paramref name="options"/> 选定的 AVB 尾部对 <paramref name="imagePath"/> 重签名，输出到
    /// <paramref name="outputPath"/>。</para>
    /// </summary>
    /// <param name="imagePath">Source partition image to sign. <para>待签名的源分区镜像。</para></param>
    /// <param name="outputPath">Destination image written atomically. <para>原子写入的目标镜像。</para></param>
    /// <param name="key">RSA key pair supplied to the signer for the VBMeta signature. <para>提供给签名方用于 VBMeta 签名的 RSA 密钥。</para></param>
    /// <param name="options">Protection scheme and its parameters; <see cref="AvbResignOptions.Mode"/> selects the branch.
    /// <para>保护方案及其参数；由 <see cref="AvbResignOptions.Mode"/> 选择分派分支。</para></param>
    /// <param name="cancellationToken">Cancels the signing run. <para>取消签名过程。</para></param>
    /// <returns>The sign result; invalid mode or a missing partition name yield a result with <c>Success</c> false
    /// instead of an exception. <para>签名结果；模式无效或分区名缺失时返回 <c>Success</c> 为 false 的结果而非抛出异常。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="options"/> is null.
    /// <para><paramref name="key"/> 或 <paramref name="options"/> 为 null。</para></exception>
    public static Task<AvbSignResult> ResignImageAsync(
        string imagePath,
        string outputPath,
        RsaKeyParameters key,
        AvbResignOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.PartitionName))
        {
            return Task.FromResult(Fail("PartitionName is required. 分区名必填。"));
        }

        return options.Mode switch
        {
            AvbResignMode.Hash => AvbSigner.AddHashFooterAsync(
                imagePath, options.PartitionName, key, outputPath, cancellationToken),
            AvbResignMode.Hashtree => AvbSigner.AddHashtreeFooterAsync(
                imagePath, options.PartitionName, key, outputPath, options.Salt, cancellationToken),
            _ => Task.FromResult(Fail($"Unsupported AVB resign mode '{options.Mode}'.")),
        };
    }

    private static AvbSignResult Fail(string message) => new() { Success = false, ErrorMessage = message };
}