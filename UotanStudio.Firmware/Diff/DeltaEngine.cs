using BsDiff;

namespace UotanStudio.Firmware.Diff;

public static class DeltaEngine
{
    /// <summary>
    /// Generates a binary delta (patch) from sourcePath to targetPath and writes it to outputPath.
    /// <para>生成从 sourcePath 到 targetPath 的二进制补丁并写入 outputPath。</para>
    /// </summary>
    public static Task<DeltaResult> GenerateDeltaAsync(string sourcePath, string targetPath, string outputPath, CancellationToken ct = default)
    {

        return Task.Run(() => GenerateDeltaCoreAsync(sourcePath, targetPath, outputPath, ct));
    }

    /// <summary>
    /// Applies the binary delta diffPath to the source file sourcePath and writes the reconstructed target to outputPath.
    /// <para>将 diffPath 中的二进制补丁应用到源文件 sourcePath， 把还原出的目标文件写入 outputPath。</para>
    /// </summary>
    public static Task<DeltaResult> ApplyDeltaAsync(string sourcePath, string diffPath, string outputPath, CancellationToken ct = default)
    {

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

public sealed record DeltaResult(bool Success, long SourceLength, long DiffLength, string? ErrorMessage);
