using UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;

namespace UotanStudio.Firmware.VendorFirmware.Allwinner.CardImage;

/// <summary>
/// Progress of one card-image assembly run. <para>一次卡镜像组装运行的进度。</para>
/// </summary>
/// <param name="Phase">Stage identifier: <c>read</c>, <c>extract</c>, <c>write</c> or <c>done</c>.
/// <para>阶段标识：<c>read</c>、<c>extract</c>、<c>write</c> 或 <c>done</c>。</para></param>
/// <param name="Partition">Partition the event belongs to, or <see langword="null"/> for run-level events.
/// <para>事件所属分区；运行级事件为 <see langword="null"/>。</para></param>
/// <param name="BytesCompleted">Bytes processed so far in the current stage. <para>当前阶段已处理字节数。</para></param>
/// <param name="BytesTotal">Bytes the current stage must process, when known. <para>当前阶段待处理字节总数；已知时给出。</para></param>
public sealed record SunxiCardAssemblyProgress(string Phase, string? Partition, long? BytesCompleted, long? BytesTotal);

/// <summary>
/// Assembled card image: what the build produced and where its material came from.
/// <para>组装出的卡镜像：构建产出与材料来源。</para>
/// </summary>
/// <param name="ImagePath">Path of the assembled image. <para>组装出的镜像路径。</para></param>
/// <param name="ImageBytes">Total image length. <para>镜像总字节长度。</para></param>
/// <param name="PartitionCount">Partitions written. <para>已写入的分区数。</para></param>
/// <param name="Kind">Input shape the material came from. <para>材料来自的输入形态。</para></param>
public sealed record SunxiCardAssemblyResult(string ImagePath, long ImageBytes, int PartitionCount,
    SunxiCardMaterialKind Kind);

/// <summary>
/// Turns Sunxi card material into one flashable SD-card image, accepting either a directory of vendor <c>.fex</c>
/// files or an <c>IMAGEWTY</c> firmware container. The partition layout always comes from the material's own
/// <c>sunxi_mbr.fex</c> table, so partition names and sector boundaries stay the vendor's.
/// <para>把全志卡材料组装为一张可烧录的 SD 卡镜像，接受厂商 <c>.fex</c> 文件目录或 <c>IMAGEWTY</c> 固件容器。
/// 分区布局始终取自材料自带的 <c>sunxi_mbr.fex</c> 表，因此分区名与扇区边界保持厂商原样。</para>
/// </summary>
/// <remarks>
/// The image capacity is derived from the table's highest declared sector, rounded up past the backup GPT, unless
/// the caller supplies one. Payloads stored inside a container are extracted into a temporary directory that is
/// removed afterwards.
/// <para>
/// 镜像容量由表中声明的最高扇区推导并向上取整越过后备 GPT，除非调用方显式给出。存放在容器内的负载会先提取到临时
/// 目录，结束后删除。
/// </para>
/// </remarks>
public static class SunxiCardAssembler
{
    /// <summary>Backup blocks reserved after the last partition by the GPT layout.
    /// <para>GPT 布局在最后一个分区之后保留的后备块数。</para>
    /// </summary>
    private const ulong BackupBlocks = 34;

    /// <summary>
    /// Reports whether the given path is material this assembler can turn into a card image.
    /// <para>判断给定路径是否为该组装器可转为卡镜像的材料。</para>
    /// </summary>
    /// <param name="path">Candidate directory or container. <para>候选目录或容器。</para></param>
    /// <param name="cancellationToken">Cancels the probe. <para>取消探测。</para></param>
    /// <returns><see langword="true"/> when the material carries a Sunxi MBR table.
    /// <para>材料携带 Sunxi MBR 表时为 <see langword="true"/>。</para></returns>
    public static bool IsSupported(string path, CancellationToken cancellationToken = default) =>
        Directory.Exists(path) ? SunxiCardMaterialReader.IsFexDirectory(path)
        : File.Exists(path) && SunxiCardMaterialReader.IsImageWtyCardSource(path, cancellationToken);

    /// <summary>
    /// Reads the material at <paramref name="sourcePath"/> and assembles the image at <paramref name="destinationPath"/>.
    /// <para>读取 <paramref name="sourcePath"/> 处的材料，并在 <paramref name="destinationPath"/> 组装镜像。</para>
    /// </summary>
    /// <param name="sourcePath">Directory of <c>.fex</c> files, or an <c>IMAGEWTY</c> container.
    /// <para><c>.fex</c> 文件目录，或 <c>IMAGEWTY</c> 容器。</para></param>
    /// <param name="destinationPath">Destination image path. <para>目标镜像路径。</para></param>
    /// <param name="capacityBytes">Requested image capacity, or <see langword="null"/> to derive it from the table.
    /// <para>请求的镜像容量；为 <see langword="null"/> 时由分区表推导。</para></param>
    /// <param name="progress">Optional assembly progress sink. <para>可选的组装进度接收器。</para></param>
    /// <param name="cancellationToken">Cancels the assembly. <para>取消组装。</para></param>
    /// <returns>What the assembly produced. <para>组装产出。</para></returns>
    /// <exception cref="InvalidDataException">The material carries no usable partition table, or a declared capacity
    /// cannot hold it. <para>材料未携带可用分区表，或声明的容量放不下。</para></exception>
    /// <exception cref="IOException">A payload cannot be read or the image cannot be written.
    /// <para>负载无法读取或镜像无法写入。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled. <para>令牌被取消。</para></exception>
    public static SunxiCardAssemblyResult Assemble(string sourcePath, string destinationPath,
        long? capacityBytes = null, IProgress<SunxiCardAssemblyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        progress?.Report(new SunxiCardAssemblyProgress("read", null, 0, null));

        var material = Directory.Exists(sourcePath)
            ? SunxiCardMaterialReader.ReadFexDirectory(sourcePath, cancellationToken)
            : SunxiCardMaterialReader.ReadImageWty(sourcePath, cancellationToken);

        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"uotan-sunxi-card-{Guid.NewGuid():N}");
        try
        {
            var table = LoadTable(material, temporaryRoot, progress, cancellationToken);
            var blockCount = ResolveBlockCount(table, material, capacityBytes);
            var partitions = BuildPartitionPlan(table, material, temporaryRoot, progress, cancellationToken);
            var input = new SunxiCardBuildInput(
                MaterializeBoot0(material, temporaryRoot, cancellationToken),
                MaterializeBootPackage(material, temporaryRoot, cancellationToken),
                blockCount);

            var writeProgress = progress is null
                ? null
                : new Progress<(string Partition, long BytesCopied)>(value =>
                    progress.Report(new SunxiCardAssemblyProgress("write", value.Partition, value.BytesCopied, null)));
            var result = SunxiCardImageWriter.Build(input, partitions, destinationPath, writeProgress,
                cancellationToken);
            progress?.Report(new SunxiCardAssemblyProgress("done", null, result.ImageBytes, result.ImageBytes));
            return new SunxiCardAssemblyResult(destinationPath, result.ImageBytes, result.PartitionCount,
                material.Kind);
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
        }
    }

    /// <summary>
    /// Loads the vendor partition table from the material, extracting it first when it lives inside a container.
    /// <para>从材料加载厂商分区表；表在容器内时先提取。</para>
    /// </summary>
    private static IReadOnlyList<SunxiMbrPartition> LoadTable(SunxiCardMaterialSet material, string temporaryRoot,
        IProgress<SunxiCardAssemblyProgress>? progress, CancellationToken cancellationToken)
    {
        if (material.Kind == SunxiCardMaterialKind.FexDirectory)
        {
            var path = Path.Combine(material.SourcePath, SunxiCardMaterialReader.PartitionTableFileName);
            var bytes = ReadAllBounded(path, cancellationToken);
            return SunxiMbrTable.Parse(bytes).Partitions;
        }

        progress?.Report(new SunxiCardAssemblyProgress("read", null, null, null));
        using var container = new FileStream(material.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.RandomAccess);
        var header = AllwinnerImageWtyReader.Read(container, cancellationToken);
        var entry = header.Entries.FirstOrDefault(candidate => string.Equals(candidate.Name,
            SunxiCardMaterialReader.PartitionTableFileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"The container carries no '{SunxiCardMaterialReader.PartitionTableFileName}' entry.");
        using var buffer = new MemoryStream();
        AllwinnerImageWtyReader.ExtractEntry(container, entry, buffer, header.IsEncrypted, cancellationToken);
        return SunxiMbrTable.Parse(buffer.ToArray()).Partitions;
    }

    /// <summary>
    /// Derives the image capacity: the highest declared sector end plus the backup GPT, or the caller's request when
    /// it is large enough. <para>推导镜像容量：表中声明最高的扇区末端加后备 GPT，或调用方给出的足够大的请求值。</para>
    /// </summary>
    private static ulong ResolveBlockCount(IReadOnlyList<SunxiMbrPartition> table, SunxiCardMaterialSet material,
        long? capacityBytes)
    {
        var highest = table.Max(partition => partition.FirstSector + partition.SectorCount);
        var required = checked(highest + BackupBlocks);
        var requested = capacityBytes is { } bytes && bytes > 0
            ? (ulong)(bytes / SunxiCardImageWriter.BlockSize)
            : material.DeclaredBytes is { } declared && declared > 0
                ? (ulong)(declared / SunxiCardImageWriter.BlockSize)
                : required;
        if (requested < required)
        {
            throw new InvalidDataException(
                $"The requested capacity of {requested} blocks cannot hold the table, which needs {required}.");
        }

        return requested;
    }

    private static List<SunxiCardWritePartition> BuildPartitionPlan(IReadOnlyList<SunxiMbrPartition> table,
        SunxiCardMaterialSet material, string temporaryRoot,
        IProgress<SunxiCardAssemblyProgress>? progress, CancellationToken cancellationToken)
    {
        var partitionRoot = Path.Combine(temporaryRoot, "partitions");
        var plan = new List<SunxiCardWritePartition>(table.Count);
        foreach (var partition in table)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = material.Partitions
                .FirstOrDefault(candidate => string.Equals(candidate.PartitionName, partition.Name,
                    StringComparison.OrdinalIgnoreCase))
                ?.Source;
            string? payloadPath = null;
            if (source is not null)
            {
                payloadPath = source.Materialize(partitionRoot, cancellationToken);
                progress?.Report(new SunxiCardAssemblyProgress("extract", partition.Name, null, null));
            }

            plan.Add(new SunxiCardWritePartition(partition.Name, partition.FirstSector, partition.SectorCount,
                DefaultTypeId, payloadPath));
        }

        return plan;
    }

    private static string? MaterializeBoot0(SunxiCardMaterialSet material, string temporaryRoot,
        CancellationToken cancellationToken)
    {
        if (material.FirstStageLoader is not { } loader)
        {
            throw new InvalidDataException(
                $"The material carries no '{SunxiCardMaterialReader.FirstStageLoaderFileName}' first-stage loader, which an Allwinner card image requires.");
        }

        return loader.Materialize(Path.Combine(temporaryRoot, "boot"), cancellationToken);
    }

    private static string? MaterializeBootPackage(SunxiCardMaterialSet material, string temporaryRoot,
        CancellationToken cancellationToken) =>
        material.BootPackage?.Materialize(Path.Combine(temporaryRoot, "boot"), cancellationToken);

    private static Guid DefaultTypeId => new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");

    private static byte[] ReadAllBounded(string path, CancellationToken cancellationToken)
    {
        const int limit = 64 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = (int)Math.Min(stream.Length, limit);
        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count <= 0)
            {
                break;
            }

            read += count;
        }

        return buffer;
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
}