using System.Net.Sockets;
using System.Security.Cryptography;

namespace UotanStudio.Services;

/// <summary>Formats user-facing failures without changing exceptions or diagnostic log messages.</summary>
internal static class UserError
{
    /// <summary>Localizes the explanation and labels the unchanged original message as diagnostic detail.</summary>
    internal static string Describe(Exception exception, string? summaryKey = null, params object[] arguments)
        => Describe(exception, LanguageService.Instance.GetString, summaryKey, arguments);

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

    /// <summary>Displays a backend result as original detail below a localized operation summary.</summary>
    internal static string WithDetails(string? details, string summaryKey = "UserError_OperationFailed", params object[] arguments)
        => WithDetails(details, LanguageService.Instance.GetString, summaryKey, arguments);

    internal static string WithDetails(string? details, Func<string, string> text, string summaryKey, params object[] arguments)
    {
        var summary = arguments.Length == 0 ? text(summaryKey) : string.Format(text(summaryKey), arguments);
        return string.IsNullOrWhiteSpace(details) ? summary
            : summary + Environment.NewLine + text("UserError_Details") + Environment.NewLine + details;
    }
}