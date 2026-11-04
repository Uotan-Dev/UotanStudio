using UotanStudio.DD.Drives;

namespace UotanStudio.DD;

/// <summary>
/// Raised before any byte is written when one or more targets fail the drive compatibility policy.
/// <para>当一个或多个目标未通过驱动兼容性策略时，在任何字节写入之前抛出。</para>
/// </summary>
public sealed class FlashValidationException : FlashException
{
    /// <summary>
    /// Creates an exception describing every rejected target.
    /// <para>创建描述全部被拒目标的异常。</para>
    /// </summary>
    /// <param name="failures">Rejected targets and their compatibility issues.
    /// <para>被拒目标及其兼容性问题。</para></param>
    public FlashValidationException(IReadOnlyList<DriveValidationFailure> failures)
        : base(BuildMessage(failures))
    {
        Failures = failures;
    }

    /// <summary>
    /// Rejected targets and their compatibility issues, in the order they were supplied.
    /// <para>被拒目标及其兼容性问题，顺序与调用方传入一致。</para>
    /// </summary>
    /// <value>A non-empty list; the instance is created only when validation failed.
    /// <para>非空列表；仅在校验失败时创建实例。</para></value>
    public IReadOnlyList<DriveValidationFailure> Failures { get; }

    private static string BuildMessage(IReadOnlyList<DriveValidationFailure> failures)
    {
        var details = string.Join(
            "; ",
            failures.Select(failure =>
                $"{failure.Drive.DisplayName} ({failure.Drive.DevicePath}): "
                + string.Join(", ", failure.Issues.Select(issue => issue.Issue))));
        return $"The image cannot be written to the selected targets: {details}";
    }
}