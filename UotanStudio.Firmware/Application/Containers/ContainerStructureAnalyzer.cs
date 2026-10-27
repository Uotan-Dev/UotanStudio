using UotanStudio.Compress.Zip;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.Application.Containers;

/// <summary>
/// Container structure analyzer: recognizes super / payload.bin / ZIP / HarmonyOS update.bin and enumerates
/// selectable inner entries. All bounded read-only parsing (metadata/manifest level), no data extraction,
/// AOT-safe (no reflection).
/// <para>容器结构分析器：识别 super / payload.bin / ZIP / HarmonyOS update.bin 并枚举内部可勾选条目。
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
        }
        catch (Exception)
        {
            return ContainerStructure.Empty;
        }

        return ContainerStructure.Empty;
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
                entries.Add(new ContainerEntry(entry.FullName, Path.GetExtension(entry.FullName).TrimStart('.'), (long)entry.Length, FromPayload: false));
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
}