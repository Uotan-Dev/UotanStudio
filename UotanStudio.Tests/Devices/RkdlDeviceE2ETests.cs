using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Real-device RKDL closed-loop checks: the connected Rockchip maskrom device must enumerate through
/// the host device pipeline, answer the protocol readiness probe, and report its identity via
/// READ_FLASH_ID and READ_CAPABILITY. Set <c>UOTAN_RKDL_E2E=1</c> with a connected maskrom device to
/// run; the suite skips itself everywhere else.
/// <para>真机 RKDL 闭环检查：已接入的瑞芯微 maskrom 设备必须经宿主设备管线枚举、应答协议就绪探测，
/// 并通过 READ_FLASH_ID 与 READ_CAPABILITY 上报自身信息。设 <c>UOTAN_RKDL_E2E=1</c> 并接入 maskrom
/// 设备后运行；其余环境一律自跳。</para>
/// </summary>
public sealed class RkdlDeviceE2ETests
{
    private const string EnableVariable = "UOTAN_RKDL_E2E";

    [Fact]
    public async Task Rkdl_Endpoint_EnumeratesCommunicatesAndReportsDeviceInformation()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal),
            $"{EnableVariable} is not set to 1; connect a maskrom device and opt in to run this loop.");

        using var monitor = DeviceMonitorService.Create();
        var device = monitor.Scan().FirstOrDefault(candidate => candidate.Protocol == DeviceProtocolKind.Rkdl);
        Assert.SkipWhen(device is null, "No RKDL endpoint was discovered on this host.");

        Assert.True(monitor.ProbeRkdl(device!), "The RKDL target did not answer the readiness probe.");

        var info = await monitor.RunRkdlAsync(device!, session => Task.FromResult(
            (
                VendorId: session.VendorId,
                ProductId: session.ProductId,
                Mode: session.Mode,
                IsMaskrom: session.IsMaskromMode,
                FlashId: session.ReadFlashId(),
                CapabilityRaw: session.ReadCapability().Raw
            )), TestContext.Current.CancellationToken);

        Assert.Equal(0x2207, info.VendorId);
        Assert.True(info.IsMaskrom,
            $"The target reported PID 0x{info.ProductId:X4} in {info.Mode} stage, which is not maskrom.");
        Assert.True(info.FlashId.Length >= 5, $"READ_FLASH_ID returned {info.FlashId.Length} bytes.");
        Assert.True(info.CapabilityRaw.Length >= 8, $"READ_CAPABILITY returned {info.CapabilityRaw.Length} bytes.");
    }
}
