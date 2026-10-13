using UotanStudio.Firmware.BlockImage.Lp;

namespace UotanStudio.Services.Flash;

/// <summary>Reads and extracts selected partitions from raw or sparse Super images.
/// <para>从原始或稀疏 Super 镜像读取并提取所选分区。</para></summary>
public static class FlashSuperService
{
    /// <summary>Returns partitions and their logical sizes from validated LP metadata.
    /// <para>从已校验的 LP 元数据返回分区及其逻辑大小。</para></summary>
    /// <param name="path">Path of the Super image to inspect. <para>要检查的 Super 镜像路径。</para></param>
    /// <param name="ct">Cancellation for the metadata walk. <para>元数据遍历的取消令牌。</para></param>
    /// <returns>Partition names with their logical byte sizes. <para>分区名及其逻辑字节大小。</para></returns>
    public static Task<IReadOnlyList<(string Name, long Size)>> ListAsync(string path, CancellationToken ct)
        => Task.Run<IReadOnlyList<(string Name, long Size)>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var metadata = MetadataReader.ReadFromImageFile(path);
            var entries = new List<(string Name, long Size)>();
            foreach (var partition in metadata.Partitions)
            {
                ct.ThrowIfCancellationRequested();
                var name = partition.NameString;
                if (!FlashManifestReader.IsPartitionName(name))
                {
                    throw new InvalidDataException("Super image has an unsafe partition name.");
                }

                long size = 0;
                foreach (var extent in metadata.Extents.GetRange(
                    checked((int)partition.FirstExtentIndex), checked((int)partition.NumExtents)))
                {
                    size = checked(size + checked((long)extent.NumSectors * MetadataFormat.LP_SECTOR_SIZE));
                }
                entries.Add((name, size));
            }
            return entries;
        }, ct);

    /// <summary>Extracts only requested partitions and atomically publishes a new directory.
    /// <para>仅提取请求的分区，并原子发布一个新目录。</para></summary>
    /// <param name="path">Path of the Super image to unpack. <para>要解包的 Super 镜像路径。</para></param>
    /// <param name="names">Requested partition names. <para>请求的分区名。</para></param>
    /// <param name="parent">Existing parent directory for the output. <para>输出所在的已存在父目录。</para></param>
    /// <param name="ct">Cancellation for extraction and publishing. <para>提取与发布过程的取消令牌。</para></param>
    /// <returns>Path of the newly published output directory. <para>新发布输出目录的路径。</para></returns>
    public static async Task<string> ExtractAsync(string path, IReadOnlyList<string> names, string parent, CancellationToken ct)
    {
        if (names.Count == 0 || names.Any(name => !FlashManifestReader.IsPartitionName(name)))
        {
            throw new ArgumentException("Select valid partitions before extraction.", nameof(names));
        }
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);

        var output = Path.Combine(parent, Path.GetFileName(path) + "-unpack-" + Guid.NewGuid().ToString("N")[..8]);
        var staging = output + ".tmp";
        try
        {
            var result = await new LpFormatHandler().ExtractSelectedAsync(path, staging, names, ct).ConfigureAwait(false);
            if (!result.Success) throw new InvalidDataException(result.Summary);
            foreach (var name in names)
            {
                if (!File.Exists(Path.Combine(staging, name + ".img")))
                {
                    throw new InvalidDataException($"Partition extraction did not produce {name}.img.");
                }
            }
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, output);
            return output;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}