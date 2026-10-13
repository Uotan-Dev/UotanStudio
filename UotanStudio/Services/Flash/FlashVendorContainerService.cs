using UotanStudio.Firmware.VendorFirmware.Nb0;
using UotanStudio.Firmware.VendorFirmware.NTPi;

namespace UotanStudio.Services.Flash;

/// <summary>Lists and selectively extracts NB0 and NTPI image entries.
/// <para>列举并选择性提取 NB0 与 NTPI 镜像条目。</para></summary>
public static class FlashVendorContainerService
{
    /// <summary>Reads image entries with unambiguous partition-like filenames.
    /// <para>读取文件名可无歧义对应分区的镜像条目。</para></summary>
    /// <param name="path">Path of the container file to inspect. <para>要检查的容器文件路径。</para></param>
    /// <param name="ct">Cancellation for parsing. <para>解析过程的取消令牌。</para></param>
    /// <returns>Partition-like name, entry path and size for each image.
    /// <para>每个镜像的类分区名、条目路径与大小。</para></returns>
    public static async Task<IReadOnlyList<(string Name, string Entry, long Size)>> ListAsync(
        string path, CancellationToken ct)
    {
        IEnumerable<(string Entry, long Size)> entries;
        if (Path.GetExtension(path).Equals(".nb0", StringComparison.OrdinalIgnoreCase))
        {
            var metadata = await Nb0Parser.ParseAsync(path, ct).ConfigureAwait(false);
            entries = [.. metadata.Entries.Select(entry => (entry.Name, entry.Size))];
        }
        else
        {
            entries = await Task.Run(() =>
            {
                using var reader = NtpiReader.CreateDefault();
                return reader.ReadInfo(path).FileEntries.Select(entry => (entry.Name, entry.OriginalLength)).ToArray();
            }, ct).ConfigureAwait(false);
        }

        var images = entries.Where(entry => entry.Entry.EndsWith(".img", StringComparison.OrdinalIgnoreCase) &&
            entry.Size >= 0 && FlashManifestReader.IsPartitionName(Path.GetFileNameWithoutExtension(entry.Entry)) &&
            Path.GetFileName(entry.Entry) == entry.Entry)
            .Select(entry => (Name: Path.GetFileNameWithoutExtension(entry.Entry), entry.Entry, entry.Size)).ToArray();
        if (images.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != images.Length)
        {
            throw new InvalidDataException("Container has duplicate partition image entries.");
        }
        return images;
    }

    /// <summary>Publishes selected original entries only when every requested image exists.
    /// <para>仅当每个请求的镜像都存在时发布所选原始条目。</para></summary>
    /// <param name="path">Path of the container file to unpack. <para>要解包的容器文件路径。</para></param>
    /// <param name="entryNames">Requested image entry names. <para>请求的镜像条目名。</para></param>
    /// <param name="parent">Existing parent directory for the output. <para>输出所在的已存在父目录。</para></param>
    /// <param name="ct">Cancellation for extraction and publishing. <para>提取与发布过程的取消令牌。</para></param>
    /// <returns>Path of the newly published output directory. <para>新发布输出目录的路径。</para></returns>
    public static async Task<string> ExtractAsync(string path, IReadOnlyList<string> entryNames, string parent, CancellationToken ct)
    {
        if (entryNames.Count == 0 || entryNames.Any(name => !name.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(name) != name || !FlashManifestReader.IsPartitionName(Path.GetFileNameWithoutExtension(name))))
        {
            throw new ArgumentException("Select valid image entries.", nameof(entryNames));
        }
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);

        var output = Path.Combine(parent, Path.GetFileName(path) + "-unpack-" + Guid.NewGuid().ToString("N")[..8]);
        var staging = output + ".tmp";
        var selected = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Path.GetExtension(path).Equals(".nb0", StringComparison.OrdinalIgnoreCase))
            {
                var result = await new Nb0Extractor().ExtractAsync(path, staging,
                    new ExtractionOptions { Overwrite = false, FileFilter = selected.Contains },
                    cancellationToken: ct).ConfigureAwait(false);
                if (!result.Success) throw new InvalidDataException(string.Join("; ", result.Errors));
            }
            else
            {
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    using var reader = NtpiReader.CreateDefault();
                    reader.Unpack(path, staging, fileFilter: selected.Contains);
                }, ct).ConfigureAwait(false);
            }
            foreach (var name in selected)
            {
                if (!File.Exists(Path.Combine(staging, name)))
                {
                    throw new InvalidDataException($"Container did not produce {name}.");
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