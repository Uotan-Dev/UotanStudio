namespace UotanStudio.Services.Flash;

/// <summary>
/// Publishes a completed staging directory under its final name, retrying the short-lived access denial Windows
/// reports while an antivirus scanner or the search indexer still holds a freshly written file open.
/// <para>把已完成的暂存目录发布为其最终名称；对 Windows 在杀毒扫描器或搜索索引仍持有新写入文件时所报告的
/// 短暂访问拒绝进行重试。</para>
/// </summary>
internal static class FlashStagingDirectory
{
    private const int MaximumAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// Moves <paramref name="staging"/> onto <paramref name="output"/>, retrying transient access denials.
    /// <para>把 <paramref name="staging"/> 移动到 <paramref name="output"/>，并对短暂的访问拒绝进行重试。</para>
    /// </summary>
    /// <param name="staging">Completed staging directory. <para>已完成的暂存目录。</para></param>
    /// <param name="output">Final directory name. <para>最终目录名。</para></param>
    /// <exception cref="IOException">The move still failed after the last attempt.
    /// <para>最后一次尝试后移动仍然失败。</para></exception>
    /// <exception cref="UnauthorizedAccessException">The move is persistently denied, not transiently locked.
    /// <para>移动被持续拒绝，而不是被短暂占用。</para></exception>
    public static void Publish(string staging, string output)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(staging, output);
                return;
            }
            catch (Exception exception) when (attempt < MaximumAttempts &&
                exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }
}
