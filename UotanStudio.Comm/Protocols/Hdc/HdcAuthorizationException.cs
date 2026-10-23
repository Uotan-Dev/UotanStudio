namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Reports that an HDC peer requires host authorization that did not complete, or explicitly denied it.
/// <para>表示 HDC 对端要求的主机授权未完成，或已被明确拒绝。</para>
/// </summary>
public sealed class HdcAuthorizationException : UnauthorizedAccessException
{
    /// <summary>
    /// Initializes the exception with a daemon authorization code and diagnostic text.
    /// <para>使用守护进程授权错误码与诊断文本初始化异常。</para>
    /// </summary>
    /// <param name="code">Daemon error code.<para>守护进程错误码。</para></param>
    /// <param name="message">Human-readable daemon diagnostic text.<para>守护进程的诊断文本。</para></param>
    public HdcAuthorizationException(string code, string message) : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>
    /// Gets the daemon authorization code that caused this exception.
    /// <para>获取导致本异常的守护进程授权错误码。</para>
    /// </summary>
    /// <value><c>E000002</c>, <c>E000003</c>, another daemon code, or <c>AUTH_FAIL</c>.
    /// <para><c>E000002</c>、<c>E000003</c>、其它守护进程错误码，或 <c>AUTH_FAIL</c>。</para></value>
    public string Code { get; }
}