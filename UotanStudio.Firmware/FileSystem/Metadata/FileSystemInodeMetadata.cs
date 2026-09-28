namespace UotanStudio.Firmware.FileSystem.Metadata;

/// <summary>A filesystem timestamp independent of the host clock range and precision.
/// <para>与宿主机时钟范围和精度无关的文件系统时间戳。</para></summary>
/// <param name="Seconds">Seconds elapsed since the Unix epoch; negative values denote pre-1970 times.
/// <para>自 Unix 纪元起的秒数；负值表示 1970 年之前的时间。</para></param>
/// <param name="Nanoseconds">Sub-second nanoseconds; validation rejects values of one second or more.
/// <para>亚秒纳秒数；校验时拒绝大于等于一秒的值。</para></param>
public sealed record FileSystemTimestamp(long Seconds, uint Nanoseconds)
{
    internal void Validate()
    {
        if (Nanoseconds >= 1_000_000_000)
        {
            throw new InvalidDataException("Filesystem timestamp nanoseconds exceed one second.");
        }
    }
}

/// <summary>Source inode attributes that must not be inferred from the extracted host files.
/// <para>不能从解包后的宿主文件反推的源 inode 属性。</para></summary>
public sealed record FileSystemInodeMetadata
{
    /// <summary>Gets the source access time, when represented by the format.
    /// <para>获取源文件的访问时间（格式有记录时）。</para></summary>
    /// <value>The captured access time, or <see langword="null"/> when the source format records none.
    /// <para>捕获的访问时间；源格式未记录时为 <see langword="null"/>。</para></value>
    public FileSystemTimestamp? AccessTime { get; init; }
    /// <summary>Gets the source inode change time, not the host creation time.
    /// <para>获取源 inode 的变更时间（ctime），而非宿主创建时间。</para></summary>
    /// <value>The captured change time, or <see langword="null"/> when the source records none.
    /// <para>捕获的变更时间；源未记录时为 <see langword="null"/>。</para></value>
    public FileSystemTimestamp? ChangeTime { get; init; }
    /// <summary>Gets the source modification time.
    /// <para>获取源文件的修改时间。</para></summary>
    /// <value>The captured modification time, or <see langword="null"/> when the source records none.
    /// <para>捕获的修改时间；源未记录时为 <see langword="null"/>。</para></value>
    public FileSystemTimestamp? ModificationTime { get; init; }
    /// <summary>Gets the source birth time, when represented by the inode.
    /// <para>获取源文件的创建时间（btime，inode 有记录时）。</para></summary>
    /// <value>The captured birth time, or <see langword="null"/> when the inode carries no crtime extension.
    /// <para>捕获的创建时间；inode 无 crtime 扩展时为 <see langword="null"/>。</para></value>
    public FileSystemTimestamp? BirthTime { get; init; }
    /// <summary>Gets the inode generation.
    /// <para>获取 inode 代数（generation）。</para></summary>
    /// <value>The source generation counter; <c>0</c> when the source stores none.
    /// <para>源 generation 计数；源未存储时为 <c>0</c>。</para></value>
    public uint Generation { get; init; }
    /// <summary>Gets the project identifier, when present.
    /// <para>获取项目标识符（存在时）。</para></summary>
    /// <value>The preserved project id for project-quota features, or <see langword="null"/> when absent.
    /// <para>为 project-quota 特性保留的项目 id；不存在时为 <see langword="null"/>。</para></value>
    public uint? ProjectId { get; init; }
    /// <summary>Gets the EXT4 inode version counter.
    /// <para>获取 EXT4 inode 版本计数。</para></summary>
    /// <value>The preserved version counter, or <see langword="null"/> when the source format records none.
    /// <para>保留的版本计数；源格式未记录时为 <see langword="null"/>。</para></value>
    public ulong? Version { get; init; }
    /// <summary>Gets the original link count; allocation-dependent counts are recomputed on editing.
    /// <para>获取原始硬链接数；依赖分配的计数在编辑时重新计算。</para></summary>
    /// <value>The hard-link count recorded by the source inode, <c>1</c> or more for regular entries.
    /// <para>源 inode 记录的硬链接数；常规条目至少为 <c>1</c>。</para></value>
    public uint LinkCount { get; init; }
    /// <summary>Gets the F2FS inode advice byte.
    /// <para>获取 F2FS inode 的 advice 字节。</para></summary>
    /// <value>The raw i_advise value; <c>0</c> when the source inode stores none.
    /// <para>原始 i_advise 值；源 inode 未存储时为 <c>0</c>。</para></value>
    public byte F2fsAdvise { get; init; }
    /// <summary>Gets the F2FS per-inode compression contract.
    /// <para>获取 F2FS 按 inode 的压缩约定。</para></summary>
    /// <value>The compression layout to preserve, or <see langword="null"/> when the file is not compressed.
    /// <para>需保留的压缩布局；文件未压缩时为 <see langword="null"/>。</para></value>
    public F2fsInodeCompression? F2fsCompression { get; init; }

    internal void Validate()
    {
        AccessTime?.Validate();
        ChangeTime?.Validate();
        ModificationTime?.Validate();
        BirthTime?.Validate();
    }
}

/// <summary>Kernel algorithm identifier, logical cluster size and flags for an F2FS inode.
/// <para>F2FS inode 的内核算法标识、逻辑簇大小与标志位。</para></summary>
/// <param name="Algorithm">Kernel compression id: <c>0</c> LZO, <c>1</c> LZ4, <c>2</c> ZSTD.
/// <para>内核算法 ID：<c>0</c> LZO、<c>1</c> LZ4、<c>2</c> ZSTD。</para></param>
/// <param name="LogClusterSize">Base-2 cluster exponent; cluster bytes = 1 &lt;&lt; (value + 12), and rebuild accepts <c>2</c> through <c>8</c>.
/// <para>簇大小的 2 为底指数；簇字节数 = 1 &lt;&lt; (值 + 12)，重建仅接受 <c>2</c> 到 <c>8</c>。</para></param>
/// <param name="Flags">Compression flags; bit 0 marks per-cluster CRC32 and the high byte carries the level, other low-byte bits are rejected on rebuild.
/// <para>压缩标志位；bit 0 表示按簇 CRC32，高字节存放压缩级别，其余低字节位在重建时被拒绝。</para></param>
public sealed record F2fsInodeCompression(byte Algorithm, byte LogClusterSize, ushort Flags);