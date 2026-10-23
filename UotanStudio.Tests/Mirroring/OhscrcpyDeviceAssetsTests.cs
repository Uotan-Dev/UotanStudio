using UotanStudio.Mirroring.Ohscrcpy;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

/// <summary>
/// Guards the embedded HarmonyOS device payload: the zstd frames plus manifest must materialize into a real directory
/// whose files the existing selection and push code can consume by name.
/// <para>守护内嵌的鸿蒙设备载荷：zstd 帧与清单必须物化成真实目录，现有选择与推送代码可按文件名消费。</para>
/// </summary>
public class OhscrcpyDeviceAssetsTests
{
    private const int DeviceLibraryCount = 15;

    [Fact]
    public void RuntimeDirectory_EmbeddedBundle_MaterializesEveryDeviceLibrary()
    {
        var directory = OhscrcpyDeviceAssets.RuntimeDirectory;
        var libraries = Directory.GetFiles(directory, "*.so");

        Assert.True(Directory.Exists(directory));
        Assert.Equal(DeviceLibraryCount, libraries.Length);
        Assert.All(libraries, path => Assert.True(new FileInfo(path).Length > 0));
    }

    [Fact]
    public void RuntimeDirectory_EmbeddedBundle_KeepsTheOriginalFileNames()
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