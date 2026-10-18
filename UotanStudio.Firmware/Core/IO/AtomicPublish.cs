namespace UotanStudio.Firmware.Core.IO;

/// <summary>
/// Publishes a fully written staged file over its target in a single overwrite move, the repo-wide atomic
/// publication step: callers write to a staging path first and only replace the destination once the staged
/// content is complete, so a failure never leaves a partially written target.
/// <para>以单次覆盖移动把已完整写出的暂存文件发布到其目标路径，即仓库统一的原子发布步骤：调用方先写入暂存路径，
/// 只在暂存内容完整后才替换目标，因此失败不会留下写了一半的目标文件。</para>
/// </summary>
/// <remarks>
/// This helper deliberately constructs no paths: the staging name convention stays with
/// <see cref="TempFileHelper.CreateAdjacentPath(string)"/> and the scratch factories, and each caller decides
/// where its target lives. It models only the move and the staged-file cleanup that exists solely because of
/// that move; cleanup for the staging write itself remains at the call site.
/// <para>本辅助类型刻意不构造任何路径：暂存命名约定仍由 <see cref="TempFileHelper.CreateAdjacentPath(string)"/>
/// 与刮擦路径工厂持有，目标位置由每个调用方决定。它只建模移动本身以及专属于该移动的暂存文件清理；
/// 针对暂存写入阶段的清理仍留在调用点。</para>
/// </remarks>
public static class AtomicPublish
{
    /// <summary>Moves <paramref name="stagedPath"/> onto <paramref name="targetPath"/>, replacing any existing target, and deletes the staged file when the move fails before re-throwing the original exception untouched.
    /// <para>将 <paramref name="stagedPath"/> 移动到 <paramref name="targetPath"/> 上并覆盖已存在的目标； 移动失败时先删除暂存文件，再原样重抛原始异常。</para></summary>
    /// <param name="stagedPath">The completed staging file to publish; it is consumed by a successful move and removed by a failed one. Never pass the destination here: on failure this method deletes the staged path.
    /// <para>已写出的待发布暂存文件；移动成功时被消耗，移动失败时被删除。切勿在此传入目标路径：失败时本方法删除的是暂存路径。</para></param>
    /// <param name="targetPath">The destination the staged file is published onto; an existing file there is replaced only when the move itself succeeds.
    /// <para>暂存文件发布到的目标路径；仅当移动本身成功时才会替换其上已存在的文件。</para></param>
    /// <exception cref="IOException">The move failed; the staged file has been removed and <paramref name="targetPath"/> keeps its previous content.
    /// <para>移动失败；暂存文件已被删除，<paramref name="targetPath"/> 保留原有内容。</para></exception>
    /// <exception cref="UnauthorizedAccessException">The move was denied by the file system or <paramref name="targetPath"/> is not a writable file position; the staged file has been removed and the exception is re-thrown unchanged.
    /// <para>移动被文件系统拒绝，或 <paramref name="targetPath"/> 不是可写入的文件位置；暂存文件已被删除并原样重抛。</para></exception>
    public static void Publish(string stagedPath, string targetPath)
    {
        try
        {
            File.Move(stagedPath, targetPath, overwrite: true);
        }
        catch
        {
            TempFileHelper.SafeDelete(stagedPath);
            throw;
        }
    }
}