using UotanStudio.Delivery;
using UotanStudio.Delivery.Build;
using Xunit;

namespace UotanStudio.Tests.Delivery;

/// <summary>
/// Guards the embedded native updater: the zstd frame must materialize into a verified arm64 ELF that passes the same
/// Edify validation the asset must pass before packing.
/// <para>守护内嵌原生更新器：zstd 帧必须解压成已校验的 arm64 ELF，并通过原先松散资产所走的同一套 Edify 校验。</para>
/// </summary>
public class EmbeddedUpdaterAssetTests
{
    private const int UpdaterLength = 2_002_464;

    [Fact]
    public void Embedded_EmbeddedUpdater_PassesEdifyValidationAndWritesTheArm64Elf()
    {
        var path = DeliveryRecoveryUpdater.Embedded();
        var bytes = File.ReadAllBytes(path);

        Assert.True(File.Exists(path));
        Assert.Equal(UpdaterLength, bytes.Length);
        Assert.Equal((byte)0x7F, bytes[0]);
        Assert.Equal((byte)'E', bytes[1]);
        Assert.Equal((byte)'L', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
        Assert.Equal(2, bytes[4]);
        Assert.Equal(1, bytes[5]);
    }

    [Fact]
    public void Extract_RepeatedCall_ReusesTheSameVerifiedFile()
    {
        var first = UpdaterAssets.Extract();
        var second = UpdaterAssets.Extract();

        Assert.Equal(first, second);
        Assert.True(File.Exists(first));
    }
}