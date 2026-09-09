using System.IO.Compression;
using UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// <para>容器类型：无 / super / payload.bin / ZIP 归档。</para>
/// Container kind: none / super / payload.bin / ZIP archive.
/// </summary>
public enum ContainerKind
{
    /// <summary><para>不是可分析的容器。</para>Not an analyzable container.</summary>
    None = 0,

    /// <summary><para>Android super 镜像（LP 元数据）。</para>Android super image (LP metadata).</summary>
    Super,

    /// <summary><para>Android OTA payload（payload.bin 或 ZIP 包裹）。</para>Android OTA payload (payload.bin or ZIP-wrapped).</summary>
    Payload,

    /// <summary><para>ZIP 归档（不含 payload.bin）。</para>ZIP archive (without payload.bin).</summary>
    Zip
}

/// <summary>
/// <para>容器内部条目：分区名或 ZIP 内文件，供新建项目列表勾选。</para>
/// A container inner entry: a partition name or a ZIP member, selectable in the new-project list.
/// </summary>
/// <param name="Name">
/// <para>显示名称：super/payload 分区名或 ZIP 条目完整路径。</para>
/// Display name: the super/payload partition name or the full ZIP entry path.
/// </param>
/// <param name="Format">
/// <para>格式列显示文本（如 payload / super / 镜像扩展名）。</para>
/// Format column text (e.g. payload / super / image extension).
/// </param>
/// <param name="SizeBytes">
/// <para>条目大小（字节）；无法确定时为 0。</para>
/// Entry size in bytes; 0 when unknown.
/// </param>
/// <param name="FromPayload">
/// <para>是否来自 payload 内部分区（ZIP 容器时用于区分 ZIP 条目与 payload 分区）。</para>
/// Whether the entry is a payload partition (distinguishes ZIP members from payload partitions inside a ZIP).
/// </param>
public sealed record ContainerEntry(
    string Name,
    string Format,
    long SizeBytes,
    bool FromPayload);

/// <summary>
/// <para>容器结构分析结果。</para>
/// Container structure analysis result.
/// </summary>
/// <param name="Kind"><para>容器类型。</para>The container kind.</param>
/// <param name="Entries"><para>内部条目列表（按展示顺序）。</para>Inner entries in display order.</param>
public sealed record ContainerStructure(
    ContainerKind Kind,
    IReadOnlyList<ContainerEntry> Entries)
{
    /// <summary><para>空结果（非容器或解析失败）。</para>Empty result (not a container or parse failed).</summary>
    public static ContainerStructure Empty { get; } = new(ContainerKind.None, []);
}

/// <summary>
/// <para>新建项目容器结构分析器：识别 super / payload.bin / ZIP 并枚举内部可勾选条目。
/// 全部为有界只读解析（元数据/清单级别），不提取数据，AOT 安全（无反射）。</para>
/// New-project container structure analyzer: recognizes super / payload.bin / ZIP and enumerates
/// selectable inner entries. All bounded read-only parsing (metadata/manifest level), no data
/// extraction, AOT-safe (no reflection).
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
    /// <para>判断目录是否「不存在或为空」——新建解决方案/子项目要求目标目录全新。</para>
    /// Whether the directory is missing or empty — new solutions/sub-projects require a fresh directory.
    /// </summary>
    /// <param name="path"><para>目录绝对路径；空白视为不合法（false）。</para>Absolute directory path; blank is invalid (false).</param>
    /// <returns><para>目录不存在或不含任何条目时 true。</para>True when the directory does not exist or has no entries.</returns>
    public static bool IsDirectoryEmptyOrMissing(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!Directory.Exists(path))
        {
            return true;
        }

        try
        {
            return !Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception)
        {
            // 目录不可读视为不满足「空目录」要求。
            // An unreadable directory does not satisfy the empty-directory requirement.
            return false;
        }
    }

    /// <summary>
    /// <para>在基础目录下探测第一个空闲的默认名称（NewProject1、NewProject2…）；
    /// <paramref name="reservedNames"/> 中的名称（如已存在的子项目）同样视为占用。</para>
    /// Probes the first free default name (NewProject1, NewProject2, …) under the base directory;
    /// names in <paramref name="reservedNames"/> (e.g. existing sub-projects) are treated as taken.
    /// </summary>
    /// <param name="baseDirectory"><para>探测的基础目录（可以不存在）。</para>Base directory to probe (may not exist).</param>
    /// <param name="reservedNames">
    /// <para>额外占用的名称集合（不区分大小写）；可为空。</para>
    /// Additional taken names (case-insensitive); may be null.
    /// </param>
    /// <returns><para>第一个空闲名称。</para>The first free name.</returns>
    public static string GetNextDefaultName(string baseDirectory, IEnumerable<string>? reservedNames = null)
    {
        var reserved = new HashSet<string>(
            reservedNames ?? [],
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        for (var i = 1; i <= 9999; i++)
        {
            var candidate = $"NewProject{i}";
            if (reserved.Contains(candidate))
            {
                continue;
            }

            var fullPath = string.IsNullOrWhiteSpace(baseDirectory) ? candidate : Path.Combine(baseDirectory, candidate);
            if (!Directory.Exists(fullPath))
            {
                return candidate;
            }
        }

        // 兜底：加时间戳后缀，保证唯一。
        // Fallback: timestamp suffix guarantees uniqueness.
        return $"NewProject_{DateTime.Now:yyyyMMddHHmmss}";
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

            if (header.Length >= 4100
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4096, 4))
                    == MetadataFormat.LP_METADATA_GEOMETRY_MAGIC)
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
        var info = UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.PayloadParser.GetPartitionInfo(filePath);
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

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (entry.FullName.EndsWith(PayloadMarker, StringComparison.OrdinalIgnoreCase))
            {
                hasPayload = true;
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
            var info = UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.PayloadParser.GetPartitionInfo(filePath);
            entries.AddRange(info.Select(p => new ContainerEntry(p.Name, "payload", (long)p.SizeBytes, FromPayload: true)));
        }

        return new ContainerStructure(ContainerKind.Zip, entries);
    }
}
