using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.UiAbstractions;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

/// <summary>
/// Covers where the EDL device-information read lands: the shared device status table, which is the one place the page
/// shows what a device reported, instead of a second list inside the Qualcomm section.
/// <para>覆盖 EDL 设备信息读取的落点：共享的设备状态表——页面展示设备上报内容的唯一位置，而不是高通分类里另起一个列表。</para>
/// </summary>
public sealed class FlashEdlInfoStatusTests
{
    [Fact]
    public void EdlInfoRows_ProjectOntoTheStatusTable()
    {
        var rows = new[]
        {
            new EdlInfoRow("Flash_EdlInfo_SaharaVersion", "2", EdlInfoStage.Sahara),
            new EdlInfoRow("Flash_EdlInfo_Serial", "0x1234", EdlInfoStage.Sahara),
            new EdlInfoRow("Flash_EdlInfo_Storage", "UFS 128 GiB", EdlInfoStage.Firehose)
        };

        var status = FlashViewModel.BuildEdlStatusRows(rows, new EchoLocalizer());

        Assert.Equal(3, status.Count);
        Assert.All(status, row => Assert.Equal(FlashDeviceStatusRowKeys.EdlInfo, row.Key));
        Assert.Equal(["Flash_EdlInfo_SaharaVersion", "Flash_EdlInfo_Serial", "Flash_EdlInfo_Storage"],
            status.Select(row => row.Name));
        Assert.Equal(["2", "0x1234", "UFS 128 GiB"], status.Select(row => row.Value));
    }

    [Fact]
    public void QualcommSection_ShowsNoInformationListOfItsOwn()
    {
        var view = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "UotanStudio", "Views", "Shell", "FlashView.axaml"));

        Assert.DoesNotContain("EdlInfoGroup", view, StringComparison.Ordinal);
        Assert.DoesNotContain("EdlInfoRows", view, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding DeviceStatus}\"", view, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UotanStudio.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>
    /// A localizer that answers each key with the key itself, so a test can read the mapping without depending on a
    /// shipped translation.
    /// <para>用键本身应答每个键的本地化器，使测试无需依赖已发布的翻译即可读取映射。</para>
    /// </summary>
    private sealed class EchoLocalizer : ILocalizer
    {
        public string GetString(string key) => key;

        public string GetString(string key, params object[] args) => key;
    }
}