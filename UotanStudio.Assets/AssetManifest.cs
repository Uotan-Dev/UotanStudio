using System.Globalization;
using System.Text;

namespace UotanStudio.Assets;

internal static class AssetManifest
{
    internal const string ManifestResourceName = "assets/manifest.tsv";

    private const int ColumnCount = 6;

    internal static IReadOnlyDictionary<string, AssetBundle> Parse(ReadOnlySpan<byte> manifest)
    {
        if (manifest.Length is 0 or > AssetLimits.MaximumManifestBytes)
        {
            throw new InvalidDataException(
                $"The asset manifest is {manifest.Length} bytes, which is outside the accepted range. "
                + $"资源清单为 {manifest.Length} 字节，超出可接受范围。");
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(manifest);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("The asset manifest is not valid UTF-8. 资源清单不是有效的 UTF-8。", error);
        }

        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        var decoded = new List<AssetEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var entry = ParseRow(line);
            if (!seen.Add(entry.Bundle + '\u0000' + entry.Path))
            {
                throw new InvalidDataException(
                    $"The asset manifest declares '{entry.Bundle}/{entry.Path}' more than once. "
                    + $"资源清单重复声明了 '{entry.Bundle}/{entry.Path}'。");
            }

            decoded.Add(entry);
            if (decoded.Count > AssetLimits.MaximumEntries)
            {
                throw new InvalidDataException(
                    $"The asset manifest declares more than {AssetLimits.MaximumEntries} entries. "
                    + $"资源清单声明的条目超过 {AssetLimits.MaximumEntries} 条。");
            }
        }

        var bundles = new Dictionary<string, AssetBundle>(StringComparer.Ordinal);
        foreach (var group in decoded.GroupBy(static entry => entry.Bundle, StringComparer.Ordinal))
        {
            var entries = group.ToArray();
            var total = 0L;
            foreach (var entry in entries)
            {
                total = checked(total + entry.Length);
                if (total > AssetLimits.MaximumBundleBytes)
                {
                    throw new InvalidDataException(
                        $"The asset bundle '{group.Key}' declares more than {AssetLimits.MaximumBundleBytes} bytes. "
                        + $"资源包 '{group.Key}' 声明的总字节数超过 {AssetLimits.MaximumBundleBytes}。");
                }
            }

            bundles[group.Key] = new AssetBundle(group.Key, entries);
        }

        return bundles;
    }

    private static AssetEntry ParseRow(string line)
    {
        var fields = line.Split('\t');
        if (fields.Length != ColumnCount)
        {
            throw new InvalidDataException(
                $"The asset manifest line '{line}' does not have {ColumnCount} tab-separated fields. "
                + $"资源清单行 '{line}' 不是 {ColumnCount} 个制表符分隔的字段。");
        }

        var bundle = fields[0];
        if (!IsUsableBundleKey(bundle))
        {
            throw new InvalidDataException(
                $"The asset manifest line '{line}' names an unusable bundle '{bundle}'. "
                + $"资源清单行 '{line}' 给出了不可用的资源包 '{bundle}'。");
        }

        var path = fields[1];
        if (!IsUsablePath(path))
        {
            throw new InvalidDataException(
                $"The asset manifest line '{line}' names an unusable file '{path}'. "
                + $"资源清单行 '{line}' 给出了不可用的文件名 '{path}'。");
        }

        if (!long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            || length is <= 0 or > AssetLimits.MaximumEntryBytes)
        {
            throw new InvalidDataException(
                $"The asset manifest records an unusable length '{fields[2]}' for '{path}'. "
                + $"资源清单为 '{path}' 记录了不可用的长度 '{fields[2]}'。");
        }

        var digest = fields[3];
        if (!IsSha256Hex(digest))
        {
            throw new InvalidDataException(
                $"The asset manifest records an unusable SHA-256 '{digest}' for '{path}'. "
                + $"资源清单为 '{path}' 记录了不可用的 SHA-256 '{digest}'。");
        }

        if (!string.Equals(fields[4], "zstd", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The asset manifest records the unsupported encoding '{fields[4]}' for '{path}'. "
                + $"资源清单为 '{path}' 记录了不支持的编码 '{fields[4]}'。");
        }

        if (fields[5] is not ("source" or "generated"))
        {
            throw new InvalidDataException(
                $"The asset manifest records the unknown origin '{fields[5]}' for '{path}'. "
                + $"资源清单为 '{path}' 记录了未知来源 '{fields[5]}'。");
        }

        return new AssetEntry(bundle, path, length, digest, AssetEncoding.Zstd);
    }

    private static bool IsUsableBundleKey(string key)
    {
        if (key.Length is 0 or > AssetLimits.MaximumBundleKeyLength
            || key[0] == '/'
            || key[^1] == '/'
            || key.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var segment in key.Split('/'))
        {
            if (!IsUsableName(segment))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUsablePath(string path) => IsUsableName(path) && !path.Contains('/', StringComparison.Ordinal);

    private static bool IsUsableName(string name)
    {
        if (name.Length is 0 or > AssetLimits.MaximumPathLength || name is "." or "..")
        {
            return false;
        }

        if (char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (char.IsControl(character)
                || character is '/' or '\\' or ':' or '\0'
                || Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSha256Hex(string digest)
    {
        if (digest.Length != 64)
        {
            return false;
        }

        foreach (var character in digest)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}