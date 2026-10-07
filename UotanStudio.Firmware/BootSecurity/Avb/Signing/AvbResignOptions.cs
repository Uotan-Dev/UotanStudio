using System.Security.Cryptography;

namespace UotanStudio.Firmware.BootSecurity.Avb.Signing;

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
/// Parameters for the high-level AVB re-sign workflow: sha256 hash, 4 KiB data/hash blocks,
/// salt = random 32 bytes, rollback index / location = 0, flags = 0.
/// <para>AVB 高层重签名工作流的参数：sha256、
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
    /// <value>The salt used in either mode; an empty array disables salting.
    /// <para>两种模式使用的盐；空数组表示不加盐。</para></value>
    public byte[]? Salt { get; init; }

    /// <summary>Rollback index stored in the VBMeta header. <para>Vbmeta 头部回滚索引。</para></summary>
    /// <value>The anti-rollback counter; defaults to 0. <para>防回滚计数器；默认为 0。</para></value>
    public ulong RollbackIndex { get; init; }

    /// <summary>Rollback index location slot (0..31). <para>回滚索引槽位（0..31）。</para></summary>
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

    /// <summary>Number of Reed-Solomon FEC roots for hashtree mode; zero disables FEC.
    /// <para>hashtree 模式的 Reed-Solomon FEC 校验根数；零表示不生成 FEC。</para></summary>
    /// <value>Zero by default; an explicit value from 1 to 254 enables FEC.
    /// <para>默认为零；显式指定 1 至 254 时生成 FEC。</para></value>
    public uint FecNumRoots { get; init; }

    /// <summary>Exact output partition capacity, including metadata and the final footer.
    /// <para>包括元数据和末尾 footer 的精确输出分区容量。</para></summary>
    /// <value>A positive multiple of 4096, or null to emit the smallest block-aligned image.
    /// <para>4096 的正整数倍；为 null 时输出最小块对齐镜像。</para></value>
    public long? PartitionSize { get; init; }

    /// <summary>Stores DO_NOT_USE_AB in the partition descriptor.
    /// <para>在分区描述符中写入 DO_NOT_USE_AB。</para></summary>
    /// <value>Whether the partition name is used without an A/B suffix. Defaults to false.
    /// <para>是否不附加 A/B 槽位后缀；默认为 false。</para></value>
    public bool DoNotUseAb { get; init; }
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
    /// <param name="imagePath">Raw or Android Sparse partition; existing valid footer metadata is replaced.
    /// <para>原始或 Android Sparse 分区；已有有效 footer 元数据会被替换。</para></param>
    /// <param name="outputPath">Raw destination image written atomically after signature and content validation.
    /// <para>经签名和内容校验后原子写入的原始目标镜像。</para></param>
    /// <param name="key">RSA key pair supplied to the signer for the VBMeta signature; it must carry a private key.
    /// <para>提供给签名方用于 VBMeta 签名的 RSA 密钥；必须带私钥。</para></param>
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
        RSA key,
        AvbResignOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.PartitionName))
        {
            return Task.FromResult(Fail("PartitionName is required. 分区名必填。"));
        }

        return AvbSigner.ResignFooterAsync(imagePath, outputPath, key, options, cancellationToken);
    }

    private static AvbSignResult Fail(string message) => new() { Success = false, ErrorMessage = message };
}