using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Patching;

namespace UotanStudio.Services.Patching;

/// <summary>Bounded offline configuration I/O. Image streams must use the image adapter instead.<para>有界的离线配置读写。镜像流应改用镜像适配器。</para></summary>
public static class PatchFileService
{
    private const int MaxBytes = TextPatch.MaxCharacters * 4;

    /// <summary>Finds files below a root without following links. Patterns are relative and case sensitive.<para>在根目录下查找文件且不跟随链接。模式为相对路径且区分大小写。</para></summary>
    /// <param name="root">Directory searched recursively.<para>递归搜索的目录。</para></param>
    /// <param name="pattern">Relative case-sensitive match pattern.<para>相对且区分大小写的匹配模式。</para></param>
    /// <param name="expectedMatches">Required number of matches.<para>要求的匹配数量。</para></param>
    /// <param name="token">Token cancelling the search.<para>取消搜索的令牌。</para></param>
    /// <returns>Sorted absolute paths of the matches.<para>匹配项排序后的绝对路径。</para></returns>
    public static IReadOnlyList<string> Find(string root, string pattern, int expectedMatches, CancellationToken token)
    {
        if (expectedMatches is < 0 or > 100_000)
        {
            throw new InvalidDataException("Invalid expected match count.");
        }
        var result = Discover(root, pattern, false, token);
        if (result.Count != expectedMatches)
        {
            throw new InvalidDataException($"Expected {expectedMatches} files; found {result.Count}.");
        }
        return result;
    }

    internal static IReadOnlyList<string> Discover(string root, string pattern, bool allowMissingRoot, CancellationToken token)
    {
        ValidatePattern(pattern);
        token.ThrowIfCancellationRequested();
        root = Path.GetFullPath(root);
        RejectLinks(root);
        if (!Directory.Exists(root))
        {
            if (allowMissingRoot && !File.Exists(root))
            {
                return [];
            }
            throw new DirectoryNotFoundException(root);
        }
        pattern = pattern.Replace('\\', '/');
        var pending = new Stack<string>();
        pending.Push(root);
        List<string> result = [];
        var visited = 0;
        var pathCharacters = 0L;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            RejectLinks(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                pathCharacters += entry.Length;
                if (++visited > 100_000 || pathCharacters > 16 * 1024 * 1024)
                {
                    throw new InvalidDataException("Search exceeds the entry or total path-length limit.");
                }
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else if (Matches(root, pattern, entry))
                {
                    result.Add(entry);
                }
            }
        }
        token.ThrowIfCancellationRequested();
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    internal static void ValidatePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)
            || pattern.Length > 4096 || Path.IsPathRooted(pattern)
            || pattern.Split('/', '\\').Any(part => part == ".."))
        {
            throw new InvalidDataException("Invalid relative search pattern or match count.");
        }
    }

    internal static bool Matches(string root, string pattern, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative != "." && relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative)
            && FileSystemName.MatchesSimpleExpression(pattern.Replace('\\', '/'), relative, false);
    }

    /// <summary>Reads strictly decoded UTF-8/UTF-16 with a fixed byte and character limit.<para>按固定的字节与字符上限严格解码并读取 UTF-8 或 UTF-16 文本。</para></summary>
    /// <param name="path">File to read.<para>要读取的文件。</para></param>
    /// <param name="token">Token cancelling the read.<para>取消读取的令牌。</para></param>
    /// <returns>Text with its physical encoding and digest.<para>含物理编码与摘要的文本。</para></returns>
    public static async Task<PatchTextDocument> ReadAsync(string path, CancellationToken token)
    {
        RejectLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, true);
        if (stream.Length > MaxBytes)
        {
            throw new InvalidDataException("Configuration exceeds the byte limit.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return Decode(bytes);
    }

    internal static PatchTextDocument Decode(ReadOnlySpan<byte> bytes)
    {
        var encoding = "utf8";
        var skip = 0;
        if (bytes is [0xef, 0xbb, 0xbf, ..])
        {
            encoding = "utf8-bom";
            skip = 3;
        }
        else if (bytes is [0xff, 0xfe, ..])
        {
            encoding = "utf16-le";
            skip = 2;
        }
        else if (bytes is [0xfe, 0xff, ..])
        {
            encoding = "utf16-be";
            skip = 2;
        }
        var text = GetEncoding(encoding).GetString(bytes[skip..]);
        if (text.Length > TextPatch.MaxCharacters || text.Contains('\0'))
        {
            throw new InvalidDataException("Input is not a supported bounded text configuration.");
        }
        return new PatchTextDocument(text, encoding, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    /// <summary>Publishes to a new output file using a sibling temporary file; existing files are preserved.<para>使用同级临时文件发布到新输出路径，已有文件保持不变。</para></summary>
    /// <param name="path">New output file path.<para>新的输出文件路径。</para></param>
    /// <param name="text">Text to publish.<para>要发布的文本。</para></param>
    /// <param name="encoding">Encoding name to write with.<para>写入所使用的编码名称。</para></param>
    /// <param name="token">Token cancelling the write.<para>取消写入的令牌。</para></param>
    public static async Task WriteNewAsync(string path, string text, string encoding, CancellationToken token)
    {
        if (text.Length > TextPatch.MaxCharacters || text.Contains('\0'))
        {
            throw new InvalidDataException("Invalid configuration text.");
        }
        path = Path.GetFullPath(path);
        RejectLinks(path);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("Output already exists; choose a new path.");
        }
        var codec = GetEncoding(encoding);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, true))
            await using (var writer = new StreamWriter(output, codec))
            {
                await writer.WriteAsync(text.AsMemory(), token).ConfigureAwait(false);
                await writer.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static Encoding GetEncoding(string name) => name switch
    {
        "utf8" => new UTF8Encoding(false, true),
        "utf8-bom" => new UTF8Encoding(true, true),
        "utf16-le" => new UnicodeEncoding(false, true, true),
        "utf16-be" => new UnicodeEncoding(true, true, true),
        _ => throw new InvalidDataException("Unsupported text encoding.")
    };

    /// <summary>
    /// Rejects reparse points between the deepest existing real directory of a path and
    /// that path itself.
    /// <para>拒绝某路径最深真实上级目录与该路径本身之间的重解析点。</para>
    /// </summary>
    /// <param name="path">Path whose directory chain is checked.<para>检查其目录链的路径。</para></param>
    private static void RejectLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var boundary = DeepestExistingAncestor(full);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var current = full;
            current is not null && !string.Equals(current, boundary, comparison);
            current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Patch paths must not traverse symbolic links or junctions.");
            }
        }
    }

    /// <summary>
    /// Returns the deepest ancestor that already exists as a real directory, skipping
    /// missing components and symbolic links.
    /// <para>返回已作为真实目录存在的最深上级目录，跳过缺失组件与符号链接。</para>
    /// </summary>
    /// <param name="full">Fully qualified path walked upwards.<para>向上遍历的完整路径。</para></param>
    /// <returns>Absolute path of the boundary directory.<para>边界目录的绝对路径。</para></returns>
    private static string DeepestExistingAncestor(string full)
    {
        for (var current = Path.GetDirectoryName(full); current is not null; current = Path.GetDirectoryName(current))
        {
            if (!Directory.Exists(current))
            {
                continue;
            }
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            return current;
        }
        return Path.GetPathRoot(full) ?? full;
    }
}

/// <summary>Text together with its physical encoding and original file identity.<para>文本连同其物理编码与原始文件身份。</para></summary>
/// <param name="Text">Decoded document text.<para>解码后的文档文本。</para></param>
/// <param name="Encoding">Name of the physical encoding.<para>物理编码的名称。</para></param>
/// <param name="Sha256">Digest of the original bytes.<para>原始字节的摘要。</para></param>
public sealed record PatchTextDocument(string Text, string Encoding, string Sha256);