namespace UotanStudio.Firmware.Payload.Core;

internal static class Guard
{
    /// <summary>
    /// Throws ArgumentNullException if the specified value is null.
    /// <para>如果指定值为 null，则抛出 ArgumentNullException。</para>
    /// </summary>
    internal static void ThrowIfNull(object? value, string paramName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }

    /// <summary>
    /// Throws ArgumentException if the specified string value is null, empty, or consists only of whitespace characters.
    /// <para>如果指定字符串值为 null、空或仅由空白字符组成，则抛出 ArgumentException。</para>
    /// </summary>
    internal static void ThrowIfNullOrWhiteSpace(string? value, string paramName, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(message, paramName);
        }
    }
}
