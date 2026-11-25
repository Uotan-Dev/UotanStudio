using UotanStudio.Compress.Zip;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.Application.Containers;

/// <summary>
/// Container structure analyzer: recognizes super / payload.bin / ZIP / HarmonyOS update.bin and, for every other
/// vendor or archive package registered in <see cref="FormatDetector"/>, enumerates the selectable inner entries
/// from the handler's own metadata. All bounded read-only parsing (metadata/manifest level), no data extraction,
/// AOT-safe (no reflection).
/// <para>容器结构分析器：识别 super / payload.bin / ZIP / HarmonyOS update.bin，并对
/// <see cref="FormatDetector"/> 中注册的其它厂商或归档包，从处理器自身的元数据枚举可勾选内部条目。
/// 全部为有界只读解析（元数据/清单级别），不提取数据，AOT 安全（无反射）。</para>
/// </summary>
public static class ContainerStructureAnalyzer
{
    private static readonly string[] ZipImageExtensions =
    [
        ".img", ".raw", ".ext4", ".erofs", ".f2fs", ".simg", ".sparse"
    ];

    /// <summary>Standard payload entry name inside a ZIP. <para>ZIP 内 payload 条目的标准名称。</para></summary>
    private const string PayloadMarker = "payload.bin";

    /// <summary>
    /// Upper bound on the entries listed from one vendor container; a package declaring more is treated as
    /// unanalyzable so an untrusted header cannot make the list unbounded.
    /// <para>单个厂商容器最多列出的条目数；声明更多的包视为不可分析，
    /// 避免不可信头部让列表无界膨胀。</para>
    /// </summary>
    private const int MaximumVendorEntries = 10000;

    /// <summary>
    /// Detects the container kind by magic and enumerates inner entries; failures (IO/format) return
    /// <see cref="ContainerStructure.Empty"/>.
    /// <para>按魔数识别容器类型并枚举内部条目；失败（IO/格式错误）返回 <see cref="ContainerStructure.Empty"/>。</para>
    /// </summary>
    /// <param name="filePath">The container file path. <para>容器文件路径。</para></param>
    /// <returns>The structure analysis result. <para>结构分析结果。</para></returns>
    public static ContainerStructure Analyze(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return ContainerStructure.Empty;
        }

        try
        {
            var header = FormatDetector.ReadHeader(filePath, 4100);
            if (header.Length >= 4 && FormatDetector.HasMagic(header, 0, "CrAU"))
            {
                return AnalyzePayload(filePath);
            }

            if (new VendorFirmware.HarmonyOS.HarmonyUpdateBinFormatHandler().CanHandle(filePath))
            {
                return AnalyzeUpdateBin(filePath);
            }

            if (new LpFormatHandler().CanHandle(filePath))
            {
                return AnalyzeSuper(filePath);
            }

            if (header.Length >= 4 && header[0] == 0x50 && header[1] == 0x4B)
            {
                return AnalyzeZip(filePath);
            }

            return AnalyzeVendor(filePath);
        }
        catch (Exception)
        {
            return ContainerStructure.Empty;
        }
    }

    /// <summary>
    /// Analyzes a payload (ZIP-wrapped included): lists manifest partition names and target sizes.
    /// <para>分析 payload（含 ZIP 包裹）：列出 manifest 中的分区名与目标大小。</para>
    /// </summary>
    private static ContainerStructure AnalyzePayload(string filePath)
    {
        var info = OtaDelta.Payload.PayloadParser.GetPartitionInfo(filePath);
        var entries = info.Select(p => new ContainerEntry(p.Name, "payload", (long)p.SizeBytes, FromPayload: true))
            .ToList();
        return new ContainerStructure(ContainerKind.Payload, entries);
    }

    /// <summary>
    /// Analyzes a HarmonyOS <c>update.bin</c> package: lists its component names, sizes and output extensions.
    /// <para>分析 HarmonyOS <c>update.bin</c> 包：列出组件名、大小与输出扩展名。</para>
    /// </summary>
    private static ContainerStructure AnalyzeUpdateBin(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        var package = VendorFirmware.HarmonyOS.HarmonyUpdateBinReader.Read(stream, stream.Length);
        var entries = new List<ContainerEntry>(package.Components.Count);
        foreach (var component in package.Components)
        {
            var extension = Path.GetExtension(component.OutputName);
            entries.Add(new ContainerEntry(component.Name,
                extension.Length > 1 ? extension[1..] : "update.bin", component.Size, FromPayload: false));
        }

        return new ContainerStructure(ContainerKind.UpdateBin, entries);
    }

    /// <summary>
    /// Analyzes a super image: reads slot-0 LP metadata and sizes each logical partition by extents.
    /// <para>分析 super 镜像：读取槽 0 LP 元数据，按 extent 计算每个逻辑分区大小。</para>
    /// </summary>
    private static ContainerStructure AnalyzeSuper(string filePath)
    {
        var metadata = MetadataReader.ReadFromImageFile(filePath);
        var sectorSize = (long)MetadataFormat.LP_SECTOR_SIZE;
        var entries = new List<ContainerEntry>(metadata.Partitions.Count);
        foreach (var partition in metadata.Partitions)
        {
            ulong sectors = 0;
            for (var i = 0; i < partition.NumExtents; i++)
            {
                sectors += metadata.Extents[(int)partition.FirstExtentIndex + i].NumSectors;
            }

            entries.Add(new ContainerEntry(partition.NameString, "super", checked((long)sectors * sectorSize), FromPayload: false));
        }

        return new ContainerStructure(ContainerKind.Super, entries);
    }

    /// <summary>
    /// Analyzes a ZIP: when a payload.bin exists, appends payload partitions after the ZIP image
    /// entries; otherwise enumerates only the image files inside the ZIP.
    /// <para>分析 ZIP：内含 payload.bin 时在 ZIP 镜像条目之外追加 payload 分区；
    /// 否则仅枚举 ZIP 内的镜像文件。</para>
    /// </summary>
    private static ContainerStructure AnalyzeZip(string filePath)
    {
        using var archive = ZipContainer.OpenRead(filePath);
        var entries = new List<ContainerEntry>();
        var hasPayload = false;
        string? payloadMemberPath = null;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (string.Equals(entry.Name, PayloadMarker, StringComparison.OrdinalIgnoreCase))
            {
                hasPayload = true;
                if (payloadMemberPath is null || entry.FullName == PayloadMarker) payloadMemberPath = entry.FullName;
                continue;
            }

            if (ZipImageExtensions.Contains(Path.GetExtension(entry.FullName), StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new ContainerEntry(entry.FullName, Path.GetExtension(entry.FullName).TrimStart('.'), (long)entry.Length, FromPayload: false)
                {
                    InnerFormat = ProbeZipEntry(entry)
                });
            }
        }

        if (hasPayload)
        {
            var info = OtaDelta.Payload.PayloadParser.GetPartitionInfo(filePath);
            entries.AddRange(info.Select(p => new ContainerEntry(p.Name, "payload", (long)p.SizeBytes, FromPayload: true)
            { PayloadMemberPath = payloadMemberPath }));
        }

        return new ContainerStructure(ContainerKind.Zip, entries);
    }

    /// <summary>
    /// Probes one ZIP member's leading bytes so the list can show the real image type instead of only the
    /// file extension; a member that cannot be read yields <see langword="null"/> and never fails the listing.
    /// <para>探测单个 ZIP 成员的起始字节，使列表能显示真实镜像类型而不只是扩展名；
    /// 成员无法读取时返回 <see langword="null"/>，绝不让整个列表失败。</para>
    /// </summary>
    /// <param name="entry">The ZIP entry to probe. <para>待探测的 ZIP 条目。</para></param>
    /// <returns>The probed type label, or <see langword="null"/>. <para>探测到的类型标签；可能为 <see langword="null"/>。</para></returns>
    private static string? ProbeZipEntry(System.IO.Compression.ZipArchiveEntry entry)
    {
        if (entry.Length <= 0)
        {
            return null;
        }

        return ContainerEntryFormatProbe.Identify(() => entry.Open(), entry.FullName);
    }

    /// <summary>
    /// Analyzes any other registered vendor or archive package through its format handler: the handler's own
    /// metadata supplies the entry names, and any size it reports is kept for the capacity estimate.
    /// <para>经格式处理器分析其它已注册的厂商或归档包：条目名来自处理器自身的元数据，
    /// 它上报的大小会被保留用于容量估算。</para>
    /// </summary>
    /// <param name="filePath">The package path. <para>包路径。</para></param>
    /// <returns>A <see cref="ContainerKind.Vendor"/> structure, or an empty structure when no handler claims the file.
    /// <para><see cref="ContainerKind.Vendor"/> 结构；没有处理器接受该文件时为空结构。</para></returns>
    private static ContainerStructure AnalyzeVendor(string filePath)
    {
        var handler = FormatDetector.Detect(filePath);
        if (handler is null)
        {
            return ContainerStructure.Empty;
        }

        if (handler is Archive.CpioFormatHandler archiveHandler)
        {
            return AnalyzeArchive(filePath, archiveHandler);
        }

        var entries = ReadVendorEntries(handler, filePath);
        if (entries.Count == 0)
        {
            return ContainerStructure.Empty;
        }

        var label = ResolveVendorLabel(filePath, handler);
        return new ContainerStructure(ContainerKind.Vendor, entries) { VendorLabel = label };
    }

    /// <summary>
    /// Analyzes a CPIO/TAR archive (including compressed variants): one bounded pass lists every member, its
    /// size and a probed content type, so the new-project list can show what each inner file actually is.
    /// <para>分析 CPIO/TAR 归档（含压缩变体）：单次有界遍历列出每个成员、其大小与探测到的内容类型，
    /// 使新建项目列表能显示每个内部文件的实际类型。</para>
    /// </summary>
    /// <param name="filePath">The archive path. <para>归档路径。</para></param>
    /// <param name="handler">The archive handler that accepted the file. <para>接受该文件的归档处理器。</para></param>
    /// <returns>A <see cref="ContainerKind.Vendor"/> structure, or an empty structure when nothing was listed.
    /// <para><see cref="ContainerKind.Vendor"/> 结构；未列出任何条目时为空结构。</para></returns>
    private static ContainerStructure AnalyzeArchive(string filePath, Archive.CpioFormatHandler handler)
    {
        var metadata = handler.ReadMetadataAsync(filePath).GetAwaiter().GetResult();
        if (!metadata.Success || metadata.Entries.Count == 0 || metadata.Entries.Count > MaximumVendorEntries)
        {
            return ContainerStructure.Empty;
        }

        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in metadata.Components)
        {
            if (!string.IsNullOrWhiteSpace(component.Name))
            {
                sizes[component.Name] = component.SizeBytes;
            }
        }

        var entries = new List<ContainerEntry>(metadata.Entries.Count);
        foreach (var name in metadata.Entries)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var size = sizes.TryGetValue(name, out var reported) ? reported : 0;
            var extension = Path.GetExtension(Path.GetFileName(name.Replace('\\', '/')));
            entries.Add(new ContainerEntry(name,
                extension.Length > 1 ? extension[1..].ToLowerInvariant() : "archive", size, FromPayload: false));
        }

        if (entries.Count == 0)
        {
            return ContainerStructure.Empty;
        }

        var probed = ProbeArchiveEntries(handler, filePath, metadata.Entries);
        entries = [.. entries.Select(entry => probed.TryGetValue(entry.Name, out var inner)
            ? entry with { InnerFormat = inner }
            : entry)];

        return new ContainerStructure(ContainerKind.Vendor, entries)
        {
            VendorLabel = ResolveVendorLabel(filePath, handler)
        };
    }

    /// <summary>
    /// Reads the leading bytes of every archive member in one bounded pass and names its content type, so the
    /// listing can show what each inner file is without extracting it; members whose payload is empty or
    /// unreadable are simply left out of the map.
    /// <para>单次有界遍历读取每个归档成员的起始字节并给出内容类型，使列表无需解包即可显示每个内部文件是什么；
    /// 负载为空或不可读的成员只是不进入映射。</para>
    /// </summary>
    /// <param name="handler">The archive handler that owns the decoding path. <para>拥有解码路径的归档处理器。</para></param>
    /// <param name="filePath">The archive path. <para>归档路径。</para></param>
    /// <param name="names">Entry names to probe. <para>待探测的条目名。</para></param>
    /// <returns>A map from entry name to the probed type label. <para>条目名到探测类型标签的映射。</para></returns>
    private static Dictionary<string, string> ProbeArchiveEntries(Archive.CpioFormatHandler handler, string filePath, IReadOnlyList<string> names) =>
        handler.ProbeEntryFormats(filePath, [.. names]);

    /// <summary>
    /// Lists a vendor package's inner entries from its handler metadata, preferring component sizes when the
    /// handler reports them; a handler that cannot list entries leaves the package unanalyzable.
    /// <para>从处理器元数据列出厂商包的内部条目，处理器上报组件大小时优先使用；
    /// 处理器无法列出条目时该包视为不可分析。</para>
    /// </summary>
    /// <param name="handler">The handler that accepted the file. <para>接受该文件的处理器。</para></param>
    /// <param name="filePath">The package path. <para>包路径。</para></param>
    /// <returns>The inner entries, or an empty list when the handler listed nothing. <para>内部条目；处理器未列出任何条目时为空列表。</para></returns>
    private static List<ContainerEntry> ReadVendorEntries(IReadonlyFormatHandler handler, string filePath)
    {
        FirmwareOpResult result;
        try
        {
            result = handler.ReadMetadataAsync(filePath).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }

        if (!result.Success)
        {
            return [];
        }

        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in result.Components)
        {
            if (!string.IsNullOrWhiteSpace(component.Name))
            {
                sizes[component.Name] = component.SizeBytes;
            }
        }

        var entries = new List<ContainerEntry>(Math.Min(result.Entries.Count, MaximumVendorEntries));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in result.Entries)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
            {
                continue;
            }

            if (entries.Count >= MaximumVendorEntries)
            {
                return [];
            }

            var size = sizes.TryGetValue(name, out var reported) ? reported : 0;
            var display = Path.GetFileName(name.Replace('\\', '/'));
            var extension = Path.GetExtension(display);
            entries.Add(new ContainerEntry(name,
                extension.Length > 1 ? extension[1..].ToLowerInvariant() : result.Metadata.TryGetValue("Format", out var format) ? format : "entry",
                size, FromPayload: false));
        }

        return entries;
    }

    /// <summary>
    /// Resolves the short label shown on the container row: the compressed or vendor extension when the file
    /// names one, otherwise the handler's format name.
    /// <para>解析容器行上显示的短标签：文件名指出压缩或厂商扩展名时用它，否则用处理器格式名。</para>
    /// </summary>
    /// <param name="filePath">The package path. <para>包路径。</para></param>
    /// <param name="handler">The handler that accepted the file. <para>接受该文件的处理器。</para></param>
    /// <returns>The lower-case label. <para>小写标签。</para></returns>
    private static string ResolveVendorLabel(string filePath, IReadonlyFormatHandler handler)
    {
        var name = Path.GetFileName(filePath);
        var doubleExtension = Path.GetExtension(Path.GetFileNameWithoutExtension(name));
        if (doubleExtension.Length > 1)
        {
            var candidate = doubleExtension[1..].ToLowerInvariant();
            if (handler.FileExtensions.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        var extension = Path.GetExtension(name);
        return extension.Length > 1 ? extension[1..].ToLowerInvariant() : handler.FormatName;
    }
}