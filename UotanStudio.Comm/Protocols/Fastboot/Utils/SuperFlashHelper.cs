namespace UotanStudio.Comm.Protocols.Fastboot.Utils;

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
    /// Flashes a prebuilt super image (LP metadata + logical partition data) to the device.
    /// <para>将预构建的 super 镜像（LP 元数据 + 逻辑分区数据）刷写到设备。</para>
    /// </summary>
    /// <remarks>
    /// The image's metadata region (geometry + all primary metadata slots) is applied first
    /// through <c>update-super</c>, which recreates the logical partitions on the device; each
    /// logical partition's data is then streamed with <c>download</c> + <c>flash</c>, reading
    /// linear extents directly out of the image (zero extents are zero-filled). This mirrors
    /// the AOSP fastboot <c>flash super</c> flow.
    /// <para>先通过 <c>update-super</c> 应用镜像的元数据区域（geometry + 全部主元数据槽位），
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

        var builder = new SuperImageBuilder();
        var metadataRegion = ReadMetadataRegion(superImagePath);
        var metadata = builder.ReadMetadata(metadataRegion, 0)
            ?? throw new InvalidDataException($"File is not a readable super image: {superImagePath} 文件不是可读的 super 镜像。");

        // The update-super payload is everything before the first logical sector: geometry,
        // primary metadata slots, and the zero padding up to the data area.
        // update-super 负载是首个逻辑扇区之前的内容：geometry、主元数据槽位及至数据区的零填充。
        var firstLogicalSector = metadata.BlockDevices.Count > 0 && metadata.BlockDevices[0].FirstLogicalSector > 0
            ? metadata.BlockDevices[0].FirstLogicalSector
            : ComputedFirstLogicalSector(metadata);
        var metadataImageLength = checked((long)firstLogicalSector * SuperImageBuilder.LpSectorSize);
        var imageLength = new FileInfo(superImagePath).Length;
        if (metadataImageLength > imageLength)
        {
            metadataImageLength = imageLength;
        }

        if (metadataImageLength > metadataRegion.Length)
        {
            var extended = new byte[metadataImageLength];
            Array.Copy(metadataRegion, extended, metadataRegion.Length);
            using (var fs = File.OpenRead(superImagePath))
            {
                fs.Position = metadataRegion.Length;
                ReadExactly(fs, extended, metadataRegion.Length, extended.Length - metadataRegion.Length);
            }

            metadataRegion = extended;
        }
        else if (metadataImageLength < metadataRegion.Length)
        {
            var trimmed = new byte[metadataImageLength];
            Array.Copy(metadataRegion, trimmed, metadataImageLength);
            metadataRegion = trimmed;
        }

        using (var metadataStream = new MemoryStream(metadataRegion, writable: false))
        {
            _session.UpdateSuper(MainPartition, metadataStream, wipe, progress, cancellationToken).ThrowIfFailed();
        }

        // Flash each logical partition's data. Each partition is materialized to a temp file
        // so multi-extent partitions can be downloaded as one contiguous payload.
        // 逐逻辑分区刷写数据：将每个分区物化到临时文件，使多 extent 分区可作为单个连续负载下载。
        using var image = File.OpenRead(superImagePath);
        foreach (var partition in metadata.Partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (partition.SizeInBytes <= 0)
            {
                // Zero-sized logical partitions carry no data to flash.
                // 零大小的逻辑分区没有可刷写的数据。
                continue;
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"uotan-super-{Guid.NewGuid():N}.img");
            try
            {
                using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, CopyBufferSize))
                {
                    WriteLogicalPartition(image, metadata, partition, firstLogicalSector, output);
                    output.Flush();
                    output.Position = 0;
                    _session.FlashPartition(partition.Name, output, output.Length, progress, cancellationToken).ThrowIfFailed();
                }
            }
            finally
            {
                TryDeleteTempFile(tempPath);
            }
        }
    }

    /// <summary>
    /// Reads the bounded metadata region (geometry + all primary metadata slots) from the
    /// beginning of a super image.
    /// <para>从 super 镜像起始处读取有界元数据区域（geometry + 全部主元数据槽位）。</para>
    /// </summary>
    private static byte[] ReadMetadataRegion(string superImagePath)
    {
        using var fs = File.OpenRead(superImagePath);
        var probe = new byte[SuperImageBuilder.GeometryBlockSize * 2];
        var probeRead = fs.Read(probe, 0, probe.Length);
        if (probeRead < probe.Length)
        {
            throw new InvalidDataException($"Super image is too small to contain LP geometry: {superImagePath} super 镜像过小，无法包含 LP geometry。");
        }

        var geometry = LpGeometry.TryParse(probe, 0) ?? LpGeometry.TryParse(probe, SuperImageBuilder.GeometryBlockSize)
            ?? throw new InvalidDataException($"No LP geometry found in super image: {superImagePath} super 镜像中未找到 LP geometry。");
        if (geometry.MetadataSlotCount == 0)
        {
            throw new InvalidDataException("Super image declares zero metadata slots. super 镜像声明的元数据槽位数为 0。");
        }

        var regionLength = checked((2L * SuperImageBuilder.GeometryBlockSize) + ((long)geometry.MetadataMaxSize * geometry.MetadataSlotCount));
        if (regionLength > fs.Length)
        {
            throw new InvalidDataException($"Super image is truncated: expected a {regionLength}-byte metadata region but the file is {fs.Length} bytes. super 镜像被截断：期望 {regionLength} 字节元数据区域，但文件仅 {fs.Length} 字节。");
        }

        var region = new byte[regionLength];
        fs.Position = 0;
        ReadExactly(fs, region, 0, (int)regionLength);
        return region;
    }

    /// <summary>
    /// Computes the first logical sector from the geometry when no block device table is present.
    /// <para>在没有块设备表时根据 geometry 计算首个逻辑扇区。</para>
    /// </summary>
    private static ulong ComputedFirstLogicalSector(SuperImageMetadata metadata)
    {
        var blockSize = metadata.Geometry.LogicalBlockSize == 0 ? 4096u : metadata.Geometry.LogicalBlockSize;
        var metadataBytes = (ulong)metadata.MetadataMaxSize * metadata.MetadataSlotCount;
        var firstLogicalByte = ((2UL * SuperImageBuilder.GeometryBlockSize) + metadataBytes + blockSize - 1) / blockSize * blockSize;
        return firstLogicalByte / (ulong)SuperImageBuilder.LpSectorSize;
    }

    /// <summary>
    /// Copies one logical partition's extent data out of the super image into <paramref name="output"/>.
    /// <para>将单个逻辑分区的 extent 数据从 super 镜像复制到 <paramref name="output"/>。</para>
    /// </summary>
    private static void WriteLogicalPartition(
        FileStream image,
        SuperImageMetadata metadata,
        SuperLogicalPartition partition,
        ulong firstLogicalSector,
        Stream output)
    {
        for (var e = 0; e < partition.NumExtents; e++)
        {
            var extentIndex = checked(partition.FirstExtentIndex + e);
            if (extentIndex >= metadata.Extents.Count)
            {
                break;
            }

            var extent = metadata.Extents[(int)extentIndex];
            var length = checked((long)extent.NumSectors * SuperImageBuilder.LpSectorSize);
            if (extent.TargetType == SuperImageBuilder.ZeroTargetType)
            {
                WriteZeros(output, length);
            }
            else
            {
                var start = checked((long)(firstLogicalSector + extent.TargetData) * SuperImageBuilder.LpSectorSize);
                CopyRange(image, output, start, length);
            }
        }
    }

    /// <summary>Writes <paramref name="count"/> zero bytes to the output stream.
    /// 向输出流写入 <paramref name="count"/> 个零字节。</summary>
    private static void WriteZeros(Stream output, long count)
    {
        var zeros = new byte[CopyBufferSize];
        var remaining = count;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(CopyBufferSize, remaining);
            output.Write(zeros, 0, chunk);
            remaining -= chunk;
        }
    }

    /// <summary>Copies a bounded byte range from the source image to the output stream.
    /// 从源镜像复制有界字节区间到输出流。</summary>
    private static void CopyRange(FileStream source, Stream output, long offset, long count)
    {
        var buffer = new byte[CopyBufferSize];
        source.Position = offset;
        var remaining = count;
        while (remaining > 0)
        {
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

    /// <summary>Reads exactly <paramref name="count"/> bytes from the stream.
    /// 从流中精确读取 <paramref name="count"/> 字节。</summary>
    private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, offset + read, count - read);
            if (n <= 0)
            {
                throw new InvalidDataException($"Super image ended early (expected {count} bytes at offset {offset + read}). super 镜像提前结束（偏移 {offset + read} 处期望 {count} 字节）。");
            }

            read += n;
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
            // Best-effort cleanup; the OS temp directory will reclaim it eventually.
            // 尽力清理；系统临时目录最终会回收。
        }
    }

    /// <summary>Bounded copy buffer size in bytes. 有界复制缓冲区大小（字节）。</summary>
    private const int CopyBufferSize = 1024 * 1024;
}