using UotanStudio.Comm.Protocols.Unisoc;
using UotanStudio.Comm.Transports.Serial;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Read-only live pass against a real UNISOC download serial port: the environment variable points at the port
/// name the vendor driver created (for example <c>COM7</c>). The test only opens the link, identifies the loader
/// stage and, in FDL2, resolves the partition table; it never uploads, writes or erases. Skip reason names the
/// variable.
/// <para>对真实紫光展锐下载串口的只读实机验证：环境变量指向厂商驱动创建的端口名（例如 <c>COM7</c>）。用例只打开
/// 链路、识别加载阶段，并在 FDL2 时解析分区表；从不上传、写入或擦除。跳过原因写明变量名。</para>
/// </summary>
public class UnisocLiveTests
{
    [Fact]
    public async Task LiveDownloadPort_IdentifiesStageWithoutMutating()
    {
        var port = Environment.GetEnvironmentVariable("UOTAN_UNISOC_DL_DEVICE");
        Assert.SkipUnless(port is { Length: > 0 },
            "UOTAN_UNISOC_DL_DEVICE must name a UNISOC download serial port for the live pass.");

        using var serial = new SerialTransport(port!, 115200, System.IO.Ports.Parity.None, 8,
            System.IO.Ports.StopBits.One, System.IO.Ports.Handshake.None);
        serial.Connect();
        using var session = await UnisocDeviceSession.OpenAsync(new UnisocSerialTransport(serial), null,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(UnisocBootStage.None, session.Stage);
        if (session.Stage == UnisocBootStage.Fdl2)
        {
            var storage = session.OpenStorage();
            var targets = await new UnisocFlasher(session).ResolvePartitionsAsync(
                TestContext.Current.CancellationToken);
            Assert.NotEmpty(targets);
            Assert.NotNull(storage);
        }
    }
}