using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Rebuilding;

namespace UotanStudio.Firmware.FileSystem.Extraction;

/// <summary>Maps image paths to portable host names without merging case aliases or treating Linux backslashes as separators.</summary>
internal sealed class PortableExtractionPaths
{
    internal static readonly Encoding NameEncoding = new UTF8Encoding(false, true);
    private const string Prefix = "uotan-name-";
    private readonly string _root;
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);

    internal PortableExtractionPaths(string root, IEnumerable<string> paths)
    {
        _root = root;
        var children = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var parent = "";
            foreach (var name in path.Split('/'))
            {
                if (name is "" or "." or ".." || name.Contains('\0')) throw new FileSystemCorruptedException("Invalid image directory entry.");
                if (!children.TryGetValue(parent, out var names)) children.Add(parent, names = new(StringComparer.Ordinal));
                names.Add(name);
                parent = parent.Length == 0 ? name : parent + "/" + name;
                if (children.Count > 100000) throw new FileSystemCorruptedException("Too many image directories.");
            }
        }
        foreach (var (parent, names) in children.OrderBy(pair => pair.Key.Count(c => c == '/')).ThenBy(pair => pair.Key.Length))
        {
            var aliases = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
                .SelectMany(g => g).ToHashSet(StringComparer.Ordinal);
            foreach (var name in names)
            {
                var stem = name.Split('.')[0];
                string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
                var encode = aliases.Contains(name) || name.Length > 120 || name.Any(c => c < 32 || "\\:*?\"<>|".Contains(c))
                    || name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                    || name.Equals(RepackMetadata.FileName, StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("_info", StringComparison.Ordinal) || name.EndsWith("_fs_config", StringComparison.Ordinal) || name.EndsWith("_file_contexts", StringComparison.Ordinal)
                    || reserved.Contains(stem, StringComparer.OrdinalIgnoreCase);
                var host = encode ? Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))) : name;
                var imagePath = parent.Length == 0 ? name : parent + "/" + name;
                _paths.Add(imagePath, parent.Length == 0 ? host : _paths[parent] + "/" + host);
                if (_paths.Count > 100000) throw new FileSystemCorruptedException("Too many image entries.");
            }
        }
        FileSystemPreservation.RecordPaths(_paths);
    }

    internal string Resolve(string imagePath) => FileExtraction.ResolvePath(_root, _paths[imagePath]);
}