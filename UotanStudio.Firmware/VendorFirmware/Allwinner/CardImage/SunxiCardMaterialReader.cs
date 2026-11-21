using System.Text.Json;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;

namespace UotanStudio.Firmware.VendorFirmware.Allwinner.CardImage;

/// <summary>
/// Where a card image's payload for one partition comes from.
/// <para>卡镜像某个分区的负载来自哪里。</para>
/// </summary>
/// <param name="PartitionName">Partition name the material belongs to. <para>该材料所属的分区名。</para></param>
/// <param name="Source">Material carrying the payload, or <see langword="null"/> when the partition has none and
/// must stay zero-filled. <para>承载负载的材料；分区没有负载、必须保持零填充时为 <see langword="null"/>。</para></param>
public sealed record SunxiCardMaterial(string PartitionName, SunxiCardPayloadSource? Source);

/// <summary>
/// A payload source: a host file, or one entry inside an <c>IMAGEWTY</c> container that has to be extracted first.
/// <para>负载来源：宿主文件，或 <c>IMAGEWTY</c> 容器内需要先提取的一个条目。</para>
/// </summary>
public abstract record SunxiCardPayloadSource
{
    /// <summary>
    /// Gets the payload as a host file path, extracting it into <paramref name="workingDirectory"/> when the source
    /// lives inside a container. <para>把负载取为宿主文件路径；来源在容器内时先提取到
    /// <paramref name="workingDirectory"/>。</para>
    /// </summary>
    /// <param name="workingDirectory">Directory that receives extracted payloads. <para>接收提取结果的目录。</para></param>
    /// <param name="cancellationToken">Cancels the extraction. <para>取消提取。</para></param>
    /// <returns>The path of the payload file. <para>负载文件路径。</para></returns>
    public abstract string Materialize(string workingDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// Payload that already exists as a host file. <para>已作为宿主文件存在的负载。</para>
/// </summary>
/// <param name="Path">Absolute path of the file. <para>文件的绝对路径。</para></param>
public sealed record SunxiCardFileSource(string Path) : SunxiCardPayloadSource
{
    /// <inheritdoc />
    public override string Materialize(string workingDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Path;
    }
}

/// <summary>
/// Payload stored inside an <c>IMAGEWTY</c> container, RC6-decrypting it when the container is encrypted.
/// <para>存放在 <c>IMAGEWTY</c> 容器内的负载；容器加密时经 RC6 解密。</para>
/// </summary>
/// <param name="ContainerPath">Path of the container. <para>容器路径。</para></param>
/// <param name="EntryName">Entry name carrying the payload. <para>承载负载的条目名。</para></param>
/// <param name="EffectiveLength">Payload length that actually contributes bytes.
/// <para>实际贡献字节的负载长度。</para></param>
/// <param name="IsEncrypted">Whether the container payloads are RC6-encrypted.
/// <para>容器负载是否经 RC6 加密。</para></param>
public sealed record SunxiCardContainerSource(string ContainerPath, string EntryName, long EffectiveLength,
    bool IsEncrypted) : SunxiCardPayloadSource
{
    /// <inheritdoc />
    public override string Materialize(string workingDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workingDirectory);
        using var container = new FileStream(ContainerPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.RandomAccess);
        var header = AllwinnerImageWtyReader.Read(container, cancellationToken);
        var entry = header.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, EntryName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"The container no longer carries the entry '{EntryName}'.");
        var destination = Path.Combine(workingDirectory, $"{Sanitize(entry.Name)}.bin");
        using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            AllwinnerImageWtyReader.ExtractEntry(container, entry, output, IsEncrypted, cancellationToken);
        }

        return destination;
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string([.. name.Where(character => !Path.GetInvalidFileNameChars().Contains(character))]);
        return cleaned.Length == 0 ? "entry" : cleaned;
    }
}

/// <summary>
/// Recognized card material: the boot region files, the partition layout and one payload per partition.
/// <para>已识别的卡材料：引导区文件、分区布局与每个分区各一份负载。</para>
/// </summary>
/// <param name="Kind">Which input shape produced this material. <para>产出该材料的输入形态。</para></param>
/// <param name="SourcePath">Path of the package or directory the material was read from.
/// <para>读取该材料所用的包或目录路径。</para></param>
/// <param name="FirstStageLoader">eGON first-stage loader source, or <see langword="null"/> when absent.
/// <para>eGON 一级引导程序来源；不存在时为 <see langword="null"/>。</para></param>
/// <param name="BootPackage">Boot package source, or <see langword="null"/> when absent.
/// <para>启动包来源；不存在时为 <see langword="null"/>。</para></param>
/// <param name="Partitions">Partitions in vendor table order. <para>按厂商表顺序排列的分区。</para></param>
/// <param name="Table">Parsed vendor partition table the layout came from.
/// <para>布局所取自的已解析厂商分区表。</para></param>
/// <param name="DeclaredBytes">Capacity the material declares, or <see langword="null"/> when it declares none.
/// <para>材料声明的容量；未声明时为 <see langword="null"/>。</para></param>
public sealed record SunxiCardMaterialSet(SunxiCardMaterialKind Kind, string SourcePath,
    SunxiCardPayloadSource? FirstStageLoader, SunxiCardPayloadSource? BootPackage,
    IReadOnlyList<SunxiCardMaterial> Partitions, IReadOnlyList<SunxiMbrPartition> Table, long? DeclaredBytes)
{
    /// <summary>
    /// Gets the number of partitions that actually carry a payload.
    /// <para>获取实际携带负载的分区数量。</para>
    /// </summary>
    public int PayloadCount => Partitions.Count(partition => partition.Source is not null);
}

/// <summary>
/// Input shape a material set was read from. <para>材料集的输入形态。</para>
/// </summary>
public enum SunxiCardMaterialKind
{
    /// <summary>A directory of vendor <c>.fex</c> files, including a <c>sunxi_mbr.fex</c> table.
    /// <para>由厂商 <c>.fex</c> 文件组成的目录，含 <c>sunxi_mbr.fex</c> 分区表。</para>
    /// </summary>
    FexDirectory,

    /// <summary>An <c>IMAGEWTY</c> firmware container. <para><c>IMAGEWTY</c> 固件容器。</para></summary>
    ImageWty,
}

/// <summary>
/// Reads Sunxi card material from either supported input: a directory of vendor <c>.fex</c> files carrying a
/// <c>sunxi_mbr.fex</c> partition table, or an <c>IMAGEWTY</c> container that stores the same table as an entry.
/// Both shapes yield the partition layout from the table itself, never from a caller guess, so the assembled image
/// keeps the vendor's partition names and sector boundaries.
/// <para>从两种受支持输入读取全志卡材料：携带 <c>sunxi_mbr.fex</c> 分区表的厂商 <c>.fex</c> 目录，或把同一张表存为
/// 条目的 <c>IMAGEWTY</c> 容器。两种形态的分区布局都取自表本身而非调用方猜测，因此组装出的镜像保留厂商分区名与
/// 扇区边界。</para>
/// </summary>
public static class SunxiCardMaterialReader
{
    /// <summary>Name of the vendor partition table file. <para>厂商分区表文件名。</para></summary>
    public const string PartitionTableFileName = "sunxi_mbr.fex";

    /// <summary>Name of the SD-card first-stage loader file. <para>SD 卡一级引导程序文件名。</para></summary>
    public const string FirstStageLoaderFileName = "boot0_sdcard.fex";

    /// <summary>Name of the boot package file. <para>启动包文件名。</para></summary>
    public const string BootPackageFileName = "boot_package.fex";

    /// <summary>Largest partition table the reader accepts. <para>读取器接受的分区表上限。</para></summary>
    private const int MaximumTableBytes = 64 * 1024;

    /// <summary>
    /// Reports whether a directory carries the vendor partition table this reader needs.
    /// <para>判断目录是否携带本读取器所需的厂商分区表。</para>
    /// </summary>
    /// <param name="directoryPath">Candidate directory. <para>候选目录。</para></param>
    /// <returns><see langword="true"/> when the table file is present. <para>表文件存在时为 <see langword="true"/>。</para></returns>
    public static bool IsFexDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        return File.Exists(Path.Combine(directoryPath, PartitionTableFileName));
    }

    /// <summary>
    /// Reads material from a directory of vendor <c>.fex</c> files.
    /// <para>从厂商 <c>.fex</c> 文件目录读取材料。</para>
    /// </summary>
    /// <param name="directoryPath">Directory holding <c>sunxi_mbr.fex</c> and the partition payloads.
    /// <para>存放 <c>sunxi_mbr.fex</c> 与分区负载的目录。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消读取。</para></param>
    /// <returns>The recognized material. <para>识别出的材料。</para></returns>
    /// <exception cref="DirectoryNotFoundException">The directory or the table file does not exist.
    /// <para>目录或表文件不存在。</para></exception>
    /// <exception cref="InvalidDataException">The table is malformed or declares no usable partitions.
    /// <para>表格式非法或未声明可用分区。</para></exception>
    public static SunxiCardMaterialSet ReadFexDirectory(string directoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var full = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"The card material directory does not exist: '{full}'.");
        }

        var tablePath = Path.Combine(full, PartitionTableFileName);
        if (!File.Exists(tablePath))
        {
            throw new FileNotFoundException(
                $"The directory carries no '{PartitionTableFileName}' partition table.", tablePath);
        }

        var table = ParseTableFile(tablePath, cancellationToken);
        var partitions = new List<SunxiCardMaterial>(table.Count);
        foreach (var partition in table)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payloadPath = Path.Combine(full, $"{partition.Name}.fex");
            if (!File.Exists(payloadPath))
            {
                payloadPath = Path.Combine(full, partition.Name);
            }

            partitions.Add(new SunxiCardMaterial(partition.Name,
                File.Exists(payloadPath) ? new SunxiCardFileSource(payloadPath) : null));
        }

        return new SunxiCardMaterialSet(SunxiCardMaterialKind.FexDirectory, full,
            OptionalFile(full, FirstStageLoaderFileName),
            OptionalFile(full, BootPackageFileName),
            partitions, table, DeclaredBytes: null);
    }

    /// <summary>
    /// Reads material from an <c>IMAGEWTY</c> container: its <c>sunxi_mbr.fex</c> entry gives the layout, its SD-card
    /// boot0 entry the first-stage loader, and its boot package entry the boot package.
    /// <para>从 <c>IMAGEWTY</c> 容器读取材料：<c>sunxi_mbr.fex</c> 条目给出布局，SD 卡 boot0 条目给出一级引导程序，
    /// 启动包条目给出启动包。</para>
    /// </summary>
    /// <param name="containerPath">Path of the container. <para>容器路径。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消读取。</para></param>
    /// <returns>The recognized material. <para>识别出的材料。</para></returns>
    /// <exception cref="FileNotFoundException">The container does not exist. <para>容器不存在。</para></exception>
    /// <exception cref="InvalidDataException">The container is malformed or carries no usable partition table.
    /// <para>容器格式非法或未携带可用分区表。</para></exception>
    public static SunxiCardMaterialSet ReadImageWty(string containerPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);
        var full = Path.GetFullPath(containerPath);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException("The Allwinner container does not exist.", full);
        }

        using var container = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.RandomAccess);
        var header = AllwinnerImageWtyReader.Read(container, cancellationToken);
        var tableEntry = header.Entries.FirstOrDefault(entry =>
            string.Equals(entry.Name, PartitionTableFileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"The container carries no '{PartitionTableFileName}' partition table entry.");
        var table = ParseTableBytes(ReadEntry(container, tableEntry, header.IsEncrypted, cancellationToken));

        var boot0Entry = header.Entries.FirstOrDefault(entry =>
            entry.Item == AllwinnerImageItem.Boot0Card
            || string.Equals(entry.Name, FirstStageLoaderFileName, StringComparison.OrdinalIgnoreCase));
        var packageEntry = header.Entries.FirstOrDefault(entry =>
            entry.Item == AllwinnerImageItem.BootPackage
            || string.Equals(entry.Name, BootPackageFileName, StringComparison.OrdinalIgnoreCase));

        var partitions = new List<SunxiCardMaterial>(table.Count);
        foreach (var partition in table)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = header.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, $"{partition.Name}.fex", StringComparison.OrdinalIgnoreCase))
                ?? header.Entries.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, partition.Name, StringComparison.OrdinalIgnoreCase));
            partitions.Add(new SunxiCardMaterial(partition.Name,
                entry is null
                    ? null
                    : new SunxiCardContainerSource(full, entry.Name,
                        AllwinnerImageWtyReader.EffectiveLength(entry), header.IsEncrypted)));
        }

        return new SunxiCardMaterialSet(SunxiCardMaterialKind.ImageWty, full,
            boot0Entry is null
                ? null
                : new SunxiCardContainerSource(full, boot0Entry.Name,
                    AllwinnerImageWtyReader.EffectiveLength(boot0Entry), header.IsEncrypted),
            packageEntry is null
                ? null
                : new SunxiCardContainerSource(full, packageEntry.Name,
                    AllwinnerImageWtyReader.EffectiveLength(packageEntry), header.IsEncrypted),
            partitions, table,
            header.ImageSize > 0 ? header.ImageSize : null);
    }

    /// <summary>
    /// Reports whether a container carries the vendor partition table, without extracting anything.
    /// <para>判断容器是否携带厂商分区表，不提取任何内容。</para>
    /// </summary>
    /// <param name="containerPath">Candidate container. <para>候选容器。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消读取。</para></param>
    /// <returns><see langword="true"/> when a partition table entry exists; <see langword="false"/> otherwise.
    /// <para>存在分区表条目时为 <see langword="true"/>；否则为 <see langword="false"/>。</para></returns>
    public static bool IsImageWtyCardSource(string containerPath, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);
            using var container = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 16, FileOptions.RandomAccess);
            Span<byte> magic = stackalloc byte[8];
            if (container.Read(magic) < 8 || !AllwinnerImageWtyReader.HasPlainMagic(magic))
            {
                return false;
            }

            var header = AllwinnerImageWtyReader.Read(container, cancellationToken);
            return header.Entries.Any(entry =>
                string.Equals(entry.Name, PartitionTableFileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static SunxiCardPayloadSource? OptionalFile(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        return File.Exists(path) ? new SunxiCardFileSource(path) : null;
    }

    private static List<SunxiMbrPartition> ParseTableFile(string path, CancellationToken cancellationToken)
    {
        var length = (int)Math.Min(new FileInfo(path).Length, MaximumTableBytes);
        var buffer = new byte[length];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
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
        }

        return ParseTableBytes(buffer);
    }

    private static List<SunxiMbrPartition> ParseTableBytes(byte[] data)
    {
        var table = SunxiMbrTable.Parse(data);
        if (table.Partitions.Count == 0)
        {
            throw new InvalidDataException("The Sunxi MBR table declares no partitions.");
        }

        return [.. table.Partitions];
    }

    private static byte[] ReadEntry(FileStream container, ImageWtyEntry entry, bool encrypted,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        AllwinnerImageWtyReader.ExtractEntry(container, entry, buffer, encrypted, cancellationToken);
        return buffer.ToArray();
    }
}