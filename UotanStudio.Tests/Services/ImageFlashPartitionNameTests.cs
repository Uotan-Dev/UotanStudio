using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// The target partition name used by the image panel's flash action is derived from the image file
/// name: compound names such as <c>system_ext_a</c> must be preserved verbatim rather than collapsing
/// to <c>system_a</c>, and an A/B suffix must not be treated as a file extension.
/// <para>镜像面板刷入时的目标分区名由镜像文件名推导：必须保留 <c>system_ext_a</c> 等多段分区名，
/// 不能塌缩成 <c>system_a</c>，也不能把槽位后缀当作扩展名截断。</para>
/// </summary>
public sealed class ImageFlashPartitionNameTests
{
    [Theory]
    [InlineData("images/system_ext_a.img", "system_ext_a")]
    [InlineData("images/system_ext_b.img", "system_ext_b")]
    [InlineData("images/system_ext.img", "system_ext")]
    [InlineData("images/system_a.img", "system_a")]
    [InlineData("images/vendor_dlkm_a.img", "vendor_dlkm_a")]
    [InlineData("system_ext_a.img", "system_ext_a")]
    public void FlashPartitionName_PreservesCompoundPartitionNames(string relativePath, string expected)
        => Assert.Equal(expected, ImageViewModel.GetEntryBaseName(new ImageEntryViewModel
        {
            RelativePath = relativePath,
            DisplayName = Path.GetFileName(relativePath)
        }));
}