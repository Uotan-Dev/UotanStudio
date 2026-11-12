using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.FileSystem.Extraction;

/// <summary>Maps image paths to portable host names without merging case aliases or treating Linux backslashes as separators. <para>把镜像路径映射为可移植的主机名，不合并大小写别名，也不把 Linux 中的反斜杠当作分隔符。</para></summary>
internal sealed class PortableExtractionPaths
{
    private static readonly SearchValues<char> UnsafeNameChars = SearchValues.Create("\\:*?\"<>|");
    private static readonly FrozenSet<string> ReservedDeviceNames = new[]
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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
            var aliases = new HashSet<string>(StringComparer.Ordinal);
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names)
            {
                if (seen.TryGetValue(n, out var alias))
                {
                    aliases.Add(alias);
                    aliases.Add(n);
                }
                else
                {
                    seen.Add(n, n);
                }
            }

            foreach (var name in names)
            {
                var dot = name.IndexOf('.');
                var stem = dot < 0 ? name : name[..dot];
                var encode = aliases.Contains(name) || name.Length > 120
                    || name.AsSpan().ContainsAnyInRange('\0', '\x1F') || name.AsSpan().ContainsAny(UnsafeNameChars)
                    || name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                    || name.Equals(RepackMetadata.FileName, StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("_info", StringComparison.Ordinal) || name.EndsWith("_fs_config", StringComparison.Ordinal) || name.EndsWith("_file_contexts", StringComparison.Ordinal)
                    || ReservedDeviceNames.Contains(stem);
                var host = encode ? Prefix + Convert.ToHexString(Sha256.ComputeHash(Encoding.UTF8.GetBytes(name))) : name;
                var imagePath = parent.Length == 0 ? name : parent + "/" + name;
                _paths.Add(imagePath, parent.Length == 0 ? host : _paths[parent] + "/" + host);
                if (_paths.Count > 100000) throw new FileSystemCorruptedException("Too many image entries.");
            }
        }
        FileSystemPreservation.RecordPaths(_paths);
    }

    internal string Resolve(string imagePath) => FileExtraction.ResolvePath(_root, _paths[imagePath]);
}