using UotanStudio.Assets;
using UotanStudio.Mirroring.Ohscrcpy;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

/// <summary>
/// Guards the shipped HarmonyOS device bundle: the OpenHarmony capture libraries and UI Test agents must materialize
/// into a real directory whose files the existing selection and push code can consume by name.
/// <para>守护随包的鸿蒙设备资源包：OpenHarmony 采集库与 UI Test 代理必须物化成真实目录，现有选择与推送代码可按文件名消费。</para>
/// </summary>
public class OhscrcpyDeviceAssetsTests
{
    private const int DeviceLibraryCount = 15;

    [Fact]
    public void RuntimeDirectory_ShippedBundle_MaterializesEveryDeviceLibrary()
    {
        var directory = OhscrcpyDeviceAssets.RuntimeDirectory;
        var libraries = Directory.GetFiles(directory, "*.so");

        Assert.Equal(DeviceLibraryCount, AssetCatalog.OhosDevice.Entries.Count);
        Assert.True(Directory.Exists(directory));
        Assert.Equal(DeviceLibraryCount, libraries.Length);
        Assert.All(libraries, path => Assert.True(new FileInfo(path).Length > 0));
    }

    [Fact]
    public void RuntimeDirectory_ShippedBundle_KeepsTheOriginalFileNames()
    {
        var directory = OhscrcpyDeviceAssets.RuntimeDirectory;

        Assert.True(File.Exists(Path.Combine(directory, "libscrcpy_server1.z.so")));
        Assert.True(File.Exists(Path.Combine(directory, "uitest_agent_1.1.12.so")));
        Assert.True(File.Exists(Path.Combine(directory, "uitest_agent_x86_1.1.12.so")));
    }

    [Fact]
    public void RuntimeDirectory_RepeatedAccess_ReturnsTheSameMaterializedDirectory()
    {
        Assert.Equal(OhscrcpyDeviceAssets.RuntimeDirectory, OhscrcpyDeviceAssets.RuntimeDirectory);
    }
}