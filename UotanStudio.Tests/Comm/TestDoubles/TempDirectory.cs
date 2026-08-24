using System.Text;

namespace UotanStudio.Tests.Comm.TestDoubles;

/// <summary>
/// A disposable temp directory for test fixtures; removed on dispose.
/// <para>测试夹具用的临时目录，释放时删除。</para>
/// </summary>
public sealed class TempDirectory : IDisposable
{
    /// <summary>Gets the directory path. 目录路径。</summary>
    public string Path { get; }

    /// <summary>Initializes a new temp directory. 创建新的临时目录。</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UotanStudioTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>Writes a UTF-8 text file and returns its full path. 写入 UTF-8 文本文件并返回完整路径。</summary>
    public string Write(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);
        return full;
    }

    /// <summary>Writes a byte file and returns its full path. 写入字节文件并返回完整路径。</summary>
    public string WriteBytes(string relativePath, byte[] content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; temp files may be locked by lingering handles.
            // 尽力清理；临时文件可能被残留句柄占用。
        }
    }
}
