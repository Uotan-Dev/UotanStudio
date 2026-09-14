using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace UotanStudio.Firmware.SharedKernel;

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