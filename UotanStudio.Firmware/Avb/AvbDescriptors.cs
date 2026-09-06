namespace UotanStudio.Firmware.Avb;

/// <summary>
/// Describes an AVB dm-verity hash tree descriptor (tag 1), following the upstream libavb
/// <c>AvbHashtreeDescriptor</c> struct: the dm-verity parameters, the tree location and size, the
/// hash algorithm, the salt and the root digest.
/// <para>描述一个 AVB dm-verity 哈希树描述符（tag 1），遵循上游 libavb
/// <c>AvbHashtreeDescriptor</c> 结构：dm-verity 参数、树的偏移与大小、哈希算法、盐值与根摘要。</para>
/// </summary>
public sealed class AvbHashtreeDescriptor
{
    /// <summary>
    /// Gets or sets the dm-verity version (1 for the standard dm-verity layout).
    /// <para>获取或设置 dm-verity 版本（标准 dm-verity 布局为 1）。</para>
    /// </summary>
    public uint DmVerityVersion { get; init; } = 1;

    /// <summary>
    /// Gets or sets the image size covered by the tree, rounded up to the data block size.
    /// <para>获取或设置树所覆盖的镜像大小，向上取整到数据块大小。</para>
    /// </summary>
    public ulong ImageSize { get; init; }

    /// <summary>
    /// Gets or sets the absolute byte offset of the hash tree within the partition image.
    /// <para>获取或设置哈希树在分区镜像中的绝对字节偏移。</para>
    /// </summary>
    public ulong TreeOffset { get; init; }

    /// <summary>
    /// Gets or sets the hash tree size in bytes (a multiple of the hash block size).
    /// <para>获取或设置哈希树大小（字节，为哈希块大小的整数倍）。</para>
    /// </summary>
    public ulong TreeSize { get; init; }

    /// <summary>
    /// Gets or sets the data block size in bytes (e.g. 4096).
    /// <para>获取或设置数据块大小（字节，如 4096）。</para>
    /// </summary>
    public uint DataBlockSize { get; init; } = 4096;

    /// <summary>
    /// Gets or sets the hash block size in bytes (e.g. 4096).
    /// <para>获取或设置哈希块大小（字节，如 4096）。</para>
    /// </summary>
    public uint HashBlockSize { get; init; } = 4096;

    /// <summary>
    /// Gets or sets the number of FEC roots (0 when FEC is not used).
    /// <para>获取或设置 FEC 根数（不使用 FEC 时为 0）。</para>
    /// </summary>
    public uint FecNumRoots { get; init; }

    /// <summary>
    /// Gets or sets the byte offset of the FEC data (0 when FEC is not used).
    /// <para>获取或设置 FEC 数据的字节偏移（不使用 FEC 时为 0）。</para>
    /// </summary>
    public ulong FecOffset { get; init; }

    /// <summary>
    /// Gets or sets the FEC data size in bytes (0 when FEC is not used).
    /// <para>获取或设置 FEC 数据大小（字节）（不使用 FEC 时为 0）。</para>
    /// </summary>
    public ulong FecSize { get; init; }

    /// <summary>
    /// Gets or sets the hash algorithm name (ASCII, up to 32 bytes; "sha256").
    /// <para>获取或设置哈希算法名（ASCII，至多 32 字节；"sha256"）。</para>
    /// </summary>
    public string HashAlgorithm { get; init; } = "sha256";

    /// <summary>
    /// Gets or sets the target partition name (ASCII, 1-255 bytes).
    /// <para>获取或设置目标分区名称（ASCII，1-255 字节）。</para>
    /// </summary>
    public string PartitionName { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the salt prefixed to every hashed block.
    /// <para>获取或设置每个被哈希块前拼接的盐值。</para>
    /// </summary>
    public byte[] Salt { get; init; } = [];

    /// <summary>
    /// Gets or sets the root digest of the hash tree (SHA-256, 32 bytes).
    /// <para>获取或设置哈希树的根摘要（SHA-256，32 字节）。</para>
    /// </summary>
    public byte[] RootDigest { get; init; } = [];

    /// <summary>
    /// Gets or sets the descriptor flags (bit 0 = do not use A/B, bit 1 = check at most once).
    /// <para>获取或设置描述符 flags（位 0 = 不使用 A/B，位 1 = 最多校验一次）。</para>
    /// </summary>
    public uint Flags { get; init; }
}

/// <summary>
/// Describes an AVB chained partition descriptor (tag 4), following the upstream libavb
/// <c>AvbChainPartitionDescriptor</c> struct: the rollback index location, the partition name and
/// the public key blob used to verify the chained partition's vbmeta.
/// <para>描述一个 AVB 链式分区描述符（tag 4），遵循上游 libavb
/// <c>AvbChainPartitionDescriptor</c> 结构：回滚索引位置、分区名与用于验证链式分区 vbmeta 的
/// 公钥数据块。</para>
/// </summary>
public sealed class AvbChainPartitionDescriptor
{
    /// <summary>
    /// Gets or sets the rollback index location to use for this chained partition.
    /// <para>获取或设置此链式分区使用的回滚索引位置。</para>
    /// </summary>
    public uint RollbackIndexLocation { get; init; }

    /// <summary>
    /// Gets or sets the target partition name (ASCII, 1-255 bytes).
    /// <para>获取或设置目标分区名称（ASCII，1-255 字节）。</para>
    /// </summary>
    public string PartitionName { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the AVB-format public key blob used to verify the chained partition.
    /// <para>获取或设置用于验证链式分区的 AVB 格式公钥数据块。</para>
    /// </summary>
    public byte[] PublicKey { get; init; } = [];
}

/// <summary>
/// Describes an AVB kernel command-line descriptor (tag 3), following the upstream libavb
/// <c>AvbKernelCmdlineDescriptor</c> struct: flags and the kernel command-line string.
/// <para>描述一个 AVB 内核命令行描述符（tag 3），遵循上游 libavb
/// <c>AvbKernelCmdlineDescriptor</c> 结构：标志位与内核命令行字符串。</para>
/// </summary>
public sealed class AvbKernelCmdlineDescriptor
{
    /// <summary>
    /// Gets or sets the descriptor flags (bit 0 = use only if hashtree not disabled,
    /// bit 1 = use only if hashtree disabled).
    /// <para>获取或设置描述符 flags（位 0 = 仅在哈希树未禁用时使用，位 1 = 仅在哈希树禁用时使用）。</para>
    /// </summary>
    public uint Flags { get; init; }

    /// <summary>
    /// Gets or sets the kernel command-line string.
    /// <para>获取或设置内核命令行字符串。</para>
    /// </summary>
    public string KernelCmdline { get; init; } = string.Empty;
}

/// <summary>
/// Describes an AVB property descriptor (tag 0), following the upstream libavb
/// <c>AvbPropertyDescriptor</c> struct: an arbitrary key/value pair.
/// <para>描述一个 AVB 属性描述符（tag 0），遵循上游 libavb
/// <c>AvbPropertyDescriptor</c> 结构：任意键值对。</para>
/// </summary>
public sealed class AvbPropertyDescriptor
{
    /// <summary>
    /// Gets or sets the property key.
    /// <para>获取或设置属性键。</para>
    /// </summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the property value bytes.
    /// <para>获取或设置属性值字节。</para>
    /// </summary>
    public byte[] Value { get; init; } = [];
}
