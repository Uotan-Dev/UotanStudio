namespace UotanStudio.Firmware.Core.IO;

/// <summary>
/// Shared helpers for transient files: a registry-based cleanup list and uniform unique path factories
/// that replace per-module hand-rolled temp naming.
/// <para>瞬态文件的共享辅助：基于注册表的清理列表，以及统一替代各模块手写临时命名的唯一路径工厂。</para>
/// </summary>
public static class TempFileHelper
{
    private static readonly List<string> TempFiles = [];
    private static readonly Lock SyncRoot = new();

    /// <summary>
    /// Creates a new temporary file and registers it for cleanup.
    /// <para>创建新临时文件并将其注册以进行清理。</para>
    /// </summary>
    public static string CreateTempFile()
    {
        var path = Path.GetTempFileName();
        lock (SyncRoot)
        {
            TempFiles.Add(path);
        }
        return path;
    }

    /// <summary>
    /// Builds a unique sibling temporary path next to the target path.
    /// <para>在目标路径旁构造唯一的同目录临时路径。</para>
    /// </summary>
    /// <param name="targetPath">Destination path the staged file sits next to; it is used verbatim as the
    /// prefix and does not have to exist. <para>暂存文件所相邻的目标路径；原样作为前缀使用，不要求已经存在。</para></param>
    /// <returns><paramref name="targetPath"/> followed by <c>.tmp-</c> and a fresh GUID, so every call yields a
    /// distinct path without creating any file.
    /// <para>由 <paramref name="targetPath"/> 加 <c>.tmp-</c> 与新 GUID 组成，每次调用结果都不同，且不会创建文件。</para></returns>
    public static string CreateAdjacentPath(string targetPath)
        => targetPath + ".tmp-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Builds a unique scratch path inside the system temporary directory with the given prefix and extension.
    /// <para>在系统临时目录内按给定前缀与扩展名构造唯一的刮擦路径。</para>
    /// </summary>
    /// <param name="prefix">File-name prefix placed before the GUID; an empty value leaves a name that starts
    /// with the separating dash. <para>置于 GUID 之前的文件名前缀；为空时名称以分隔短横线开头。</para></param>
    /// <param name="extension">File-name suffix appended after the GUID, for example <c>.dat</c>; defaults to
    /// <c>.tmp</c>. <para>追加在 GUID 之后的文件名后缀，例如 <c>.dat</c>；默认为 <c>.tmp</c>。</para></param>
    /// <returns>A path inside the system temporary directory built from <paramref name="prefix"/>, a fresh
    /// GUID and <paramref name="extension"/>; no file is created. <para>系统临时目录内由前缀、新 GUID 与扩展名组成的路径，不会创建文件。</para></returns>
    public static string CreateScratchPath(string prefix, string extension = ".tmp")
        => CreateScratchPathIn(Path.GetTempPath(), prefix, extension);

    /// <summary>
    /// Builds a unique scratch path inside <paramref name="directory"/> with the given prefix and extension.
    /// <para>在 <paramref name="directory"/> 内按给定前缀与扩展名构造唯一的刮擦路径。</para>
    /// </summary>
    public static string CreateScratchPathIn(string directory, string prefix, string extension = ".tmp")
        => Path.Combine(directory, $"{prefix}-{Guid.NewGuid():N}{extension}");

    /// <summary>
    /// Safely deletes a temporary file, suppressing any exceptions.
    /// <para>安全删除临时文件，抑制任何异常。</para>
    /// </summary>
    /// <param name="path">File to remove; <see langword="null"/>, empty values and paths that do not exist are
    /// ignored, and a failed delete is swallowed rather than reported.
    /// <para>待删除的文件；<see langword="null"/>、空值与不存在的路径都会被忽略，删除失败同样被吞掉而不上报。</para></param>
    public static void SafeDelete(string? path)
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
    public static void CleanupAll()
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