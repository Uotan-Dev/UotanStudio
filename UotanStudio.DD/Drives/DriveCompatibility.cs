namespace UotanStudio.DD.Drives;

/// <summary>
/// Severity of a drive compatibility finding.
/// <para>驱动兼容性发现的严重程度。</para>
/// </summary>
public enum DriveCompatibilitySeverity
{
    /// <summary>
    /// The target can be used, but the caller should surface a confirmation.
    /// <para>目标可用，但调用方应提示确认。</para>
    /// </summary>
    Warning,

    /// <summary>
    /// The target must not be used without changing the request.
    /// <para>不改变请求时不得使用该目标。</para>
    /// </summary>
    Error,
}

/// <summary>
/// Conditions that make a drive unsuitable or risky for the current image.
/// <para>使驱动器不适合或对当前镜像存在风险的状况。</para>
/// </summary>
public enum DriveCompatibilityIssue
{
    /// <summary>
    /// The device is write-protected and cannot accept the image.
    /// <para>设备处于写保护状态，无法接收镜像。</para>
    /// </summary>
    Locked,

    /// <summary>
    /// The drive hosts the running operating system.
    /// <para>该驱动器承载当前运行的操作系统。</para>
    /// </summary>
    SystemDrive,

    /// <summary>
    /// The source image file resides on this drive.
    /// <para>源镜像文件位于该驱动器上。</para>
    /// </summary>
    ContainsImage,

    /// <summary>
    /// The drive is significantly larger than the image and needs explicit confirmation.
    /// <para>驱动器明显大于镜像，需要显式确认。</para>
    /// </summary>
    LargeDrive,

    /// <summary>
    /// The drive cannot hold the image.
    /// <para>驱动器容量不足以容纳镜像。</para>
    /// </summary>
    TooSmall,
}

/// <summary>
/// One compatibility finding attached to a drive.
/// <para>附加到驱动器的一项兼容性发现。</para>
/// </summary>
/// <param name="Issue">The detected condition.<para>检测到的状况。</para></param>
/// <param name="Severity">Whether the condition blocks writing.<para>该状况是否阻止写入。</para></param>
public sealed record DriveCompatibilityIssueInfo(DriveCompatibilityIssue Issue, DriveCompatibilitySeverity Severity);

/// <summary>
/// A rejected target and the findings that caused the rejection.
/// <para>被拒绝的目标及导致拒绝的发现。</para>
/// </summary>
/// <param name="Drive">Rejected drive.<para>被拒绝的驱动器。</para></param>
/// <param name="Issues">Non-empty findings for that drive.<para>该驱动器的非空发现列表。</para></param>
public sealed record DriveValidationFailure(FlashDrive Drive, IReadOnlyList<DriveCompatibilityIssueInfo> Issues);