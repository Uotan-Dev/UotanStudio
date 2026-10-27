namespace UotanStudio.Firmware.FileSystem.Ext4;

/// <summary>Selects whether byte-identical regular-file data blocks each keep their own physical blocks or may share one.
/// <para>选择内容完全相同的普通文件数据块是各自占用物理块，还是允许共享同一个物理块。</para></summary>
public enum Ext4BlockSharingMode
{
    /// <summary>Every file owns its data blocks independently. <para>每个文件独立拥有自己的数据块。</para></summary>
    Independent,

    /// <summary>Byte-identical data blocks are deduplicated onto a single shared physical block; requires a read-only filesystem. <para>内容相同的数据块去重到同一个共享物理块；要求文件系统为只读。</para></summary>
    ShareDuplicateData,
}

/// <summary>Selects how much trailing capacity a rebuilt filesystem keeps.
/// <para>选择重建后的文件系统保留多少尾部空闲容量。</para></summary>
public enum Ext4CapacityMode
{
    /// <summary>Keeps the full planned capacity, including all allocated block groups. <para>保留完整规划容量，包含所有已分配的块组。</para></summary>
    Default,

    /// <summary>Trims the image down to the last block actually used, dropping unused trailing blocks. <para>将镜像裁剪到最后一个实际使用的块，丢弃未使用的尾部块。</para></summary>
    Minimize,
}

/// <summary>
/// Input options for building an ext4 image from a source directory.
/// <para>从源目录构建 ext4 镜像时的输入选项。</para>
/// </summary>
public sealed class Ext4Options
{
    /// <summary>Filesystem block size in bytes; the builder requires at least 4096 and a power of two.
    /// <para>文件系统块大小（字节）；构建器要求至少 4096 且为 2 的幂。</para></summary>
    /// <value>The block size used for layout planning; defaults to <c>4096</c>.
    /// <para>用于布局规划的块大小；默认 <c>4096</c>。</para></value>
    public int BlockSize { get; init; } = 4096;

    /// <summary>Inode size in bytes; the builder requires at least 128 and a power of two.
    /// <para>inode 大小（字节）；构建器要求至少 128 且为 2 的幂。</para></summary>
    /// <value>Bytes per inode slot; defaults to <c>256</c>.
    /// <para>每个 inode 槽位的字节数；默认 <c>256</c>。</para></value>
    public int InodeSize { get; init; } = 256;

    /// <summary>Compression-intent flag carried with the options; the EXT4 build pipeline never reads it.
    /// <para>随选项携带的压缩意图标志；EXT4 构建流程不会读取它。</para></summary>
    /// <value>Defaults to <c>false</c>; setting it has no effect on the produced image bytes.
    /// <para>默认 <c>false</c>；设置它不会改变生成镜像的字节内容。</para></value>
    public bool Ext4Compress { get; set; } = false;

    /// <summary>Enables hole detection so zero-filled sparse regions of source files stay unallocated.
    /// <para>启用空洞检测，使源文件中全零的稀疏区域不分配数据块。</para></summary>
    /// <value><c>true</c> scans each regular file for holes and records them as extent gaps; defaults to <c>false</c>.
    /// <para><c>true</c> 时扫描每个常规文件的空洞并以 extent 间隙记录；默认 <c>false</c>。</para></value>
    public bool EnableSparse { get; set; } = false;

    /// <summary>Filesystem volume label, limited to 16 UTF-8 bytes.
    /// <para>文件系统卷标，限制为 16 个 UTF-8 字节。</para></summary>
    /// <value>The label written to the superblock; defaults to <c>"UOTANSTUDIO"</c>. A label whose UTF-8 encoding exceeds the 16-byte superblock slot fails the build.
    /// <para>写入超级块的卷标；默认 <c>"UOTANSTUDIO"</c>。UTF-8 编码超出超级块 16 字节槽位时构建失败。</para></value>
    public string VolumeName { get; init; } = "UOTANSTUDIO";

    /// <summary>Filesystem UUID override, applied only when it is exactly 16 bytes long.
    /// <para>文件系统 UUID 覆盖值，仅在长度恰为 16 字节时生效。</para></summary>
    /// <value><see langword="null"/> or a non-16-byte array falls back to the builder's fixed default UUID; defaults to <see langword="null"/>.
    /// <para><see langword="null"/> 或长度不为 16 字节的数组将回退到构建器的固定默认 UUID；默认 <see langword="null"/>。</para></value>
    public byte[]? Uuid { get; init; }

    /// <summary>EXT4 casefold filename encoding ID; currently only UTF-8 (1) is supported.
    /// <para>EXT4 casefold 文件名编码 ID；当前仅支持 UTF-8（1）。</para></summary>
    /// <value><see langword="null"/> builds without the casefold feature; any value other than <c>1</c> is rejected during validation.
    /// <para><see langword="null"/> 表示构建时不启用 casefold；除 <c>1</c> 以外的取值会在校验阶段被拒绝。</para></value>
    public ushort? CasefoldEncoding { get; init; }

    /// <summary>EXT4 casefold encoding flags inherited from the source superblock.
    /// <para>从源超级块继承的 EXT4 casefold 编码标志。</para></summary>
    /// <value>Written next to the encoding ID; must be <c>0</c> when <see cref="CasefoldEncoding"/> is <see langword="null"/>. Defaults to <c>0</c>.
    /// <para>与编码 ID 一并写入；当 <see cref="CasefoldEncoding"/> 为 <see langword="null"/> 时必须为 <c>0</c>。默认 <c>0</c>。</para></value>
    public ushort CasefoldEncodingFlags { get; init; }

    /// <summary>Permission-preservation intent flag; the EXT4 build pipeline never reads it.
    /// <para>权限保留意图标志；EXT4 构建流程不会读取它。</para></summary>
    /// <value>Defaults to <c>true</c>; inode modes come from scanned source attributes and metadata overrides regardless of this value.
    /// <para>默认 <c>true</c>；无论取值如何，inode 模式均来自源属性扫描与元数据覆盖。</para></value>
    public bool PreservePermissions { get; set; } = true;

    /// <summary>Timestamp-preservation intent flag; the EXT4 build pipeline never reads it.
    /// <para>时间戳保留意图标志；EXT4 构建流程不会读取它。</para></summary>
    /// <value>Defaults to <c>true</c>; inode timestamps are taken from the source file times regardless of this value.
    /// <para>默认 <c>true</c>；无论取值如何，inode 时间戳均取自源文件时间。</para></value>
    public bool PreserveTimestamps { get; set; } = true;

    /// <summary>Enables metadata checksums over the superblock, group descriptors, inodes and directory tails.
    /// <para>启用覆盖超级块、块组描述符、inode 与目录尾部的元数据校验和。</para></summary>
    /// <value><c>true</c> (the default) sets the metadata_csum read-only compatible feature and writes CRC32c checksums.
    /// <para><c>true</c>（默认）设置 metadata_csum 只读兼容特性并写入 CRC32c 校验和。</para></value>
    public bool EnableMetadataCsum { get; set; } = true;

    /// <summary>Includes an internal ext4 journal in the built image.
    /// <para>在生成的镜像中包含内置 ext4 日志。</para></summary>
    /// <value><c>true</c> (the default) reserves 1024 journal blocks in block group 0, writes the journal area and inode 8, and sets the has_journal compatibility flag.
    /// <para><c>true</c>（默认）在 0 号块组预留 1024 个日志块，写入日志区域与 8 号 inode，并设置 has_journal 兼容标志。</para></value>
    public bool IncludeJournal { get; set; } = true;

    /// <summary>Optional exact filesystem capacity in bytes; must be block aligned.
    /// <para>可选的精确文件系统容量（字节）；必须按块对齐。</para></summary>
    /// <value><see langword="null"/> lets the builder size the image from its content; a value must be positive, a multiple of <see cref="BlockSize"/>, and large enough for metadata plus content, otherwise validation or planning throws. It is mutually exclusive with <see cref="Ext4CapacityMode.Minimize"/>.
    /// <para><see langword="null"/> 时由构建器按内容定容；取值必须为正、为 <see cref="BlockSize"/> 的整数倍，且足以容纳元数据与内容，否则在校验或规划阶段抛出。与 <see cref="Ext4CapacityMode.Minimize"/> 互斥。</para></value>
    public long? TargetSizeBytes { get; init; }

    /// <summary>Controls whether the rebuilt filesystem keeps normal capacity or trims unused trailing blocks.
    /// <para>控制重建后的文件系统是保留常规容量，还是裁掉未使用的尾部块。</para></summary>
    /// <value><see cref="Ext4CapacityMode.Default"/> (the default) keeps the full planned block count; <see cref="Ext4CapacityMode.Minimize"/> shrinks the image to the last used block and forces the stream build through a staging file.
    /// <para><see cref="Ext4CapacityMode.Default"/>（默认）保留完整规划块数；<see cref="Ext4CapacityMode.Minimize"/> 将镜像缩小到最后一个已用块，并使流式构建走暂存文件路径。</para></value>
    public Ext4CapacityMode CapacityMode { get; init; }

    /// <summary>Sets the EXT4 read-only compatible feature bit.
    /// <para>设置 EXT4 只读兼容特性位。</para></summary>
    /// <value>Defaults to <c>false</c>; <c>true</c> marks the filesystem readonly, and <see cref="Ext4BlockSharingMode.ShareDuplicateData"/> requires it.
    /// <para>默认 <c>false</c>；<c>true</c> 将文件系统标记为只读，且 <see cref="Ext4BlockSharingMode.ShareDuplicateData"/> 要求其为此值。</para></value>
    public bool ReadOnly { get; init; }

    /// <summary>Controls whether byte-identical regular-file data blocks may share physical blocks.
    /// <para>控制内容相同的普通文件数据块是否允许共享物理块。</para></summary>
    /// <value>Defaults to <see cref="Ext4BlockSharingMode.Independent"/>; <see cref="Ext4BlockSharingMode.ShareDuplicateData"/> deduplicates identical blocks and is rejected unless <see cref="ReadOnly"/> is set.
    /// <para>默认 <see cref="Ext4BlockSharingMode.Independent"/>；<see cref="Ext4BlockSharingMode.ShareDuplicateData"/> 对相同块去重，仅在设置了 <see cref="ReadOnly"/> 时被接受。</para></value>
    public Ext4BlockSharingMode BlockSharingMode { get; init; }

    internal CancellationToken CancellationToken { get; init; }

    internal Func<ReadOnlyMemory<byte>, byte[]>? BlockHashProvider { get; init; }

    internal string? BlockIndexDirectory { get; init; }

    internal Action? SharedBlockPlanCompleted { get; init; }

    /// <summary>
    /// Optional path to an Android-style <c>fs_config</c> file (<c>path uid gid mode capabilities</c>)
    /// applied over the source tree; matching entries override the scanned uid/gid/mode.
    /// <para>可选的 Android 风格 <c>fs_config</c> 文件路径（<c>path uid gid mode capabilities</c>），
    /// 覆盖源树扫描结果：命中的条目以文件中的 uid/gid/mode 为准。</para>
    /// </summary>
    /// <value><see langword="null"/> (the default) means no fs_config override is applied.
    /// <para><see langword="null"/>（默认）表示不应用 fs_config 覆盖。</para></value>
    public string? FsConfigPath { get; init; }

    /// <summary>
    /// Optional path to an Android-style <c>file_contexts</c> file (<c>path label</c>) applied over
    /// the source tree; matching entries are stored as <c>security.selinux</c> xattrs.
    /// <para>可选的 Android 风格 <c>file_contexts</c> 文件路径（<c>path label</c>），
    /// 命中的条目以 <c>security.selinux</c> xattr 写入镜像。</para>
    /// </summary>
    /// <value><see langword="null"/> (the default) means no SELinux label override is applied.
    /// <para><see langword="null"/>（默认）表示不应用 SELinux 标签覆盖。</para></value>
    public string? FileContextsPath { get; init; }

    /// <summary>Preloaded override tables; when set they take precedence over the path-based sources above. <para>预加载的覆盖表；设置后优先于上面的按路径来源。</para></summary>
    internal IReadOnlyDictionary<string, Metadata.FsConfigEntry>? FsConfig { get; set; }
    internal IReadOnlyDictionary<string, string>? FileContexts { get; set; }
}