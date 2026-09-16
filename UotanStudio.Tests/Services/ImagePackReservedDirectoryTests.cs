using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// <para>打包视图不得把子项目的保留目录（尤其是交付输出目录 <c>delivery/</c>）登记为可打包的解包产物。</para>
/// The pack view must not register a sub-project's reserved folders (especially the delivery output
/// folder <c>delivery/</c>) as packable extraction results.
/// </summary>
public sealed class ImagePackReservedDirectoryTests
{
    [Theory]
    [InlineData("delivery", true)]
    [InlineData("Delivery", true)]
    [InlineData("images", true)]
    [InlineData("output", true)]
    [InlineData(".uotan", true)]
    [InlineData(".deleted", true)]
    [InlineData("system", false)]
    [InlineData("vendor", false)]
    public void PackMode_ReservedDirectories_AreExcluded(string name, bool reserved)
        => Assert.Equal(reserved, ImageViewModel.IsReservedPackDirectory(name));
}