using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins the pack-time feature gate: a public pack refuses feature bits whose managed path is not fully
/// implemented, while the direct builder keeps its opt-in behavior for tests that deliberately exercise a
/// partial layout.
/// <para>钉住打包期特性闸门：公开打包会拒绝托管路径尚未完整实现的特性位，而直接构建器保留其显式选择行为，
/// 供刻意验证部分布局的测试使用。</para>
/// </summary>
public sealed class ErofsPackFeatureRejectionTests
{
    [Theory]
    [InlineData(ErofsSuperblock.IncompatDeviceTable)]
    [InlineData(ErofsSuperblock.IncompatZtailpacking)]
    [InlineData(ErofsSuperblock.IncompatFragments)]
    [InlineData(ErofsSuperblock.IncompatXattrPrefixes)]
    public void Build_PackGateRejectsUnsupportedIncompatBit(uint bit)
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[8192]);
        var options = Gated(bit, 0);

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("does not fully implement", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ErofsSuperblock.CompatSharedEaInMetabox)]
    [InlineData(ErofsSuperblock.CompatPlainXattrPfx)]
    [InlineData(ErofsSuperblock.CompatIshareXattrs)]
    public void Build_PackGateRejectsUnsupportedCompatBit(uint bit)
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[8192]);
        var options = Gated(0, bit);

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("does not fully implement", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_PackGateRejectsLayoutImpliedFragments()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[8192]);
        var options = new ErofsOptions
        {
            Compress = false,
            Fragments = true,
            RejectIncompleteFeatures = true,
        };

        Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));
    }

    [Fact]
    public void Build_PackGateRejectsLayoutImpliedZtailpacking()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[8192]);
        var options = new ErofsOptions
        {
            CompressedTailPacking = true,
            RejectIncompleteFeatures = true,
        };

        Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));
    }

    [Fact]
    public void Build_PackGateAllowsDefaultLayout()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[8192]);
        var options = new ErofsOptions { RejectIncompleteFeatures = true };

        var image = new ErofsBuilder().Build(source.Path, options);

        Assert.NotEmpty(image);
    }

    private static ErofsOptions Gated(uint incompat, uint compat) => new()
    {
        Compress = false,
        Features = ErofsFeatureSet.FromWords(compat, incompat),
        FeaturesAuthoritative = true,
        RejectIncompleteFeatures = true,
    };
}
