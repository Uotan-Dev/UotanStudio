namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.IO;

internal static class TempFileHelper
{
    private static readonly List<string> TempFiles = [];
    private static readonly Lock SyncRoot = new();

    /// <summary>
    /// Creates a new temporary file and registers it for cleanup.
    /// <para>创建新临时文件并将其注册以进行清理。</para>
    /// </summary>
    internal static string CreateTempFile()
    {
        var path = Path.GetTempFileName();
        lock (SyncRoot)
        {
            TempFiles.Add(path);
        }
        return path;
    }

    /// <summary>
    /// Safely deletes a temporary file, suppressing any exceptions.
    /// <para>安全删除临时文件，抑制任何异常。</para>
    /// </summary>
    internal static void SafeDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                lock (SyncRoot)
                {
                    TempFiles.Remove(path);
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Cleans up all registered temporary files.
    /// <para>清理所有已注册的临时文件。</para>
    /// </summary>
    internal static void CleanupAll()
    {
        lock (SyncRoot)
        {
            foreach (var path in TempFiles)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                }
            }
            TempFiles.Clear();
        }
    }
}