namespace UotanStudio.Net.IO;

/// <summary>
/// Publishes a fully written staged file over its target with a single overwrite move.
/// <para>以单次覆盖移动把已完整写出的暂存文件发布到目标路径。</para>
/// </summary>
internal static class AtomicPublish
{
    /// <summary>
    /// Moves <paramref name="stagedPath"/> onto <paramref name="targetPath"/>, deleting the staged file when the move fails.
    /// <para>把 <paramref name="stagedPath"/> 移动到 <paramref name="targetPath"/>；移动失败时删除暂存文件。</para>
    /// </summary>
    /// <param name="stagedPath">Completed staging file placed in the target directory by the caller.
    /// <para>调用方放在目标目录中、已完整写出的暂存文件。</para></param>
    /// <param name="targetPath">Destination that is replaced only when the move itself succeeds.
    /// <para>仅在移动本身成功时才被替换的目标。</para></param>
    public static void Publish(string stagedPath, string targetPath)
    {
        try
        {
            File.Move(stagedPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(stagedPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}