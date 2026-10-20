using System.Text;

namespace UotanStudio.Tests.TestInfrastructure;

/// <summary>
/// A disposable temp directory for test fixtures; removed on dispose.
/// <para>测试夹具用的临时目录，释放时删除。</para>
/// </summary>
public sealed class TempDirectory : IDisposable
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Gets the directory path.
    /// <para>目录路径。</para>
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Initializes a new temp directory.
    /// <para>创建新的临时目录。</para>
    /// </summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UotanStudioTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>
    /// Writes a UTF-8 text file and returns its full path.
    /// <para>写入 UTF-8 文本文件并返回完整路径。</para>
    /// </summary>
    public string Write(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Utf8WithoutBom);
        return full;
    }

    /// <summary>
    /// Writes a byte file and returns its full path.
    /// <para>写入字节文件并返回完整路径。</para>
    /// </summary>
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
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                if (!Directory.Exists(Path))
                {
                    return;
                }

                if (attempt > 0)
                {
                    ClearReadOnly(Path);
                }

                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            Thread.Sleep(attempt < 10 ? 25 : 100);
        }
    }

    private static void ClearReadOnly(string root)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}