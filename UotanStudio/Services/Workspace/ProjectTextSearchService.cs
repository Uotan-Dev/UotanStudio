using System.Text;

namespace UotanStudio.Services.Workspace;

/// <summary>
/// A matching project file together with the text that was searched.
/// <para>命中匹配的项目文件及被搜索的文本。</para>
/// </summary>
/// <param name="Path">
/// The matching file path.
/// <para>匹配的文件路径。</para>
/// </param>
/// <param name="Text">
/// The contents that were searched.
/// <para>被搜索的文件内容。</para>
/// </param>
/// <param name="Matches">
/// The matches found in that text.
/// <para>该文本中找到的匹配项。</para>
/// </param>
internal sealed record ProjectTextSearchResult(string Path, string Text, IReadOnlyList<TextSearchMatch> Matches);

/// <summary>
/// Reads text files without following directory links and without artificial scan caps.
/// <para>读取文本文件，不跟随目录链接，也不设置人为的扫描上限。</para>
/// </summary>
internal static class ProjectTextSearchService
{
    internal static IReadOnlyList<ProjectTextSearchResult> Find(string root, TextSearchOptions options,
        IReadOnlySet<string> excludedPaths, CancellationToken cancellationToken)
    {
        List<ProjectTextSearchResult> result = [];
        var directories = new Stack<string>();
        directories.Push(Path.GetFullPath(root));
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (Path.GetFileName(path) is not (".git" or ".uotan" or ".deleted")) directories.Push(path);
                    continue;
                }
                if (excludedPaths.Contains(path)) continue;
                var text = ReadText(path, cancellationToken);
                if (text is null)
                {
                    text = FormatSearchText.TryRead(path, cancellationToken);
                    if (text is null) continue;
                }
                var found = TextSearchService.Find(text, options, cancellationToken: cancellationToken);
                if (found.Count != 0) result.Add(new(path, text, found));
            }
        }
        return result;
    }

    /// <summary>
    /// Reads a file's search text: plain text first, then the binary-format adapter; <c>null</c> when
    /// the file is not a supported searchable carrier.
    /// <para>读取文件的搜索文本：优先纯文本，其次二进制格式适配；不是可搜索载体时为 <c>null</c>。</para>
    /// </summary>
    /// <param name="path">
    /// The file to read.
    /// <para>待读取的文件。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The token checked while reading.
    /// <para>读取期间检查的取消令牌。</para>
    /// </param>
    /// <returns>
    /// The search text, or <c>null</c> when unreadable or unsupported.
    /// <para>搜索文本；无法读取或不受支持时为 <c>null</c>。</para>
    /// </returns>
    internal static string? ReadSearchText(string path, CancellationToken cancellationToken) =>
        ReadText(path, cancellationToken) ?? FormatSearchText.TryRead(path, cancellationToken);

    internal static string? ReadText(string path, CancellationToken cancellationToken)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[4];
        var headerLength = file.Read(header);
        Encoding encoding = new UTF8Encoding(false, true);
        var preamble = 0;
        if (headerLength >= 4 && header is [0xff, 0xfe, 0, 0])
        { encoding = new UTF32Encoding(false, true, true); preamble = 4; }
        else if (headerLength >= 4 && header is [0, 0, 0xfe, 0xff])
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
                for (var i = 0; i < count; i++)
                    if (char.IsControl(buffer[i]) && buffer[i] is not ('\r' or '\n' or '\t' or '\f')) return null;
                text.Append(buffer[..count]);
            }
            return text.ToString();
        }
        catch (DecoderFallbackException) { return null; }
    }
}