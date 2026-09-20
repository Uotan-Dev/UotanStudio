using System.IO.Compression;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Models;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Reading;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.Application.Containers;

/// <summary>
/// <para>容器结构分析器：识别 super / payload.bin / ZIP 并枚举内部可勾选条目。
/// 全部为有界只读解析（元数据/清单级别），不提取数据，AOT 安全（无反射）。</para>
/// Container structure analyzer: recognizes super / payload.bin / ZIP and enumerates selectable
/// inner entries. All bounded read-only parsing (metadata/manifest level), no data extraction,
/// AOT-safe (no reflection).
/// </summary>
public static class ContainerStructureAnalyzer
{
    private static readonly string[] ZipImageExtensions =
    [
        ".img", ".raw", ".ext4", ".erofs", ".f2fs", ".simg", ".sparse"
    ];

    /// <summary><para>ZIP 内 payload 条目的标准名称。</para>Standard payload entry name inside a ZIP.</summary>
    private const string PayloadMarker = "payload.bin";

    /// <summary>
    /// <para>检查头部指定偏移处是否为指定 ASCII 字符串。</para>
    /// Whether the header carries the given ASCII string at the offset.
    /// </summary>
    private static bool IsAscii(byte[] header, int offset, string expected)
    {
        if (header.Length < offset + expected.Length)
        {
            return false;
        }

        for (var i = 0; i < expected.Length; i++)
        {
            if (header[offset + i] != (byte)expected[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <para>按魔数识别容器类型并枚举内部条目；失败（IO/格式错误）返回 <see cref="ContainerStructure.Empty"/>。</para>
    /// Detects the container kind by magic and enumerates inner entries; failures (IO/format) return
    /// <see cref="ContainerStructure.Empty"/>.
    /// </summary>
    /// <param name="filePath"><para>容器文件路径。</para>The container file path.</param>
    /// <returns><para>结构分析结果。</para>The structure analysis result.</returns>
    public static ContainerStructure Analyze(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return ContainerStructure.Empty;
        }

        try
        {
            // 读取 4100 字节头部：payload 魔数 "CrAU"@0、ZIP "PK"@0、LP geometry 魔数@4096。
            // Read a 4100-byte header: payload "CrAU"@0, ZIP "PK"@0, LP geometry magic@4096.
            var header = FormatDetector.ReadHeader(filePath, 4100);
            if (header.Length >= 4 && IsAscii(header, 0, "CrAU"))
            {
                return AnalyzePayload(filePath);
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
    /// <para>分析 payload（含 ZIP 包裹）：列出 manifest 中的分区名与目标大小。</para>
    /// Analyzes a payload (ZIP-wrapped included): lists manifest partition names and target sizes.
    /// </summary>
    private static ContainerStructure AnalyzePayload(string filePath)
    {
        // GetPayloadMetadata/GetPartitionInfo 均支持原始 payload.bin 与 ZIP 包裹（内部定位 payload.bin）。
        // GetPayloadMetadata/GetPartitionInfo both accept raw payload.bin and ZIP-wrapped payloads.
        var info = UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Parser.PayloadParser.GetPartitionInfo(filePath);
        var entries = info.Select(p => new ContainerEntry(p.Name, "payload", (long)p.SizeBytes, FromPayload: true))
            .ToList();
        return new ContainerStructure(ContainerKind.Payload, entries);
    }

    /// <summary>
    /// <para>分析 super 镜像：读取槽 0 LP 元数据，按 extent 计算每个逻辑分区大小。</para>
    /// Analyzes a super image: reads slot-0 LP metadata and sizes each logical partition by extents.
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
    /// <para>分析 ZIP：内含 payload.bin 时在 ZIP 镜像条目之外追加 payload 分区；
    /// 否则仅枚举 ZIP 内的镜像文件。</para>
    /// Analyzes a ZIP: when a payload.bin exists, appends payload partitions after the ZIP image
    /// entries; otherwise enumerates only the image files inside the ZIP.
    /// </summary>
    private static ContainerStructure AnalyzeZip(string filePath)
    {
        using var archive = new ZipArchive(File.OpenRead(filePath), ZipArchiveMode.Read, leaveOpen: false);
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

            // 仅枚举镜像文件；其他资源（配置文本、签名等）不进入勾选列表。
            // Only image files are enumerated; other resources (configs, signatures) are not listed.
            if (ZipImageExtensions.Contains(Path.GetExtension(entry.FullName), StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new ContainerEntry(entry.FullName, Path.GetExtension(entry.FullName).TrimStart('.'), (long)entry.Length, FromPayload: false));
            }
        }

        if (hasPayload)
        {
            // GetPartitionInfo 支持 ZIP 包裹的 payload（内部自动定位 payload.bin）。
            // GetPartitionInfo accepts a ZIP-wrapped payload (locates payload.bin internally).
            var info = UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Parser.PayloadParser.GetPartitionInfo(filePath);
            entries.AddRange(info.Select(p => new ContainerEntry(p.Name, "payload", (long)p.SizeBytes, FromPayload: true)
            { PayloadMemberPath = payloadMemberPath }));
        }

        return new ContainerStructure(ContainerKind.Zip, entries);
    }
}