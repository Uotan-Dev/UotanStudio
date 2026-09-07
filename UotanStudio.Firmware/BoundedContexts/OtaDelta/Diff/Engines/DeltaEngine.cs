namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff;

/// <summary>Creates and applies streaming BSDIFF40 patches. <para>流式生成和应用 BSDIFF40 补丁。</para></summary>
public static class DeltaEngine
{
    /// <summary>Generates a patch and atomically commits it. <para>生成补丁并原子提交。</para></summary>
    public static Task<DeltaResult> GenerateDeltaAsync(string sourcePath, string targetPath, string outputPath, CancellationToken ct = default)
        => Task.Run(() => Run(sourcePath, targetPath, outputPath, generate: true, ct), ct);

    /// <summary>Applies a patch and preserves the existing destination on failure. <para>应用补丁，失败时保留已有目标。</para></summary>
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
public sealed record DeltaResult(bool Success, long SourceLength, long DiffLength, string? ErrorMessage);