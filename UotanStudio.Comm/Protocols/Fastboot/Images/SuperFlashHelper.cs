using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Writing;

namespace UotanStudio.Comm.Protocols.Fastboot.Images;

/// <summary>
/// Coordinates super partition operations against a <see cref="FastbootSession"/>: querying
/// the super device size, reading logical partitions from a super image (see
/// <see cref="SuperImageBuilder"/>), dispatching userspace-fastboot super commands, and
/// flashing a complete super image via update-super + per-logical-partition flashes.
/// <para>协调针对 <see cref="FastbootSession"/> 的 super 分区操作：查询 super 设备大小、
/// 从 super 镜像读取逻辑分区（见 <see cref="SuperImageBuilder"/>）、派发用户空间 fastboot
/// 的 super 命令，以及通过 update-super + 逐逻辑分区刷写完整 super 镜像。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SuperFlashHelper"/> class.
/// <para>初始化 <see cref="SuperFlashHelper"/> 类的新实例。</para>
/// </remarks>
/// <param name="session">The fastboot session. fastboot 会话。</param>
/// <param name="mainPartition">The super partition name. super 分区名称。</param>
internal sealed class SuperFlashHelper(FastbootSession session, string mainPartition = "super")
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
    /// Flashes a prebuilt super image (LP metadata + logical partition data) to the device.
    /// <para>将预构建的 super 镜像（LP 元数据 + 逻辑分区数据）刷写到设备。</para>
    /// </summary>
    /// <remarks>
    /// A compact metadata image (one geometry block followed by one metadata table) is applied first
    /// through <c>update-super</c>, which recreates the logical partitions on the device; each
    /// logical partition's data is then streamed with <c>download</c> + <c>flash</c>, reading
    /// linear extents directly out of the image (zero extents are zero-filled). This mirrors
    /// the AOSP fastboot <c>flash super</c> flow.
    /// <para>先通过 <c>update-super</c> 应用精简元数据镜像（一块 geometry 后接一份元数据表），
    /// 由设备重建逻辑分区；随后将每个逻辑分区的数据通过 <c>download</c> + <c>flash</c>
    /// 流式刷写，线性 extent 直接从镜像中读取（零 extent 补零）。此流程对应 AOSP fastboot 的
    /// <c>flash super</c> 语义。</para>
    /// </remarks>
    /// <param name="superImagePath">The prebuilt super image file path. 预构建的 super 镜像文件路径。</param>
    /// <param name="wipe">Whether to wipe the super partition before updating its metadata.
    /// 更新元数据前是否擦除 super 分区。</param>
    /// <param name="progress">Optional progress reporter for download/upload byte counts.
    /// 可选的进度报告器（下载/上传字节数）。</param>
    /// <param name="cancellationToken">The cancellation token. 取消令牌。</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="superImagePath"/> is null
    /// or empty. <paramref name="superImagePath"/> 为 null 或空时抛出。</exception>
    /// <exception cref="FileNotFoundException">Thrown when the super image file does not exist.
    /// super 镜像文件不存在时抛出。</exception>
    /// <exception cref="InvalidDataException">Thrown when the file is not a readable super image.
    /// 文件不是可读的 super 镜像时抛出。</exception>
    /// <exception cref="FastbootException">Thrown when a fastboot command fails.
    /// fastboot 命令失败时抛出。</exception>
    public void FlashSuper(
        string superImagePath,
        bool wipe = false,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(superImagePath))
        {
            throw new ArgumentException("Super image path is required. super 镜像路径必填。", nameof(superImagePath));
        }

        if (!File.Exists(superImagePath))
        {
            throw new FileNotFoundException($"Super image file not found: {superImagePath} super 镜像文件不存在。", superImagePath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var image = File.OpenRead(superImagePath);
        var firmwareMetadata = SuperImageBuilder.ReadFirmwareMetadata(image, 0);
        var metadata = SuperImageMetadata.FromFirmware(firmwareMetadata, 0);
        if (metadata.BlockDevices.Count != 1)
            throw new InvalidDataException("Flashing a single raw image requires exactly one LP block device.");
        foreach (var extent in metadata.Extents)
        {
            if (extent.TargetType == SuperImageBuilder.ZeroTargetType) continue;
            var imageSectors = (ulong)image.Length / SuperImageBuilder.LpSectorSize;
            if (extent.TargetSource != 0 || extent.TargetData > imageSectors || extent.NumSectors > imageSectors - extent.TargetData)
                throw new InvalidDataException("LP extent exceeds the supplied raw super image.");
        }

        using (var metadataStream = new MemoryStream())
        {
            metadataStream.Write(MetadataWriter.SerializeGeometry(firmwareMetadata.Geometry));
            metadataStream.Write(MetadataWriter.SerializeMetadata(firmwareMetadata));
            metadataStream.Position = 0;
            _session.UpdateSuper(MainPartition, metadataStream, wipe, progress, cancellationToken).ThrowIfFailed();
        }

        foreach (var partition in metadata.Partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (partition.SizeInBytes <= 0)
            {
                continue;
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"uotan-super-{Guid.NewGuid():N}.img");
            try
            {
                using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, CopyBufferSize))
                {
                    WriteLogicalPartition(image, metadata, partition, output, cancellationToken);
                    output.Flush();
                    output.Position = 0;
                    _session.FlashPartition(partition.Name, output, output.Length, progress, cancellationToken, resolveSlot: false).ThrowIfFailed();
                }
            }
            finally
            {
                TryDeleteTempFile(tempPath);
            }
        }
    }

    /// <summary>
    /// Copies one logical partition's extent data out of the super image into <paramref name="output"/>.
    /// <para>将单个逻辑分区的 extent 数据从 super 镜像复制到 <paramref name="output"/>。</para>
    /// </summary>
    private static void WriteLogicalPartition(
        FileStream image,
        SuperImageMetadata metadata,
        SuperLogicalPartition partition,
        Stream output,
        CancellationToken cancellationToken)
    {
        for (var e = 0; e < partition.NumExtents; e++)
        {
            var extentIndex = checked(partition.FirstExtentIndex + e);
            if (extentIndex >= metadata.Extents.Count)
            {
                throw new InvalidDataException("LP partition references a missing extent.");
            }

            var extent = metadata.Extents[(int)extentIndex];
            var length = checked((long)extent.NumSectors * SuperImageBuilder.LpSectorSize);
            if (extent.TargetType == SuperImageBuilder.ZeroTargetType)
            {
                WriteZeros(output, length, cancellationToken);
            }
            else
            {
                var start = checked((long)extent.TargetData * SuperImageBuilder.LpSectorSize);
                CopyRange(image, output, start, length, cancellationToken);
            }
        }
    }

    /// <summary>Writes <paramref name="count"/> zero bytes to the output stream.
    /// 向输出流写入 <paramref name="count"/> 个零字节。</summary>
    private static void WriteZeros(Stream output, long count, CancellationToken cancellationToken)
    {
        var zeros = new byte[CopyBufferSize];
        var remaining = count;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = (int)Math.Min(CopyBufferSize, remaining);
            output.Write(zeros, 0, chunk);
            remaining -= chunk;
        }
    }

    /// <summary>Copies a bounded byte range from the source image to the output stream.
    /// 从源镜像复制有界字节区间到输出流。</summary>
    private static void CopyRange(FileStream source, Stream output, long offset, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        source.Position = offset;
        var remaining = count;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min(CopyBufferSize, remaining);
            var read = source.Read(buffer, 0, toRead);
            if (read <= 0)
            {
                throw new InvalidDataException($"Super image ended early while extracting partition data at offset {offset}. 提取分区数据时 super 镜像在偏移 {offset} 处提前结束。");
            }

            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    /// <summary>Best-effort deletion of a temp partition file. 尽力删除临时分区文件。</summary>
    private static void TryDeleteTempFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Bounded copy buffer size in bytes. 有界复制缓冲区大小（字节）。</summary>
    private const int CopyBufferSize = 1024 * 1024;
}