using UotanStudio.Comm.Protocols.Edl.FlashBundle;

namespace UotanStudio.Services.Flash;

/// <summary>Inspects a flat Qualcomm image directory without inferring EDL addresses from filenames.</summary>
public static class FlashBinPackage
{
    /// <summary>Returns ordinary partition images, with rawprogram entries used for EDL when present.</summary>
    /// <remarks>
    /// <c>Programmer</c> is only the unambiguous candidate; packages that ship several
    /// <c>prog_*firehose*.elf</c> loaders report every candidate in <c>Programmers</c> so the caller can select
    /// one instead of losing the programmer entirely.
    /// </remarks>
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

        var manifests = Directory.EnumerateFiles(directory, "rawprogram*.xml", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var programs = manifests.Length == 0 ? [] : RawProgramParser.ParseAll(manifests, directory);
        var programmers = Directory.EnumerateFiles(directory, "prog_*firehose*.elf", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        return (images, programs, programmers.Length == 1 ? programmers[0] : null, programmers);
    }
}