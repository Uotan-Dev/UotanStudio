using UotanStudio.Comm.Protocols.Sunxi;
using UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Live Allwinner checks gated by <c>UOTAN_SUNXI_FEL_DEVICE</c>: the identity handshake that classifies FEL
/// against FES and one read-only storage or memory read. No erase, write or mode switch ever runs here.
/// <para>由 <c>UOTAN_SUNXI_FEL_DEVICE</c> 门控的全志真机检查：区分 FEL 与 FES 的身份握手，以及一次只读的存储
/// 或内存读取。本测试绝不执行擦除、写入或模式切换。</para>
/// </summary>
public sealed class SunxiFelLiveTests
{
    private const string DeviceGateVariable = "UOTAN_SUNXI_FEL_DEVICE";

    [Fact]
    public async Task LiveDevice_AnswersIdentityHandshakeAndReadOnlyRead()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(DeviceGateVariable) is { Length: > 0 },
            $"Set {DeviceGateVariable}=1 with an Allwinner FEL/FES device attached to run the live identity " +
            "handshake and one read-only read; nothing else is issued to the device.");

        using var monitor = DeviceMonitorService.Create();
        var device = monitor.Scan().FirstOrDefault(candidate => candidate.Protocol == DeviceProtocolKind.Sunxi);
        if (device is null)
        {
            Assert.Skip($"No Allwinner FEL/FES endpoint is visible while {DeviceGateVariable} is set.");
        }

        var status = await monitor.QuerySunxiStatusAsync(device, TestContext.Current.CancellationToken);

        // The identity handshake is the only way to tell FEL from FES; both modes enumerate with the same
        // VID/PID pair and must answer a defined mode word.
        // 身份握手是区分 FEL 与 FES 的唯一方式；两种模式以相同 VID/PID 枚举，必须应答已定义的模式字。
        Assert.NotEqual(SunxiDeviceMode.None, status.Mode);
        Assert.True(Enum.IsDefined(status.Mode));
        Assert.Equal(0x00185900u, status.Identity.Id);
        Assert.Equal("D1/D1S/F133/R528/T113", status.ChipName);

        if (status.IsFes)
        {
            var table = await monitor.ReadSunxiMbrAsync(device, TestContext.Current.CancellationToken);
            Assert.Equal(SunxiMbrTable.TableSize, table.Length);
            Assert.True(SunxiMbrTable.HasMagic(table), "the FES MBR read answered without the softw411 magic");
            return;
        }

        // The xfel reference reaches the D1 SID block at 0x03006200 with a scratchpad payload; a bare FEL read of
        // the same window proves the read pipeline against real silicon.
        // xfel 参考实现经 scratchpad 载荷读取 0x03006200 处的 D1 SID 区块；对同一窗口的一次裸 FEL 读取即可在
        // 真实硅片上证明读取管线。
        var sid = await monitor.ReadSunxiMemoryAsync(device, 0x03006200, 16, TestContext.Current.CancellationToken);
        Assert.Equal(16, sid.Length);
        Assert.False(sid.All(b => b is 0x00 or 0xFF), "the SID block answered with an empty read");
    }
}