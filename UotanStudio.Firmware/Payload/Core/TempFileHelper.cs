namespace UotanStudio.Firmware.Payload.Core;

/// <summary>
/// Provides helper methods for managing temporary files with guaranteed cleanup.
/// <para>提供临时文件管理辅助方法，确保清理工作。</para>
/// </summary>
internal static class TempFileHelper
{
    private static readonly List<string> TempFiles = [];
    private static readonly Lock SyncRoot = new();

    /// <summary>
    /// Creates a new temporary file and registers it for cleanup.
    /// <para>创建新临时文件并将其注册以进行清理。</para>
    /// </summary>
    /// <returns>The path to the created temporary file.
    /// <para>创建的临时文件的路径。</para>
    /// </returns>
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
    /// <param name="path">The path to the temporary file.
    /// <para>临时文件的路径。</para>
    /// </param>
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