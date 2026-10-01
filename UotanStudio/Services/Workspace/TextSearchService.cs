using System.Diagnostics;
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
        var end = start + count;
        if (!options.UseRegex && !options.WholeWord)
        {

            return FindLiteral(text, options, start, end, cancellationToken);
        }
        var pattern = options.UseRegex ? options.Pattern : Regex.Escape(options.Pattern);
        if (options.WholeWord) pattern = @"(?<![\p{L}\p{N}\p{M}_])(?:" + pattern + @")(?![\p{L}\p{N}\p{M}_])";
        var flags = RegexOptions.CultureInvariant | RegexOptions.Multiline;
        if (!options.MatchCase) flags |= RegexOptions.IgnoreCase;
        var regex = new Regex(pattern, flags, TimeSpan.FromMilliseconds(250));
        var clock = Stopwatch.StartNew();
        var matches = new List<TextSearchMatch>();
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

    /// <summary>
    /// Literal (non-regex, non-whole-word) search fast path. Matches the Regex path's
    /// boundary, limit and timeout semantics via IndexOf instead of an interpreted Regex.
    /// <para>字面量（非正则、非整词）搜索快路径：用 IndexOf 保持与 Regex 路径一致的
    /// 边界、限额与超时语义。</para>
    /// </summary>
    private static List<TextSearchMatch> FindLiteral(string text, TextSearchOptions options,
        int start, int end, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var matches = new List<TextSearchMatch>();
        long replacementLength = 0;
        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var index = start;
        while (index <= end)
        {
            var found = text.IndexOf(options.Pattern, index, comparison);
            if (found < 0 || found > end)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("Search exceeded its time limit.");
            if (found + options.Pattern.Length > end)
            {
                break;
            }

            if (matches.Count == MaximumMatches) throw new IOException("Search exceeds the 10000 match limit. Narrow the scope.");
            var replacement = options.Replacement;
            if (options.PreserveCase) replacement = PreserveCase(text.Substring(found, options.Pattern.Length), replacement);
            replacementLength += replacement.Length;
            if (replacementLength > MaximumTextLength) throw new IOException("Replacement exceeds the 8 MiB text limit.");
            matches.Add(new(found, options.Pattern.Length, text.Substring(found, options.Pattern.Length), replacement));
            index = found + options.Pattern.Length;
        }

        return matches;
    }

    internal static string Apply(string text, IReadOnlyList<TextSearchMatch> matches)
    {
        long resultLength = 0;
        var offset = 0;
        foreach (var match in matches)
        {
            if (match.Offset < offset || match.Offset > text.Length || match.Length > text.Length - match.Offset
                || !text.AsSpan(match.Offset, match.Length).SequenceEqual(match.Value))
                throw new IOException("The document changed after searching. Search again.");
            resultLength += (long)match.Offset - offset + match.Replacement.Length;
            if (resultLength > MaximumTextLength)
                throw new IOException("Replacement exceeds the 8 MiB text limit.");
            offset = match.Offset + match.Length;
        }
        resultLength += text.Length - offset;
        if (resultLength > MaximumTextLength)
            throw new IOException("Replacement exceeds the 8 MiB text limit.");
        if (matches.Count == 0)
        {
            return text;
        }

        return string.Create((int)resultLength, (text, matches), static (destination, state) =>
        {
            var sourceOffset = 0;
            var written = 0;
            foreach (var match in state.matches)
            {
                var unchanged = state.text.AsSpan(sourceOffset, match.Offset - sourceOffset);
                unchanged.CopyTo(destination[written..]);
                written += unchanged.Length;
                match.Replacement.AsSpan().CopyTo(destination[written..]);
                written += match.Replacement.Length;
                sourceOffset = match.Offset + match.Length;
            }
            state.text.AsSpan(sourceOffset).CopyTo(destination[written..]);
        });
    }

    private static string PreserveCase(string source, string replacement)
    {
        var hasLetters = false;
        var allUpper = true;
        var allLower = true;
        var titleCase = true;
        foreach (var letter in source)
        {
            if (!char.IsLetter(letter)) continue;
            var upper = char.IsUpper(letter);
            var lower = char.IsLower(letter);
            titleCase &= hasLetters ? lower : upper;
            allUpper &= upper;
            allLower &= lower;
            hasLetters = true;
        }
        if (!hasLetters) return replacement;
        if (allUpper) return replacement.ToUpperInvariant();
        if (allLower) return replacement.ToLowerInvariant();
        if (titleCase)
        {
            var chars = replacement.ToLowerInvariant().ToCharArray();
            var first = Array.FindIndex(chars, char.IsLetter);
            if (first >= 0) chars[first] = char.ToUpperInvariant(chars[first]);
            return new string(chars);
        }
        return replacement;
    }
}