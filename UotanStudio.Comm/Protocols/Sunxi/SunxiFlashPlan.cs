using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;

namespace UotanStudio.Comm.Protocols.Sunxi;

/// <summary>
/// Kind of one download step of a Sunxi flash plan.
/// <para>全志烧录计划中一个下载步骤的种类。</para>
/// </summary>
public enum SunxiFlashStepKind
{
    /// <summary>The Sunxi MBR partition table, downloaded under the MBR tag at sector zero.
    /// <para>Sunxi MBR 分区表，以 MBR 标签下载到扇区 0。</para></summary>
    Mbr,

    /// <summary>One partition payload resolved from the MBR and the vendor partition list, downloaded under the
    /// flash tag and sector addressing. <para>由 MBR 与厂商分区清单解析出的分区负载，以闪存标签与扇区寻址下载。</para></summary>
    Partition,

    /// <summary>The optional preboot image, downloaded under the preboot tag at sector zero.
    /// <para>可选的预启动镜像，以预启动标签下载到扇区 0。</para></summary>
    Preboot,

    /// <summary>The first-stage boot loader, downloaded under the boot0 tag at sector zero.
    /// <para>一级引导程序，以 boot0 标签下载到扇区 0。</para></summary>
    Boot0,

    /// <summary>The boot package, downloaded under the boot1 tag at sector zero.
    /// <para>启动包，以 boot1 标签下载到扇区 0。</para></summary>
    Boot1
}

/// <summary>
/// One download step of a Sunxi flash plan: its kind, display name, data tag, first sector and payload byte
/// count. <para>全志烧录计划的一个下载步骤：种类、显示名、数据标签、起始扇区与负载字节数。</para>
/// </summary>
/// <param name="Kind">Step kind. <para>步骤种类。</para></param>
/// <param name="Name">Display name, which is the partition name for partition steps.
/// <para>显示名；分区步骤即分区名。</para></param>
/// <param name="Tag">Data tag the download uses. <para>下载使用的数据标签。</para></param>
/// <param name="FirstSector">First 512-byte sector of the transfer; partition transfers count sectors,
/// everything else transfers at zero. <para>本次传输的首个 512 字节扇区；分区传输按扇区计数，其余在零处传输。</para></param>
/// <param name="ByteCount">Payload byte count the step transfers. <para>该步骤传输的负载字节数。</para></param>
/// <param name="EntryIndex">Position of the source record inside the package container.
/// <para>源记录在包容器内的位置。</para></param>
/// <param name="BootRole">Boot variant the step fills, or <see langword="null"/> for table and partition
/// steps. <para>该步骤承担的引导变体；表与分区步骤为 <see langword="null"/>。</para></param>
public sealed record SunxiFlashStep(SunxiFlashStepKind Kind, string Name, SunxiFesDataTag Tag, uint FirstSector,
    long ByteCount, int EntryIndex, SunxiBootImageRole? BootRole = null);

/// <summary>
/// Download plan of one Allwinner IMAGEWTY package for a FES flash run: the partition table, every partition
/// payload the table names, and every boot image variant the package carries, ordered like the reference flow
/// (table, partitions, preboot, boot package, boot0). The flasher picks the boot variants that match the
/// service's security and storage answers. The plan keeps the container open and must be disposed after the run.
/// <para>一次 FES 烧录的全志 IMAGEWTY 包下载计划：分区表、表内点名的每个分区负载，以及包内携带的每种引导
/// 镜像变体，顺序与参考流程一致（表、分区、预启动、启动包、boot0）。烧录器按服务的安全与存储应答选择引导
/// 变体。计划保持容器打开，运行后必须释放。</para>
/// </summary>
public sealed class SunxiFlashPlan : IDisposable
{
    /// <summary>Largest tagged entry the plan reads fully into memory; boot images stay far below it.
    /// <para>计划整读进内存的最大带标签条目；引导镜像远低于该值。</para></summary>
    private const long MaximumTaggedEntryBytes = 64 * 1024 * 1024;

    /// <summary>Largest MBR entry the parser accepts; the vendor table is 16 KiB.
    /// <para>解析器接受的 MBR 条目上限；厂商整表为 16 KiB。</para></summary>
    private const long MaximumMbrEntryBytes = 1024 * 1024;

    /// <summary>Largest vendor partition list the parser accepts. <para>解析器接受的厂商分区清单上限。</para></summary>
    private const long MaximumPartitionListBytes = 1024 * 1024;

    /// <summary>Chunk-count ceiling handed to the sparse reader. <para>传给稀疏读取器的块数量上限。</para></summary>
    private const int MaximumSparseChunks = 1024 * 1024;

    private readonly FileStream _container;
    private readonly ImageWtyHeader _header;
    private readonly Dictionary<int, SunxiPartitionSource> _sources;
    private bool _disposed;

    private SunxiFlashPlan(FileStream container, ImageWtyHeader header, IReadOnlyList<SunxiFlashStep> steps,
        IReadOnlyList<string> missingPartitions, Dictionary<int, SunxiPartitionSource> sources)
    {
        _container = container;
        _header = header;
        Steps = steps;
        MissingPartitions = missingPartitions;
        _sources = sources;
    }

    /// <summary>Path of the package container the plan reads from. <para>计划读取的包容器路径。</para></summary>
    public string PackagePath => _container.Name;

    /// <summary>Download steps in execution order, including every boot variant the package carries.
    /// <para>按执行顺序排列的下载步骤，含包内携带的每种引导变体。</para></summary>
    public IReadOnlyList<SunxiFlashStep> Steps { get; }

    /// <summary>Partition names the table references but the package does not carry; the flasher reports them
    /// as skipped instead of writing anything. <para>分区表点名但包内不存在的分区名；烧录器将其上报为跳过而不
    /// 写入任何字节。</para></summary>
    public IReadOnlyList<string> MissingPartitions { get; }

    /// <summary>
    /// Loads a package and builds its download plan, rejecting containers without a Sunxi MBR, tables whose
    /// payload exceeds its partition capacity, duplicate boot variants and payloads that cannot be decoded.
    /// <para>加载包并构建下载计划；拒绝缺少 Sunxi MBR 的容器、负载超出分区容量的表、重复的引导变体以及无法
    /// 解码的负载。</para>
    /// </summary>
    /// <param name="imagePath">IMAGEWTY container file. <para>IMAGEWTY 容器文件。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>The built plan holding the open container. <para>持有已打开容器的计划。</para></returns>
    /// <exception cref="FileNotFoundException">The image file does not exist. <para>镜像文件不存在。</para></exception>
    /// <exception cref="InvalidDataException">The container is not a valid IMAGEWTY image, carries no Sunxi MBR,
    /// carries duplicate boot variants, or names a payload the container cannot satisfy.
    /// <para>容器不是有效 IMAGEWTY 镜像、没有 Sunxi MBR、带有重复引导变体，或点名的负载在容器中无法满足。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public static SunxiFlashPlan Load(string imagePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"The Allwinner image '{imagePath}' does not exist.", imagePath);
        }

        var container = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sources = new Dictionary<int, SunxiPartitionSource>();
        try
        {
            var header = AllwinnerImageWtyReader.Read(container, cancellationToken);
            var (steps, missing) = BuildSteps(container, header, sources, cancellationToken);
            return new SunxiFlashPlan(container, header, steps, missing, sources);
        }
        catch
        {
            foreach (var source in sources.Values)
            {
                source.Dispose();
            }

            container.Dispose();
            throw;
        }
    }

    /// <summary>Closes the package container and every entry view the plan holds.
    /// <para>关闭包容器与计划持有的每个条目视图。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var source in _sources.Values)
        {
            source.Dispose();
        }

        _sources.Clear();
        _container.Dispose();
    }

    /// <summary>
    /// Reads one boot or table entry fully into memory.
    /// <para>把一个引导或表条目整读进内存。</para>
    /// </summary>
    internal byte[] ReadTaggedEntry(SunxiFlashStep step, CancellationToken cancellationToken)
    {
        var entry = _header.Entries[step.EntryIndex];
        var length = AllwinnerImageWtyReader.EffectiveLength(entry);
        if (length > MaximumTaggedEntryBytes)
        {
            throw new InvalidDataException(
                $"The '{step.Name}' entry is {length} bytes, above the {MaximumTaggedEntryBytes}-byte plan limit.");
        }

        using var output = new MemoryStream();
        AllwinnerImageWtyReader.ExtractEntry(_container, entry, output, _header.IsEncrypted, cancellationToken);
        return output.ToArray();
    }

    /// <summary>
    /// Extracts one segment of a partition payload into the destination buffer and returns the byte count
    /// written; sparse payloads are read through their expanded logical view.
    /// <para>把分区负载的一个段提取到目标缓冲区并返回写出字节数；稀疏负载经其展开后的逻辑视图读取。</para>
    /// </summary>
    internal async Task<int> ExtractSegmentAsync(SunxiFlashStep step, long offset, byte[] destination,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var count = (int)Math.Min(destination.Length, step.ByteCount - offset);
        if (!_sources.TryGetValue(step.EntryIndex, out var source))
        {
            throw new InvalidOperationException($"The step '{step.Name}' has no partition payload source.");
        }

        var filled = 0;
        while (filled < count)
        {
            var read = await source.ReadAsync(offset + filled, destination.AsMemory(filled, count - filled),
                cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new EndOfStreamException($"The payload of '{step.Name}' ended before its declared length.");
            }

            filled += read;
        }

        return count;
    }

    private static (IReadOnlyList<SunxiFlashStep> Steps, IReadOnlyList<string> Missing) BuildSteps(
        FileStream container, ImageWtyHeader header, Dictionary<int, SunxiPartitionSource> sources,
        CancellationToken cancellationToken)
    {
        var steps = new List<SunxiFlashStep>();
        var missing = new List<string>();
        var mbrEntry = header.Entries.Select((entry, index) => (entry, index))
            .FirstOrDefault(pair => pair.entry.Item == AllwinnerImageItem.Mbr);
        if (mbrEntry.entry is null)
        {
            throw new InvalidDataException("The package carries no sunxi_mbr.fex entry.");
        }

        var mbrLength = AllwinnerImageWtyReader.EffectiveLength(mbrEntry.entry);
        if (mbrLength > MaximumMbrEntryBytes)
        {
            throw new InvalidDataException($"The sunxi_mbr.fex entry is {mbrLength} bytes, above the 1 MiB limit.");
        }

        steps.Add(new SunxiFlashStep(SunxiFlashStepKind.Mbr, "sunxi_mbr.fex", SunxiFesDataTag.Mbr, 0, mbrLength,
            mbrEntry.index));

        using var mbrStream = new MemoryStream();
        AllwinnerImageWtyReader.ExtractEntry(container, mbrEntry.entry, mbrStream, header.IsEncrypted,
            cancellationToken);
        var table = SunxiMbrTable.Parse(mbrStream.ToArray());
        var partitionFiles = ReadPartitionFiles(container, header, cancellationToken);
        foreach (var partition in table.Partitions)
        {
            partitionFiles.TryGetValue(partition.Name, out var definition);
            var entryIndex = FindPartitionEntry(header, partition.Name, definition?.DownloadFile);
            if (entryIndex < 0)
            {
                missing.Add(partition.Name);
                continue;
            }

            if (partition.FirstSector > uint.MaxValue)
            {
                throw new InvalidDataException(
                    $"Partition '{partition.Name}' starts at sector {partition.FirstSector}, beyond the "
                    + $"32-bit FES address field.");
            }

            var capacity = checked((long)partition.SectorCount * SunxiEfexConstants.SectorSize);
            var source = SunxiPartitionSource.Create(container, header.Entries[entryIndex], header.IsEncrypted,
                capacity, cancellationToken);
            if (sources.ContainsKey(entryIndex))
            {
                source.Dispose();
                throw new InvalidDataException(
                    $"More than one partition names the package entry '{header.Entries[entryIndex].Name}'.");
            }

            if (source.Length > capacity)
            {
                source.Dispose();
                throw new InvalidDataException(
                    $"The payload for partition '{partition.Name}' is {source.Length} bytes, above its "
                    + $"{capacity}-byte capacity.");
            }

            sources[entryIndex] = source;
            steps.Add(new SunxiFlashStep(SunxiFlashStepKind.Partition, partition.Name, SunxiFesDataTag.Flash,
                (uint)partition.FirstSector, source.Length, entryIndex));
        }

        AppendBootSteps(steps, header, SunxiFlashStepKind.Preboot,
            [AllwinnerImageItem.Preboot, AllwinnerImageItem.Toc0Preboot]);
        AppendBootSteps(steps, header, SunxiFlashStepKind.Boot1,
            [AllwinnerImageItem.UBoot, AllwinnerImageItem.Toc1, AllwinnerImageItem.BootPackage,
                AllwinnerImageItem.BootPackageNor]);
        AppendBootSteps(steps, header, SunxiFlashStepKind.Boot0,
            [AllwinnerImageItem.Boot0Nand, AllwinnerImageItem.Boot0Card, AllwinnerImageItem.Boot0Nor,
                AllwinnerImageItem.Boot0Ufs, AllwinnerImageItem.Toc0, AllwinnerImageItem.Toc0SdCard,
                AllwinnerImageItem.Toc0Nand, AllwinnerImageItem.Toc0Nor, AllwinnerImageItem.Toc0Ufs]);
        return (steps, missing);
    }

    private static void AppendBootSteps(List<SunxiFlashStep> steps, ImageWtyHeader header,
        SunxiFlashStepKind kind, AllwinnerImageItem[] items)
    {
        var seenRoles = new HashSet<SunxiBootImageRole>();
        for (var index = 0; index < header.Entries.Count; index++)
        {
            var entry = header.Entries[index];
            if (!items.Contains(entry.Item) || !TryMapBootRole(entry.Item, out var role))
            {
                continue;
            }

            if (!seenRoles.Add(role))
            {
                throw new InvalidDataException(
                    $"The package carries more than one image for the boot role {role} ({entry.Name}).");
            }

            steps.Add(new SunxiFlashStep(kind, entry.Name,
                kind == SunxiFlashStepKind.Preboot ? SunxiFesDataTag.Preboot
                    : kind == SunxiFlashStepKind.Boot1 ? SunxiFesDataTag.Boot1
                    : SunxiFesDataTag.Boot0,
                0, AllwinnerImageWtyReader.EffectiveLength(entry), index, role));
        }
    }

    private static bool TryMapBootRole(AllwinnerImageItem item, out SunxiBootImageRole role)
    {
        SunxiBootImageRole? mapped = item switch
        {
            AllwinnerImageItem.Preboot => SunxiBootImageRole.PrebootNormal,
            AllwinnerImageItem.Toc0Preboot => SunxiBootImageRole.PrebootToc,
            AllwinnerImageItem.Boot0Nand => SunxiBootImageRole.Boot0NormalNand,
            AllwinnerImageItem.Boot0Card => SunxiBootImageRole.Boot0NormalCard,
            AllwinnerImageItem.Boot0Nor => SunxiBootImageRole.Boot0NormalNor,
            AllwinnerImageItem.Boot0Ufs => SunxiBootImageRole.Boot0NormalUfs,
            AllwinnerImageItem.Toc0 => SunxiBootImageRole.Boot0TocDefault,
            AllwinnerImageItem.Toc0SdCard => SunxiBootImageRole.Boot0TocSd,
            AllwinnerImageItem.Toc0Nand => SunxiBootImageRole.Boot0TocNand,
            AllwinnerImageItem.Toc0Nor => SunxiBootImageRole.Boot0TocNor,
            AllwinnerImageItem.Toc0Ufs => SunxiBootImageRole.Boot0TocUfs,
            AllwinnerImageItem.UBoot => SunxiBootImageRole.Boot1Normal,
            AllwinnerImageItem.Toc1 => SunxiBootImageRole.Boot1Toc,
            AllwinnerImageItem.BootPackage => SunxiBootImageRole.Boot1Package,
            AllwinnerImageItem.BootPackageNor => SunxiBootImageRole.Boot1PackageNor,
            _ => null
        };
        role = mapped ?? default;
        return mapped.HasValue;
    }

    private static Dictionary<string, SunxiPartitionDefinition> ReadPartitionFiles(FileStream container,
        ImageWtyHeader header, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, SunxiPartitionDefinition>(StringComparer.OrdinalIgnoreCase);
        var entry = header.Entries.FirstOrDefault(candidate => candidate.Item == AllwinnerImageItem.SysPartition);
        if (entry is null)
        {
            return files;
        }

        var length = AllwinnerImageWtyReader.EffectiveLength(entry);
        if (length > MaximumPartitionListBytes)
        {
            throw new InvalidDataException(
                $"The sys_partition.fex entry is {length} bytes, above the {MaximumPartitionListBytes}-byte limit.");
        }

        using var output = new MemoryStream();
        AllwinnerImageWtyReader.ExtractEntry(container, entry, output, header.IsEncrypted, cancellationToken);
        foreach (var definition in SunxiSysPartitionTable.Parse(output.ToArray()))
        {
            files[definition.Name] = definition;
        }

        return files;
    }

    private static int FindPartitionEntry(ImageWtyHeader header, string partitionName, string? downloadFile)
    {
        if (!string.IsNullOrEmpty(downloadFile))
        {
            for (var index = 0; index < header.Entries.Count; index++)
            {
                if (header.Entries[index].Name.Equals(downloadFile, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
        }

        return FindPartitionEntry(header, partitionName);
    }

    private static int FindPartitionEntry(ImageWtyHeader header, string partitionName)
    {
        for (var index = 0; index < header.Entries.Count; index++)
        {
            var name = header.Entries[index].Name;
            if (name.Equals(partitionName, StringComparison.OrdinalIgnoreCase)
                || name.Equals(partitionName + ".fex", StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}

/// <summary>
/// One partition payload view: a raw entry stream or an expanded Android sparse view over it.
/// <para>一个分区负载视图：原始条目流，或以其为基础的 Android 稀疏展开视图。</para>
/// </summary>
internal sealed class SunxiPartitionSource : IDisposable
{
    private readonly Stream _entry;
    private readonly Stream _logical;
    private bool _disposed;

    private SunxiPartitionSource(Stream entry, Stream logical, long length)
    {
        _entry = entry;
        _logical = logical;
        Length = length;
    }

    /// <summary>Logical payload length in bytes, after sparse expansion.
    /// <para>稀疏展开后的逻辑负载字节长度。</para>
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// Opens one entry and detects whether it stores an Android sparse image; a sparse entry is wrapped in an
    /// expanded logical view bounded by the target partition capacity.
    /// <para>打开一个条目并检测其中是否存储 Android 稀疏镜像；稀疏条目会包装为以目标分区容量为上界的展开
    /// 逻辑视图。</para>
    /// </summary>
    /// <param name="container">Container stream shared by every view. <para>所有视图共享的容器流。</para></param>
    /// <param name="entry">Container entry. <para>容器条目。</para></param>
    /// <param name="decrypt">Whether payloads are RC6-encrypted. <para>负载是否经 RC6 加密。</para></param>
    /// <param name="maximumLength">Largest logical length the view may expose.
    /// <para>视图允许公开的最大逻辑长度。</para></param>
    /// <param name="cancellationToken">Cancels sparse parsing. <para>取消稀疏解析。</para></param>
    /// <returns>The opened source. <para>已打开的来源。</para></returns>
    /// <exception cref="InvalidDataException">The sparse image is malformed or exceeds
    /// <paramref name="maximumLength"/>. <para>稀疏镜像结构无效或超过 <paramref name="maximumLength"/>。</para></exception>
    public static SunxiPartitionSource Create(Stream container, ImageWtyEntry entry, bool decrypt,
        long maximumLength, CancellationToken cancellationToken)
    {
        var entryStream = AllwinnerImageWtyReader.OpenEntryStream(container, entry, decrypt);
        try
        {
            Span<byte> magic = stackalloc byte[sizeof(uint)];
            var isSparse = entryStream.Length >= magic.Length;
            if (isSparse)
            {
                entryStream.ReadExactly(magic);
                entryStream.Position = 0;
                isSparse = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(magic)
                    == SparseFormat.SparseHeaderMagic;
            }

            if (!isSparse)
            {
                return new SunxiPartitionSource(entryStream, entryStream, entryStream.Length);
            }

            var sparse = SparseReader.FromStream(entryStream, validateCrc: false, verbose: false,
                maximumChunks: 1024 * 1024, cancellationToken: cancellationToken,
                maximumExpandedLength: maximumLength);
            var logical = new SparseReadStream(sparse);
            return new SunxiPartitionSource(entryStream, logical, logical.Length);
        }
        catch
        {
            entryStream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads expanded logical bytes at an absolute offset.
    /// <para>在绝对偏移处读取展开后的逻辑字节。</para>
    /// </summary>
    /// <param name="offset">Absolute logical offset. <para>绝对逻辑偏移。</para></param>
    /// <param name="buffer">Destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>Bytes read, or zero at the end. <para>读取的字节数；末尾时为 0。</para></returns>
    public Task<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _logical.Position = offset;
        return _logical.ReadAsync(buffer, cancellationToken).AsTask();
    }

    /// <summary>Releases the logical view and the underlying entry stream.
    /// <para>释放逻辑视图与底层条目流。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!ReferenceEquals(_logical, _entry))
        {
            _logical.Dispose();
        }

        _entry.Dispose();
    }
}