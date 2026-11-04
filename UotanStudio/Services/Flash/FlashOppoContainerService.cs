using UotanStudio.Firmware.VendorFirmware.MediaTek;
using UotanStudio.Firmware.VendorFirmware.Oppo;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Lists and selectively extracts image entries from Oppo/Realme firmware archives. Qualcomm-style archives
/// carry <c>.img</c> image entries; MediaTek-style archives name their partition images after the scatter
/// partitions and are mapped through the archive's <c>scatter</c> entry.
/// <para>列出欧加/realme 固件包的镜像条目并按选择解压。高通风格包的镜像条目带 <c>.img</c> 扩展名；
/// 联发科风格包的分区镜像按 scatter 分区命名，须经包内 <c>scatter</c> 条目映射。</para>
/// </summary>
public static class FlashOppoContainerService
{
    /// <summary>Entry whose text names the flashable partitions of a MediaTek-style archive.
    /// <para>联发科风格包中列出可刷写分区的条目名。</para></summary>
    private const string ScatterEntryName = "scatter";

    /// <summary>
    /// Returns unambiguous image entries, preserving their original archive names; MediaTek-style archives fall
    /// back to their scatter-driven partition images.
    /// <para>返回不存在歧义的镜像条目并保留其在包中的原名；联发科风格包回退到 scatter 驱动的分区镜像。</para>
    /// </summary>
    /// <param name="path">Archive file to inspect.<para>要检查的固件包文件。</para></param>
    /// <param name="ct">Token cancelling the listing.<para>用于取消列举的令牌。</para></param>
    /// <returns>The image name, archive entry, output name and size.<para>镜像名、包内条目、输出文件名与大小。</para></returns>
    /// <exception cref="InvalidDataException">The archive carries ambiguous image entries.
    /// <para>包内的镜像条目存在歧义。</para></exception>
    public static async Task<IReadOnlyList<(string Name, string Entry, string OutputName, long Size)>> ListAsync(
        string path, CancellationToken ct)
    {
        var archive = await new OppReader().ParseAsync(path, ct).ConfigureAwait(false);
        var images = ImageEntries(archive);
        if (images.Count == 0)
        {
            images = await ScatterEntriesAsync(path, archive, ct).ConfigureAwait(false);
        }
        if (images.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != images.Count ||
            images.Select(entry => entry.OutputName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != images.Count)
        {
            throw new InvalidDataException("Oppo archive contains ambiguous image entries.");
        }
        return images;
    }

    /// <summary>
    /// Publishes only selected images after verifying every extracted output.
    /// <para>校验全部解压结果后才发布所选镜像。</para>
    /// </summary>
    /// <param name="path">Archive file to extract from.<para>要从中解压的固件包文件。</para></param>
    /// <param name="entries">Archive entry names to extract.<para>要解压的包内条目名。</para></param>
    /// <param name="parent">Existing directory that receives the new output directory.<para>接收新输出目录的既有目录。</para></param>
    /// <param name="ct">Token cancelling the extraction.<para>用于取消解压的令牌。</para></param>
    /// <returns>Path of the directory holding the published images.<para>存放已发布镜像的目录路径。</para></returns>
    public static async Task<string> ExtractAsync(string path, IReadOnlyList<string> entries, string parent, CancellationToken ct)
    {
        if (entries.Count == 0 || entries.Any(entry => !IsFlatEntryName(entry)))
        {
            throw new ArgumentException("Select valid archive image entries.", nameof(entries));
        }
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);

        var output = Path.Combine(parent, Path.GetFileName(path) + "-unpack-" + Guid.NewGuid().ToString("N")[..8]);
        var staging = output + ".tmp";
        try
        {
            var result = await new OppReader().ExtractAsync(path, staging,
                new OppExtractOptions { SelectedEntries = entries, Overwrite = false },
                cancellationToken: ct).ConfigureAwait(false);
            if (result.ExtractedFileCount != entries.Count)
            {
                throw new InvalidDataException("Oppo archive did not extract every selected image.");
            }
            foreach (var entry in entries)
            {
                if (!File.Exists(Path.Combine(staging, entry.Replace('/', '_'))))
                {
                    throw new InvalidDataException($"Missing extracted image: {entry}.");
                }
            }
            ct.ThrowIfCancellationRequested();
            FlashStagingDirectory.Publish(staging, output);
            return output;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>
    /// Returns the Qualcomm-style image entries: <c>.img</c> archives members whose stem is a partition name.
    /// <para>返回高通风格的镜像条目：主干为分区名的 <c>.img</c> 包内成员。</para>
    /// </summary>
    /// <param name="archive">Parsed archive. <para>已解析的归档。</para></param>
    private static List<(string Name, string Entry, string OutputName, long Size)> ImageEntries(OppArchive archive)
        => archive.Entries.Where(entry => IsImageEntry(entry.Name) && entry.Size > 0)
            .Select(entry => (
                Name: Path.GetFileNameWithoutExtension(entry.Name),
                Entry: entry.Name,
                OutputName: entry.Name.Replace('/', '_'),
                entry.Size))
            .ToList();

    /// <summary>
    /// Maps a MediaTek-style archive's flashable scatter partitions onto its archive members; the scatter text
    /// is extracted to a temporary directory, parsed and joined with the members by partition name.
    /// <para>把联发科风格包中可刷写的 scatter 分区映射到包内成员；scatter 文本先解压到临时目录再解析，
    /// 并按分区名与包内成员连接。</para>
    /// </summary>
    /// <param name="path">Archive file path, used to extract the scatter entry.
    /// <para>固件包文件路径，用于解压 scatter 条目。</para></param>
    /// <param name="archive">Parsed archive. <para>已解析的归档。</para></param>
    /// <param name="ct">Token cancelling the mapping.<para>取消映射的令牌。</para></param>
    private static async Task<List<(string Name, string Entry, string OutputName, long Size)>> ScatterEntriesAsync(
        string path, OppArchive archive, CancellationToken ct)
    {
        var scatterEntry = archive.Entries.FirstOrDefault(entry =>
            entry.Name.Equals(ScatterEntryName, StringComparison.OrdinalIgnoreCase) && entry.Size > 0);
        if (scatterEntry is null)
        {
            return [];
        }

        var staging = Path.Combine(Path.GetTempPath(), "ofp-scatter-" + Guid.NewGuid().ToString("N"));
        try
        {
            _ = await new OppReader().ExtractAsync(path, staging,
                new OppExtractOptions { SelectedEntries = [scatterEntry.Name], Overwrite = false },
                cancellationToken: ct).ConfigureAwait(false);
            var scatterFile = Directory.EnumerateFiles(staging).Single();
            MtkScatterDocument document;
            using (var stream = File.OpenRead(scatterFile))
            {
                document = MtkScatterReader.Read(stream, ct);
            }

            var members = archive.Entries.Where(entry => entry.Size > 0)
                .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var rows = new List<(string Name, string Entry, string OutputName, long Size)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var storage in document.Storages)
            {
                foreach (var partition in storage.Partitions)
                {
                    if (!partition.IsDownload || !seen.Add(partition.Name) ||
                        !members.TryGetValue(partition.Name, out var member) || !IsFlatEntryName(member.Name))
                    {
                        continue;
                    }

                    rows.Add((partition.Name, member.Name, member.Name.Replace('/', '_'), member.Size));
                }
            }
            return rows;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>
    /// Whether an archive member is a Qualcomm-style partition image: an <c>.img</c> name whose stem is a
    /// partition name and whose path segments are flat and portable.
    /// <para>判断包内成员是否为高通风格分区镜像：<c>.img</c> 名且主干是分区名、路径段扁平可移植。</para>
    /// </summary>
    /// <param name="entry">Archive member name. <para>包内成员名。</para></param>
    private static bool IsImageEntry(string entry)
    {
        if (!entry.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ||
            !FlashManifestReader.IsPartitionName(Path.GetFileNameWithoutExtension(entry)))
        {
            return false;
        }

        return IsFlatEntryName(entry);
    }

    /// <summary>
    /// Whether an archive member name is a flat portable name without directory traversal.
    /// <para>判断包内成员名是否为不含目录穿越的扁平可移植名。</para>
    /// </summary>
    /// <param name="entry">Archive member name. <para>包内成员名。</para></param>
    private static bool IsFlatEntryName(string entry)
    {
        if (entry.Length == 0 || entry.Contains('\\'))
        {
            return false;
        }

        return entry.Split('/').All(segment => segment.Length > 0 && segment is not ("." or "..") &&
            segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'));
    }
}