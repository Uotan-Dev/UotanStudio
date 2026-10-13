using UotanStudio.Firmware.OtaDelta.Payload;

namespace UotanStudio.Services.Flash;

/// <summary>Lists and extracts images from a local Payload container.<para>从本地 Payload 容器中列举并解压镜像。</para></summary>
public static class FlashPayloadService
{
    /// <summary>Reads partition metadata without materializing their image data.<para>只读取分区元数据，不展开镜像数据。</para></summary>
    /// <param name="path">Payload file to read.<para>要读取的 Payload 文件。</para></param>
    /// <param name="ct">Token cancelling the read.<para>用于取消读取的令牌。</para></param>
    /// <returns>The accepted partition names and declared sizes.<para>被接受的分区名与声明的大小。</para></returns>
    public static async Task<IReadOnlyList<(string Name, ulong Size)>> ListAsync(string path, CancellationToken ct)
    {
        var info = await PayloadParser.GetPartitionInfoAsync(path, ct).ConfigureAwait(false);
        return [.. info.Where(partition => partition.Name.Length is > 0 and <= 128 &&
                                       partition.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            .Select(partition => (partition.Name, partition.SizeBytes))];
    }

    /// <summary>Publishes selected images as a new directory only after extraction succeeds.<para>解压成功后才把所选镜像发布为新目录。</para></summary>
    /// <param name="path">Payload file to extract from.<para>要从中解压的 Payload 文件。</para></param>
    /// <param name="partitions">Partition names to extract.<para>要解压的分区名。</para></param>
    /// <param name="parent">Existing directory that receives the new output directory.<para>接收新输出目录的既有目录。</para></param>
    /// <param name="ct">Token cancelling the extraction.<para>用于取消解压的令牌。</para></param>
    /// <returns>Path of the directory holding the extracted images.<para>存放解压镜像的目录路径。</para></returns>
    public static async Task<string> ExtractAsync(string path, IReadOnlyList<string> partitions, string parent, CancellationToken ct)
    {
        if (partitions.Count == 0 || partitions.Any(name => name.Length is < 1 or > 128 ||
            !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))
        {
            throw new ArgumentException("Select valid partitions before extraction.", nameof(partitions));
        }

        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(parent);
        }

        var output = Path.Combine(parent, Path.GetFileName(path) + "-unpack-" + Guid.NewGuid().ToString("N")[..8]);
        var staging = output + ".tmp";
        try
        {
            Directory.CreateDirectory(staging);
            await PayloadParser.ExtractAsync(path, [.. partitions], staging, ct).ConfigureAwait(false);
            foreach (var name in partitions)
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
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    /// <summary>Builds a publish-on-success Fastbootd package with one matching TXT plan.<para>构建成功才发布的 Fastbootd 包，并附上同组的 TXT 计划。</para></summary>
    /// <param name="path">Payload file supplying the images.<para>提供镜像的 Payload 文件。</para></param>
    /// <param name="partitions">Distinct partition names to include.<para>要包含的去重分区名。</para></param>
    /// <param name="parent">Existing directory that receives the new package.<para>接收新包的既有目录。</para></param>
    /// <param name="ct">Token cancelling the build.<para>用于取消构建的令牌。</para></param>
    /// <returns>Path of the package directory.<para>包目录的路径。</para></returns>
    public static async Task<string> BuildPackageAsync(string path, IReadOnlyList<string> partitions, string parent, CancellationToken ct)
    {
        if (partitions.Count == 0 || partitions.Any(name => !FlashManifestReader.IsPartitionName(name)) ||
            partitions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != partitions.Count)
        {
            throw new ArgumentException("The package needs distinct, valid partition names.", nameof(partitions));
        }
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);

        var output = Path.Combine(parent, Path.GetFileName(path) + "-fastbootd-" + Guid.NewGuid().ToString("N")[..8]);
        var staging = output + ".tmp";
        try
        {
            var images = Path.Combine(staging, "images");
            Directory.CreateDirectory(images);
            await PayloadParser.ExtractAsync(path, [.. partitions], images, ct).ConfigureAwait(false);
            foreach (var name in partitions)
            {
                if (!File.Exists(Path.Combine(images, name + ".img")))
                {
                    throw new InvalidDataException($"Package is missing {name}.img.");
                }
            }
            await File.WriteAllLinesAsync(Path.Combine(staging, "flash_fastbootd.txt"),
                ["mode:fastbootd", .. partitions], ct).ConfigureAwait(false);
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