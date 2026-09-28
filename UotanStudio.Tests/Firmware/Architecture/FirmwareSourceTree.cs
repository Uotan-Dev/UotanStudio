using Xunit;

namespace UotanStudio.Tests.Firmware.Architecture;

/// <summary>
/// Locates the checked-out <c>UotanStudio.Firmware</c> source tree for architecture tests.
/// <para>为架构测试定位检出的 <c>UotanStudio.Firmware</c> 源码树。</para>
/// </summary>
public static class FirmwareSourceTree
{
    /// <summary>
    /// Returns the firmware project directory, or skips the calling test when the repository is not available to the
    /// test host.
    /// <para>返回固件工程目录；测试宿主找不到仓库时跳过调用方测试。</para>
    /// </summary>
    /// <returns>Absolute path of the <c>UotanStudio.Firmware</c> directory.</returns>
    public static string LocateFirmwareDirectory()
    {
        if (!FirmwareContextDependencyScanner.TryFindRepositoryRoot(AppContext.BaseDirectory, out var repositoryRoot))
        {
            Assert.Skip($"No UotanStudio.slnx found above AppContext.BaseDirectory '{AppContext.BaseDirectory}'; the firmware source tree is unavailable to this test host.");
        }

        var firmwareDirectory = Path.Combine(repositoryRoot!, "UotanStudio.Firmware");
        if (!Directory.Exists(firmwareDirectory))
        {
            Assert.Skip($"Firmware source directory '{firmwareDirectory}' is missing; the library was not checked out next to the solution.");
        }

        return firmwareDirectory;
    }
}