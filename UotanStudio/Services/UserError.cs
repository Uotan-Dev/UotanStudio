using System.Net.Sockets;
using System.Security.Cryptography;

namespace UotanStudio.Services;

/// <summary>
/// Formats user-facing failures without changing exceptions or diagnostic log messages.
/// <para>格式化面向用户的失败信息，不改动异常内容与诊断日志文本。</para>
/// </summary>
internal static class UserError
{
    /// <summary>
    /// Localizes the explanation and labels the unchanged original message as diagnostic detail.
    /// <para>本地化说明文字，并把保持原样的原始消息标注为诊断细节。</para>
    /// </summary>
    /// <param name="exception">
    /// Exception whose message is reported.
    /// <para>要上报其消息的异常。</para>
    /// </param>
    /// <param name="summaryKey">
    /// Optional localization key overriding the inferred one.
    /// <para>可选的本地化键，用于覆盖推断出的键。</para>
    /// </param>
    /// <param name="arguments">
    /// Values substituted into the localized summary.
    /// <para>填入本地化摘要的替换值。</para>
    /// </param>
    /// <returns>
    /// The localized summary followed by the original detail.
    /// <para>本地化摘要及其后的原始细节。</para>
    /// </returns>
    internal static string Describe(Exception exception, string? summaryKey = null, params object[] arguments)
        => Describe(exception, LanguageService.Instance.GetString, summaryKey, arguments);

    /// <summary>
    /// Localizes the explanation and labels the unchanged original message as diagnostic detail.
    /// <para>本地化说明文字，并把保持原样的原始消息标注为诊断细节。</para>
    /// </summary>
    /// <param name="exception">
    /// Exception whose message is reported.
    /// <para>要上报其消息的异常。</para>
    /// </param>
    /// <param name="text">
    /// Resolves a localization key to display text.
    /// <para>把本地化键解析为显示文本。</para>
    /// </param>
    /// <param name="summaryKey">
    /// Optional localization key overriding the inferred one.
    /// <para>可选的本地化键，用于覆盖推断出的键。</para>
    /// </param>
    /// <param name="arguments">
    /// Values substituted into the localized summary.
    /// <para>填入本地化摘要的替换值。</para>
    /// </param>
    /// <returns>
    /// The localized summary followed by the original detail.
    /// <para>本地化摘要及其后的原始细节。</para>
    /// </returns>
    internal static string Describe(Exception exception, Func<string, string> text, string? summaryKey = null, params object[] arguments)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var cause = exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1
            ? aggregate.InnerExceptions[0] : exception;
        var key = summaryKey ?? cause switch
        {
            OperationCanceledException => "UserError_Cancelled",
            TimeoutException => "UserError_Timeout",
            FileNotFoundException or DirectoryNotFoundException => "UserError_NotFound",
            UnauthorizedAccessException or System.Security.SecurityException => "UserError_AccessDenied",
            SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "UserError_ConnectionRefused",
            SocketException or HttpRequestException => "UserError_ConnectionFailed",
            EndOfStreamException or InvalidDataException or FormatException or System.Text.Json.JsonException or System.Xml.XmlException => "UserError_InvalidData",
            CryptographicException => "UserError_Cryptography",
            NotSupportedException => "UserError_NotSupported",
            ArgumentException => "UserError_InvalidInput",
            IOException => "UserError_Io",
            _ => "UserError_OperationFailed"
        };
        return WithDetails(exception.Message, text, key, arguments);
    }

    /// <summary>
    /// Displays a backend result as original detail below a localized operation summary.
    /// <para>把后端结果作为原始细节显示在本地化的操作摘要之下。</para>
    /// </summary>
    /// <param name="details">
    /// Original detail text, possibly empty.
    /// <para>原始细节文本，可以为空。</para>
    /// </param>
    /// <param name="summaryKey">
    /// Localization key of the operation summary.
    /// <para>操作摘要的本地化键。</para>
    /// </param>
    /// <param name="arguments">
    /// Values substituted into the localized summary.
    /// <para>填入本地化摘要的替换值。</para>
    /// </param>
    /// <returns>
    /// The summary, followed by the detail when one exists.
    /// <para>摘要文本；存在细节时附于其后。</para>
    /// </returns>
    internal static string WithDetails(string? details, string summaryKey = "UserError_OperationFailed", params object[] arguments)
        => WithDetails(details, LanguageService.Instance.GetString, summaryKey, arguments);

    /// <summary>
    /// Displays a backend result as original detail below a localized operation summary.
    /// <para>把后端结果作为原始细节显示在本地化的操作摘要之下。</para>
    /// </summary>
    /// <param name="details">
    /// Original detail text, possibly empty.
    /// <para>原始细节文本，可以为空。</para>
    /// </param>
    /// <param name="text">
    /// Resolves a localization key to display text.
    /// <para>把本地化键解析为显示文本。</para>
    /// </param>
    /// <param name="summaryKey">
    /// Localization key of the operation summary.
    /// <para>操作摘要的本地化键。</para>
    /// </param>
    /// <param name="arguments">
    /// Values substituted into the localized summary.
    /// <para>填入本地化摘要的替换值。</para>
    /// </param>
    /// <returns>
    /// The summary, followed by the detail when one exists.
    /// <para>摘要文本；存在细节时附于其后。</para>
    /// </returns>
    internal static string WithDetails(string? details, Func<string, string> text, string summaryKey, params object[] arguments)
    {
        var summary = arguments.Length == 0 ? text(summaryKey) : string.Format(text(summaryKey), arguments);
        return string.IsNullOrWhiteSpace(details) ? summary
            : summary + Environment.NewLine + text("UserError_Details") + Environment.NewLine + details;
    }
}