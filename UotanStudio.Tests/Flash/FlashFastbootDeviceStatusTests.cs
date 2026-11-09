using UotanStudio.Comm.Protocols.SpacemiT;
using UotanStudio.Services;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

/// <summary>
/// A fastboot read fills the page's device status table, and the table is cached per device: the boot stage the endpoint
/// answered at comes first, then one row per variable it reported, and switching away and back restores those rows
/// instead of showing an empty table or the device the user just left.
/// <para>fastboot 读取填入页面的设备状态表，且表格按设备缓存：先写端点应答时的启动阶段，再为它上报的每个变量写一行；切走再
/// 切回时恢复这些行，而不是显示空表或刚离开那台设备的内容。</para>
/// </summary>
public sealed class FlashFastbootDeviceStatusTests
{
    private const string FdlDevice = "usb:k1-fdl";

    [Fact]
    public void Publish_FdlRead_FillsTheStageAndEveryFact()
    {
        var model = NewModel();

        model.PublishFastbootDeviceStatus(FdlRead(), FdlDevice, model.BeginDeviceStatusRefresh());

        Assert.Equal(4, model.DeviceStatus.Count);
        Assert.All(model.DeviceStatus, row => Assert.Equal(FlashDeviceStatusRowKeys.Fastboot, row.Key));
        Assert.Equal(LanguageService.Instance.GetString("Flash_FastbootInfo_Stage"), model.DeviceStatus[0].Name);
        Assert.Equal(LanguageService.Instance.GetString("Flash_FastbootInfo_StageFdl"), model.DeviceStatus[0].Value);
        Assert.Equal(LanguageService.Instance.GetString("Flash_FastbootInfo_BromVersion"), model.DeviceStatus[1].Name);
        Assert.Equal("v0.9.1", model.DeviceStatus[1].Value);
    }

    [Fact]
    public void Publish_UbootRead_NamesTheUbootStage()
    {
        var model = NewModel();

        model.PublishFastbootDeviceStatus(
            new FastbootDeviceInfo(FastbootInfoBranch.SpacemiTUboot, SpacemiTBootStage.Uboot,
                [new FastbootInfoFact("Flash_FastbootInfo_UbootVersion", "0.1")], []),
            FdlDevice, model.BeginDeviceStatusRefresh());

        Assert.Equal(2, model.DeviceStatus.Count);
        Assert.Equal(LanguageService.Instance.GetString("Flash_FastbootInfo_StageUboot"),
            model.DeviceStatus[0].Value);
        Assert.Equal("0.1", model.DeviceStatus[1].Value);
    }

    [Fact]
    public void Publish_GenericReadWithoutFacts_LeavesTheTableEmpty()
    {
        var model = NewModel();

        model.PublishFastbootDeviceStatus(
            new FastbootDeviceInfo(FastbootInfoBranch.Generic, SpacemiTBootStage.Unknown, [],
                [("boot", 4L * 1024 * 1024)]),
            FdlDevice, model.BeginDeviceStatusRefresh());

        // A generic gadget reports no stage and no facts, so the table describes nothing rather than keeping rows of a
        // previously read device.
        // 通用 gadget 既不报阶段也不报事实，因此表格什么都不描述，而不是留着上一台设备的行。
        Assert.Empty(model.DeviceStatus);
    }

    [Fact]
    public void Publish_StaleRead_IsDropped()
    {
        var model = NewModel();
        var epoch = model.BeginDeviceStatusRefresh();
        _ = model.BeginDeviceStatusRefresh();

        model.PublishFastbootDeviceStatus(FdlRead(), FdlDevice, epoch);

        Assert.Empty(model.DeviceStatus);
    }

    [Fact]
    public void Publish_ReplacesThePreviousRead()
    {
        var model = NewModel();
        model.PublishFastbootDeviceStatus(FdlRead(), FdlDevice, model.BeginDeviceStatusRefresh());

        model.PublishFastbootDeviceStatus(
            new FastbootDeviceInfo(FastbootInfoBranch.SpacemiTUboot, SpacemiTBootStage.Uboot,
                [new FastbootInfoFact("Flash_FastbootInfo_Product", "k1-pro")], []),
            FdlDevice, model.BeginDeviceStatusRefresh());

        Assert.Equal(2, model.DeviceStatus.Count);
        Assert.Equal(LanguageService.Instance.GetString("Flash_FastbootInfo_StageUboot"),
            model.DeviceStatus[0].Value);
        Assert.Equal("k1-pro", model.DeviceStatus[1].Value);
    }

    [Fact]
    public void Restore_SwitchingAwayAndBack_RebuildsThatDevicesRows()
    {
        var model = NewModel();
        model.PublishFastbootDeviceStatus(FdlRead(), FdlDevice, model.BeginDeviceStatusRefresh());

        model.RestoreDeviceStatus("usb:other");
        Assert.Empty(model.DeviceStatus);

        model.RestoreDeviceStatus(FdlDevice);

        Assert.Equal(4, model.DeviceStatus.Count);
        Assert.Equal(LanguageService.Instance.GetString("Flash_FastbootInfo_StageFdl"), model.DeviceStatus[0].Value);
        Assert.Equal("v0.9.1", model.DeviceStatus[1].Value);
    }

    [Fact]
    public void Restore_NeverReadDeviceOrNoSelection_ShowsTheEmptyState()
    {
        var model = NewModel();
        model.PublishFastbootDeviceStatus(FdlRead(), FdlDevice, model.BeginDeviceStatusRefresh());

        model.RestoreDeviceStatus("usb:never-read");
        Assert.Empty(model.DeviceStatus);

        model.RestoreDeviceStatus(FdlDevice);
        Assert.NotEmpty(model.DeviceStatus);

        model.RestoreDeviceStatus(null);
        Assert.Empty(model.DeviceStatus);
    }

    [Fact]
    public void Restore_InvalidatesAReadStillInFlight()
    {
        var model = NewModel();
        var epoch = model.BeginDeviceStatusRefresh();

        // The user switched devices while the read was running, so its result describes the device that is no longer
        // selected and must not fill the new device's table.
        // 读取进行期间用户切换了设备，因此其结果描述的是已不再被选中的设备，不得填入新设备的表格。
        model.RestoreDeviceStatus("usb:other");
        model.PublishFastbootDeviceStatus(FdlRead(), FdlDevice, epoch);

        Assert.Empty(model.DeviceStatus);
    }

    [Fact]
    public void Publish_EmptyRows_ForgetsTheCachedDevice()
    {
        var model = NewModel();
        model.PublishFastbootDeviceStatus(FdlRead(), FdlDevice, model.BeginDeviceStatusRefresh());

        model.PublishFastbootDeviceStatus(
            new FastbootDeviceInfo(FastbootInfoBranch.Generic, SpacemiTBootStage.Unknown, [], []),
            FdlDevice, model.BeginDeviceStatusRefresh());

        // A re-read that produced nothing drops the older rows instead of leaving them cached for the next switch back.
        // 一次没有产生内容的重新读取会删除旧行，而不是把它们留到下次切回时再显示。
        model.RestoreDeviceStatus(FdlDevice);
        Assert.Empty(model.DeviceStatus);
    }

    [Fact]
    public void ReadInfo_DoesNotTouchThePartitionGridItWasGiven()
    {
        var model = NewModel();
        model.Partitions.Add(new FlashPartitionEntry { Name = "boot", Size = "64.0 MB" });

        model.PublishFastbootDeviceStatus(FdlRead(), FdlDevice, model.BeginDeviceStatusRefresh());

        // The information read publishes device status only; the partition list belongs to the package and to the
        // separate partition-table action.
        // 信息读取只发布设备状态；分区列表属于刷机包与独立的分区表动作。
        Assert.Single(model.Partitions);
        Assert.Equal("boot", model.Partitions[0].Name);
    }

    [Fact]
    public void ShouldReplacePartitionRows_OnlyWhenTheDeviceTableWasReadAndNoPackageOwnsTheRows()
    {
        var withoutTable = new FastbootDeviceInfo(FastbootInfoBranch.Generic, SpacemiTBootStage.Unknown, [], []);

        Assert.False(FlashViewModel.ShouldReplacePartitionRows(withoutTable, spacemiTPackageLoaded: false),
            "a read that never enumerated the table must leave the grid alone");
        Assert.False(FlashViewModel.ShouldReplacePartitionRows(FdlRead(), spacemiTPackageLoaded: false),
            "the BootROM download mode has no table to replace the grid with");
        Assert.False(FlashViewModel.ShouldReplacePartitionRows(
            ReadWithTable(), spacemiTPackageLoaded: true),
            "a loaded SpacemiT package owns the rows that drive its actions");
        Assert.True(FlashViewModel.ShouldReplacePartitionRows(ReadWithTable(), spacemiTPackageLoaded: false));
    }

    private static FastbootDeviceInfo ReadWithTable() => new(FastbootInfoBranch.Generic,
        SpacemiTBootStage.Unknown, [], [("boot", 4L * 1024 * 1024)])
    {
        PartitionTableAvailable = true
    };

    private static FastbootDeviceInfo FdlRead() => new(FastbootInfoBranch.SpacemiTFdl, SpacemiTBootStage.BromFdl,
        [
            new FastbootInfoFact("Flash_FastbootInfo_BromVersion", "v0.9.1"),
            new FastbootInfoFact("Flash_FastbootInfo_MtdSize", "2M"),
            new FastbootInfoFact("Flash_FastbootInfo_BlockSize", "7.3G")
        ],
        []);

    private static FlashViewModel NewModel() => new(FlashService.Default, LanguageService.Instance);
}