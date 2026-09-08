using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace UotanStudio.Services.Workspace;

/// <summary>Options shared by document and project searches. 文档与项目搜索共用选项。</summary>
internal sealed record TextSearchOptions(string Pattern, string Replacement = "", bool MatchCase = false,
    bool WholeWord = false, bool UseRegex = false, bool PreserveCase = false);

/// <summary>A replacement against an immutable text snapshot. 针对不可变文本快照的替换。</summary>
internal sealed record TextSearchMatch(int Offset, int Length, string Value, string Replacement);

/// <summary>Bounded, cancellable text matching without UI dependencies. 无 UI 依赖的有界可取消文本匹配。</summary>
internal static class TextSearchService
{
    internal const int MaximumTextLength = 8 * 1024 * 1024;
    internal const int MaximumMatches = 10000;

    internal static IReadOnlyList<TextSearchMatch> Find(string text, TextSearchOptions options,
        int start = 0, int? length = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (text.Length > MaximumTextLength) throw new IOException("Text exceeds the 8 MiB search limit.");
        var count = length ?? text.Length - start;
        if (start < 0 || start > text.Length || count < 0 || count > text.Length - start)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (options.Pattern.Length == 0) return [];
        var pattern = options.UseRegex ? options.Pattern : Regex.Escape(options.Pattern);
        if (options.WholeWord) pattern = @"(?<![\p{L}\p{N}\p{M}_])(?:" + pattern + @")(?![\p{L}\p{N}\p{M}_])";
        var flags = RegexOptions.CultureInvariant | RegexOptions.Multiline;
        if (!options.MatchCase) flags |= RegexOptions.IgnoreCase;
        var regex = new Regex(pattern, flags, TimeSpan.FromMilliseconds(250));
        var clock = Stopwatch.StartNew();
        var matches = new List<TextSearchMatch>();
        var end = start + count;
        long replacementLength = 0;
        for (var match = regex.Match(text, start); match.Success && match.Index <= end; match = match.NextMatch())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("Search exceeded its time limit.");
            if (match.Length > end - match.Index) continue;
            if (matches.Count == MaximumMatches) throw new IOException("Search exceeds the 10000 match limit. Narrow the scope.");
            var replacement = options.UseRegex ? match.Result(options.Replacement) : options.Replacement;
            if (options.PreserveCase) replacement = PreserveCase(match.Value, replacement);
            replacementLength += replacement.Length;
            if (replacementLength > MaximumTextLength) throw new IOException("Replacement exceeds the 8 MiB text limit.");
            matches.Add(new(match.Index, match.Length, match.Value, replacement));
        }
        return matches;
    }

    internal static string Apply(string text, IReadOnlyList<TextSearchMatch> matches)
    {
        var result = new StringBuilder();
        var offset = 0;
        foreach (var match in matches)
        {
            if (match.Offset < offset || match.Offset > text.Length || match.Length > text.Length - match.Offset
                || !text.AsSpan(match.Offset, match.Length).SequenceEqual(match.Value))
                throw new IOException("The document changed after searching. Search again.");
            if ((long)result.Length + match.Offset - offset + match.Replacement.Length > MaximumTextLength)
                throw new IOException("Replacement exceeds the 8 MiB text limit.");
            result.Append(text, offset, match.Offset - offset).Append(match.Replacement);
            offset = match.Offset + match.Length;
        }
        if ((long)result.Length + text.Length - offset > MaximumTextLength)
            throw new IOException("Replacement exceeds the 8 MiB text limit.");
        return result.Append(text, offset, text.Length - offset).ToString();
    }

    private static string PreserveCase(string source, string replacement)
    {
        var letters = source.Where(char.IsLetter).ToArray();
        if (letters.Length == 0) return replacement;
        if (letters.All(char.IsUpper)) return replacement.ToUpperInvariant();
        if (letters.All(char.IsLower)) return replacement.ToLowerInvariant();
        if (char.IsUpper(letters[0]) && letters.Skip(1).All(char.IsLower))
        {
            var chars = replacement.ToLowerInvariant().ToCharArray();
            var first = Array.FindIndex(chars, char.IsLetter);
            if (first >= 0) chars[first] = char.ToUpperInvariant(chars[first]);
            return new string(chars);
        }
        return replacement;
    }
}