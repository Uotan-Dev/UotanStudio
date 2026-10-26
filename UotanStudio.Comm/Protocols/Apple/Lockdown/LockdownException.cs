namespace UotanStudio.Comm.Protocols.Apple.Lockdown;

/// <summary>
/// Raised when lockdownd rejects a request or the lockdown protocol fails.
/// <para>lockdownd 拒绝请求或 lockdown 协议失败时抛出。</para>
/// </summary>
/// <param name="message">The failure description. <para>失败描述。</para></param>
/// <param name="errorCode">The lockdownd error code, or <see cref="UnknownErrorCode"/> when absent. <para>lockdownd 错误码；缺失时为 <see cref="UnknownErrorCode"/>。</para></param>
public sealed class LockdownException(string message, string errorCode = "Unknown") : IOException(message)
{
    /// <summary>Gets the error code value used when lockdownd did not report one. <para>获取 lockdownd 未上报错误码时使用的值。</para></summary>
    public const string UnknownErrorCode = "Unknown";

    /// <summary>Gets the lockdownd error code, for example <c>PairingDialogResponsePending</c>. <para>获取 lockdownd 错误码，例如 <c>PairingDialogResponsePending</c>。</para></summary>
    public string ErrorCode { get; } = errorCode;
}