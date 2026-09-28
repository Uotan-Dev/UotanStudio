using UotanStudio.Firmware.OtaDelta.Diff;
namespace UotanStudio.Firmware.OtaDelta.Diff;

/// <summary>Creates and applies streaming BSDIFF40 patches. <para>流式生成和应用 BSDIFF40 补丁。</para></summary>
public static class DeltaEngine
{
    /// <summary>Generates a patch and atomically commits it. <para>生成补丁并原子提交。</para></summary>
    /// <param name="sourcePath">
    /// Base file the diff is computed against; opened for reading and must exist. <para>计算差异所依据的基文件；以只读方式打开且必须存在。</para>
    /// </param>
    /// <param name="targetPath">
    /// File the patch should reproduce; opened for reading and must exist. <para>补丁应当还原的文件；以只读方式打开且必须存在。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination patch file, created after its parent directory; an existing file is replaced only after the
    /// whole patch has been written successfully.
    /// <para>目标补丁文件，写出前会先创建其父目录；只有整份补丁成功写出后才会替换已有文件。</para>
    /// </param>
    /// <param name="ct">
    /// Cancels before and after the patch is written and is forwarded to the streaming writer.
    /// <para>在补丁写出前后各检查一次，并传递给流式写入器。</para>
    /// </param>
    /// <returns>
    /// A task whose result carries <see cref="DeltaResult.Success"/> as <see langword="true"/> with the source and
    /// patch lengths on success, or <see langword="false"/> with both lengths at <c>0</c> and the failure text in
    /// <see cref="DeltaResult.ErrorMessage"/>. Cancellation is the only condition that faults the task.
    /// <para>成功时结果中 <see cref="DeltaResult.Success"/> 为 <see langword="true"/> 并携带源与补丁长度；失败时为
    /// <see langword="false"/>，两个长度均为 <c>0</c> 且失败文本位于 <see cref="DeltaResult.ErrorMessage"/>。只有取消
    /// 会让任务进入故障状态。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled; the temporary file is removed and <paramref name="outputPath"/> keeps
    /// its previous content. <para><paramref name="ct"/> 被取消；临时文件会被删除，<paramref name="outputPath"/> 保留原有内容。</para>
    /// </exception>
    public static Task<DeltaResult> GenerateDeltaAsync(string sourcePath, string targetPath, string outputPath, CancellationToken ct = default)
        => Task.Run(() => Run(sourcePath, targetPath, outputPath, generate: true, ct), ct);

    /// <summary>Applies a patch and preserves the existing destination on failure. <para>应用补丁，失败时保留已有目标。</para></summary>
    /// <param name="sourcePath">
    /// Base file the patch transforms; opened for reading and must exist. <para>被补丁转换的基文件；以只读方式打开且必须存在。</para>
    /// </param>
    /// <param name="diffPath">
    /// BSDIFF40 patch read for its declared output length and its diff and control streams; must exist.
    /// <para>BSDIFF40 补丁，用于读取其声明的输出长度以及差异与控制流；必须存在。</para>
    /// </param>
    /// <param name="outputPath">
    /// Reconstructed file, created after its parent directory; an existing file is replaced only after the whole
    /// image has been written successfully.
    /// <para>还原出的文件，写出前会先创建其父目录；只有整份镜像成功写出后才会替换已有文件。</para>
    /// </param>
    /// <param name="ct">
    /// Cancels before and after reconstruction and is forwarded to the streaming reader and writer.
    /// <para>在还原前后各检查一次，并传递给流式读取器与写入器。</para>
    /// </param>
    /// <returns>
    /// A task whose result reports success with the source length and the patch length, or failure with both
    /// lengths at <c>0</c> and the reason in <see cref="DeltaResult.ErrorMessage"/>. A malformed or truncated patch
    /// is reported through that failure result instead of an exception.
    /// <para>返回的任务结果在成功时携带源长度与补丁长度，失败时两个长度均为 <c>0</c> 并在
    /// <see cref="DeltaResult.ErrorMessage"/> 给出原因。畸形或被截断的补丁通过该失败结果上报，而非抛出异常。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled; the temporary file is removed and <paramref name="outputPath"/> keeps
    /// its previous content. <para><paramref name="ct"/> 被取消；临时文件会被删除，<paramref name="outputPath"/> 保留原有内容。</para>
    /// </exception>
    public static Task<DeltaResult> ApplyDeltaAsync(string sourcePath, string diffPath, string outputPath, CancellationToken ct = default)
        => Task.Run(() => Run(sourcePath, diffPath, outputPath, generate: false, ct), ct);

    private static DeltaResult Run(string sourcePath, string secondPath, string outputPath, bool generate, CancellationToken ct)
    {
        string? temporaryPath = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            temporaryPath = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            long sourceLength;
            long patchLength;
            using (var source = File.OpenRead(sourcePath))
            using (var second = File.OpenRead(secondPath))
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                sourceLength = source.Length;
                if (generate) BinaryDeltaPatch.Create(source, second, output, ct);
                else BinaryDeltaPatch.Apply(source, second, output, BinaryDeltaPatch.ReadOutputLength(second), ct);
                patchLength = generate ? output.Length : second.Length;
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: true);
            return new DeltaResult(true, sourceLength, patchLength, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new DeltaResult(false, 0, 0, ex.Message); }
        finally { if (temporaryPath is not null) File.Delete(temporaryPath); }
    }
}

/// <summary>Describes a delta operation result. <para>描述差分操作结果。</para></summary>
/// <param name="Success">
/// Whether the patch was written or the reconstruction committed. <para>补丁是否已写出、还原结果是否已提交。</para>
/// </param>
/// <param name="SourceLength">
/// Size in bytes of the base file as opened; <c>0</c> on failure. <para>打开时基文件的字节大小；失败时为 <c>0</c>。</para>
/// </param>
/// <param name="DiffLength">
/// Size in bytes of the patch: the newly written file when generating, the input patch when applying; <c>0</c> on
/// failure. <para>补丁的字节大小：生成时为新写出的文件，应用时为输入补丁；失败时为 <c>0</c>。</para>
/// </param>
/// <param name="ErrorMessage">
/// Failure text taken from the caught exception's message; <see langword="null"/> on success.
/// <para>取自所捕获异常消息的失败文本；成功时为 <see langword="null"/>。</para>
/// </param>
public sealed record DeltaResult(bool Success, long SourceLength, long DiffLength, string? ErrorMessage);