using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Hash.RockchipCrc32;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Bootrom;

/// <summary>
/// RKBOOT loader image parsing and 0x0471/0x0472 loader download tests. RC4-encrypted loaders are
/// rejected before any vendor request, since entry payloads go out verbatim.
/// <para>RKBOOT loader 镜像解析与 0x0471/0x0472 loader 下载测试。RC4 加密的 loader 会在任何 vendor
/// 请求前被拒绝，因为条目负载原样发送。</para>
/// </summary>
public class RkdlLoaderTests
{
    private const int EntrySize = 57;

    [Fact]
    public void Parse_PlainLoaderImage_ReportsNoRc4AndOneEntry471()
    {
        var image = BuildLoaderImage(rc4Enabled: false);

        var parsed = RkBootImage.Parse(image);

        Assert.False(parsed.Rc4Enabled);
        Assert.Equal("BOOT", parsed.Tag);
        var entry = Assert.Single(parsed.Entries471);
        Assert.Equal("471", entry.Name);
        Assert.Equal(200u, entry.DataOffset);
        Assert.Equal(32u, entry.DataSize);
        Assert.Equal(0u, entry.DataDelay);
        Assert.Empty(parsed.Entries472);
        Assert.Empty(parsed.LoaderEntries);
    }

    [Fact]
    public void Parse_Rc4FlagSet_ReportsRc4Enabled()
    {
        var image = BuildLoaderImage(rc4Enabled: true);

        var parsed = RkBootImage.Parse(image);

        Assert.True(parsed.Rc4Enabled);
        Assert.Equal(0x01, image[44]);
    }

    [Fact]
    public void Parse_TruncatedImage_ThrowsInvalidParameter()
    {
        var exception = Assert.Throws<RkdlException>(() => RkBootImage.Parse(new byte[512]));

        Assert.Equal(RkdlError.InvalidParameter, exception.ErrorCode);
    }

    [Fact]
    public void Parse_UnknownTag_ThrowsInvalidParameter()
    {
        var image = BuildLoaderImage(rc4Enabled: false);
        BinaryPrimitives.WriteUInt32LittleEndian(image, 0x12345678);

        var exception = Assert.Throws<RkdlException>(() => RkBootImage.Parse(image));

        Assert.Equal(RkdlError.InvalidParameter, exception.ErrorCode);
        Assert.Contains("0x12345678", exception.Message);
    }

    [Fact]
    public void Parse_CorruptedPayload_ThrowsAndNamesTheStoredCrc()
    {
        var image = BuildLoaderImage(rc4Enabled: false);
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1020));
        image[200] ^= 0xFF;
        var recomputed = RockchipCrc32.Compute(image.AsSpan(0, 1020));

        var exception = Assert.Throws<RkdlException>(() => RkBootImage.Parse(image));

        Assert.Equal(RkdlError.InvalidParameter, exception.ErrorCode);
        Assert.NotEqual(stored, recomputed);
        Assert.Contains($"stored 0x{stored:X8}, computed 0x{recomputed:X8}", exception.Message);
    }

    [Fact]
    public void Parse_EntrySizeBelowSerializedEntry_ThrowsInvalidParameter()
    {
        var image = BuildLoaderImage(rc4Enabled: false);
        image[30] = EntrySize - 1;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(1020), RockchipCrc32.Compute(image.AsSpan(0, 1020)));

        var exception = Assert.Throws<RkdlException>(() => RkBootImage.Parse(image));

        Assert.Equal(RkdlError.InvalidParameter, exception.ErrorCode);
    }

    [Fact]
    public void Parse_EntryTableBeyondImage_ThrowsInvalidParameter()
    {
        var image = BuildLoaderImage(rc4Enabled: false);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(26), 4000);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(1020), RockchipCrc32.Compute(image.AsSpan(0, 1020)));

        var exception = Assert.Throws<RkdlException>(() => RkBootImage.Parse(image));

        Assert.Equal(RkdlError.InvalidParameter, exception.ErrorCode);
    }

    [Fact]
    public void DownloadBootLoader_Rc4EnabledImage_ThrowsNotSupportedBeforeAnyDeviceRequest()
    {
        var usbSession = new FakeRkdlUsbSession();
        var transport = new UsbDeviceTransport(usbSession);
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var image = BuildLoaderImage(rc4Enabled: true);

        var exception = Assert.Throws<RkdlException>(() => session.DownloadBootLoader(image));

        Assert.Equal(RkdlError.NotSupported, exception.ErrorCode);
        Assert.Contains("RC4", exception.Message);
        Assert.Empty(usbSession.ControlTransfers);
        Assert.Empty(usbSession.BulkWrites);
    }

    [Fact]
    public async Task DownloadBootLoaderAsync_Rc4EnabledImage_ThrowsNotSupportedBeforeAnyDeviceRequest()
    {
        var usbSession = new FakeRkdlUsbSession();
        var transport = new UsbDeviceTransport(usbSession);
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var image = BuildLoaderImage(rc4Enabled: true);
        var cancellationToken = TestContext.Current.CancellationToken;

        var exception = await Assert.ThrowsAsync<RkdlException>(
            () => session.DownloadBootLoaderAsync(image, cancellationToken));

        Assert.Equal(RkdlError.NotSupported, exception.ErrorCode);
        Assert.Empty(usbSession.ControlTransfers);
    }

    [Fact]
    public void DownloadBootLoader_RockusbModeDevice_ThrowsNotSupportedForMaskromOnlyRequest()
    {
        var usbSession = new FakeRkdlUsbSession
        {
            DeviceInfo = new()
            {
                VendorId = 0x2207,
                ProductId = 0x350A,
                ApiName = "fake-rkdl",
            }
        };
        var transport = new UsbDeviceTransport(usbSession);
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var image = BuildLoaderImage(rc4Enabled: true);

        var exception = Assert.Throws<RkdlException>(() => session.DownloadBootLoader(image));

        Assert.Equal(RkdlError.NotSupported, exception.ErrorCode);
        Assert.Contains("maskrom", exception.Message);
    }

    [Fact]
    public void DownloadBootLoader_PlainLoaderImage_SendsEntry471WithCrc16Framing()
    {
        var usbSession = new FakeRkdlUsbSession();
        var transport = new UsbDeviceTransport(usbSession);
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var image = BuildLoaderImage(rc4Enabled: false);

        var total = session.DownloadBootLoader(image);

        Assert.Equal(34, total);
        var (setup, data) = Assert.Single(usbSession.ControlTransfers);
        Assert.Equal(0x40, setup.RequestType);
        Assert.Equal(0x0C, setup.Request);
        Assert.Equal((ushort)0, setup.Value);
        Assert.Equal(0x0471, setup.Index);
        Assert.Equal(34, setup.Length);
        Assert.Equal(34, data.Length);

        var expectedPayload = new byte[32];
        for (var i = 0; i < expectedPayload.Length; i++)
        {
            expectedPayload[i] = (byte)(i + 1);
        }

        Assert.Equal(expectedPayload, data[..32]);
        Assert.Equal(0xC1, data[32]);
        Assert.Equal(0x11, data[33]);
    }

    [Fact]
    public void DownloadBootLoader471_NonUsbTransport_ThrowsNotSupported()
    {
        var transport = new FakeRkdlDeviceTransport();
        using var session = new RkdlSession(transport, timeoutMs: 5000);

        Assert.Throws<NotSupportedException>(() => session.DownloadBootLoader471([0x01, 0x02]));
    }

    [Fact]
    public void DeviceRequest_EmptyLoaderData_ThrowsInvalidParameter()
    {
        var usbSession = new FakeRkdlUsbSession();
        var transport = new UsbDeviceTransport(usbSession);
        using var session = new RkdlSession(transport, timeoutMs: 5000);

        Assert.Throws<RkdlException>(() => session.DownloadBootLoader471([]));
        Assert.Empty(usbSession.ControlTransfers);
    }

    [Fact]
    public void DeviceRequest_LoaderDataAboveMaximum_ThrowsLoaderTooLarge()
    {
        var usbSession = new FakeRkdlUsbSession();
        var transport = new UsbDeviceTransport(usbSession);
        using var session = new RkdlSession(transport, timeoutMs: 5000);

        var exception = Assert.Throws<RkdlException>(
            () => session.DownloadBootLoader472(new byte[RkdlConstants.MaxLoaderSize + 1]));

        Assert.Equal(RkdlError.LoaderTooLarge, exception.ErrorCode);
        Assert.Empty(usbSession.ControlTransfers);
    }

    [Fact]
    public void DeviceRequest_ChunkBoundaryPadding_AppendsOneByteFor4095AndPadPacketFor4094()
    {
        var transport4095 = new FakeRkdlUsbSession();
        using var session4095 = new RkdlSession(new UsbDeviceTransport(transport4095), timeoutMs: 5000);

        Assert.Equal(4098, session4095.DownloadBootLoader471(new byte[4095]));

        Assert.Equal(2, transport4095.ControlTransfers.Count);
        Assert.Equal(4096, transport4095.ControlTransfers[0].Setup.Length);
        Assert.Equal(2, transport4095.ControlTransfers[1].Setup.Length);
        Assert.Equal(0xEF, transport4095.ControlTransfers[1].Data[0]);
        Assert.Equal(0xDF, transport4095.ControlTransfers[1].Data[1]);

        var transport4094 = new FakeRkdlUsbSession();
        using var session4094 = new RkdlSession(new UsbDeviceTransport(transport4094), timeoutMs: 5000);

        Assert.Equal(4096, session4094.DownloadBootLoader471(new byte[4094]));

        Assert.Equal(2, transport4094.ControlTransfers.Count);
        Assert.Equal(4096, transport4094.ControlTransfers[0].Setup.Length);
        Assert.Equal(1, transport4094.ControlTransfers[1].Setup.Length);
        Assert.Equal([0x00], transport4094.ControlTransfers[1].Data);
        Assert.Equal(0x19, transport4094.ControlTransfers[0].Data[4094]);
        Assert.Equal(0xBF, transport4094.ControlTransfers[0].Data[4095]);
    }

    [Fact]
    public void DeviceRequest_LargerThanOneChunk_SplitsInto4096ByteControlTransfers()
    {
        var usbSession = new FakeRkdlUsbSession();
        var transport = new UsbDeviceTransport(usbSession);
        using var session = new RkdlSession(transport, timeoutMs: 5000);

        var total = session.DownloadBootLoader471(new byte[9000]);

        Assert.Equal(9002, total);
        Assert.Equal(3, usbSession.ControlTransfers.Count);
        Assert.Equal([4096, 4096, 810], usbSession.ControlTransfers.Select(t => t.Setup.Length).ToArray());
    }

    [Fact]
    public void DownloadBootLoader_DataDelayOneMillisecondAboveBound_ThrowsBeforeAnyDeviceRequest()
    {
        var usbSession = new FakeRkdlUsbSession();
        var transport = new UsbDeviceTransport(usbSession);
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var image = BuildLoaderImage(rc4Enabled: false, dataDelay: 60_001u);

        var exception = Assert.Throws<RkdlException>(() => session.DownloadBootLoader(image));

        Assert.Equal(RkdlError.InvalidParameter, exception.ErrorCode);
        Assert.Contains("60001", exception.Message);
        Assert.Empty(usbSession.ControlTransfers);
        Assert.Empty(usbSession.BulkWrites);
    }

    [Fact]
    public void DownloadBootLoader_DataDelayRawUIntMaximum_ThrowsBeforeAnyDeviceRequest()
    {
        var usbSession = new FakeRkdlUsbSession();
        var transport = new UsbDeviceTransport(usbSession);
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var image = BuildLoaderImage(rc4Enabled: false, dataDelay: uint.MaxValue);

        var exception = Assert.Throws<RkdlException>(() => session.DownloadBootLoader(image));

        Assert.Equal(RkdlError.InvalidParameter, exception.ErrorCode);
        Assert.Contains("4294967295", exception.Message);
        Assert.Empty(usbSession.ControlTransfers);
    }

    [Fact]
    public void DownloadBootLoader_DataDelayJustAboveInt32Maximum_ThrowsRkdlExceptionNotArgumentOutOfRange()
    {
        var usbSession = new FakeRkdlUsbSession();
        var transport = new UsbDeviceTransport(usbSession);
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var image = BuildLoaderImage(rc4Enabled: false, dataDelay: 2_147_483_648u);

        var exception = Assert.Throws<RkdlException>(() => session.DownloadBootLoader(image));

        Assert.Equal(RkdlError.InvalidParameter, exception.ErrorCode);
        Assert.Contains("2147483648", exception.Message);
        Assert.Empty(usbSession.ControlTransfers);
    }

    [Fact]
    public void DownloadBootLoader_DataDelayZero_CompletesWithoutWaitingBeyondTheSettleWindow()
    {
        var usbSession = new FakeRkdlUsbSession();
        var transport = new UsbDeviceTransport(usbSession);
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var image = BuildLoaderImage(rc4Enabled: false, dataDelay: 0u);

        var stopwatch = Stopwatch.StartNew();
        var total = session.DownloadBootLoader(image);
        stopwatch.Stop();

        Assert.Equal(34, total);
        Assert.Single(usbSession.ControlTransfers);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900), $"Only the settle window should have been waited, but the call returned after {stopwatch.ElapsedMilliseconds} ms.");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(2500), $"A zero dwDataDelay must not add a wait beyond the settle window; elapsed was {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task DownloadBootLoaderAsync_DataDelayEqualToBound_IsAcceptedAndAwaitedCancellably()
    {
        var usbSession = new FakeRkdlUsbSession();
        var transport = new UsbDeviceTransport(usbSession);
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var image = BuildLoaderImage(rc4Enabled: false, dataDelay: 60_000u);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.DownloadBootLoaderAsync(image, cancellation.Token));
        stopwatch.Stop();

        Assert.Single(usbSession.ControlTransfers);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"The dwDataDelay equal to the bound must be awaited cancellably, not rejected or slept through; elapsed was {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task DownloadBootLoaderAsync_CancellationDuringDataDelay_ThrowsWithinTheDelayWindow()
    {
        var usbSession = new FakeRkdlUsbSession();
        var transport = new UsbDeviceTransport(usbSession);
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var image = BuildLoaderImage(rc4Enabled: false, dataDelay: 20_000u);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.DownloadBootLoaderAsync(image, cancellation.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Cancellation must abort inside the 20000 ms delay window; elapsed was {stopwatch.ElapsedMilliseconds} ms.");
        Assert.Single(usbSession.ControlTransfers);
    }

    [Fact]
    public void CreateLoaderTransitionOffer_SerialBearingUsbDevice_CarriesPhysicalIdentityAndRelinkTimeout()
    {
        var usbSession = new FakeRkdlUsbSession
        {
            DeviceInfo = new()
            {
                VendorId = 0x2207,
                ProductId = 0x350B,
                ApiName = "fake-rkdl",
                DevicePath = "fake://rkdl",
                DeviceKey = "fake-rkdl-key",
                SerialNumber = "RKLOADERTEST1",
            }
        };
        var transport = new UsbDeviceTransport(usbSession);
        using var session = new RkdlSession(transport, timeoutMs: 5000);

        var offer = session.CreateLoaderTransitionOffer();

        Assert.NotNull(offer.Identity);
        Assert.Equal("usb:2207:350B:RKLOADERTEST1", offer.Identity.Key);
        Assert.Null(offer.PreviousEndpoint);
        Assert.Null(offer.ObservedGeneration);
        Assert.Equal(TimeSpan.FromMilliseconds(30000), offer.Timeout);
    }

    [Fact]
    public void CreateLoaderTransitionOffer_ContainerBearingUsbDevice_PrefersContainerIdentity()
    {
        var usbSession = new FakeRkdlUsbSession
        {
            DeviceInfo = new()
            {
                VendorId = 0x2207,
                ProductId = 0x350B,
                ApiName = "fake-rkdl",
                DevicePath = "fake://rkdl",
                DeviceKey = "fake-rkdl-key",
                ContainerId = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"),
            }
        };
        var transport = new UsbDeviceTransport(usbSession);
        using var session = new RkdlSession(transport, timeoutMs: 5000);

        var offer = session.CreateLoaderTransitionOffer();

        Assert.NotNull(offer.Identity);
        Assert.Equal(new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"), offer.Identity.ContainerId);
        Assert.Equal("usb-container:0f8fad5bd9cb469fa16570867728950e", offer.Identity.Key);
    }

    [Fact]
    public void CreateLoaderTransitionOffer_NonUsbTransport_PublishesNoIdentity()
    {
        var transport = new FakeRkdlDeviceTransport();
        using var session = new RkdlSession(transport, timeoutMs: 5000);

        var offer = session.CreateLoaderTransitionOffer();

        Assert.Null(offer.Identity);
        Assert.Null(offer.PreviousEndpoint);
        Assert.Equal(TimeSpan.FromMilliseconds(30000), offer.Timeout);
    }

    private static byte[] BuildLoaderImage(bool rc4Enabled, uint dataDelay = 0)
    {
        var image = new byte[1024];
        BinaryPrimitives.WriteUInt32LittleEndian(image, RkBootImage.TagBoot);

        image[25] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(26), 100);
        image[30] = EntrySize;
        image[31] = 0;
        image[37] = 0;
        image[44] = rc4Enabled ? (byte)1 : (byte)0;

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(101), 1);
        Encoding.Unicode.GetBytes("471").CopyTo(image.AsSpan(105, 40));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(145), 200);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(149), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(153), dataDelay);

        for (var i = 0; i < 32; i++)
        {
            image[200 + i] = (byte)(i + 1);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(1020), RockchipCrc32.Compute(image.AsSpan(0, 1020)));
        return image;
    }
}