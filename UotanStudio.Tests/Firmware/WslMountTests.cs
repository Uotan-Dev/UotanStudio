using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Verifies generated images through the WSL2 kernel. <para>通过 WSL2 内核验证生成的镜像。</para></summary>
public class WslMountTests
{
    [Theory]
    [InlineData("ext4", "", null)]
    [InlineData("erofs", "", null)]
    [InlineData("erofs", "lz4", "CONFIG_EROFS_FS_ZIP")]
    [InlineData("erofs", "lzma", "CONFIG_EROFS_FS_ZIP_LZMA")]
    [InlineData("erofs", "deflate", "CONFIG_EROFS_FS_ZIP_DEFLATE")]
    [InlineData("erofs", "zstd", "CONFIG_EROFS_FS_ZIP_ZSTD")]
    [InlineData("f2fs", "", null)]
    [InlineData("f2fs", "lz4", "CONFIG_F2FS_FS_COMPRESSION")]
    [InlineData("f2fs", "zstd", "CONFIG_F2FS_FS_COMPRESSION")]
    [InlineData("f2fs", "lzo", "CONFIG_F2FS_FS_COMPRESSION")]
    public void MountReadOnly_MatchesDirectoryTreeAndHashes(string format, string compression, string? requiredConfiguration)
    {
        var capability = KernelMountVerifier.Probe(format, requiredConfiguration);
        if (!capability.Available) Assert.Skip(capability.Detail);
        using var source = new TempDirectory();
        using var output = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(source.Path, "nested", "empty"));
        File.WriteAllText(Path.Combine(source.Path, "hello.txt"), "kernel mount verification\n");
        File.WriteAllText(Path.Combine(source.Path, "nested", "name with spaces.txt"), "nested file\n");
        File.WriteAllBytes(Path.Combine(source.Path, "empty.bin"), []);
        var random = new byte[64 * 1024 + 7];
        new Random(20260907).NextBytes(random);
        File.WriteAllBytes(Path.Combine(source.Path, "random.bin"), random);
        File.WriteAllText(Path.Combine(source.Path, "large.txt"), string.Concat(Enumerable.Repeat("abcdefgh01234567: mount conformance\n", 40000)));
        var image = Path.Combine(output.Path, "image.img");
        switch (format)
        {
            case "ext4": Ext4Builder.Build(source.Path, image); break;
            case "erofs":
                new ErofsBuilder().Build(source.Path, image, new ErofsOptions
                {
                    Compress = compression.Length > 0,
                    Algorithm = compression.Length == 0 ? ErofsCompressionAlgorithm.Lz4 : Enum.Parse<ErofsCompressionAlgorithm>(compression, true)
                });
                break;
            case "f2fs":
                F2fsBuilder.Build(source.Path, image, new F2fsOptions
                {
                    EnableCompression = compression.Length > 0,
                    CompressionAlgorithm = compression.Length == 0 ? CompressionAlgorithm.Lz4 : Enum.Parse<CompressionAlgorithm>(compression, true)
                });
                break;
        }
        var result = KernelMountVerifier.Verify(image, source.Path, format);
        Assert.True(result.Ok, result.Output);
    }
}