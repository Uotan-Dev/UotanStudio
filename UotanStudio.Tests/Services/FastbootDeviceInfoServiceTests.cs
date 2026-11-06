using UotanStudio.Comm.Protocols.SpacemiT;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Device-info reads split by fastboot branch: a generic gadget keeps the standard read, while the SpacemiT board is
/// read through its BootROM FDL branch or its U-Boot branch depending on the stage the device reports.
/// <para>设备信息读取按 fastboot 分支拆分：通用 gadget 沿用标准读取，进迭时空板卡则按设备上报的阶段走 BootROM FDL 分支
/// 或 U-Boot 分支。</para>
/// </summary>
public sealed class FastbootDeviceInfoServiceTests
{
    [Theory]
    [InlineData(SpacemiTBootStage.Unknown)]
    [InlineData(SpacemiTBootStage.Uboot)]
    public void ResolveBranch_NonSpacemiTEndpoint_ReadsTheStandardBranch(SpacemiTBootStage stage) =>
        Assert.Equal(FastbootInfoBranch.Generic, FastbootDeviceInfoService.ResolveBranch(false, stage));

    [Fact]
    public void ResolveBranch_BootRomFdl_IsDecidedByTheWireNotTheDescriptors()
    {
        // `version-brom` is a vendor key, so an endpoint that answers it is that stage even when its descriptors did not
        // say so — that is what keeps a board whose vid/pid is not recognised out of a failing `getvar all`.
        // `version-brom` 是厂商键，因此应答它的端点就是该阶段，即使其描述符没有表明——这正是让 vid/pid 未被识别的板卡
        // 不会落入失败的 `getvar all` 的原因。
        Assert.Equal(FastbootInfoBranch.SpacemiTFdl, FastbootDeviceInfoService.ResolveBranch(false,
            SpacemiTBootStage.BromFdl));
        Assert.Equal(FastbootInfoBranch.SpacemiTFdl, FastbootDeviceInfoService.ResolveBranch(true,
            SpacemiTBootStage.BromFdl));
    }

    [Fact]
    public void ResolveBranch_SpacemiTGadget_SplitsTheTwoStages()
    {
        Assert.Equal(FastbootInfoBranch.SpacemiTFdl,
            FastbootDeviceInfoService.ResolveBranch(true, SpacemiTBootStage.BromFdl));
        Assert.Equal(FastbootInfoBranch.SpacemiTUboot,
            FastbootDeviceInfoService.ResolveBranch(true, SpacemiTBootStage.Uboot));

        // A gadget whose stage could not be established keeps the standard read: nothing proves the boot-chain variables
        // are the ones it answers, and a fabricated FDL report would hide the device's own error.
        // 阶段无法确定的 gadget 保持标准读取：没有证据表明它应答的是引导链变量，编造一份 FDL 报告会掩盖设备自身的错误。
        Assert.Equal(FastbootInfoBranch.Generic,
            FastbootDeviceInfoService.ResolveBranch(true, SpacemiTBootStage.Unknown));
    }

    [Fact]
    public void BuildFacts_FdlBranch_ReportsTheBootChainVariablesInOrder()
    {
        var facts = FastbootDeviceInfoService.BuildFacts(FastbootInfoBranch.SpacemiTFdl,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mtd-size"] = "2M",
                ["version-brom"] = "v0.9.1",
                ["blk-size"] = "7.3G",
                ["product"] = "k1x"
            });

        Assert.Equal(
            ["Flash_FastbootInfo_BromVersion", "Flash_FastbootInfo_MtdSize",
             "Flash_FastbootInfo_BlockSize", "Flash_FastbootInfo_Product"],
            facts.Select(fact => fact.LabelKey));
        Assert.Equal(["v0.9.1", "2M", "7.3G", "k1x"], facts.Select(fact => fact.Value));
    }

    [Fact]
    public void BuildFacts_FdlBranch_DropsAbsentAndNullAnswers()
    {
        var facts = FastbootDeviceInfoService.BuildFacts(FastbootInfoBranch.SpacemiTFdl,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["version-brom"] = " v0.9.1 ",
                // The vendor answers NULL for a storage kind the board does not carry; that is not a size to print.
                // 厂商对板卡未装配的存储类型应答 NULL；那不是可以打印的容量。
                ["mtd-size"] = "NULL",
                ["blk-size"] = "   ",
                ["serialno"] = "K1-SERIAL"
            });

        Assert.Equal(
            ["Flash_FastbootInfo_BromVersion", "Flash_FastbootInfo_Serial"],
            facts.Select(fact => fact.LabelKey));
        Assert.Equal("v0.9.1", facts[0].Value);
    }

    [Fact]
    public void BuildFacts_UbootBranch_ReportsUbootFactsAndStorageSizes()
    {
        var facts = FastbootDeviceInfoService.BuildFacts(FastbootInfoBranch.SpacemiTUboot,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["version"] = "0.1",
                ["product"] = "k1-pro",
                ["bootloader"] = "U-Boot 2024.01",
                ["serialno"] = "K1-SERIAL",
                ["mtd-size"] = "2M",
                ["blk-size"] = "7.3G",
                ["partition-size:boot"] = "0x4000000",
                ["is-userspace"] = "no"
            });

        Assert.Equal(
            ["Flash_FastbootInfo_UbootVersion", "Flash_FastbootInfo_Product",
             "Flash_FastbootInfo_Bootloader", "Flash_FastbootInfo_Serial",
             "Flash_FastbootInfo_MtdSize", "Flash_FastbootInfo_BlockSize"],
            facts.Select(fact => fact.LabelKey));
        Assert.DoesNotContain(facts, fact => fact.Value.StartsWith("0x", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildFacts_GenericBranch_ReportsNothing()
    {
        var facts = FastbootDeviceInfoService.BuildFacts(FastbootInfoBranch.Generic,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["version"] = "0.1",
                ["product"] = "k1-pro",
                ["partition-size:boot"] = "0x4000000"
            });

        Assert.Empty(facts);
    }

    [Fact]
    public void BuildStandardFacts_ReportsWhatAGadgetWithoutAVariableListAnswers()
    {
        // A gadget that refuses `getvar all` still answers individual keys, so the page shows those instead of nothing.
        // 拒绝 `getvar all` 的 gadget 仍会应答单个键，页面因此显示它们而不是什么都不显示。
        var facts = FastbootDeviceInfoService.BuildStandardFacts(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["product"] = "k1x",
                ["bootloader"] = "U-Boot 2024.01",
                ["mtd-size"] = "NULL",
                ["partition-size:boot"] = "0x4000000"
            });

        Assert.Equal(["Flash_FastbootInfo_Product", "Flash_FastbootInfo_Bootloader"],
            facts.Select(fact => fact.LabelKey));
    }

    [Fact]
    public void IsSpacemiTGadget_RequiresTheVendorPairOnAFastbootEndpoint()
    {
        Assert.True(FastbootDeviceInfoService.IsSpacemiTGadget(FastbootDevice(0x361C, 0x1001)));
        Assert.False(FastbootDeviceInfoService.IsSpacemiTGadget(FastbootDevice(0x18D1, 0x4EE7)));
        Assert.False(FastbootDeviceInfoService.IsSpacemiTGadget(new DetectedDevice
        {
            Key = "usb:other",
            Serial = "OTHER",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb,
            VendorId = 0x361C,
            ProductId = 0x1001
        }));
    }

    [Fact]
    public async Task ReadAsync_NonFastbootDevice_RejectsTheRead()
    {
        var device = new DetectedDevice
        {
            Key = "usb:rkdl",
            Serial = "RK01",
            Protocol = DeviceProtocolKind.Rkdl,
            Transport = DeviceTransportKind.Usb
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => FastbootDeviceInfoService.ReadAsync(device, showAll: false, TestContext.Current.CancellationToken));
    }

    private static DetectedDevice FastbootDevice(ushort vendorId, ushort productId) => new()
    {
        Key = "usb:fastboot",
        Serial = SpacemiTBootStageClassifier.FdlSerial,
        Protocol = DeviceProtocolKind.Fastboot,
        Transport = DeviceTransportKind.Usb,
        VendorId = vendorId,
        ProductId = productId,
        FastbootStage = SpacemiTBootStage.BromFdl
    };
}