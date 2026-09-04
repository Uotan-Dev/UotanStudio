namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// erofs 打包选项。
/// <para>EROFS packing options.</para>
/// </summary>
public sealed class ErofsOptions
{
    /// <summary>
    /// 块大小（字节，须为不小于 512 的 2 的幂；默认 4096）。
    /// <para>Block size in bytes (a power of two of at least 512; default 4096).</para>
    /// </summary>
    public int BlockSize { get; set; } = 4096;

    /// <summary>
    /// 是否使用压缩（pcluster 边界对齐块大小）。压缩算法见 <see cref="Algorithm"/>。
    /// <para>Whether to use compression (pcluster boundaries aligned to the block size). The
    /// algorithm is selected via <see cref="Algorithm"/>.</para>
    /// </summary>
    public bool Compress { get; set; } = true;

    /// <summary>
    /// 压缩算法（默认 LZ4）。<see cref="Compress"/> 关闭时该值不影响输出（仅写入
    /// s_available_compr_algs 为 0）。
    /// <para>Compression algorithm (LZ4 by default). When <see cref="Compress"/> is off this value
    /// does not affect the output (s_available_compr_algs is written as 0).</para>
    /// </summary>
    public ErofsCompressionAlgorithm Algorithm { get; set; } = ErofsCompressionAlgorithm.Lz4;

    /// <summary>
    /// 压缩级别 1-9：LZ4 映射为快速（1-4）/高压缩（5-9）模式；LZMA/DEFLATE/ZSTD 映射为
    /// 各自的码率级别。
    /// <para>Compression level 1-9: mapped to LZ4 fast (1-4) / high-compression (5-9) modes, or to
    /// the rate level of the selected codec (LZMA/DEFLATE/ZSTD).</para>
    /// </summary>
    public int CompressionLevel { get; set; } = 1;

    /// <summary>
    /// 是否对压缩文件使用紧凑（amortized）索引写端而非 legacy full 8B 索引。
    /// 默认 false（legacy full，与 mkfs 旧布局一致且已被广泛验证）；开启后压缩文件
    /// 使用 COMPRESSED_COMPACT datalayout 与 4B amortized 位打包索引。
    /// <para>Whether compressed files use the compacted (amortized) index writer instead of the
    /// legacy full 8-byte index. Defaults to false (legacy full, the well-validated mkfs-era
    /// layout); when enabled, compressed files use the COMPRESSED_COMPACT datalayout with 4-byte
    /// amortized bit-packed indexes.</para>
    /// </summary>
    public bool UseCompactedIndexes { get; set; }

    /// <summary>
    /// 卷名（写入超级块 s_volume_name，截断或 NUL 填充至 16 字节）。
    /// <para>Volume name (written to the superblock s_volume_name, truncated or NUL padded to 16
    /// bytes).</para>
    /// </summary>
    public string VolumeName { get; set; } = "erofs";

    /// <summary>
    /// 可选的按文件 xattr 注入：键为镜像内相对路径（'/' 分隔，如 "system/etc/hosts"），
    /// 值为该文件应写入的 xattr 条目列表。条目名称为完整名称（如 "security.selinux"）。
    /// 构建器把这些 xattr 编码为 inline ibody 并设置 inode 的 xattr 字段；未列出的文件不带
    /// xattr。为 null 时所有文件均不带 xattr（与旧行为一致）。
    /// <para>Optional per-file xattr injection: keys are image-relative paths ('/' separated,
    /// e.g. "system/etc/hosts"), values are the xattr entries to write for that file. Entry names
    /// are full names (e.g. "security.selinux"). The builder encodes them as an inline ibody and
    /// sets the inode's xattr fields; files not listed carry no xattr. Null means no file carries
    /// any xattr (the legacy behavior).</para>
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ErofsXattrEntry>>? FileXattrs { get; set; }

    /// <summary>
    /// 是否启用尾部打包（tail-packing）：不足一块的文件尾部内联在 inode 之后。
    /// 默认 true，与 mkfs 默认行为一致。
    /// <para>Whether to enable tail-packing: file tails smaller than a block are inlined after the
    /// inode. Defaults to true, matching the mkfs default.</para>
    /// </summary>
    public bool TailPacking { get; set; } = true;

    /// <summary>
    /// 是否启用 fragments（尾部碎片聚合）：不足一块的文件（原本经 TailPacking 整文件内联在
    /// inode 后）改为按序拼入 packed inode 的数据区，各文件 inode 以 fragment 引用指向 packed
    /// 区内的字节偏移。开启后 <see cref="TailPacking"/> 会被强制关闭（避免双重内联）；文件级
    /// 去重见 <see cref="Dedup"/>。
    /// <para>Whether to enable fragments (tail-fragment aggregation): files smaller than one block
    /// (previously inlined entirely after the inode via TailPacking) are instead concatenated into
    /// the data area of a synthetic "packed inode", and each file's inode carries a fragment
    /// reference pointing at its byte offset inside the packed region. Enabling this forces
    /// <see cref="TailPacking"/> off (to avoid double inlining); whole-file dedupe is selected via
    /// <see cref="Dedup"/>.</para>
    /// </summary>
    public bool Fragments { get; set; }

    /// <summary>
    /// 是否启用整文件去重：内容完全相同的文件共享同一块区（FLAT 整块区，或 fragments 模式下
    /// packed 区内的同一字节区间）。Pass1 流式计算每文件 SHA-256（<see
    /// cref="System.Security.Cryptography.IncrementalHash"/>，不整读大文件），分配时命中相同
    /// 哈希即复用首个文件的存储位置。
    /// <para>Whether to enable whole-file dedupe: files with identical content share one block
    /// range (a FLAT whole-block range, or -- under the fragments feature -- the same byte range
    /// inside the packed area). Pass 1 hashes every file with SHA-256 streamed via
    /// <see cref="System.Security.Cryptography.IncrementalHash"/> (never loading big files wholly),
    /// and allocation reuses the first file's storage when a hash matches.</para>
    /// </summary>
    public bool Dedup { get; set; }

    /// <summary>
    /// 是否启用 METABOX（incompat 0x100）写端子集：把“纯 flat 全块存储的普通文件”的 32 字节
    /// compact inode 记录打包进 metabox 容器（metabox inode 的数据区），目录条目以 bit63 nid
    /// 引用容器槽位。该子集与解析侧 <see cref="Erofs.Parser.ErofsParser"/> 的 metabox 读路径镜像
    /// （metabox inode 数据 = 紧凑 inode 槽表，槽位 = nid 低 63 位）。目录、符号链接、设备与
    /// 压缩/碎片/xattr 文件仍留在经典 inode 表。开启后强制关闭 <see cref="TailPacking"/>
    /// （尾部余数改走全块 flat，保证入容器记录恒为纯 32 字节）。
    /// 默认 false（经典 flat 布局，与 erofs-utils 1.8.x 产物一致且可被其 fsck 校验）。
    /// <para>Whether to enable the METABOX (incompat 0x100) write-side subset: the 32-byte compact
    /// inode records of plain flat whole-block regular files are packed into the metabox container
    /// (the metabox inode's data area), and directory entries reference container slots through
    /// bit-63 nids. The subset mirrors the metabox read path of <see cref="Erofs.Parser.ErofsParser"/>
    /// (metabox inode data = a compact-inode slot table addressed by the low 63 bits of the nid).
    /// Directories, symlinks, devices and compressed/fragment/xattr files stay in the classic inode
    /// table. Enabling it forces <see cref="TailPacking"/> off (remainders are stored as whole flat
    /// blocks so every container record stays a pure 32 bytes). Defaults to false (the classic flat
    /// layout, matching erofs-utils 1.8.x output and verifiable by its fsck).</para>
    /// </summary>
    public bool Metabox { get; set; }

    /// <summary>
    /// 额外设备数量（多设备镜像中除主设备外的附加设备数）。
    /// 写入超级块 s_extra_devices 并在 s_devt_slotoff 处分配设备表；设备表位于
    /// 块对齐的 s_devt_slotoff * 128 偏移处。
    /// <para>Number of extra devices (additional devices beyond the primary device in a multi-device
    /// image). Written to the superblock s_extra_devices and allocates a device table at
    /// s_devt_slotoff; the device table sits at the block-aligned offset s_devt_slotoff * 128.</para>
    /// </summary>
    public int ExtraDeviceCount { get; set; }

    /// <summary>
    /// 校验并规整选项。
    /// <para>Validates and normalizes the options.</para>
    /// </summary>
    internal void Validate()
    {
        if (BlockSize < 512 || (BlockSize & (BlockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BlockSize),
                "BlockSize must be a power of two of at least 512 bytes.");
        }

        CompressionLevel = Math.Clamp(CompressionLevel, 1, 9);
        if (!Enum.IsDefined(Algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(Algorithm), Algorithm,
                "Unknown EROFS compression algorithm.");
        }

        if (string.IsNullOrEmpty(VolumeName))
        {
            VolumeName = "erofs";
        }

        if (ExtraDeviceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ExtraDeviceCount), ExtraDeviceCount,
                "ExtraDeviceCount must be non-negative.");
        }

        // fragments 的 packed 区与 TailPacking 内联互斥：前者已把不足一块的文件余量汇入 packed
        // inode，尾部若再内联会双重存储。Fragments 开启时强制关闭 TailPacking（与 mkfs 中
        // fragment 与 tail-end inline 二选一的约定一致）。
        // The packed area of the fragments feature and TailPacking inlining are mutually exclusive:
        // fragments already merge sub-block remainders into the packed inode, and inlining the same
        // bytes again after each inode would store them twice. Fragments therefore forces
        // TailPacking off (mirroring the mkfs convention of choosing fragments over tail-end inline).
        if (Fragments)
        {
            TailPacking = false;
        }

        // METABOX 记录必须是纯 32 字节 compact inode（无内联尾部），故开启时同样强制关闭
        // TailPacking：非块对齐文件的余数改走全块 flat 数据区，其记录才可进入 metabox 容器。
        // A METABOX record must be a pure 32-byte compact inode (no inline tail), so enabling it also
        // forces TailPacking off: non-block-aligned files store their remainder as whole flat blocks
        // and only then can their records live in the metabox container.
        if (Metabox)
        {
            TailPacking = false;
        }
    }
}