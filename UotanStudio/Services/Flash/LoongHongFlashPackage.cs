using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using UotanStudio.Compress.Zip;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.Fat;

namespace UotanStudio.Services.Flash;

/// <summary>
/// One partition declared by a LoongHong flash package's <c>partition_config.json</c>.
/// <para>LoongHong 烧录包的 <c>partition_config.json</c> 声明的一个分区。</para>
/// </summary>
/// <param name="Name">Partition name, which is also the device's <c>by-name</c> link.
/// <para>分区名，同时也是设备上的 <c>by-name</c> 链接名。</para></param>
/// <param name="Label">File-system volume label written by the vendor's <c>mkfs</c> call.
/// <para>厂商 <c>mkfs</c> 写入的文件系统卷标。</para></param>
/// <param name="SizeBytes">Declared extent in bytes; <c>0</c> means "the rest of the disk".
/// <para>声明的分区字节长度；<c>0</c> 表示“占用磁盘剩余空间”。</para></param>
/// <param name="TypeCode">GPT type code from the vendor table, such as <c>EF00</c> or <c>8300</c>.
/// <para>厂商表中的 GPT 类型码，例如 <c>EF00</c> 或 <c>8300</c>。</para></param>
/// <param name="FileSystem">File system the vendor script formats: <c>vfat</c>, <c>ext4</c> or <c>raw</c>.
/// <para>厂商脚本执行格式化的文件系统：<c>vfat</c>、<c>ext4</c> 或 <c>raw</c>。</para></param>
public sealed record LoongHongPartition(
    string Name,
    string Label,
    long SizeBytes,
    string TypeCode,
    string FileSystem);

/// <summary>
/// How a LoongHong package is turned into one flashable disk image.
/// <para>LoongHong 包如何组装为一张可烧录的磁盘镜像。</para>
/// </summary>
public sealed record LoongHongSynthesisOptions
{
    /// <summary>
    /// Gets whether <c>userdata.img</c> is written and the userdata partition is extended to the rest of the
    /// target disk, mirroring the vendor script's <c>-u</c> switch.
    /// <para>获取是否写入 <c>userdata.img</c> 并把 userdata 分区扩展到目标磁盘的剩余空间，对应厂商脚本的
    /// <c>-u</c> 开关。</para>
    /// </summary>
    /// <value>
    /// Defaults to <see langword="false"/>: the image keeps the vendor's content sizes and writes nothing the
    /// vendor script would leave untouched.
    /// <para>默认 <see langword="false"/>：镜像保持厂商的内容大小，不写厂商脚本本该保持原样的内容。</para>
    /// </value>
    public bool IncludeUserData { get; init; }

    /// <summary>
    /// Gets the capacity the image must fill, in bytes, or <see langword="null"/> to size the image from its
    /// content. A capacity is required when <see cref="IncludeUserData"/> extends the userdata partition.
    /// <para>获取镜像必须占满的容量（字节）；为 <see langword="null"/> 时按内容大小生成。当
    /// <see cref="IncludeUserData"/> 需要把 userdata 扩展到整盘剩余空间时，必须给出容量。</para>
    /// </summary>
    public long? CapacityBytes { get; init; }
}

/// <summary>
/// Progress event of a LoongHong image synthesis run.
/// <para>LoongHong 镜像合成运行的进度事件。</para>
/// </summary>
/// <param name="Phase">Stage identifier: <c>layout</c>, <c>write-partition</c> or <c>done</c>.
/// <para>阶段标识：<c>layout</c>、<c>write-partition</c> 或 <c>done</c>。</para>
/// <param name="Partition">Partition the event belongs to; <see langword="null"/> for run-level events.
/// <para>事件所属分区；运行级事件为 <see langword="null"/>。</para></param>
/// <param name="BytesCompleted">Bytes processed so far, when byte accounting applies.
/// <para>目前已处理的字节数；无字节计量时为 <see langword="null"/>。</para></param>
/// <param name="BytesTotal">Total bytes the stage must process, when known in advance.
/// <para>该阶段待处理的字节总数；无法预知时为 <see langword="null"/>。</para></param>
public sealed record LoongHongProgress(string Phase, string? Partition, long? BytesCompleted, long? BytesTotal);

/// <summary>
/// Outcome of a LoongHong synthesis run.
/// <para>LoongHong 合成运行的结果。</para>
/// </summary>
/// <param name="ImageSizeBytes">Total image length in bytes. <para>镜像总字节长度。</para></param>
/// <param name="PartitionCount">GPT entries published in the image. <para>镜像中已发布的分区条目数。</para></param>
/// <param name="IncludedUserData">Whether <c>userdata.img</c> was written. <para>是否已写入 <c>userdata.img</c>。</para></param>
public sealed record LoongHongSynthesisResult(long ImageSizeBytes, int PartitionCount, bool IncludedUserData);

/// <summary>
/// Recognizes a LoongHong (Loongson 3A6000 tablet) flash-tool ZIP and assembles its per-partition images into one
/// flashable GPT disk image. The package carries a vendor <c>partition_config.json</c> plus an <c>images/</c>
/// directory; synthesis reproduces the vendor script's <c>flash_loongson_tablet.sh</c>: partitions are laid out in
/// table order from the 1 MiB boundary, the EFI partition keeps the original <c>efi.img</c> volume and gains the
/// <c>ramdisk.img</c>/<c>vmlinux.efi</c>/<c>updater.img</c> files grub loads from its root, and partitions without an
/// image receive a freshly formatted empty volume instead of being left unreadable.
/// <para>识别 LoongHong（龙芯 3A6000 平板）烧录工具 ZIP，并把其中的逐分区镜像组装成一张可烧录的 GPT 磁盘镜像。
/// 该包内含厂商 <c>partition_config.json</c> 与 <c>images/</c> 目录；合成过程复刻厂商脚本
/// <c>flash_loongson_tablet.sh</c>：分区按表顺序从 1 MiB 边界起排列；EFI 分区保留原始 <c>efi.img</c> 卷内容并
/// 补入 grub 从卷根加载的 <c>ramdisk.img</c>、<c>vmlinux.efi</c> 与 <c>updater.img</c>；没有对应镜像的分区会得到
/// 一个新格式化的空卷，而不是无法识别的裸数据。</para>
/// </summary>
public sealed class LoongHongFlashPackage
{
    private const int MaxEntryNameLength = 512;
    private const long SectorSize = 512;
    private const long FirstPartitionLba = 2048;
    private const long ImageAlignBytes = 4096;
    private const string ConfigFileName = "partition_config.json";
    private const string ImagesDirectoryName = "images";
    private const string EfiPartitionName = "efi";
    private const string UserDataPartitionName = "userdata";
    private const long MinimumUserDataBytes = 1L << 30;

    /// <summary>Files the vendor script copies into the EFI volume root after writing <c>efi.img</c>; grub's
    /// generated <c>grub.cfg</c> loads <c>/vmlinux.efi</c> and <c>/ramdisk.img</c> from there.
    /// <para>厂商脚本在写入 <c>efi.img</c> 后复制到 EFI 卷根目录的文件；grub 生成的 <c>grub.cfg</c> 会从那里加载
    /// <c>/vmlinux.efi</c> 与 <c>/ramdisk.img</c>。</para></summary>
    private static readonly string[] RequiredEfiBootFiles = ["ramdisk.img", "vmlinux.efi"];

    /// <summary>Optional EFI volume payload the vendor script also copies when present.
    /// <para>厂商脚本在存在时同样复制的可选 EFI 卷负载。</para></summary>
    private static readonly string[] OptionalEfiBootFiles = ["updater.img"];

    private static readonly ZipContainerOptions PackageBounds = new(
        MaxEntries: 4096,
        MaxEntryBytes: 512L << 30,
        MaxTotalBytes: 16L << 40);

    /// <summary>Upper bound for the assembled image, catching an absurd table before it asks the file system for
    /// an exabyte-scale file. <para>组装镜像的上限，在向文件系统申请 EB 级文件之前拦截荒谬的分区表。</para>
    /// </summary>
    private const long MaximumImageBytes = 16L << 40;

    private LoongHongFlashPackage(string packagePath, string configEntryName,
        IReadOnlyList<LoongHongPartition> partitions, IReadOnlyDictionary<string, string> images,
        IReadOnlyDictionary<string, string> efiBootFiles, long estimatedImageSizeBytes)
    {
        PackagePath = packagePath;
        ConfigEntryName = configEntryName;
        Partitions = partitions;
        ImageEntries = images;
        EfiBootEntries = efiBootFiles;
        EstimatedImageSizeBytes = estimatedImageSizeBytes;
    }

    /// <summary>
    /// Gets the absolute path of the ZIP package.
    /// <para>获取 ZIP 包的绝对路径。</para>
    /// </summary>
    public string PackagePath { get; }

    /// <summary>
    /// Gets the archive entry name of the vendor partition table.
    /// <para>获取厂商分区表在归档内的条目名。</para>
    /// </summary>
    public string ConfigEntryName { get; }

    /// <summary>
    /// Gets the declared partitions in table order.
    /// <para>获取按表中顺序声明的分区。</para>
    /// </summary>
    public IReadOnlyList<LoongHongPartition> Partitions { get; }

    /// <summary>
    /// Gets the archive entry name of each partition image, keyed by partition name.
    /// <para>获取每个分区镜像在归档内的条目名，以分区名为键。</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> ImageEntries { get; }

    /// <summary>
    /// Gets the archive entry name of each EFI boot file, keyed by file name.
    /// <para>获取每个 EFI 启动文件在归档内的条目名，以文件名为键。</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> EfiBootEntries { get; }

    /// <summary>
    /// Gets the size, in bytes, of the image assembled without extending userdata; used for the selection summary.
    /// <para>获取不扩展 userdata 时组装出的镜像字节大小，用于选择阶段的摘要显示。</para>
    /// </summary>
    public long EstimatedImageSizeBytes { get; }

    /// <summary>
    /// Gets whether the package carries <c>userdata.img</c>.
    /// <para>获取包内是否带有 <c>userdata.img</c>。</para>
    /// </summary>
    public bool HasUserDataImage => ImageEntries.ContainsKey(UserDataPartitionName);

    /// <summary>
    /// Recognizes a LoongHong flash-tool ZIP at <paramref name="packagePath"/>.
    /// <para>识别 <paramref name="packagePath"/> 处的 LoongHong 烧录工具 ZIP。</para>
    /// </summary>
    /// <param name="packagePath">Path to the ZIP archive. <para>ZIP 归档路径。</para></param>
    /// <param name="cancellationToken">Cancels the recognition. <para>取消识别。</para></param>
    /// <returns>The recognized package, or <see langword="null"/> when the file is not a readable ZIP or carries no
    /// usable LoongHong partition table. <para>识别出的包；文件不是可读 ZIP 或不含可用 LoongHong 分区表时为
    /// <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidDataException">A table is present but its partitions, sizes or image references are
    /// inconsistent or unsafe. <para>存在分区表，但其分区、大小或镜像引用不一致或不安全。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public static LoongHongFlashPackage? TryOpen(string packagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var fullPath = Path.GetFullPath(packagePath);
        ZipArchive archive;
        try
        {
            archive = ZipContainer.OpenRead(fullPath, PackageBounds);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        using (archive)
        {
            var entries = BuildEntryIndex(archive, cancellationToken);
            var config = FindConfigEntry(entries);
            if (config is null)
            {
                return null;
            }

            var partitions = ParseConfig(archive, config, cancellationToken);
            var imagesPrefix = DirectoryOf(config) + ImagesDirectoryName + "/";
            var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var partition in partitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.ResolveOptional(imagesPrefix + partition.Name + ".img") is { } entry)
                {
                    images[partition.Name] = entry;
                }
            }

            var bootFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in RequiredEfiBootFiles.Concat(OptionalEfiBootFiles))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.ResolveOptional(imagesPrefix + name) is { } entry)
                {
                    bootFiles[name] = entry;
                }
            }

            foreach (var name in RequiredEfiBootFiles)
            {
                if (!bootFiles.ContainsKey(name))
                {
                    throw new InvalidDataException(
                        $"The LoongHong package is missing the EFI boot file '{imagesPrefix}{name}'.");
                }
            }

            var estimate = EstimateImageSize(partitions, archive, images, cancellationToken);
            return new LoongHongFlashPackage(fullPath, config, partitions, images, bootFiles, estimate);
        }
    }

    /// <summary>
    /// Assembles the flashable disk image at <paramref name="destinationPath"/>; the archive stays open only for the
    /// duration of this call and the published image is complete before it replaces the destination.
    /// <para>在 <paramref name="destinationPath"/> 组装可烧录的磁盘镜像；归档仅在本调用期间保持打开，且镜像完整
    /// 写出后才替换目标。</para>
    /// </summary>
    /// <param name="destinationPath">Destination image path. <para>目标镜像路径。</para></param>
    /// <param name="options">Sizing and userdata switches. <para>容量与 userdata 开关。</para></param>
    /// <param name="progress">Optional synthesis progress sink. <para>可选的合成进度接收器。</para></param>
    /// <param name="cancellationToken">Cancels the synthesis; a cancelled run removes its partial output.
    /// <para>取消合成；取消会删除未完成的输出。</para></param>
    /// <returns>The final image size and what it carries. <para>最终镜像大小及其内容。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The layout is not representable, or a partition image does not fit its
    /// declared extent. <para>布局无法表达，或某分区镜像超出其声明区间。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public LoongHongSynthesisResult Synthesize(string destinationPath, LoongHongSynthesisOptions? options = null,
        IProgress<LoongHongProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var settings = options ?? new LoongHongSynthesisOptions();
        var plan = PlanLayout(settings);
        progress?.Report(new LoongHongProgress("layout", null, 0, plan.ImageSizeBytes));

        var staging = destinationPath + ".uotan-tmp";
        var temporaryRoot = Path.Combine(Path.GetTempPath(),
            $"uotan-loonghong-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            GptImageComposer.WriteNewImage(staging, (int)SectorSize, plan.BlockCount, plan.Entries,
                diskId: null, cancellationToken);

            using var archive = ZipContainer.OpenRead(PackagePath, PackageBounds);
            using (var image = new FileStream(staging, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                       1 << 20))
            {
                foreach (var partition in plan.Partitions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WritePartition(image, archive, partition, temporaryRoot, plan, progress, cancellationToken);
                }

                image.Flush(flushToDisk: true);
            }

            File.Move(staging, destinationPath, overwrite: true);
            progress?.Report(new LoongHongProgress("done", null, plan.ImageSizeBytes, plan.ImageSizeBytes));
            return new LoongHongSynthesisResult(plan.ImageSizeBytes, plan.Entries.Count, plan.IncludedUserData);
        }
        finally
        {
            TryDelete(staging);
            TryDeleteDirectory(temporaryRoot);
        }
    }

    private sealed record PlannedPartition(LoongHongPartition Declared, long Offset, long Extent, string? ImageEntry);

    private sealed record LayoutPlan(long ImageSizeBytes, ulong BlockCount, List<GptImagePartition> Entries,
        List<PlannedPartition> Partitions, bool IncludedUserData);

    /// <summary>
    /// Derives the image layout the way the vendor script partitions the disk: the first partition starts at the 1 MiB
    /// boundary, each declared size advances the cursor, a size of zero takes the remaining capacity, and every
    /// partition keeps a 4 KiB-aligned start.
    /// <para>按厂商脚本的分区方式推导镜像布局：首个分区从 1 MiB 边界开始，每个声明的长度推进游标，长度为 0 时占用
    /// 剩余容量，所有分区起点按 4 KiB 对齐。</para>
    /// </summary>
    private LayoutPlan PlanLayout(LoongHongSynthesisOptions options)
    {
        var entries = new List<GptImagePartition>(Partitions.Count);
        var planned = new List<PlannedPartition>(Partitions.Count);
        var ranges = new List<(long Start, long End)>();
        var cursor = FirstPartitionLba * SectorSize;
        var userDataExtent = 0L;
        var includedUserData = options.IncludeUserData && HasUserDataImage;

        if (options.IncludeUserData && options.CapacityBytes is null)
        {
            throw new InvalidDataException(
                "Extending the userdata partition requires the target capacity in bytes.");
        }

        foreach (var partition in Partitions)
        {
            var isUserData = string.Equals(partition.Name, UserDataPartitionName, StringComparison.OrdinalIgnoreCase);
            var imageEntry = ImageEntries.GetValueOrDefault(partition.Name);
            long extent;
            if (isUserData)
            {
                extent = includedUserData
                    ? UserDataExtent(options.CapacityBytes!.Value, cursor)
                    : Math.Max(ImageLengthFor(partition.Name), MinimumUserDataBytes);
                extent = AlignUp(extent);
                userDataExtent = extent;
            }
            else if (partition.SizeBytes > 0)
            {
                extent = AlignUp(partition.SizeBytes);
            }
            else
            {
                throw new InvalidDataException(
                    $"Partition '{partition.Name}' declares no size and is not the userdata partition.");
            }

            var offset = AlignUp(cursor);
            var end = checked(offset + extent);
            foreach (var (start, stop) in ranges)
            {
                if (offset < stop && start < end)
                {
                    throw new InvalidDataException(
                        $"Partition '{partition.Name}' overlaps another partition on the disk.");
                }
            }

            ranges.Add((offset, end));
            var firstLba = (ulong)(offset / SectorSize);
            var lastLba = checked(firstLba + (ulong)(extent / SectorSize) - 1UL);
            entries.Add(new GptImagePartition(partition.Name, TypeIdFor(partition.TypeCode),
                DeriveUniqueId(partition.Name), firstLba, lastLba));
            planned.Add(new PlannedPartition(partition, offset, extent, imageEntry));
            cursor = end;
        }

        var imageSize = AlignUp(checked(cursor + FirstPartitionLba * SectorSize));
        if (imageSize > MaximumImageBytes)
        {
            throw new InvalidDataException(
                $"The partition table implies a {imageSize}-byte image, above the {MaximumImageBytes}-byte limit.");
        }

        if (userDataExtent == 0 && Partitions.Any(p => p.Name.Equals(UserDataPartitionName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The userdata partition resolved to a zero extent.");
        }

        return new LayoutPlan(imageSize, (ulong)(imageSize / SectorSize), entries, planned, includedUserData);
    }

    private long UserDataExtent(long capacityBytes, long usedThrough)
    {
        var tail = FirstPartitionLba * SectorSize;
        var available = capacityBytes - usedThrough - tail;
        if (available <= 0)
        {
            throw new InvalidDataException(
                $"The target capacity of {capacityBytes} bytes leaves no room for the userdata partition.");
        }

        return (available / SectorSize) * SectorSize;
    }

    private long ImageLengthFor(string partitionName)
    {
        if (!ImageEntries.TryGetValue(partitionName, out var entryName))
        {
            return 0;
        }

        using var archive = ZipContainer.OpenRead(PackagePath, PackageBounds);
        return (long)(archive.GetEntry(entryName)?.Length ?? 0);
    }

    private void WritePartition(FileStream image, ZipArchive archive, PlannedPartition partition,
        string temporaryRoot, LayoutPlan plan, IProgress<LoongHongProgress>? progress,
        CancellationToken cancellationToken)
    {
        var declared = partition.Declared;
        var isEfi = string.Equals(declared.Name, EfiPartitionName, StringComparison.OrdinalIgnoreCase);

        if (isEfi)
        {
            WriteEfiPartition(image, archive, partition, temporaryRoot, plan, progress, cancellationToken);
            return;
        }

        if (partition.ImageEntry is { } entryName)
        {
            var entry = archive.GetEntry(entryName)
                ?? throw new InvalidDataException(
                    $"The partition image '{entryName}' disappeared from the package.");
            var length = entry.Length;
            if (length > partition.Extent)
            {
                throw new InvalidDataException(
                    $"The image '{entryName}' ({length} bytes) does not fit partition '{declared.Name}' ({partition.Extent} bytes).");
            }

            CopyEntryTo(image, entry, partition.Offset, length, declared.Name, plan,
                progress, cancellationToken);
            return;
        }

        if (string.Equals(declared.FileSystem, "raw", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // The vendor script formats a partition that has no image; building an empty volume of the declared
        // extent keeps the partition mountable instead of leaving it as unreadable zero bytes.
        // 厂商脚本会格式化没有镜像的分区；这里按声明长度构建空卷，使该分区可挂载，而不是无法识别的零字节。
        var emptyRoot = Path.Combine(temporaryRoot, "empty");
        Directory.CreateDirectory(emptyRoot);
        var volumePath = Path.Combine(temporaryRoot, "empty-volume.img");
        BuildEmptyVolume(emptyRoot, volumePath, declared, partition.Extent, cancellationToken);
        using (var volume = new FileStream(volumePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                   1 << 20, FileOptions.SequentialScan))
        {
            CopyStreamTo(image, volume, partition.Offset, Math.Min(volume.Length, partition.Extent),
                declared.Name, plan, progress, cancellationToken);
        }

        TryDelete(volumePath);
    }

    /// <summary>
    /// Writes the EFI partition: the original <c>efi.img</c> volume is unpacked, the boot files grub loads from the
    /// volume root are added, and the tree is packed back into a volume of exactly the declared extent.
    /// <para>写入 EFI 分区：先解包原始 <c>efi.img</c> 卷，再把 grub 从卷根加载的启动文件加入，最后按声明的精确长度
    /// 重新打包该目录树。</para>
    /// </summary>
    private void WriteEfiPartition(FileStream image, ZipArchive archive, PlannedPartition partition,
        string temporaryRoot, LayoutPlan plan, IProgress<LoongHongProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(temporaryRoot, "efi");
        Directory.CreateDirectory(root);

        if (partition.ImageEntry is { } entryName)
        {
            var entry = archive.GetEntry(entryName)
                ?? throw new InvalidDataException($"The EFI image '{entryName}' disappeared from the package.");
            using var stream = entry.Open();
            using var volume = CopyToSeekableFile(stream, Path.Combine(temporaryRoot, "efi-source.img"),
                entry.Length, cancellationToken);
            using var parser = new FatParser(volume);
            parser.ExtractToDirectory(root, overwrite: true, maximumOutputBytes: partition.Extent * 2,
                byteProgress: null, cancellationToken);
        }

        foreach (var (name, bootEntryName) in EfiBootEntries)
        {
            var entry = archive.GetEntry(bootEntryName)
                ?? throw new InvalidDataException($"The EFI boot file '{bootEntryName}' disappeared from the package.");
            var destination = Path.Combine(root, name);
            using var source = entry.Open();
            using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                1 << 20);
            source.CopyTo(target);
        }

        var efiVolumePath = Path.Combine(temporaryRoot, "efi-volume.img");
        // The vendor formats this partition with mkfs.vfat -F32; FAT32 needs at least 65525 clusters, so the
        // cluster size is pinned to one sector. Letting the builder pick a larger cluster would push the volume
        // past the partition extent and silently degrade it to FAT16.
        // 厂商用 mkfs.vfat -F32 格式化该分区；FAT32 至少需要 65525 簇，因此把簇大小固定为一个扇区。若让构建器
        // 自选更大的簇，卷会涨出分区长度并静默降级为 FAT16。
        var efiOptions = new FatBuildOptions
        {
            VolumeLabel = string.IsNullOrWhiteSpace(partition.Declared.Label) ? "EFI" : partition.Declared.Label,
            TotalBytes = partition.Extent,
            Variant = FatVariant.Fat32,
            SectorsPerCluster = 1,
        };
        FatBuilder.Build(root, efiVolumePath, efiOptions, progress: null, cancellationToken);
        if (new FileInfo(efiVolumePath).Length > partition.Extent)
        {
            // FAT32 has a floor of 65525 clusters, so a small ESP would grow past its own partition. The vendor's
            // 511 MiB ESP is above that floor and keeps FAT32 exactly as its own mkfs.vfat -F32 produced it.
            // FAT32 有 65525 簇的下限，较小的 ESP 会因此涨出其分区长度；厂商 511 MiB 的 ESP 高于该下限，因此保持
            // 与其 mkfs.vfat -F32 相同的 FAT32。
            TryDelete(efiVolumePath);
            FatBuilder.Build(root, efiVolumePath, efiOptions with { Variant = FatVariant.Auto },
                progress: null, cancellationToken);
        }

        using (var volume = new FileStream(efiVolumePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                   1 << 20, FileOptions.SequentialScan))
        {
            if (volume.Length > partition.Extent)
            {
                throw new InvalidDataException(
                    $"The rebuilt EFI volume ({volume.Length} bytes) does not fit partition '{partition.Declared.Name}' ({partition.Extent} bytes).");
            }

            CopyStreamTo(image, volume, partition.Offset, volume.Length, partition.Declared.Name, plan,
                progress, cancellationToken);
        }

        TryDelete(efiVolumePath);
        TryDeleteDirectory(root);
    }

    private static void BuildEmptyVolume(string sourceDirectory, string outputPath, LoongHongPartition partition,
        long extent, CancellationToken cancellationToken)
    {
        if (string.Equals(partition.FileSystem, "ext4", StringComparison.OrdinalIgnoreCase))
        {
            Ext4Builder.Build(sourceDirectory, outputPath, new Ext4Options
            {
                VolumeName = string.IsNullOrWhiteSpace(partition.Label) ? partition.Name : partition.Label,
                TargetSizeBytes = extent,
            });
            return;
        }

        if (string.Equals(partition.FileSystem, "vfat", StringComparison.OrdinalIgnoreCase))
        {
            FatBuilder.Build(sourceDirectory, outputPath, new FatBuildOptions
            {
                VolumeLabel = string.IsNullOrWhiteSpace(partition.Label) ? partition.Name : partition.Label,
                TotalBytes = extent,
            }, progress: null, cancellationToken);
            return;
        }

        throw new InvalidDataException(
            $"Partition '{partition.Name}' declares unsupported file system '{partition.FileSystem}'.");
    }

    private static void CopyEntryTo(FileStream image, ZipArchiveEntry entry, long offset, long length,
        string partitionName, LayoutPlan plan, IProgress<LoongHongProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var source = entry.Open();
        CopyStreamTo(image, source, offset, length, partitionName, plan, progress, cancellationToken);
    }

    private static void CopyStreamTo(FileStream image, Stream source, long offset, long length,
        string partitionName, LayoutPlan plan, IProgress<LoongHongProgress>? progress,
        CancellationToken cancellationToken)
    {
        image.Position = offset;
        var buffer = new byte[1 << 20];
        long written = 0;
        while (written < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = (int)Math.Min(buffer.Length, length - written);
            var read = source.Read(buffer, 0, want);
            if (read <= 0)
            {
                break;
            }

            image.Write(buffer, 0, read);
            written += read;
            progress?.Report(new LoongHongProgress("write-partition", partitionName, written, length));
        }

        if (written < length)
        {
            throw new EndOfStreamException(
                $"The content for partition '{partitionName}' ended after {written} of {length} bytes.");
        }
    }

    private static FileStream CopyToSeekableFile(Stream source, string path, long length,
        CancellationToken cancellationToken)
    {
        var target = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        try
        {
            var buffer = new byte[1 << 20];
            long copied = 0;
            while (copied < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var want = (int)Math.Min(buffer.Length, length - copied);
                var read = source.Read(buffer, 0, want);
                if (read <= 0)
                {
                    break;
                }

                target.Write(buffer, 0, read);
                copied += read;
            }

            target.Flush();
            target.Position = 0;
            return target;
        }
        catch
        {
            target.Dispose();
            TryDelete(path);
            throw;
        }
    }

    private static long EstimateImageSize(IReadOnlyList<LoongHongPartition> partitions, ZipArchive archive,
        IReadOnlyDictionary<string, string> images, CancellationToken cancellationToken)
    {
        var cursor = FirstPartitionLba * SectorSize;
        foreach (var partition in partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long extent;
            if (partition.SizeBytes > 0)
            {
                extent = AlignUp(partition.SizeBytes);
            }
            else if (images.TryGetValue(partition.Name, out var entryName))
            {
                extent = AlignUp(Math.Max((long)(archive.GetEntry(entryName)?.Length ?? 0), MinimumUserDataBytes));
            }
            else
            {
                extent = AlignUp(MinimumUserDataBytes);
            }

            cursor = AlignUp(cursor) + extent;
        }

        return AlignUp(checked(cursor + FirstPartitionLba * SectorSize));
    }

    private static PartitionEntryIndex BuildEntryIndex(ZipArchive archive, CancellationToken cancellationToken)
    {
        var entries = new List<(string Name, ZipArchiveEntry Entry)>(archive.Entries.Count);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = entry.FullName;
            if (raw.Length == 0 || raw[^1] is '/' or '\\')
            {
                continue;
            }

            if (raw.Length > MaxEntryNameLength)
            {
                throw new InvalidDataException($"A ZIP entry name exceeds {MaxEntryNameLength} characters.");
            }

            string normalized;
            try
            {
                normalized = ZipContainer.NormalizeEntryPath(raw);
            }
            catch (ArgumentException e)
            {
                throw new InvalidDataException($"The ZIP entry name '{raw}' is not a safe relative path.", e);
            }

            entries.Add((normalized, entry));
        }

        return new PartitionEntryIndex(entries);
    }

    private static string? FindConfigEntry(PartitionEntryIndex index)
    {
        foreach (var (name, _) in index.Entries)
        {
            if (string.Equals(Basename(name), ConfigFileName, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    private static List<LoongHongPartition> ParseConfig(ZipArchive archive, string configEntryName,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(configEntryName)
            ?? throw new InvalidDataException($"The package has no '{configEntryName}' entry.");
        JsonNode? root;
        using (var stream = entry.Open())
        {
            try
            {
                root = JsonNode.Parse(stream, new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                    new JsonDocumentOptions { AllowTrailingCommas = false, MaxDepth = 16 });
            }
            catch (JsonException e)
            {
                throw new InvalidDataException("Malformed LoongHong partition configuration.", e);
            }
        }

        if (root is not JsonObject table || table["partitions"] is not JsonArray array || array.Count == 0)
        {
            throw new InvalidDataException("The LoongHong partition configuration has no partition list.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var partitions = new List<LoongHongPartition>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject entryObject)
            {
                throw new InvalidDataException("LoongHong partition entries must be objects.");
            }

            var name = ReadString(entryObject, "name");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 36)
            {
                throw new InvalidDataException("A LoongHong partition name is missing or too long for GPT.");
            }

            foreach (var existing in partitions)
            {
                if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"The partition name '{name}' appears more than once.");
                }
            }

            partitions.Add(new LoongHongPartition(
                name,
                ReadString(entryObject, "label") ?? name,
                ParseSize(ReadString(entryObject, "size")),
                (ReadString(entryObject, "type") ?? "8300").Trim().ToUpperInvariant(),
                (ReadString(entryObject, "fs") ?? "ext4").Trim().ToLowerInvariant()));
        }

        return partitions;
    }

    private static string? ReadString(JsonObject entry, string key) =>
        entry[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null;

    /// <summary>
    /// Parses the vendor's size notation, such as <c>511M</c>, <c>4G</c> or <c>0</c> for "the rest of the disk".
    /// <para>解析厂商的长度写法，例如 <c>511M</c>、<c>4G</c>，以及表示“剩余空间”的 <c>0</c>。</para>
    /// </summary>
    private static long ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var value = text.Trim();
        var unit = char.ToUpperInvariant(value[^1]);
        var digits = char.IsLetter(value[^1]) ? value[..^1] : value;
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0)
        {
            throw new InvalidDataException($"The partition size '{text}' is not a valid size.");
        }

        if (!char.IsLetter(value[^1]))
        {
            return number;
        }

        var multiplier = unit switch
        {
            'K' => 1L << 10,
            'M' => 1L << 20,
            'G' => 1L << 30,
            'T' => 1L << 40,
            _ => throw new InvalidDataException($"The partition size '{text}' uses an unsupported unit."),
        };
        return checked(number * multiplier);
    }

    /// <summary>
    /// Maps a vendor GPT type code onto its type GUID. The EFI System type matters for boot, the Linux root type
    /// matches the vendor table for userdata, and every other code falls back to the generic Linux filesystem type
    /// the vendor uses for its data partitions.
    /// <para>把厂商 GPT 类型码映射为类型 GUID。EFI 系统类型影响启动，userdata 使用厂商表里的 Linux 根类型，
    /// 其余类型码回退为厂商用于数据分区的通用 Linux 文件系统类型。</para>
    /// </summary>
    internal static Guid TypeIdFor(string typeCode) => typeCode switch
    {
        "EF00" => new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B"),
        "8305" => new Guid("B921B045-1DF0-41C3-AF44-4C6F280D3FAE"),
        _ => new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4"),
    };

    /// <summary>
    /// Derives a deterministic unique partition GUID from the partition name, so repeated synthesis of the same
    /// package yields identical GPT entries. This is an integrity label, not a security claim.
    /// <para>由分区名推导确定性的唯一分区 GUID，使同一包的重复组装产出完全一致的 GPT 条目。该标识是完整性标记，
    /// 不构成安全声明。</para>
    /// </summary>
    private static Guid DeriveUniqueId(string name)
    {
        var digest = UotanStudio.Hash.Digests.Md5.ComputeHash(
            System.Text.Encoding.UTF8.GetBytes($"loonghong-disk-image:{name}"));
        digest[7] = (byte)((digest[7] & 0x0F) | 0x50);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        return new Guid(digest);
    }

    private static long AlignUp(long value) =>
        value <= 0 ? 0 : (value + ImageAlignBytes - 1) / ImageAlignBytes * ImageAlignBytes;

    private static string Basename(string name) => name[(name.LastIndexOf('/') + 1)..];

    private static string DirectoryOf(string name)
    {
        var index = name.LastIndexOf('/');
        return index < 0 ? string.Empty : name[..(index + 1)];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class PartitionEntryIndex(List<(string Name, ZipArchiveEntry Entry)> entries)
    {
        internal IReadOnlyList<(string Name, ZipArchiveEntry Entry)> Entries { get; } = entries;

        internal string? ResolveOptional(string path) => Entries
            .Where(item => string.Equals(item.Name, path, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Name)
            .FirstOrDefault();
    }
}