namespace UotanStudio.Services.Compare;

/// <summary>
/// Reads bounded hex-editor buffers and atomically saves byte snapshots.
/// <para>读取有界十六进制编辑缓冲，并原子保存字节快照。</para>
/// </summary>
internal static class HexFileEditService
{
    internal const long MaximumBytes = 256L * 1024 * 1024;

    /// <summary>Reads the expected file length with cancellation.<para>读取预期长度的文件，支持取消。</para></summary>
    internal static async Task<byte[]> ReadAsync(string path, long expectedLength, CancellationToken ct)
    {
        if (path.Length == 0)
        {
            return [];
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedLength || stream.Length > MaximumBytes)
        {
            throw new IOException("The file length changed or exceeds the hex editing limit.");
        }
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    /// <summary>Atomically saves an immutable snapshot beside its destination.<para>通过目标同目录的临时文件原子保存不可变快照。</para></summary>
    internal static async Task SaveAsync(string path, byte[] snapshot, CancellationToken ct)
    {
        var temporary = path + ".save-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, snapshot, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}