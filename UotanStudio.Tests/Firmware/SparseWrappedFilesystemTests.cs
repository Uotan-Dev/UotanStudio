using UotanStudio.Firmware.Erofs;
using UotanStudio.Firmware.Ext4;
using UotanStudio.Firmware.Sparse;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Sparse-wrapped filesystem integration tests: an ext4 / erofs image is built with the builders,
/// wrapped into an Android sparse image, and then verified end-to-end — detection via
/// <see cref="FileOpenMode"/>, loading via the file-manager editor path (through the
/// <see cref="SparseReadStream"/> logical view) and cascade unpacking via
/// <see cref="SparseFormatHandler.UnpackAsync"/>.
/// <para>sparse 包装文件系统集成测试：先用构建器生成 ext4/erofs 镜像，包装为 Android sparse 镜像，
/// 再端到端验证——<see cref="FileOpenMode"/> 检测、经 <see cref="SparseReadStream"/> 逻辑视图的文件管理器
/// 加载路径，以及 <see cref="SparseFormatHandler.UnpackAsync"/> 的级联解包。</para>
/// </summary>
public sealed class SparseWrappedFilesystemTests
{
    [Fact]
    public void SparseWrappedExt4_IsDetectedAsFilesystemImage()
    {
        using var fixture = BuildExt4Fixture();

        Assert.True(FileOpenMode.IsSparseImage(fixture.SparsePath));
        Assert.True(FileOpenMode.IsFilesystemImage(fixture.SparsePath));
        Assert.Equal(EditorOpenMode.FileSystem, FileOpenMode.ResolveMode(fixture.SparsePath));
    }

    [Fact]
    public void SparseWrappedErofs_IsDetectedAsFilesystemImage()
    {
        using var fixture = BuildErofsFixture();

        Assert.True(FileOpenMode.IsSparseImage(fixture.SparsePath));
        Assert.True(FileOpenMode.IsFilesystemImage(fixture.SparsePath));
        Assert.Equal(EditorOpenMode.FileSystem, FileOpenMode.ResolveMode(fixture.SparsePath));
    }

    [Fact]
    public async Task SparseFormatHandler_UnpackAsync_CascadesExt4Contents()
    {
        using var fixture = BuildExt4Fixture();
        var outDir = Path.Combine(Path.GetTempPath(), "uotan-sparse-unpack-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new SparseFormatHandler().UnpackAsync(fixture.SparsePath, outDir, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.True(File.Exists(Path.Combine(outDir, "hello.txt")));
            Assert.True(File.Exists(Path.Combine(outDir, "blob.bin")));
            Assert.True(File.Exists(Path.Combine(outDir, "subdir", "inner.txt")));
            Assert.Contains("Extracted", result.Summary);
        }
        finally
        {
            DeleteRecursive(outDir);
        }
    }

    [Fact]
    public async Task SparseFormatHandler_UnpackAsync_CascadesErofsContents()
    {
        using var fixture = BuildErofsFixture();
        var outDir = Path.Combine(Path.GetTempPath(), "uotan-sparse-unpack-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new SparseFormatHandler().UnpackAsync(fixture.SparsePath, outDir, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.True(File.Exists(Path.Combine(outDir, "hello.txt")));
            Assert.True(File.Exists(Path.Combine(outDir, "blob.bin")));
        }
        finally
        {
            DeleteRecursive(outDir);
        }
    }

    [Fact]
    public async Task SparseFormatHandler_UnpackAsync_NonFilesystem_FallsBackToRaw()
    {
        // 内容为随机字节而非文件系统的 sparse：级联探测未命中，回退为转 .raw。
        // A sparse image whose content is random bytes (not a filesystem): the cascade probe misses and
        // the handler falls back to a .raw conversion.
        var dir = Path.Combine(Path.GetTempPath(), "uotan-sparse-raw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var rawPath = Path.Combine(dir, "random.raw");
        var sparsePath = Path.Combine(dir, "random.img");
        var outDir = Path.Combine(dir, "out");
        try
        {
            var randomBytes = new byte[4096 * 8];
            new Random(42).NextBytes(randomBytes);
            File.WriteAllBytes(rawPath, randomBytes);

            SparseImageConverter.ConvertRawToSparse(rawPath, sparsePath);
            Assert.True(FileOpenMode.IsSparseImage(sparsePath));
            Assert.False(FileOpenMode.IsFilesystemImage(sparsePath));

            var result = await new SparseFormatHandler().UnpackAsync(sparsePath, outDir, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.Success);
            Assert.True(File.Exists(Path.Combine(outDir, "random.raw")));
            Assert.Equal(randomBytes, File.ReadAllBytes(Path.Combine(outDir, "random.raw")));
        }
        finally
        {
            DeleteRecursive(dir);
        }
    }

    private static Fixture BuildExt4Fixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var rawPath = Path.Combine(Path.GetTempPath(), "uotan-raw-" + Guid.NewGuid().ToString("N") + ".img");
        var sparsePath = Path.Combine(Path.GetTempPath(), "uotan-sparse-" + Guid.NewGuid().ToString("N") + ".img");

        File.WriteAllText(Path.Combine(root, "hello.txt"), "hello ext4\n");
        File.WriteAllBytes(Path.Combine(root, "blob.bin"), BinaryBlob);
        Directory.CreateDirectory(Path.Combine(root, "subdir"));
        File.WriteAllText(Path.Combine(root, "subdir", "inner.txt"), "inner content\n");

        Ext4Builder.Build(root, rawPath);
        SparseImageConverter.ConvertRawToSparse(rawPath, sparsePath);
        return new Fixture(sparsePath, rawPath, root);
    }

    private static Fixture BuildErofsFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var rawPath = Path.Combine(Path.GetTempPath(), "uotan-raw-" + Guid.NewGuid().ToString("N") + ".img");
        var sparsePath = Path.Combine(Path.GetTempPath(), "uotan-sparse-" + Guid.NewGuid().ToString("N") + ".img");

        File.WriteAllText(Path.Combine(root, "hello.txt"), "hello erofs\n");
        File.WriteAllBytes(Path.Combine(root, "blob.bin"), BinaryBlob);

        new ErofsBuilder().Build(root, rawPath);
        SparseImageConverter.ConvertRawToSparse(rawPath, sparsePath);
        return new Fixture(sparsePath, rawPath, root);
    }

    private static readonly byte[] BinaryBlob = [0x00, 0x01, 0x02, 0xFE, 0xFF, 0x80, 0x7F, 0x10, 0x20, 0x30];

    private static void DeleteRecursive(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>
    /// <para>测试用临时文件集（sparse 镜像 / raw 镜像 / 源目录），释放时全部删除。</para>
    /// A temporary fixture (sparse image / raw image / source directory) that deletes everything on dispose.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture(string sparsePath, string rawPath, string sourceDir)
        {
            SparsePath = sparsePath;
            RawPath = rawPath;
            SourceDir = sourceDir;
        }

        public string SparsePath { get; }
        public string RawPath { get; }
        public string SourceDir { get; }

        public void Dispose()
        {
            foreach (var file in new[] { SparsePath, RawPath })
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // 清理失败不影响测试结果。
                    // Cleanup failures must not affect the test result.
                }
            }

            DeleteRecursive(SourceDir);
        }
    }
}