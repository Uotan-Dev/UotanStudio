using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Cross-validates packed images with isolated Linux drivers and native extraction. <para>通过隔离 Linux 驱动及原生提取验证打包镜像。</para></summary>
public sealed class FirmwareMountCheckTests(FirmwareMountFixture fixture, ITestOutputHelper output) : IClassFixture<FirmwareMountFixture>
{
    /// <summary>Verifies read-only mounts, complete trees, file hashes and unchanged images. <para>检查只读挂载、完整目录树、文件哈希及镜像未被修改。</para></summary>
    [Theory]
    [InlineData("ext4-default")]
    [InlineData("ext4-no-journal")]
    [InlineData("erofs-flat")]
    [InlineData("erofs-lz4")]
    [InlineData("erofs-lzma")]
    [InlineData("erofs-deflate")]
    [InlineData("erofs-zstd")]
    [InlineData("erofs-lz4-full-index")]
    [InlineData("erofs-fragments-dedup")]
    [InlineData("erofs-metabox")]
    [InlineData("f2fs-flat")]
    [InlineData("f2fs-lz4")]
    [InlineData("f2fs-zstd")]
    [InlineData("f2fs-lzo")]
    public void PackedImage_MountsInQemu(string name) => output.WriteLine(fixture.VerifyQemu(name));

    /// <summary>Checks DEFLATE extraction independently of guest kernel support. <para>独立于客体内核能力检查 DEFLATE 原生提取。</para></summary>
    [Fact]
    public void ErofsDeflate_NativeExtractionMatchesSource() => output.WriteLine(fixture.VerifyNativeDeflate());
}