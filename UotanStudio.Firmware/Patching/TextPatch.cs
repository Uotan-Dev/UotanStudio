using System.Text;
using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.Patching;

/// <summary>
/// Text operations use explicit anchors; insertion never relies on line numbers.
/// <para>文本操作使用显式锚点；插入从不依赖行号。</para>
/// </summary>
public enum TextPatchOperation
{
    /// <summary>Replaces every matched span with the replacement text. <para>将每个命中的片段替换为替换文本。</para></summary>
    Replace,

    /// <summary>Inserts the replacement immediately before each matched span, keeping the match. <para>在每个命中片段之前插入替换文本，并保留匹配内容。</para></summary>
    InsertBefore,

    /// <summary>Inserts the replacement immediately after each matched span, keeping the match. <para>在每个命中片段之后插入替换文本，并保留匹配内容。</para></summary>
    InsertAfter,

    /// <summary>Removes every matched span; the replacement text is not emitted. <para>移除每个命中的片段；不输出替换文本。</para></summary>
    Delete
}

/// <summary>
/// An immutable transformation result; publication is a separate operation.
/// <para>不可变的转换结果；发布是独立的操作。</para>
/// </summary>
/// <param name="Text">The resulting document text. <para>结果文档文本。</para></param>
/// <param name="Matches">Number of targets the operation matched. <para>操作命中的目标数量。</para></param>
/// <param name="Changed"><see langword="true"/> when the text differs from the pre-patch input; the untouched input is
/// returned otherwise. <para>文本与补丁前输入不同时为 <see langword="true"/>；否则原样返回输入。</para></param>
public sealed record TextPatchResult(string Text, int Matches, bool Changed);

/// <summary>
/// Bounded, deterministic text patching with exact match-count guards.
/// <para>有界、确定性的文本补丁，带精确的匹配数量守卫。</para>
/// </summary>
public static class TextPatch
{
    /// <summary>Maximum configuration text size, in UTF-16 code units. <para>配置文本的最大长度，以 UTF-16 代码单元计数。</para></summary>
    public const int MaxCharacters = 4 * 1024 * 1024;

    /// <summary>Applies non-overlapping matches. Replacement is literal, including for regex searches.
    /// <para>应用互不重叠的匹配。替换文本按字面处理，正则搜索也不例外。</para></summary>
    /// <param name="text">Source text, bounded by <see cref="MaxCharacters"/>.
    /// <para>源文本，大小受 <see cref="MaxCharacters"/> 限制。</para></param>
    /// <param name="find">Non-empty anchor, at most 16384 characters; a literal anchor is escaped, and a regex anchor is matched culture-invariant with a one-second timeout.
    /// <para>非空锚点，最多 16384 个字符；字面锚点会被转义，正则锚点以文化无关语义匹配，超时为一秒。</para></param>
    /// <param name="replacement">Literal text emitted for each match, bounded by <see cref="MaxCharacters"/>; it is ignored by <see cref="TextPatchOperation.Delete"/> but still size-checked.
    /// <para>每个匹配处输出的字面文本，大小受 <see cref="MaxCharacters"/> 限制；<see cref="TextPatchOperation.Delete"/> 会忽略它，但仍会检查大小。</para></param>
    /// <param name="operation">A defined <see cref="TextPatchOperation"/> value controlling how each match and the replacement are emitted.
    /// <para>已定义的 <see cref="TextPatchOperation"/> 值，决定每个匹配及替换文本的输出方式。</para></param>
    /// <param name="regex"><see langword="true"/> treats <paramref name="find"/> as a regular expression; otherwise it is matched as a literal.
    /// <para>为 <see langword="true"/> 时将 <paramref name="find"/> 作为正则表达式处理，否则按字面量匹配。</para></param>
    /// <param name="expectedMatches">Exact number of matches <paramref name="find"/> must produce, 0 to 100000.
    /// <para><paramref name="find"/> 必须产生的精确匹配数，范围 0 到 100000。</para></param>
    /// <param name="cancellationToken">Checked before the scan and after every match; cancellation aborts before any result is produced.
    /// <para>在扫描前及每个匹配之后检查；取消时不会产出任何结果。</para></param>
    /// <returns>The patched text, the match count, and a changed flag set when the result differs from the input under ordinal comparison.
    /// <para>补丁后的文本、匹配数，以及在 Ordinal 比较下结果与输入不同时置位的修改标志。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="replacement"/> is <see langword="null"/>, or <paramref name="find"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 或 <paramref name="replacement"/> 为 <see langword="null"/>，或 <paramref name="find"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="find"/> is empty.
    /// <para><paramref name="find"/> 为空字符串。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="find"/> exceeds 16384 characters, <paramref name="expectedMatches"/> is outside 0 to 100000, or <paramref name="operation"/> is not a defined value.
    /// <para><paramref name="find"/> 超过 16384 个字符、<paramref name="expectedMatches"/> 不在 0 到 100000 范围内，或 <paramref name="operation"/> 不是已定义的值。</para></exception>
    /// <exception cref="InvalidDataException">A size limit is exceeded, the anchor matches an empty string, or the match count differs from <paramref name="expectedMatches"/>.
    /// <para>超出大小上限、锚点匹配了空字符串，或匹配数与 <paramref name="expectedMatches"/> 不一致。</para></exception>
    /// <exception cref="RegexMatchTimeoutException">A regex search exceeded the one-second match timeout.
    /// <para>正则搜索超过一秒的匹配超时。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public static TextPatchResult Apply(string text, string find, string replacement,
        TextPatchOperation operation, bool regex, int expectedMatches, CancellationToken cancellationToken = default)
    {
        ValidateSize(text);
        ValidateSize(replacement);
        ArgumentException.ThrowIfNullOrEmpty(find);
        if (find.Length > 16_384 || expectedMatches is < 0 or > 100_000 || !Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedMatches));
        }

        var expression = new Regex(regex ? find : Regex.Escape(find), RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        var output = new StringBuilder(Math.Min(text.Length, 65_536));
        var position = 0;
        var count = 0;
        foreach (Match match in expression.Matches(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (match.Length == 0)
            {
                throw new InvalidDataException("A patch anchor must not match an empty string.");
            }
            if (++count > expectedMatches)
            {
                throw new InvalidDataException($"Expected {expectedMatches} matches; found more.");
            }
            Append(output, text.AsSpan(position, match.Index - position));
            if (operation == TextPatchOperation.InsertAfter)
            {
                Append(output, match.ValueSpan);
            }
            if (operation != TextPatchOperation.Delete)
            {
                Append(output, replacement);
            }
            if (operation == TextPatchOperation.InsertBefore)
            {
                Append(output, match.ValueSpan);
            }
            position = match.Index + match.Length;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (count != expectedMatches)
        {
            throw new InvalidDataException($"Expected {expectedMatches} matches; found {count}.");
        }
        Append(output, text.AsSpan(position));
        var result = output.ToString();
        return new TextPatchResult(result, count, !string.Equals(text, result, StringComparison.Ordinal));
    }

    internal static void ValidateSize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxCharacters)
        {
            throw new PatchingCorruptedException("Configuration text exceeds the patch size limit.");
        }
    }

    private static void Append(StringBuilder builder, ReadOnlySpan<char> value)
    {
        if (value.Length > MaxCharacters - builder.Length)
        {
            throw new InvalidDataException("Patched text exceeds the patch size limit.");
        }
        builder.Append(value);
    }
}