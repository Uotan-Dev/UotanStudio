namespace UotanStudio.Comm.Protocols.Fastboot.Utils;

/// <summary>
/// Coordinates super partition operations against a <see cref="FastbootSession"/>: querying
/// the super device size, reading logical partitions from a super image (see
/// <see cref="SuperImageBuilder"/>), and dispatching userspace-fastboot super commands.
/// Building an optimized super image is not supported without the external LP package and is
/// left as an explicit skeleton.
/// <para>协调针对 <see cref="FastbootSession"/> 的 super 分区操作：查询 super 设备大小、
/// 从 super 镜像读取逻辑分区（见 <see cref="SuperImageBuilder"/>），并派发用户空间 fastboot
/// 的 super 命令。未引入外部 LP 包时不支持构建优化的 super 镜像，保留为显式骨架。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SuperFlashHelper"/> class.
/// <para>初始化 <see cref="SuperFlashHelper"/> 类的新实例。</para>
/// </remarks>
/// <param name="session">The fastboot session. fastboot 会话。</param>
/// <param name="mainPartition">The super partition name. super 分区名称。</param>
public sealed class SuperFlashHelper(FastbootSession session, string mainPartition = "super")
{
    private readonly FastbootSession _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>
    /// Gets the main super partition name (usually "super").
    /// <para>获取主 super 分区名称（通常为 "super"）。</para>
    /// </summary>
    public string MainPartition { get; } = string.IsNullOrWhiteSpace(mainPartition) ? "super" : mainPartition;

    /// <summary>
    /// Queries the super partition size in bytes via <c>getvar:partition-size:&lt;super&gt;</c>.
    /// <para>通过 <c>getvar:partition-size:&lt;super&gt;</c> 查询 super 分区大小（字节）。</para>
    /// </summary>
    /// <returns>The partition size in bytes, or 0 when the device does not report it. 分区大小（字节）；设备未报告时返回 0。</returns>
    public long GetSuperSize() => _session.GetPartitionSizeLong(MainPartition);

    /// <summary>
    /// Returns whether the file is a super partition image.
    /// <para>返回文件是否为 super 分区镜像。</para>
    /// </summary>
    /// <param name="superImagePath">The super image file path. super 镜像文件路径。</param>
    /// <returns><c>true</c> when the file is a super image. 文件为 super 镜像时返回 <c>true</c>。</returns>
    public bool IsSuperImage(string superImagePath)
        => new SuperImageBuilder().IsSuperImage(superImagePath);

    /// <summary>
    /// Enumerates the logical partitions described by a super image file.
    /// <para>枚举 super 镜像文件描述的逻辑分区。</para>
    /// </summary>
    /// <param name="superImagePath">The super image file path. super 镜像文件路径。</param>
    /// <param name="slot">The metadata slot index (0-based). 元数据槽位索引（从 0 开始）。</param>
    /// <returns>The logical partitions. 逻辑分区列表。</returns>
    public IReadOnlyList<SuperLogicalPartition> ReadLogicalPartitions(string superImagePath, int slot = 0)
        => new SuperImageBuilder().GetLogicalPartitions(superImagePath, slot);

    /// <summary>
    /// Updates the super partition metadata from a metadata image (without wiping by default).
    /// <para>从元数据镜像更新 super 分区元数据（默认不擦除）。</para>
    /// </summary>
    /// <param name="metadataImagePath">The LP metadata image file path. LP 元数据镜像文件路径。</param>
    /// <param name="wipe">Whether to wipe the super partition before updating. 更新前是否擦除 super 分区。</param>
    /// <returns>The fastboot response. fastboot 响应。</returns>
    public FastbootResponse UpdateSuperMetadata(string metadataImagePath, bool wipe = false)
        => _session.UpdateSuper(MainPartition, metadataImagePath, wipe);

    /// <summary>
    /// Creates a logical partition (requires userspace fastboot mode).
    /// <para>创建逻辑分区（需要用户空间 fastboot 模式）。</para>
    /// </summary>
    /// <param name="name">The logical partition name. 逻辑分区名称。</param>
    /// <param name="size">The partition size in bytes. 分区大小（字节）。</param>
    /// <returns>The fastboot response. fastboot 响应。</returns>
    public FastbootResponse CreateLogicalPartition(string name, long size)
        => _session.CreateLogicalPartition(name, size);

    /// <summary>
    /// Deletes a logical partition (requires userspace fastboot mode).
    /// <para>删除逻辑分区（需要用户空间 fastboot 模式）。</para>
    /// </summary>
    /// <param name="name">The logical partition name. 逻辑分区名称。</param>
    /// <returns>The fastboot response. fastboot 响应。</returns>
    public FastbootResponse DeleteLogicalPartition(string name)
        => _session.DeleteLogicalPartition(name);

    /// <summary>
    /// Resizes a logical partition (requires userspace fastboot mode).
    /// <para>调整逻辑分区大小（需要用户空间 fastboot 模式）。</para>
    /// </summary>
    /// <param name="name">The logical partition name. 逻辑分区名称。</param>
    /// <param name="size">The new partition size in bytes. 新分区大小（字节）。</param>
    /// <returns>The fastboot response. fastboot 响应。</returns>
    public FastbootResponse ResizeLogicalPartition(string name, long size)
        => _session.ResizeLogicalPartition(name, size);

    /// <summary>
    /// Wipes the super partition via <c>wipe-super</c>.
    /// <para>通过 <c>wipe-super</c> 清除 super 分区。</para>
    /// </summary>
    /// <returns>The fastboot response. fastboot 响应。</returns>
    public FastbootResponse WipeSuper() => _session.WipeSuper(MainPartition);

    /// <summary>
    /// Flashes a prebuilt super image (metadata + logical partition data) to the device.
    /// <para>将预构建的 super 镜像（元数据 + 逻辑分区数据）刷写到设备。</para>
    /// </summary>
    /// <remarks>
    /// Skeleton only: a production implementation must split the super image into download-sized
    /// chunks (respecting <c>max-download-size</c>) and stream them via
    /// <c>download</c> + <c>flash</c>, ideally by generating an optimized sparse super image —
    /// which requires the LP metadata writer (FirmwareKit.Lp) that this project does not
    /// reference. Use <see cref="UpdateSuperMetadata"/> for metadata-only updates instead.
    /// <para>仅骨架：生产实现必须将 super 镜像按下载上限（<c>max-download-size</c>）切块，
    /// 通过 <c>download</c> + <c>flash</c> 流式写入，理想情况下生成优化的稀疏 super 镜像——
    /// 这需要本项目未引用的 LP 元数据写入器（FirmwareKit.Lp）。仅元数据更新请改用
    /// <see cref="UpdateSuperMetadata"/>。</para>
    /// </remarks>
    /// <param name="superImagePath">The prebuilt super image file path. 预构建的 super 镜像文件路径。</param>
    /// <exception cref="NotSupportedException">Thrown because building/streaming a full super
    /// image requires the external LP package. 因构建/流式写入完整 super 镜像需要外部 LP 包而抛出。</exception>
    public void FlashSuper(string superImagePath)
    {
        throw new NotSupportedException(
            "Streaming a full super image requires the LP metadata writer (FirmwareKit.Lp), " +
            "which is not referenced in this project. 流式写入完整 super 镜像需要 LP 元数据" +
            "写入器（FirmwareKit.Lp），本项目未引用该包。");
    }
}