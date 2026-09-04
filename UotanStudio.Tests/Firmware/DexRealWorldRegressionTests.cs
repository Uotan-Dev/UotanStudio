using UotanStudio.Firmware.Dex;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// 真实世界 DEX 文件回归测试：magisk classes2.dex 解析时可能出现
/// "Index was outside the bounds of the array"。
/// <para>Real-world DEX regression test: the magisk classes2.dex can fail with
/// "Index was outside the bounds of the array".</para>
/// </summary>
public class DexRealWorldRegressionTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Firmware", "Files", "magisk_classes2.dex");

    [Fact]
    public void LoadRealMagiskDex_DoesNotThrow()
    {
        var dex = DexFile.Load(FixturePath);
        Assert.NotEmpty(dex.Classes);
        Assert.NotEmpty(dex.Strings);
        Assert.NotEmpty(dex.Methods);
        Assert.Contains("classes:", dex.Describe());
    }

    [Fact]
    public void RealMagiskDex_RoundTripsThroughWriter()
    {
        var dex = DexFile.Load(FixturePath);
        var bytes = dex.ToArray();
        Assert.True(bytes.Length > 0);
        var reloaded = DexFile.Load(bytes);
        Assert.Equal(dex.Classes.Count, reloaded.Classes.Count);
        Assert.Equal(dex.Methods.Count, reloaded.Methods.Count);
    }
}