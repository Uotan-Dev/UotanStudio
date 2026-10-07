using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace UotanStudio.Firmware.Core;

/// <summary>
/// Lightweight argument validation helpers for firmware library code.
/// <para>固件库代码的轻量级参数校验辅助方法。</para>
/// </summary>
internal static class Guard
{
    /// <summary>
    /// Throws an <see cref="ArgumentNullException"/> when the specified argument is null.
    /// <para>当指定参数为 null 时抛出 <see cref="ArgumentNullException"/>。</para>
    /// </summary>
    /// <param name="argument">Reference tested against <see langword="null"/>; when the call returns it is
    /// guaranteed non-null. <para>与 <see langword="null"/> 比较的引用；调用返回后即保证其非空。</para></param>
    /// <param name="paramName">Name reported by the exception; the compiler fills it from the call site when
    /// omitted. <para>异常报告的参数名；省略时由编译器按调用处实参填充。</para></param>
    public static void ThrowIfNull(
        [NotNull] object? argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (argument is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }

    /// <summary>
    /// Throws an <see cref="ArgumentException"/> when the specified string argument is null, empty,
    /// or consists only of white-space characters.
    /// <para>当指定字符串参数为 null、空或仅包含空白字符时抛出 <see cref="ArgumentException"/>。</para>
    /// </summary>
    /// <param name="argument">String tested against <see langword="null"/>, emptiness and whitespace-only
    /// content; when the call returns it holds at least one non-white-space character.
    /// <para>检查是否为 <see langword="null"/>、空串或仅含空白字符的字符串；调用返回后至少包含一个非空白字符。</para></param>
    /// <param name="paramName">Name reported by the exception; the compiler fills it from the call site when
    /// omitted. <para>异常报告的参数名；省略时由编译器按调用处实参填充。</para></param>
    public static void ThrowIfNullOrWhiteSpace(
        [NotNull] string? argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (argument is null)
        {
            throw new ArgumentNullException(paramName);
        }

        if (string.IsNullOrWhiteSpace(argument))
        {
            throw new ArgumentException("The value cannot be an empty string or composed entirely of whitespace.", paramName);
        }
    }
}