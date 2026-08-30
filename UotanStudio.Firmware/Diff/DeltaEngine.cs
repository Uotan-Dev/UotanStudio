using BsDiff;

namespace UotanStudio.Firmware.Diff;

/// <summary>
/// bsdiff-based binary delta engine for firmware files: generates a binary patch between a source
/// and a target file, and applies such a patch to reconstruct the target from the source.
/// All file-based operations go through <see cref="FileStream"/> and never throw; failures are
/// reported through <see cref="DeltaResult"/>.
/// <para>基于 bsdiff 的固件二进制差分引擎：在源文件与目标文件之间生成二进制补丁，并将补丁应用于
/// 源文件以还原目标文件。所有文件操作均经 <see cref="FileStream"/> 完成且不抛出异常，失败通过
/// <see cref="DeltaResult"/> 上报。</para>
/// </summary>
public static class DeltaEngine
{
    /// <summary>
    /// Generates a binary delta (patch) from <paramref name="sourcePath"/> to
    /// <paramref name="targetPath"/> and writes it to <paramref name="outputPath"/>.
    /// The method never throws; any failure is reported via <see cref="DeltaResult.Success"/> and
    /// <see cref="DeltaResult.ErrorMessage"/>. BsDiff requires the patch output stream to be seekable,
    /// so the patch is written through a <see cref="FileStream"/>.
    /// <para>生成从 <paramref name="sourcePath"/> 到 <paramref name="targetPath"/> 的二进制补丁并写入
    /// <paramref name="outputPath"/>。方法不抛出异常；任何失败通过 <see cref="DeltaResult.Success"/> 与
    /// <see cref="DeltaResult.ErrorMessage"/> 上报。BsDiff 要求补丁输出流可 seek，因此补丁经
    /// <see cref="FileStream"/> 写入。</para>
    /// </summary>
    /// <param name="sourcePath">Path to the old/source file. 源（旧）文件路径。</param>
    /// <param name="targetPath">Path to the new/target file. 目标（新）文件路径。</param>
    /// <param name="outputPath">Path where the generated patch is written. 生成的补丁写入路径。</param>
    /// <param name="ct">Cancellation token; cancelling aborts the operation and yields a failed result. 取消令牌；取消将中止操作并返回失败结果。</param>
    /// <returns>A <see cref="DeltaResult"/> describing the outcome. 描述结果的 <see cref="DeltaResult"/>。</returns>
    public static Task<DeltaResult> GenerateDeltaAsync(string sourcePath, string targetPath, string outputPath, CancellationToken ct = default)
    {
        // BsDiff.Create 是 CPU 密集型（后缀数组构造），放到线程池执行，避免阻塞调用方线程。
        return Task.Run(() => GenerateDeltaCoreAsync(sourcePath, targetPath, outputPath, ct));
    }

    /// <summary>
    /// Applies the binary delta <paramref name="diffPath"/> to the source file
    /// <paramref name="sourcePath"/> and writes the reconstructed target to
    /// <paramref name="outputPath"/>. The method never throws; any failure is reported via
    /// <see cref="DeltaResult.Success"/> and <see cref="DeltaResult.ErrorMessage"/>. BsDiff requires
    /// the source and patch streams to be seekable, so both are opened as <see cref="FileStream"/>.
    /// <para>将 <paramref name="diffPath"/> 中的二进制补丁应用到源文件 <paramref name="sourcePath"/>，
    /// 把还原出的目标文件写入 <paramref name="outputPath"/>。方法不抛出异常；任何失败通过
    /// <see cref="DeltaResult.Success"/> 与 <see cref="DeltaResult.ErrorMessage"/> 上报。BsDiff 要求
    /// 源流与补丁流均可 seek，因此两者均以 <see cref="FileStream"/> 打开。</para>
    /// </summary>
    /// <param name="sourcePath">Path to the old/source file. 源（旧）文件路径。</param>
    /// <param name="diffPath">Path to the binary patch produced by <see cref="GenerateDeltaAsync"/>. 由 <see cref="GenerateDeltaAsync"/> 生成的补丁文件路径。</param>
    /// <param name="outputPath">Path where the reconstructed target is written. 还原目标文件的写入路径。</param>
    /// <param name="ct">Cancellation token; cancelling aborts the operation and yields a failed result. 取消令牌；取消将中止操作并返回失败结果。</param>
    /// <returns>A <see cref="DeltaResult"/> describing the outcome. 描述结果的 <see cref="DeltaResult"/>。</returns>
    public static Task<DeltaResult> ApplyDeltaAsync(string sourcePath, string diffPath, string outputPath, CancellationToken ct = default)
    {
        // BsDiff.Apply 是 CPU 密集型，放到线程池执行，避免阻塞调用方线程。
        return Task.Run(() => ApplyDeltaCoreAsync(sourcePath, diffPath, outputPath, ct));
    }

    private static async Task<DeltaResult> GenerateDeltaCoreAsync(string sourcePath, string targetPath, string outputPath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return new DeltaResult(false, 0, 0, $"Source file not found: {sourcePath}");
            }

            if (!File.Exists(targetPath))
            {
                return new DeltaResult(false, 0, 0, $"Target file not found: {targetPath}");
            }

            ct.ThrowIfCancellationRequested();

            // BsDiff.Create 仅接受整块字节数组（内部构造后缀数组），此处以 FileStream 读取后交由
            // 库完成计算；BsDiff 无法处理超过 int.MaxValue 字节的输入，长度先行按 int 校验。
            byte[] oldData;
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                oldData = new byte[checked((int)source.Length)];
                await source.ReadExactlyAsync(oldData, ct);
            }

            byte[] newData;
            await using (var target = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                newData = new byte[checked((int)target.Length)];
                await target.ReadExactlyAsync(newData, ct);
            }

            // BsDiff.Create 需要可 seek 的输出流（写完控制块后回填头部长度），因此用 FileStream。
            await using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                await Task.Run(() => BinaryPatch.Create(oldData, newData, output), ct);
            }

            return new DeltaResult(true, oldData.Length, new FileInfo(outputPath).Length, null);
        }
        catch (Exception ex)
        {
            return new DeltaResult(false, 0, 0, ex.Message);
        }
    }

    private static async Task<DeltaResult> ApplyDeltaCoreAsync(string sourcePath, string diffPath, string outputPath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return new DeltaResult(false, 0, 0, $"Source file not found: {sourcePath}");
            }

            if (!File.Exists(diffPath))
            {
                return new DeltaResult(false, 0, 0, $"Patch file not found: {diffPath}");
            }

            ct.ThrowIfCancellationRequested();

            // BsDiff.Apply 需要源流与补丁流均可 seek，输出流顺序写即可；全部使用 FileStream。
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                await Task.Run(
                    () => BinaryPatch.Apply(
                        source,
                        () => new FileStream(diffPath, FileMode.Open, FileAccess.Read, FileShare.Read),
                        output),
                    ct);
            }

            return new DeltaResult(true, new FileInfo(sourcePath).Length, new FileInfo(diffPath).Length, null);
        }
        catch (Exception ex)
        {
            return new DeltaResult(false, 0, 0, ex.Message);
        }
    }
}

/// <summary>
/// Result of a delta (bsdiff) operation, carrying the success flag, the source length and the patch
/// length. Record field order is part of the public contract and must not be changed.
/// <para>差分（bsdiff）操作结果，承载成功标志、源长度与补丁长度。record 字段顺序属于公共契约的一部分，不得更改。</para>
/// </summary>
/// <param name="Success">Whether the operation succeeded; when false, <paramref name="ErrorMessage"/> is set. 操作是否成功；失败时 <paramref name="ErrorMessage"/> 有值。</param>
/// <param name="SourceLength">Length of the source file in bytes; 0 on failure. 源文件字节长度；失败时为 0。</param>
/// <param name="DiffLength">Length of the patch file in bytes; 0 on failure. 补丁文件字节长度；失败时为 0。</param>
/// <param name="ErrorMessage">Failure detail, or null on success. 失败详情；成功时为 null。</param>
public sealed record DeltaResult(bool Success, long SourceLength, long DiffLength, string? ErrorMessage);
