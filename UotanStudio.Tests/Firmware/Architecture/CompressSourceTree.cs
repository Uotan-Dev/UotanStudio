using Xunit;

namespace UotanStudio.Tests.Firmware.Architecture;

/// <summary>
/// Locates the checked-out <c>UotanStudio.Compress</c> source tree for its boundary tests.
/// <para>为 <c>UotanStudio.Compress</c> 的边界测试定位检出的源码树。</para>
/// </summary>
public static class CompressSourceTree
{
    /// <summary>
    /// Returns the compression project directory, or skips the calling test when the repository is not available to
    /// the test host.
    /// <para>返回压缩工程目录；测试宿主找不到仓库时跳过调用方测试。</para>
    /// </summary>
    /// <returns>Absolute path of the <c>UotanStudio.Compress</c> directory.</returns>
    public static string LocateCompressDirectory()
    {
        if (!FirmwareContextDependencyScanner.TryFindRepositoryRoot(AppContext.BaseDirectory, out var repositoryRoot))
        {
            Assert.Skip($"No UotanStudio.slnx found above AppContext.BaseDirectory '{AppContext.BaseDirectory}'; the compression source tree is unavailable to this test host.");
        }

        var compressDirectory = Path.Combine(repositoryRoot!, "UotanStudio.Compress");
        if (!Directory.Exists(compressDirectory))
        {
            Assert.Skip($"Compression source directory '{compressDirectory}' is missing; the library was not checked out next to the solution.");
        }

        return compressDirectory;
    }

    /// <summary>
    /// Reads every C# source file below the compression project directory, skipping build outputs.
    /// <para>读取压缩工程目录下的每个 C# 源文件，跳过构建产物。</para>
    /// </summary>
    /// <returns>Path and content pairs for the boundary tests to analyse.</returns>
    public static IEnumerable<(string Path, string Content)> ReadSourceFiles()
        => FirmwareContextDependencyScanner.ReadSourceFiles(LocateCompressDirectory());
}