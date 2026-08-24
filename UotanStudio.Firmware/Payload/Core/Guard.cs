namespace UotanStudio.Firmware.Payload.Core;

/// <summary>
/// Provides internal parameter validation helper methods.
/// <para>提供内部参数验证辅助方法。</para>
/// </summary>
internal static class Guard
{
    /// <summary>
    /// Throws <see cref="ArgumentNullException"/> if the specified value is <c>null</c>.
    /// <para>如果指定值为 <c>null</c>，则抛出 <see cref="ArgumentNullException"/>。</para>
    /// </summary>
    /// <param name="value">The value to check for <c>null</c>.
    /// <para>要检查是否为 <c>null</c> 的值。</para>
    /// </param>
    /// <param name="paramName">The name of the parameter being validated.
    /// <para>正在验证的参数名称。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <c>null</c>.
    /// <para><paramref name="value"/> 为 <c>null</c>。</para>
    /// </exception>
    internal static void ThrowIfNull(object? value, string paramName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> if the specified string value is <c>null</c>, empty, or consists only of whitespace characters.
    /// <para>如果指定字符串值为 <c>null</c>、空或仅由空白字符组成，则抛出 <see cref="ArgumentException"/>。</para>
    /// </summary>
    /// <param name="value">The string value to validate.
    /// <para>要验证的字符串值。</para>
    /// </param>
    /// <param name="paramName">The name of the parameter being validated.
    /// <para>正在验证的参数名称。</para>
    /// </param>
    /// <param name="message">The error message for the exception.
    /// <para>异常的错误消息。</para>
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or whitespace.
    /// <para><paramref name="value"/> 为空或空白字符。</para>
    /// </exception>
    internal static void ThrowIfNullOrWhiteSpace(string? value, string paramName, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(message, paramName);
        }
    }
}