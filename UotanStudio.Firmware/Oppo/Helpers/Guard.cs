using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace UotanStudio.Firmware.Oppo.Helpers;

/// <summary>
/// <para>提供统一的参数验证方法，用于在方法入口处验证参数的有效性。</para>
/// Provides unified argument validation methods for validating parameter validity at method entry points.
/// </summary>
internal static class Guard
{
    /// <summary>
    /// <para>当指定参数为 null 时抛出 <see cref="ArgumentNullException"/>。</para>
    /// Throws an <see cref="ArgumentNullException"/> when the specified argument is null.
    /// </summary>
    /// <param name="argument"><para>要验证的参数。</para>The argument to validate.</param>
    /// <param name="paramName"><para>参数名称，由编译器自动填充。</para>The name of the parameter, automatically filled by the compiler.</param>
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
    /// <para>当指定字符串参数为 null、空或仅包含空白字符时抛出 <see cref="ArgumentException"/>。</para>
    /// Throws an <see cref="ArgumentException"/> when the specified string argument is null, empty, or consists only of white-space characters.
    /// </summary>
    /// <param name="argument"><para>要验证的字符串参数。</para>The string argument to validate.</param>
    /// <param name="paramName"><para>参数名称，由编译器自动填充。</para>The name of the parameter, automatically filled by the compiler.</param>
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