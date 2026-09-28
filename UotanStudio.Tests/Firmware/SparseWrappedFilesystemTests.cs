using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Conversion;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Tests filesystem parsing through sparse logical streams and recursive unpacking.
/// <para>验证稀疏逻辑流上的文件系统解析与级联解包。</para>
/// </summary>
public sealed class SparseWrappedFilesystemTests
{
    [Fact]
    public void SparseWrappedExt4_ParserReadsLogicalFileContents()
    {
        using var fixture = BuildExt4Fixture();

        using var sparse = SparseReader.FromImageFile(fixture.SparsePath);
        using var logical = new SparseReadStream(sparse);
        using var parser = new Ext4Parser(logical);
        using var output = new MemoryStream();
        parser.ReadFile("blob.bin", output);
        Assert.Equal(BinaryBlob, output.ToArray());
    }

    [Fact]
    public void SparseWrappedErofs_ParserReadsLogicalFileContents()
    {
        using var fixture = BuildErofsFixture();

        using var sparse = SparseReader.FromImageFile(fixture.SparsePath);
        using var logical = new SparseReadStream(sparse);
        using var parser = new ErofsParser(logical);
        Assert.Equal(BinaryBlob, parser.ReadFileBytes("blob.bin"));
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
            Assert.Equal("hello ext4\n", File.ReadAllText(Path.Combine(outDir, "hello.txt")));
            Assert.Equal(BinaryBlob, File.ReadAllBytes(Path.Combine(outDir, "blob.bin")));
            Assert.Equal("inner content\n", File.ReadAllText(Path.Combine(outDir, "subdir", "inner.txt")));
            Assert.Contains("Extracted", result.Summary);
            var metadata = UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.Read(outDir)!;
            Assert.Equal("EXT4", metadata.Configuration["fileSystem"]!.GetValue<string>());
            Assert.NotNull(metadata.Configuration["superblock"]!["journalInum"]);
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
            Assert.Equal("hello erofs\n", File.ReadAllText(Path.Combine(outDir, "hello.txt")));
            var metadata = UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.Read(outDir)!;
            Assert.Equal("EROFS", metadata.Configuration["fileSystem"]!.GetValue<string>());
            Assert.NotNull(metadata.Configuration["superblock"]!["availableComprAlgs"]);
            Assert.Equal(BinaryBlob, File.ReadAllBytes(Path.Combine(outDir, "blob.bin")));
        }
        finally
        {
            DeleteRecursive(outDir);
        }
    }

    [Fact]
    public async Task SparseFormatHandler_UnpackAsync_NonFilesystem_FallsBackToRaw()
    {
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

            var result = await new SparseFormatHandler().UnpackAsync(sparsePath, outDir, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Summary);
            Assert.True(File.Exists(Path.Combine(outDir, "random.raw")));
            Assert.Equal(randomBytes, File.ReadAllBytes(Path.Combine(outDir, "random.raw")));
            var rebuiltPath = Path.Combine(dir, "rebuilt.img");
            var packed = await new SparseFormatHandler().PackAsync(outDir, rebuiltPath, TestContext.Current.CancellationToken);
            Assert.True(packed.Success, packed.Summary);
            using var rebuilt = SparseReader.FromImageFile(rebuiltPath);
            using var decoded = new MemoryStream();
            rebuilt.WriteRawToStream(decoded);
            Assert.Equal(randomBytes, decoded.ToArray());
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
                }
            }

            DeleteRecursive(SourceDir);
        }
    }
}