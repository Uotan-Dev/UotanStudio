using UotanStudio.Comm.Protocols.Edl.FlashBundle;

namespace UotanStudio.Services.Flash;

/// <summary>Inspects a flat Qualcomm image directory without inferring EDL addresses from filenames.<para>检查扁平的高通镜像目录，不从文件名推断 EDL 地址。</para></summary>
public static class FlashBinPackage
{
    /// <summary>Returns ordinary partition images, with rawprogram entries used for EDL when present.<para>返回常规分区镜像，并在存在时附带用于 EDL 的 rawprogram 条目。</para></summary>
    /// <param name="folder">Directory holding the images and rawprogram manifests.<para>存放镜像与 rawprogram 清单的目录。</para></param>
    /// <returns>The images, rawprogram entries, sole programmer candidate and all candidates.<para>镜像、rawprogram 条目、唯一的 programmer 候选以及全部候选。</para></returns>
    public static (IReadOnlyList<(string Partition, string Path, long Length)> Images,
        IReadOnlyList<RawProgramEntry> Programs, string? Programmer, IReadOnlyList<string> Programmers) Read(string folder)
    {
        var directory = Directory.Exists(Path.Combine(folder, "images"))
            ? Path.Combine(folder, "images") : folder;
        var images = new List<(string Partition, string Path, long Length)>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.bin", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length is < 1 or > 128 ||
                !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ||
                name.StartsWith("gpt_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("ALIGN_TO_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var length = new FileInfo(path).Length;
            if (length > 0)
            {
                images.Add((name, path, length));
            }
        }

        var manifests = FlashManifestSelection.Enumerate(directory);
        var programs = manifests.Length == 0 ? [] : RawProgramParser.ParseAll(manifests, directory);
        var programmers = Directory.EnumerateFiles(directory, "prog_*firehose*.elf", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        return (images, programs, programmers.Length == 1 ? programmers[0] : null, programmers);
    }
}