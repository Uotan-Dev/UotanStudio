namespace UotanStudio.RegexInfer;

/// <summary>
/// Character classes the generaliser may collapse a set of constant strings into.
/// <para>泛化器可以把一组常量字符串折叠成的字符类。</para>
/// </summary>
public enum CharClassKind
{
    /// <summary>No generalisation: the constant text is emitted verbatim. <para>不做泛化：常量文本原样输出。</para></summary>
    None = 0,

    /// <summary>At least one ASCII digit, rendered as <c>\d</c>. <para>至少一个 ASCII 数字，渲染为 <c>\d</c>。</para></summary>
    Digit = 1,

    /// <summary>At least one ASCII letter, rendered as <c>[a-zA-Z]</c>. <para>至少一个 ASCII 字母，渲染为 <c>[a-zA-Z]</c>。</para></summary>
    Alpha = 2,

    /// <summary>ASCII letters or digits, rendered as <c>\w</c>. <para>ASCII 字母或数字，渲染为 <c>\w</c>。</para></summary>
    Alphanumeric = 3,

    /// <summary>At least one whitespace character, rendered as <c>\s</c>. <para>至少一个空白字符，渲染为 <c>\s</c>。</para></summary>
    Whitespace = 4
}