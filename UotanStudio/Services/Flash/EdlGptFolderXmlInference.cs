using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Recognises a Qualcomm flat firmware folder that carries partition-table backups instead of manifests and builds
/// the per-LUN XML package from those files offline: each <c>gpt_main&lt;lun&gt;.bin</c> is parsed into a table, the
/// same rawprogram/patch/partition documents the device-based writer produces are generated from it, and every
/// program row is bound to the image file that exists beside it.
/// <para>识别携带分区表备份而非清单的高通平铺固件目录，并离线用这些文件构建每 LUN 的 XML 包：把每个
/// <c>gpt_main&lt;lun&gt;.bin</c> 解析成表，生成与设备侧写入器相同的 rawprogram/patch/partition 文档，
/// 并把每条 program 条目绑定到其旁边实际存在的镜像文件。</para>
/// </summary>
internal static class EdlGptFolderXmlInference
{
    /// <summary>File-name patterns accepted as a region's primary GPT backup; the capture is the LUN number.
    /// <para>被接受为区域主 GPT 备份的文件名模式；捕获组是 LUN 号。</para></summary>
    private static readonly Regex PrimaryGptName = new(
        @"^gpt_main(\d*)\.bin$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Image extensions tried, in order, when binding a partition row to an existing file.
    /// <para>把分区条目绑定到现有文件时按顺序尝试的镜像扩展名。</para></summary>
    private static readonly string[] ImageExtensions = [".img", ".bin", ".ext4", ".sparse"];

    /// <summary>Partition labels whose presence marks a folder as Qualcomm-specific firmware; generic labels such
    /// as <c>boot</c> or <c>system</c> are excluded on purpose.
    /// <para>其存在标志着目录为高通专属固件的分区标签；刻意排除 <c>boot</c>、<c>system</c> 等通用标签。</para></summary>
    private static readonly string[] QualcommSignatureLabels =
    [
        "abl", "xbl", "xbl_config", "tz", "hyp", "devcfg", "keymaster", "cmnlib", "cmnlib64",
        "imagefv", "uefisecapp", "multiimgoem", "multiimgqti", "aop", "qupfw", "storsec", "mdtp"
    ];

    /// <summary>
    /// Reports whether the folder carries at least one primary GPT backup and one Qualcomm-signature partition
    /// image, the two markers of an inferable flat layout.
    /// <para>报告目录是否至少含有一份主 GPT 备份与一个高通特征分区镜像，即可推断平铺布局的两个标记。</para>
    /// </summary>
    /// <param name="folder">The folder to inspect. <para>要检查的目录。</para></param>
    /// <returns><see langword="true"/> when both markers are present. <para>两个标记都在时为 <see langword="true"/>。</para></returns>
    public static bool HasInferableLayout(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Directory.Exists(folder) || FindPrimaryGptFiles(folder).Count == 0)
        {
            return false;
        }

        var stems = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToArray();
        return stems.Any(stem => QualcommSignatureLabels.Any(label =>
            stem.StartsWith(label, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Builds the per-LUN XML package from the folder's partition-table files, writing the generated manifests
    /// into the folder itself and binding every partition row to an existing image file when one matches.
    /// <para>从目录内的分区表文件构建每 LUN 的 XML 包，把生成的清单写入该目录本身，并在有匹配镜像时把每条
    /// 分区条目绑定到现有文件。</para>
    /// </summary>
    /// <param name="folder">The flat firmware folder; the generated files land inside it.
    /// <para>平铺固件目录；生成的文件落在其中。</para></param>
    /// <param name="cancellationToken">Cancels the parse and the writes. <para>取消解析与写文件。</para></param>
    /// <returns>The generated package set, or <see langword="null"/> when no table file parsed into a usable layout.
    /// <para>生成的包集合；没有任何表文件解析出可用布局时为 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentNullException">The folder path is null. <para>目录路径为空。</para></exception>
    public static async Task<EdlGptXmlPackageSet?> TryBuildAsync(string folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var primaries = FindPrimaryGptFiles(folder);
        if (primaries.Count == 0)
        {
            return null;
        }

        var packages = new List<EdlGptXmlPackage>(primaries.Count);
        foreach (var (lun, primaryPath) in primaries)
        {
            EdlGptXmlPackage? package;
            try
            {
                package = await BuildLunPackageAsync(folder, lun, primaryPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is InvalidDataException or IOException
                or InvalidOperationException or ArgumentException)
            {
                continue;
            }

            if (package is not null)
            {
                packages.Add(package);
            }
        }

        return packages.Count == 0 ? null : new EdlGptXmlPackageSet(folder, packages);
    }

    /// <summary>
    /// Collects the primary GPT backup files as LUN-numbered pairs, accepting the unnumbered name as LUN zero.
    /// <para>把主 GPT 备份文件收集为按 LUN 编号的配对；无编号名按 LUN 零接受。</para>
    /// </summary>
    /// <param name="folder">The folder to scan. <para>要扫描的目录。</para></param>
    /// <returns>LUN-to-file pairs ordered by LUN. <para>按 LUN 排序的 LUN 与文件对。</para></returns>
    private static List<(int Lun, string Path)> FindPrimaryGptFiles(string folder)
    {
        var found = new List<(int Lun, string Path)>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.bin", SearchOption.TopDirectoryOnly))
        {
            var match = PrimaryGptName.Match(Path.GetFileName(path));
            if (!match.Success)
            {
                continue;
            }

            var lun = match.Groups[1].Value.Length == 0 ? 0 : int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            found.Add((lun, path));
        }

        found.Sort((left, right) => left.Lun.CompareTo(right.Lun));
        return found;
    }

    /// <summary>
    /// Parses one region's primary GPT file and writes that LUN's manifests, binding the partition rows and the two
    /// GPT rows to the files that exist in the folder. The GPT header carries no sector-size field, so the sector
    /// size is derived from the header's own file offset divided by its declared current-LBA value — the way the
    /// Qualcomm toolchain infers it — and every LBA then comes from the header and the entry array directly. The two
    /// GPT blob spans come from the files' own lengths, so the program and patch rows name the same extents the
    /// folder actually carries.
    /// <para>解析一个区域的主 GPT 文件并写出该 LUN 的清单，把分区条目与两条 GPT 条目绑定到目录内存在的文件。
    /// GPT 头本身不含扇区大小字段，因此按头部自身文件偏移除以其声明的当前 LBA 值推导扇区大小——与高通工具链的
    /// 推断方式一致；其后每个 LBA 都直接取自头部与条目数组。两份 GPT 镜像的跨度取自文件自身长度，使 program 与
    /// patch 条目描述的正是目录实际携带的范围。</para>
    /// </summary>
    /// <param name="folder">The package folder. <para>包目录。</para></param>
    /// <param name="lun">The region number the file addresses. <para>该文件寻址的区域号。</para></param>
    /// <param name="primaryPath">The primary GPT file. <para>主 GPT 文件。</para></param>
    /// <param name="cancellationToken">Cancels the writes. <para>取消写文件。</para></param>
    /// <returns>The written package, or <see langword="null"/> when the file holds no usable table.
    /// <para>已写出的包；文件没有可用表时为 <see langword="null"/>。</para></returns>
    private static async Task<EdlGptXmlPackage?> BuildLunPackageAsync(string folder, int lun, string primaryPath,
        CancellationToken cancellationToken)
    {
        var tableBytes = await File.ReadAllBytesAsync(primaryPath, cancellationToken).ConfigureAwait(false);
        if (!TryParsePrimaryTable(tableBytes, out var sectorSize, out var diskSectors, out var lastUsableLba,
                out var entryCount, out var entrySize, out var primaryArrayLba, out var backupArrayLba,
                out var partitions))
        {
            return null;
        }

        var backupPath = Path.Combine(folder, FormattableString.Invariant($"gpt_backup{lun}.bin"));
        var backupName = File.Exists(backupPath)
            ? Path.GetFileName(backupPath)
            : FormattableString.Invariant($"gpt_backup{lun}.bin");
        var primaryBlobSectors = Math.Max(1, tableBytes.Length / sectorSize);
        var arraySectors = ((long)entryCount * entrySize + sectorSize - 1) / sectorSize;
        var backupBlobSectors = File.Exists(backupPath)
            ? Math.Max(arraySectors + 1, new FileInfo(backupPath).Length / sectorSize)
            : arraySectors + 1;
        var lastUsableOffset = diskSectors > 0 && lastUsableLba > 0 && (long)lastUsableLba < diskSectors
            ? diskSectors - (long)lastUsableLba
            : primaryBlobSectors;
        var layout = new EdlGptXmlLayout(lun, sectorSize, diskSectors, primaryBlobSectors, backupBlobSectors,
            lastUsableOffset, entryCount, entrySize, primaryArrayLba, backupArrayLba, partitions);

        var rawProgramPath = Path.Combine(folder, FormattableString.Invariant($"rawprogram{lun}.xml"));
        var patchPath = Path.Combine(folder, FormattableString.Invariant($"patch{lun}.xml"));
        var partitionPath = Path.Combine(folder, FormattableString.Invariant($"partition{lun}.xml"));
        var imageBinding = BindImage(folder);
        await File.WriteAllTextAsync(rawProgramPath,
            EdlGptXmlPackageWriter.BuildRawProgram(layout, imageBinding, Path.GetFileName(primaryPath),
                backupName), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(patchPath,
            EdlGptXmlPackageWriter.BuildPatch(layout, Path.GetFileName(primaryPath), backupName),
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(partitionPath,
            EdlGptXmlPackageWriter.BuildPartition(layout, imageBinding), new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        return new EdlGptXmlPackage(folder, rawProgramPath, patchPath, partitionPath, primaryPath, backupPath, layout);
    }

    /// <summary>Byte offset of the current-LBA field inside the GPT header.
    /// <para>GPT 头内当前 LBA 字段的字节偏移。</para></summary>
    private const int HeaderCurrentLbaOffset = 24;

    /// <summary>Byte offset of the last-usable-LBA field inside the GPT header.
    /// <para>GPT 头内最后可用 LBA 字段的字节偏移。</para></summary>
    private const int HeaderLastUsableOffset = 48;

    /// <summary>Byte offset of the entry-array location field inside the GPT header.
    /// <para>GPT 头内条目数组位置字段的字节偏移。</para></summary>
    private const int HeaderEntryArrayOffset = 72;

    /// <summary>Byte size of one GUID. <para>单个 GUID 的字节长度。</para></summary>
    private const int GuidBytes = 16;

    /// <summary>
    /// Parses a primary GPT backup file against the real sector size derived from the header's own position: the
    /// header's byte offset divided by its declared current LBA is the region's sector size, and every other field
    /// is read straight from the header and from the entry array at <c>entryLba * sectorSize</c>.
    /// <para>按头部自身位置推导的真实扇区大小解析主 GPT 备份文件：头部字节偏移除以其声明的当前 LBA 即区域扇区
    /// 大小，其余字段直接取自头部与 <c>entryLba * sectorSize</c> 处的条目数组。</para>
    /// </summary>
    /// <param name="bytes">The primary GPT file bytes. <para>主 GPT 文件字节。</para></param>
    /// <param name="sectorSize">The derived region sector size. <para>推导出的区域扇区大小。</para></param>
    /// <param name="diskSectors">The region's total sector count. <para>区域总扇区数。</para></param>
    /// <param name="lastUsableLba">The header's last usable LBA. <para>头部声明的最后可用 LBA。</para></param>
    /// <param name="entryCount">The declared entry-array capacity. <para>声明的条目数组容量。</para></param>
    /// <param name="entrySize">The declared single-entry size. <para>声明的单条目字节数。</para></param>
    /// <param name="primaryArrayLba">The primary entry-array LBA. <para>主条目数组 LBA。</para></param>
    /// <param name="backupArrayLba">The backup entry-array LBA derived from the disk size and the array span.
    /// <para>按磁盘大小与数组跨度推导的备份条目数组 LBA。</para></param>
    /// <param name="partitions">The table's named entries in table order, including the zero-length grow placeholder
    /// and zero-length partitions such as <c>userdata</c>; unused slots are skipped.
    /// <para>表中按顺序排列的具名条目，包含零长度 grow 占位以及 <c>userdata</c> 这类零长度分区；未使用的槽位被跳过。</para></param>
    /// <returns><see langword="true"/> when the header validated and the array parsed.
    /// <para>头部校验通过且条目数组解析成功时为 <see langword="true"/>。</para></returns>
    private static bool TryParsePrimaryTable(ReadOnlySpan<byte> bytes, out long sectorSize, out long diskSectors,
        out ulong lastUsableLba, out uint entryCount, out uint entrySize, out long primaryArrayLba,
        out long backupArrayLba, out IReadOnlyList<GptPartition> partitions)
    {
        sectorSize = 0;
        diskSectors = 0;
        lastUsableLba = 0;
        entryCount = 0;
        entrySize = 0;
        primaryArrayLba = 0;
        backupArrayLba = 0;
        partitions = [];
        var headerOffset = GptImageEditor.FindHeaderOffset(bytes);
        if (headerOffset < 0 || headerOffset + 92 > bytes.Length)
        {
            return false;
        }

        var header = bytes[(int)headerOffset..];
        var currentLba = BinaryPrimitives.ReadUInt64LittleEndian(header[HeaderCurrentLbaOffset..]);
        var alternateLba = BinaryPrimitives.ReadUInt64LittleEndian(header[32..]);
        if (currentLba == 0 || headerOffset % (long)currentLba != 0)
        {
            return false;
        }

        sectorSize = headerOffset / (long)currentLba;
        if (sectorSize < 512 || sectorSize > 65536)
        {
            return false;
        }

        lastUsableLba = BinaryPrimitives.ReadUInt64LittleEndian(header[HeaderLastUsableOffset..]);
        entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
        entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header[84..]);
        primaryArrayLba = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(header[HeaderEntryArrayOffset..]));
        diskSectors = alternateLba > 0 ? checked((long)alternateLba + 1) : 0;
        if (entryCount == 0 || entrySize is 0 or > 1024 || primaryArrayLba <= 0 ||
            primaryArrayLba * sectorSize + entryCount * entrySize > bytes.Length)
        {
            return false;
        }

        var array = new List<GptPartition>();
        for (var index = 0; index < entryCount; index++)
        {
            var entryOffset = checked((int)(primaryArrayLba * sectorSize + index * entrySize));
            if (entryOffset + entrySize > bytes.Length)
            {
                break;
            }

            var entry = bytes.Slice(entryOffset, (int)entrySize);
            var typeId = new Guid(entry.Slice(0, GuidBytes));
            var firstLba = BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]);
            var lastLba = BinaryPrimitives.ReadUInt64LittleEndian(entry[40..]);
            var name = Encoding.Unicode.GetString(entry[56..128]).TrimEnd('\0').Trim();
            if (firstLba == 0 || name.Length == 0 || (typeId == Guid.Empty && lastLba >= firstLba))
            {
                // Unused slots stay empty; the Qualcomm grow placeholder carries a zero type GUID and a zero range.
                // 未使用的槽位保持为空；高通 grow 占位携带零类型 GUID 与零长度范围。
                continue;
            }

            var sectors = lastLba >= firstLba ? lastLba - firstLba + 1 : 0;
            if (diskSectors > 0 && sectors > (ulong)diskSectors)
            {
                continue;
            }

            var partitionId = new Guid(entry.Slice(GuidBytes, GuidBytes));
            var attributes = BinaryPrimitives.ReadUInt64LittleEndian(entry[48..]);
            array.Add(new GptPartition(index, typeId, partitionId, firstLba, lastLba, attributes, name,
                checked((long)(firstLba * (ulong)sectorSize)),
                checked((long)(sectors * (ulong)sectorSize))));
        }

        partitions = array;
        return array.Count > 0;
    }

    /// <summary>
    /// Binds one partition row to the image file that exists beside it in the folder, or returns
    /// <see langword="null"/> so the row keeps its placeholder name.
    /// <para>把分区条目绑定到目录内其旁边存在的镜像文件；无匹配时返回 <see langword="null"/>，
    /// 使条目保留占位名。</para>
    /// </summary>
    /// <param name="folder">The package folder. <para>包目录。</para></param>
    /// <returns>The binding function. <para>绑定函数。</para></returns>
    private static Func<GptPartition, string?> BindImage(string folder)
    {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(file => ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return partition =>
        {
            foreach (var stem in BindingStems(partition.Name))
            {
                var match = files.FirstOrDefault(file =>
                    string.Equals(Path.GetFileNameWithoutExtension(file), stem, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }

            return null;
        };
    }

    /// <summary>
    /// Yields the stems an image file may use for one table label: the label itself first, then the label with its
    /// <c>_a</c>/<c>_b</c> slot suffix removed so both slots bind the single unsuffixed image the vendor ships.
    /// <para>依次给出某表标签可用的镜像词干：先是标签本身，再是去掉 <c>_a</c>/<c>_b</c> 槽位后缀的标签，使两个槽位
    /// 都绑定到厂商提供的同一个无后缀镜像。</para>
    /// </summary>
    /// <param name="label">The table label. <para>表标签。</para></param>
    /// <returns>The candidate stems in match order. <para>按匹配顺序排列的候选词干。</para></returns>
    private static IEnumerable<string> BindingStems(string label)
    {
        yield return label;
        if (label.Length > 2 && label[^2] == '_' && char.ToLowerInvariant(label[^1]) is 'a' or 'b')
        {
            yield return label[..^2];
        }
    }
}