using System.Globalization;
using UotanStudio.Services;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Renders the user-facing text the flash services report through <see cref="FlashResult.Summary"/>,
/// <see cref="FlashProgress.Status"/> and <c>BackupProgress.Status</c>: every line goes through the
/// bundled locale dictionaries, so a protocol-level result follows the interface language instead of
/// carrying one fixed language for its whole lifetime.
/// <para>渲染烧录服务经 <see cref="FlashResult.Summary"/>、<see cref="FlashProgress.Status"/> 与
/// <c>BackupProgress.Status</c> 上报的面向用户文本：每一行都经过内置语言字典，使协议层结果跟随界面语言，
/// 而不是在其整个生命周期里固定一种语言。</para>
/// </summary>
internal static class FlashLogText
{
    /// <summary>
    /// Gets the localized text for a resource key.
    /// <para>获取资源键对应的本地化文本。</para>
    /// </summary>
    /// <param name="key">The bundled resource key. <para>内置资源键。</para></param>
    /// <returns>The active-language value, or the key itself when unknown.
    /// <para>当前语言下的取值；键未知时为键名本身。</para></returns>
    internal static string Get(string key) => LanguageService.Instance.GetString(key);

    /// <summary>
    /// Gets the localized text for a resource key and substitutes its placeholders.
    /// <para>获取资源键对应的本地化文本并填入其占位符。</para>
    /// </summary>
    /// <param name="key">The bundled resource key. <para>内置资源键。</para></param>
    /// <param name="arguments">Values for the placeholders, in ordinal order.
    /// <para>按顺序填入占位符的取值。</para></param>
    /// <returns>The formatted value in the active language. <para>当前语言下已格式化的取值。</para></returns>
    internal static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    /// <summary>
    /// Shortens a free-form device or protocol message to one bounded line item, so a transport
    /// exception cannot flood the page log with embedded newlines.
    /// <para>把设备或协议的自由文本压缩成有界的单行文本，使传输层异常不会因内嵌换行而灌满页面日志。</para>
    /// </summary>
    /// <param name="detail">The raw message; null or blank becomes a localized placeholder.
    /// <para>原始消息；为空或空白时使用本地化占位文本。</para></param>
    /// <returns>The trimmed message with its line breaks folded to spaces.
    /// <para>去掉首尾空白并把换行折叠为空格后的消息。</para></returns>
    internal static string Detail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return Get("Flash_Log_NoDetail");
        }

        return detail.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Trim();
    }
}