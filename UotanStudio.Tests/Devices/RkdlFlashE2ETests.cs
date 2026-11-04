using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Real-device RKDL loop for a Linux vendor bundle: send the loader from mask ROM, then write each row at its own
/// address on its own medium, the round trip a package without a partition table needs. Set
/// <c>UOTAN_RKDL_FLASH_E2E=1</c>, point <c>UOTAN_RKDL_LOADER</c> at the loader and
/// <c>UOTAN_RKDL_FLASH_SPI_IMAGE</c> (and optionally <c>UOTAN_RKDL_FLASH_EMMC_IMAGE</c>) at the images to write; the
/// suite skips itself everywhere else. These cases write to the board, so they never run by default.
/// <para>瑞芯微 Linux 厂商包的真机闭环：先从 Mask ROM 发送 loader，再把每一行写到自己介质上的自身地址，这正是没有
/// 分区表的包所需要的往返流程。设 <c>UOTAN_RKDL_FLASH_E2E=1</c>，用 <c>UOTAN_RKDL_LOADER</c> 指向 loader、用
/// <c>UOTAN_RKDL_FLASH_SPI_IMAGE</c>（可选 <c>UOTAN_RKDL_FLASH_EMMC_IMAGE</c>）指向要写入的镜像后运行；其余环境
/// 一律自跳。这些用例会真实写盘，因此默认从不执行。</para>
/// </summary>
public sealed class RkdlFlashE2ETests
{
    private const string EnableVariable = "UOTAN_RKDL_FLASH_E2E";
    private const string LoaderVariable = "UOTAN_RKDL_LOADER";
    private const string SpiImageVariable = "UOTAN_RKDL_FLASH_SPI_IMAGE";
    private const string EmmcImageVariable = "UOTAN_RKDL_FLASH_EMMC_IMAGE";

    [Fact]
    public async Task SendLoader_MaskRomBoard_ReEnumeratesAsLoader()
    {
        Assert.SkipUnless(IsEnabled(), $"{EnableVariable} is not set to 1; connect a Rockchip board, point the image "
            + "variables at the files to write and opt in to run this loop.");
        var loader = Environment.GetEnvironmentVariable(LoaderVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(loader) || !File.Exists(loader),
            $"{LoaderVariable} is not set to an existing loader image.");

        using var monitor = DeviceMonitorService.Create();
        var device = FindRkdl(monitor);
        Assert.SkipWhen(device.Serial.Contains("rockchip", StringComparison.OrdinalIgnoreCase),
            "The board already reports the loader stage through its serial, so no boot download is needed.");
        var bytes = await File.ReadAllBytesAsync(loader, TestContext.Current.CancellationToken);
        var transitioned = await monitor.RunRkdlLoaderTransitionAsync(device,
            async (session, token) =>
            {
                if (session.IsRockusbMode)
                {
                    return null;
                }

                await session.DownloadBootLoaderAsync(bytes, token);
                return session.CreateLoaderTransitionOffer();
            },
            (session, _) => Task.FromResult(session.IsRockusbMode),
            TestContext.Current.CancellationToken);

        Assert.True(transitioned, "The board did not report the loader stage after the download.");
    }

    [Fact]
    public async Task FlashLba_SpiImageOnSpiNor_LandsAtSectorZeroAndReadsBackIdentical()
    {
        Assert.SkipUnless(IsEnabled(), $"{EnableVariable} is not set to 1; connect a Rockchip loader-stage device, "
            + "point the image variables at the images to write and opt in to run this loop.");
        var image = Environment.GetEnvironmentVariable(SpiImageVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(image) || !File.Exists(image),
            $"{SpiImageVariable} is not set to an existing image file.");

        using var monitor = DeviceMonitorService.Create();
        var device = FindRkdl(monitor);
        var sectors = SectorCount(image);
        var expected = await File.ReadAllBytesAsync(image, TestContext.Current.CancellationToken);
        var result = await FlashService.FlashRkdlLbaAsync(new DeviceInfo(device), "uboot", 0, image,
            StorageId("SPINOR"), progress: null, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        var readback = await monitor.RunRkdlAsync(device,
            session => Task.FromResult(session.ReadLbaSectors(0, sectors)),
            TestContext.Current.CancellationToken);
        Assert.True(readback.AsSpan(0, expected.Length).SequenceEqual(expected),
            $"The first {expected.Length} bytes of SPINOR do not match {Path.GetFileName(image)}.");
    }

    [Fact]
    public async Task FlashLba_LinuxImageOnEmmc_LandsAtSectorZero()
    {
        Assert.SkipUnless(IsEnabled(), $"{EnableVariable} is not set to 1; connect a Rockchip loader-stage device, "
            + "point the image variables at the images to write and opt in to run this loop.");
        var image = Environment.GetEnvironmentVariable(EmmcImageVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(image) || !File.Exists(image),
            $"{EmmcImageVariable} is not set to an existing image file.");

        using var monitor = DeviceMonitorService.Create();
        var device = FindRkdl(monitor);
        var result = await FlashService.FlashRkdlLbaAsync(new DeviceInfo(device), "rootfs", 0, image,
            StorageId("EMMC"), progress: null, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        var expected = ReadPrefix(image, 1024 * 1024);
        var readback = await monitor.RunRkdlAsync(device,
            session => Task.FromResult(session.ReadLbaSectors(0, SectorCount(expected.Length))),
            TestContext.Current.CancellationToken);
        Assert.True(readback.AsSpan(0, expected.Length).SequenceEqual(expected),
            $"The first {expected.Length} bytes of eMMC do not match {Path.GetFileName(image)}.");
    }

    [Fact]
    public async Task EraseRange_SpiNorLastBlock_UsesBlockEraseAndLeavesTheLoaderImageIntact()
    {
        Assert.SkipUnless(IsEnabled(), $"{EnableVariable} is not set to 1; connect a Rockchip loader-stage device and "
            + "opt in to run this loop.");
        using var monitor = DeviceMonitorService.Create();
        var device = FindRkdl(monitor);
        var log = new List<string>();
        await monitor.RunRkdlAsync(device, session =>
        {
            session.ChangeStorage((byte)StorageId("SPINOR"));
            var geometry = session.ReadFlashInfo();
            var blockSectors = Math.Max((int)geometry.BlockSizeSectors, 1);
            var lastBlock = geometry.SectorCount - blockSectors;
            log.Add($"sectors={geometry.SectorCount} block={blockSectors} lastBlock={lastBlock}");
            var badBlocks = session.EraseRange(lastBlock, blockSectors);
            log.Add($"erased badBlocks={badBlocks}");
            var erasedTail = session.ReadLbaSectors(lastBlock, Math.Min(blockSectors, 8));
            var head = session.ReadLbaSectors(0, 1);
            log.Add($"tail first=0x{erasedTail[0]:X2} head first=0x{head[0]:X2}");
            Assert.All(erasedTail, value => Assert.Equal(0xFF, value));
            Assert.NotEqual(0xFF, head[0]);
            return Task.FromResult(true);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TestDeviceReady_ConnectedBoard_ReportsReadyWithinTheBudget()
    {
        Assert.SkipUnless(IsEnabled(), $"{EnableVariable} is not set to 1; connect a Rockchip loader-stage device and "
            + "opt in to run this loop.");
        using var monitor = DeviceMonitorService.Create();
        var device = FindRkdl(monitor);
        var ready = await monitor.RunRkdlAsync(device,
            session => session.WaitForLoaderReadyAsync(30_000),
            TestContext.Current.CancellationToken);

        Assert.True(ready, "The board never answered TEST_UNIT_READY as ready.");
    }

    private static bool IsEnabled()
        => string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal);

    private static DetectedDevice FindRkdl(DeviceMonitorService monitor)
    {
        var device = monitor.Scan().FirstOrDefault(candidate => candidate.Protocol == DeviceProtocolKind.Rkdl);
        Assert.SkipWhen(device is null, "No RKDL endpoint was discovered on this host.");
        return device!;
    }

    private static int StorageId(string medium)
    {
        var index = Array.IndexOf(RkdlStorage.Names, medium);
        Assert.True(index >= 0, $"{medium} is not a Rockchip storage name.");
        return RkdlStorage.WireBits[index];
    }

    private static int SectorCount(string path) => SectorCount(new FileInfo(path).Length);

    private static int SectorCount(long length)
        => (int)((length + RkdlConstants.SectorSize - 1) / RkdlConstants.SectorSize);

    private static byte[] ReadPrefix(string path, int length)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }
}