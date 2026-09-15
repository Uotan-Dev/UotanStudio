using System.Text;

namespace UotanStudio.Services.Workspace;

/// <summary>A matching project file and its searched contents. 匹配的项目文件及搜索时的内容。</summary>
internal sealed record ProjectTextSearchResult(string Path, string Text, IReadOnlyList<TextSearchMatch> Matches);

/// <summary>Reads bounded text files without following directory links. 有界读取文本文件，不跟随目录链接。</summary>
internal static class ProjectTextSearchService
{
    internal static IReadOnlyList<ProjectTextSearchResult> Find(string root, TextSearchOptions options,
        IReadOnlySet<string> excludedPaths, CancellationToken cancellationToken)
    {
        var result = new List<ProjectTextSearchResult>();
        var directories = new Stack<string>();
        directories.Push(Path.GetFullPath(root));
        long bytes = 0;
        var entries = 0;
        var matches = 0;
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > 10000) throw new IOException("Project search exceeds 10000 entries. Narrow the scope.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (Path.GetFileName(path) is not (".git" or ".uotan" or ".deleted")) directories.Push(path);
                    continue;
                }
                if (excludedPaths.Contains(path)) continue;
                var size = new FileInfo(path).Length;
                if (size > TextSearchService.MaximumTextLength) continue;
                bytes += size;
                if (bytes > 64 * 1024 * 1024) throw new IOException("Project search exceeds the 64 MiB scan limit. Narrow the scope.");
                var text = ReadText(path, cancellationToken);
                if (text is null)
                {
                    // 非纯文本：尝试二进制格式（DTB/ARSC/AXML/DEX/JAR）适配文本化，失败即跳过。
                    // Not plain text: try the binary-format adapter (DTB/ARSC/AXML/DEX/JAR); skip on failure.
                    text = FormatSearchText.TryRead(path, cancellationToken);
                    if (text is null) continue;
                    // 生成文本计入 64 MiB 扫描上限，避免单个小文件展开出超大搜索文本。
                    // The generated text counts toward the 64 MiB scan cap so a small file cannot expand unboundedly.
                    bytes += text.Length;
                    if (bytes > 64 * 1024 * 1024) throw new IOException("Project search exceeds the 64 MiB scan limit. Narrow the scope.");
                }
                var found = TextSearchService.Find(text, options, cancellationToken: cancellationToken);
                matches += found.Count;
                if (matches > TextSearchService.MaximumMatches) throw new IOException("Project search exceeds 10000 matches. Narrow the scope.");
                if (found.Count != 0) result.Add(new(path, text, found));
            }
        }
        return result;
    }

    /// <summary>
    /// <para>读取文件的搜索文本：优先纯文本，其次二进制格式适配；均失败或超限时返回 <c>null</c>。
    /// 用于项目范围结果导航前核对磁盘内容与搜索快照是否一致。</para>
    /// Reads a file's search text: plain text first, then the binary-format adapter; returns
    /// <c>null</c> when both fail or the result is over-limit. Used to verify a project-scope result's
    /// on-disk content matches the search snapshot before navigation.
    /// </summary>
    internal static string? ReadSearchText(string path, CancellationToken cancellationToken) =>
        ReadText(path, cancellationToken) ?? FormatSearchText.TryRead(path, cancellationToken);

    internal static string? ReadText(string path, CancellationToken cancellationToken)
    {
        using var file = File.OpenRead(path);
        if (file.Length > TextSearchService.MaximumTextLength) return null;
        Span<byte> header = stackalloc byte[4];
        var headerLength = file.Read(header);
        Encoding encoding = new UTF8Encoding(false, true);
        var preamble = 0;
        if (headerLength >= 4 && header.SequenceEqual(new byte[] { 0xff, 0xfe, 0, 0 }))
        { encoding = new UTF32Encoding(false, true, true); preamble = 4; }
        else if (headerLength >= 4 && header.SequenceEqual(new byte[] { 0, 0, 0xfe, 0xff }))
        { encoding = new UTF32Encoding(true, true, true); preamble = 4; }
        else if (headerLength >= 2 && header[0] == 0xff && header[1] == 0xfe)
        { encoding = new UnicodeEncoding(false, true, true); preamble = 2; }
        else if (headerLength >= 2 && header[0] == 0xfe && header[1] == 0xff)
        { encoding = new UnicodeEncoding(true, true, true); preamble = 2; }
        else if (headerLength >= 3 && header[0] == 0xef && header[1] == 0xbb && header[2] == 0xbf) preamble = 3;
        file.Position = preamble;
        using var reader = new StreamReader(file, encoding, detectEncodingFromByteOrderMarks: false);
        Span<char> buffer = stackalloc char[4096];
        var text = new StringBuilder();
        try
        {
            int count;
            while ((count = reader.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (text.Length > TextSearchService.MaximumTextLength - count) return null;
                for (var i = 0; i < count; i++)
                    if (char.IsControl(buffer[i]) && buffer[i] is not ('\r' or '\n' or '\t' or '\f')) return null;
                text.Append(buffer[..count]);
            }
            return text.ToString();
        }
        catch (DecoderFallbackException) { return null; }
    }
}