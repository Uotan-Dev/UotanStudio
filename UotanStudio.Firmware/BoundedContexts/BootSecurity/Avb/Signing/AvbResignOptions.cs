using Org.BouncyCastle.Crypto.Parameters;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Models;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Signing;

/// <summary>
/// Selects the AVB protection scheme applied by <see cref="AvbSigner.ResignImageAsync"/>.
/// <para>选择 <see cref="AvbSigner.ResignImageAsync"/> 应用的 AVB 保护方案。</para>
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
    public AvbResignMode Mode { get; init; } = AvbResignMode.Hash;

    /// <summary>Partition name embedded in the descriptor. Required; ASCII, ≤ 255 bytes.
    /// <para>描述符中的分区名；必填，ASCII，至多 255 字节。</para></summary>
    public required string PartitionName { get; init; }

    /// <summary>Optional salt; <c>null</c> fills 32 random bytes.
    /// <para>可选盐；<c>null</c> 时使用 32 字节随机值。</para></summary>
    public byte[]? Salt { get; init; }

    /// <summary>Rollback index stored in the VBMeta header. <para>Vbmeta 头部回滚索引。</para></summary>
    public ulong RollbackIndex { get; init; }

    /// <summary>Rollback index location slot (0..3). <para>回滚索引槽位（0..3）。</para></summary>
    public uint RollbackIndexLocation { get; init; }

    /// <summary>VBMeta flags (hashtree-disabled, verification-disabled). <para>Vbmeta 标志位。</para></summary>
    public uint Flags { get; init; }

    /// <summary>dm-verity data block size (hashtree mode only). <para>dm-verity 数据块大小（仅 hashtree 模式）。</para></summary>
    public int DataBlockSize { get; init; } = 4096;

    /// <summary>dm-verity hash block size (hashtree mode only). <para>dm-verity 哈希块大小（仅 hashtree 模式）。</para></summary>
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