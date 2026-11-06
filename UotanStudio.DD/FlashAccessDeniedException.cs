namespace UotanStudio.DD;

/// <summary>
/// Raised when the platform refused to open a target for writing because the process lacks the privilege
/// the operation requires.
/// <para>平台因进程缺少所需权限而拒绝以写方式打开目标时抛出。</para>
/// </summary>
public sealed class FlashAccessDeniedException : FlashException
{
    /// <summary>
    /// Creates an exception describing the denied target.
    /// <para>创建描述被拒绝目标的异常。</para>
    /// </summary>
    /// <param name="message">Failure description naming the target.
    /// <para>点名该目标的失败描述。</para></param>
    /// <param name="innerException">The platform refusal.<para>平台拒绝的原始异常。</para></param>
    public FlashAccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Classifies the platform error that stopped a target from being opened for writing.
/// <para>归类阻止以写方式打开目标的平台错误。</para>
/// </summary>
public static class FlashAccessClassifier
{
    /// <summary>
    /// The Windows <c>ERROR_ACCESS_DENIED</c> code reported when a process may not write a raw disk.
    /// <para>进程无权写入裸磁盘时 Windows 上报的 <c>ERROR_ACCESS_DENIED</c> 代码。</para>
    /// </summary>
    public const int WindowsAccessDenied = 5;

    /// <summary>
    /// Determines whether a platform error means the process lacks write privilege rather than a device fault.
    /// <para>判断平台错误是否表示进程缺少写权限，而不是设备本身故障。</para>
    /// </summary>
    /// <param name="nativeErrorCode">The reported native error code.
    /// <para>已上报的原生错误码。</para></param>
    /// <returns>
    /// <see langword="true"/> when the code means "access denied" on the current platform.
    /// <para>当前平台上该错误码表示「拒绝访问」时为 <see langword="true"/>。</para>
    /// </returns>
    public static bool IsAccessDenied(int nativeErrorCode) => OperatingSystem.IsWindows()
        ? nativeErrorCode == WindowsAccessDenied
        : nativeErrorCode is 1 or 13;
}