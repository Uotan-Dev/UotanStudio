using UotanStudio.Firmware.VendorFirmware.Oppo;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;

namespace UotanStudio.Services.Flash;

/// <summary>Lists and selectively extracts image entries from Oppo firmware archives.<para>列出 Oppo 固件包中的镜像条目并按选择解压。</para></summary>
public static class FlashOppoContainerService
{
    /// <summary>Returns unambiguous image entries, preserving their original archive names.<para>返回不存在歧义的镜像条目，并保留其在包中的原名。</para></summary>
    /// <param name="path">Archive file to inspect.<para>要检查的固件包文件。</para></param>
    /// <param name="ct">Token cancelling the listing.<para>用于取消列举的令牌。</para></param>
    /// <returns>The image name, archive entry, output name and size.<para>镜像名、包内条目、输出文件名与大小。</para></returns>
    public static async Task<IReadOnlyList<(string Name, string Entry, string OutputName, long Size)>> ListAsync(
        string path, CancellationToken ct)
    {
        var archive = await new OppReader().ParseAsync(path, ct).ConfigureAwait(false);
        var images = archive.Entries.Where(entry => IsSafeEntry(entry.Name) && entry.Size > 0)
            .Select(entry => (
                Name: Path.GetFileNameWithoutExtension(entry.Name),
                Entry: entry.Name,
                OutputName: entry.Name.Replace('/', '_'),
                entry.Size)).ToArray();
        if (images.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != images.Length ||
            images.Select(entry => entry.OutputName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != images.Length)
        {
            throw new InvalidDataException("Oppo archive contains ambiguous image entries.");
        }
        return images;
    }

    /// <summary>Publishes only selected images after verifying every extracted output.<para>校验全部解压结果后才发布所选镜像。</para></summary>
    /// <param name="path">Archive file to extract from.<para>要从中解压的固件包文件。</para></param>
    /// <param name="entries">Archive entry names to extract.<para>要解压的包内条目名。</para></param>
    /// <param name="parent">Existing directory that receives the new output directory.<para>接收新输出目录的既有目录。</para></param>
    /// <param name="ct">Token cancelling the extraction.<para>用于取消解压的令牌。</para></param>
    /// <returns>Path of the directory holding the published images.<para>存放已发布镜像的目录路径。</para></returns>
    public static async Task<string> ExtractAsync(string path, IReadOnlyList<string> entries, string parent, CancellationToken ct)
    {
        if (entries.Count == 0 || entries.Any(entry => !IsSafeEntry(entry)))
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
            Directory.Move(staging, output);
            return output;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static bool IsSafeEntry(string entry)
    {
        if (!entry.EndsWith(".img", StringComparison.OrdinalIgnoreCase) || entry.Contains('\\') ||
            !FlashManifestReader.IsPartitionName(Path.GetFileNameWithoutExtension(entry)))
        {
            return false;
        }

        return entry.Split('/').All(segment => segment.Length > 0 && segment is not ("." or "..") &&
            segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'));
    }
}