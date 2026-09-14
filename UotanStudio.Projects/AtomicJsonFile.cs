using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace UotanStudio.Services.Projects;

internal static class AtomicJsonFile
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<bool> WriteIfChangedAsync<T>(string path, T value, JsonTypeInfo<T> type,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, type);
        if (Encoding.UTF8.GetByteCount(json) > 32 * 1024 * 1024)
            throw new InvalidDataException("Project state exceeds 32 MiB.");
        await Gate.WaitAsync(cancellationToken);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length <= 32 * 1024 * 1024
                && await File.ReadAllTextAsync(path, cancellationToken) == json) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        finally
        {
            try { File.Delete(temporary); }
            finally { Gate.Release(); }
        }
    }
}