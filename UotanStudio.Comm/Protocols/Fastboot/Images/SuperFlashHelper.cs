using UotanStudio.Firmware.BlockImage.Lp;

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

    /// <summary>Flashes a prebuilt super image (LP metadata + logical partition data) to the device.
    /// <para>将预构建的 super 镜像（LP 元数据 + 逻辑分区数据）刷写到设备。</para></summary>
    /// <remarks>A compact metadata image (one geometry block followed by one metadata table) is applied first through <c>update-super</c>, which recreates the logical partitions on the device; each logical partition's data is then streamed with <c>download</c> + <c>flash</c>, reading linear extents directly out of the image (zero extents are zero-filled). This mirrors the AOSP fastboot <c>flash super</c> flow.
    /// <para>先通过 <c>update-super</c> 应用精简元数据镜像（一块 geometry 后接一份元数据表）， 由设备重建逻辑分区；随后将每个逻辑分区的数据通过 <c>download</c> + <c>flash</c> 流式刷写，线性 extent 直接从镜像中读取（零 extent 补零）。此流程对应 AOSP fastboot 的 <c>flash super</c> 语义。</para><para> The stream handed to <c>update-super</c> is deliberately the compact <c>SerializeGeometry ‖ SerializeMetadata</c> pair — a single 4096-byte geometry slot at offset 0 immediately followed by the metadata header and tables, with no reserved prefix and no backup geometry. That is the shape three independent producers in this repository agree on: the host's standalone <c>super_empty</c> apply path, <c>UotanStudio.Delivery</c>'s generated <c>super_empty.img</c>, and the third fallback branch of <c>MetadataReader.ReadFromImageStream</c>, which exists precisely to parse that layout. It is <em>not</em> the shape <c>MetadataWriter.WriteToImageStream</c> produces: that one writes a complete raw super image (4096 reserved zero bytes at offset 0, the primary geometry at 4096, its backup at 8192, then each primary metadata slot and its backup near the end of the device), because it has to place data inside a full-sized partition image rather than describe one. Substituting the full-image layout here would disagree with the other two producers and with the reader fallback that accepts them. </para><para> 交给 <c>update-super</c> 的流刻意采用精简形态 <c>SerializeGeometry ‖ SerializeMetadata</c>—— 偏移 0 处是单个 4096 字节的 geometry 槽，紧随其后是元数据头与表，既无保留前缀也无备份 geometry。 仓库内三个独立生产者对这一形态达成一致：宿主的独立 <c>super_empty</c> 应用路径、 <c>UotanStudio.Delivery</c> 生成的 <c>super_empty.img</c>，以及 <c>MetadataReader.ReadFromImageStream</c> 的第三个回退分支（它正是为解析该布局而存在）。 这与 <c>MetadataWriter.WriteToImageStream</c> 的产物<em>不同</em>：后者写出的是完整 super 裸镜像 （偏移 0 处 4096 字节保留零、4096 处主 geometry、8192 处备份 geometry，随后是各主元数据槽及其 位于设备末尾附近的备份），因为它必须把数据放进一个全尺寸分区镜像，而不是描述一个镜像。 在此处改用完整镜像布局会与另外两个生产者以及接受它们的读取回退分支相矛盾。 </para><para> Every check that can be answered without mutating the device runs before <c>update-super</c>: extent bounds against the image, the device's super capacity against the image and its block device, and each logical partition's expanded size against <c>max-download-size</c>. Committing the metadata first and discovering an unflashable partition afterwards would leave the device with declared-but-empty partitions, which does not boot, and the protocol offers no rollback. </para><para>所有无需改动设备即可回答的检查都在 <c>update-super</c> 之前完成：extent 相对镜像的边界、 设备 super 容量相对镜像及其块设备、以及每个逻辑分区的展开尺寸相对 <c>max-download-size</c>。 若先提交元数据、之后才发现某个分区无法刷写，设备就会留下“已声明但为空”的分区而无法启动， 且协议不提供回滚。</para></remarks>
    /// <param name="superImagePath">The prebuilt super image file path. 预构建的 super 镜像文件路径。</param>
    /// <param name="wipe">Whether to wipe the super partition before updating its metadata. 更新元数据前是否擦除
    /// super 分区。</param>
    /// <param name="progress">Optional progress reporter for download/upload byte counts.
    /// 可选的进度报告器（下载/上传字节数）。</param>
    /// <param name="cancellationToken">The cancellation token. 取消令牌。</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="superImagePath"/>is null or
    /// empty. <paramref name="superImagePath"/>为 null 或空时抛出。</exception>
    /// <exception cref="FileNotFoundException">Thrown when the super image file does not exist. super
    /// 镜像文件不存在时抛出。</exception>
    /// <exception cref="InvalidDataException">Thrown when the file is not a readable super image, when
    /// its extents fall outside the image, or when its declared super capacity disagrees with the
    /// device. 文件不是可读的 super 镜像、其 extent 落在镜像之外，或其声明的 super 容量与设备不一致时抛出。</exception>
    /// <exception cref="FastbootException">Thrown when a fastboot command fails, or when a logical
    /// partition is larger than the device's max-download-size and could never be streamed. fastboot
    /// 命令失败，或某逻辑分区大于设备 max-download-size 因而永远无法流式传输时抛出。</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/>is
    /// cancelled. <paramref name="cancellationToken"/>被取消时抛出。</exception>
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

        ValidateAgainstDevice(image.Length, metadata, cancellationToken);

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
                    _session.FlashPartition(partition.Name, output, output.Length, progress, cancellationToken,
                        resolveSlot: false, options: FlashPartitionOptions.NoPreflight).ThrowIfFailed();
                }
            }
            finally
            {
                TryDeleteTempFile(tempPath);
            }
        }
    }

    /// <summary>
    /// Runs the device-side capacity and transfer-limit checks before <c>update-super</c> commits any
    /// metadata, so a super image the device can never accept is rejected while the device is still
    /// untouched.
    /// <para>在 <c>update-super</c> 提交任何元数据之前执行设备侧的容量与传输上限检查，使设备永远无法接受
    /// 的 super 镜像在设备仍未被触碰时就被拒绝。</para>
    /// </summary>
    /// <param name="imageLength">Length of the raw super image on disk. 磁盘上 super 裸镜像的长度。</param>
    /// <param name="metadata">Metadata parsed from that image. 从该镜像解析出的元数据。</param>
    /// <param name="cancellationToken">Checked before each device query. 每次设备查询前检查。</param>
    /// <exception cref="InvalidDataException">The device reports no super size, or its super capacity disagrees with the image.
    /// 设备未上报 super 大小，或其 super 容量与镜像不一致。</exception>
    /// <exception cref="FastbootException">A logical partition's expanded size exceeds max-download-size.
    /// 某逻辑分区的展开尺寸超过 max-download-size。</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <paramref name="cancellationToken"/> 被取消。</exception>
    private void ValidateAgainstDevice(long imageLength, SuperImageMetadata metadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deviceSuperSize = GetSuperSize();
        var declaredSize = (long)metadata.BlockDevices[0].Size;
        if (deviceSuperSize <= 0)
        {
            throw new InvalidDataException(
                $"The device reports no size for '{MainPartition}'; the super image cannot be validated before flashing. 设备未上报 '{MainPartition}' 的大小，刷写前无法校验 super 镜像。");
        }

        if (deviceSuperSize != declaredSize)
        {
            throw new InvalidDataException(
                $"The device '{MainPartition}' is {deviceSuperSize} bytes but the image declares {declaredSize} bytes. 设备 '{MainPartition}' 为 {deviceSuperSize} 字节，而镜像声明 {declaredSize} 字节。");
        }

        if (imageLength > deviceSuperSize)
        {
            throw new InvalidDataException(
                $"The super image is {imageLength} bytes, which does not fit the {deviceSuperSize}-byte '{MainPartition}' partition. super 镜像为 {imageLength} 字节，放不进 {deviceSuperSize} 字节的 '{MainPartition}' 分区。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var maxDownloadSize = _session.GetMaxDownloadSize();
        foreach (var partition in metadata.Partitions)
        {
            if (partition.SizeInBytes <= 0)
            {
                continue;
            }

            if (partition.SizeInBytes > maxDownloadSize)
            {
                throw new FastbootException(FastbootResponseState.Fail,
                    $"logical partition '{partition.Name}' expands to {partition.SizeInBytes} bytes, which exceeds max-download-size {maxDownloadSize}; it cannot be streamed after update-super commits the metadata");
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

    /// <summary>
    /// Best-effort deletion of a temp partition file.
    /// <para>尽力删除临时分区文件。</para>
    /// </summary>
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

    /// <summary>
    /// Bounded copy buffer size in bytes.
    /// <para>有界复制缓冲区大小（字节）。</para>
    /// </summary>
    private const int CopyBufferSize = 1024 * 1024;
}