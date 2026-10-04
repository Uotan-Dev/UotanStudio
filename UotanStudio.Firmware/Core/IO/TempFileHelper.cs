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
    /// Builds a unique sibling path next to <paramref name="targetPath"/> in the repo-wide
    /// <c>&lt;target&gt;.tmp-&lt;guid&gt;</c> convention, typically for staged publication (write the
    /// staged file first, then move it over <paramref name="targetPath"/>) or for a sibling scratch
    /// directory. Keep the <c>.tmp-</c> infix: the test suite detects leftovers with the <c>*.tmp-*</c> glob.
    /// <para>按仓库统一的 <c>&lt;target&gt;.tmp-&lt;guid&gt;</c> 约定在 <paramref name="targetPath"/>
    /// 旁构造唯一暂存路径，典型用途是先写暂存再原子发布覆盖 <paramref name="targetPath"/>，或作为同目录
    /// 的刮擦临时目录。不要改动 <c>.tmp-</c> 中缀：测试套件以 <c>*.tmp-*</c> 通配符检测残留。</para>
    /// </summary>
    public static string CreateAdjacentPath(string targetPath)
        => targetPath + ".tmp-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Builds a unique scratch path inside the system temporary directory with the given prefix and extension.
    /// <para>在系统临时目录内按给定前缀与扩展名构造唯一的刮擦路径。</para>
    /// </summary>
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