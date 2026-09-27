using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Discovery;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed class MtkDeviceSessionFactoryDiscoveryTests
{
    [Fact]
    public async Task CreateAsync_BootRomDescriptors_RecordsCompletedModeDiscoveryEvidence()
    {
        var factory = new MtkDeviceSessionFactory();
        var session = new FakeUsbSession(Device(0x0003, CommandInterface(0, 0x81, 0x01)));

        var created = (MtkSession)await factory.CreateAsync(Endpoint("mtk"),
            new OwnedDeviceTransport<IUsbDeviceSession>(session), TestContext.Current.CancellationToken);

        Assert.Equal(MtkUsbMode.BootRom, created.DiscoveredUsbMode!.Mode);
        var discovery = Assert.Single(created.Evidence, evidence => evidence.Phase == "mode-discovery");
        Assert.Equal(CommandOutcomeKind.Completed, discovery.Outcome);
        Assert.Equal(0, session.WriteCount);
        created.Dispose();
    }

    [Fact]
    public async Task CreateAsync_MissingInterfaceMetadata_RecordsUnknownEvidenceWithoutThrowing()
    {
        var factory = new MtkDeviceSessionFactory();
        var session = new FakeUsbSession(new UsbDeviceInfo { VendorId = 0x0E8D, ProductId = 0x2000 });

        var created = (MtkSession)await factory.CreateAsync(Endpoint("mtk"),
            new OwnedDeviceTransport<IUsbDeviceSession>(session), TestContext.Current.CancellationToken);

        Assert.Equal(MtkUsbMode.Unknown, created.DiscoveredUsbMode!.Mode);
        var discovery = Assert.Single(created.Evidence, evidence => evidence.Phase == "mode-discovery");
        Assert.Equal(CommandOutcomeKind.Unknown, discovery.Outcome);
        Assert.True(created.IsConnected);
        created.Dispose();
    }

    [Fact]
    public async Task CreateAsync_LoggingOnlyDescriptors_RejectsBeforeAnyProtocolByte()
    {
        var factory = new MtkDeviceSessionFactory();
        var session = new FakeUsbSession(Device(0x2001, LoggingInterface(0)));

        var error = await Assert.ThrowsAsync<NotSupportedException>(async () => await factory.CreateAsync(
            Endpoint("mtk"),
            new OwnedDeviceTransport<IUsbDeviceSession>(session), TestContext.Current.CancellationToken));

        Assert.Contains("must not receive MTK commands", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, session.WriteCount);
    }

    [Fact]
    public async Task CreateAsync_TwoCommandPairDevices_KeepClassificationsPerDevice()
    {
        var factory = new MtkDeviceSessionFactory();
        var first = new FakeUsbSession(Device(0x2000, CommandInterface(0, 0x81, 0x01)));
        var second = new FakeUsbSession(Device(0x2000,
            CommandInterface(0, 0x81, 0x01),
            CommandInterface(1, 0x82, 0x02)));

        var firstSession = (MtkSession)await factory.CreateAsync(Endpoint("mtk"),
            new OwnedDeviceTransport<IUsbDeviceSession>(first), TestContext.Current.CancellationToken);
        var secondSession = (MtkSession)await factory.CreateAsync(Endpoint("mtk"),
            new OwnedDeviceTransport<IUsbDeviceSession>(second), TestContext.Current.CancellationToken);

        Assert.Equal(MtkUsbMode.Preloader, firstSession.DiscoveredUsbMode!.Mode);
        Assert.Equal(MtkUsbMode.Unknown, secondSession.DiscoveredUsbMode!.Mode);
        Assert.Contains("enumeration order", secondSession.DiscoveredUsbMode.Reason, StringComparison.Ordinal);
        firstSession.Dispose();
        secondSession.Dispose();
    }

    private static DeviceEndpoint Endpoint(string protocol) => new()
    {
        Source = "usb-test",
        Transport = "usb",
        Address = "mtk-a",
        Serial = "mtk-a",
        Route = "usb",
        Protocol = protocol
    };

    private static UsbDeviceInfo Device(ushort productId, params UsbInterfaceInfo[] interfaces) => new()
    {
        VendorId = 0x0E8D,
        ProductId = productId,
        InterfaceMetadataObserved = true,
        Interfaces = interfaces
    };

    private static UsbInterfaceInfo CommandInterface(byte number, byte endpointIn, byte endpointOut) => new()
    {
        InterfaceNumber = number,
        Class = 0xFF,
        Endpoints = [new UsbEndpointInfo { EndpointAddress = endpointIn, Attributes = 0x02 },
            new UsbEndpointInfo { EndpointAddress = endpointOut, Attributes = 0x02 }]
    };

    private static UsbInterfaceInfo LoggingInterface(byte number) => new()
    {
        InterfaceNumber = number,
        Class = 0xFF,
        Endpoints = [new UsbEndpointInfo { EndpointAddress = (byte)(0x82 + number), Attributes = 0x02 }]
    };

    private sealed class FakeUsbSession(UsbDeviceInfo deviceInfo) : IUsbDeviceSession
    {
        public int WriteCount { get; private set; }

        public int DefaultTimeoutMs => 5000;

        public UsbDeviceInfo DeviceInfo { get; } = deviceInfo;

        public byte EndpointIn => 0x81;

        public byte EndpointOut => 0x01;

        public byte[] Read(int length) => throw new NotSupportedException();

        public byte[] Read(int length, int timeoutMs) => throw new NotSupportedException();

        public int ReadInto(byte[] buffer, int offset, int length) => throw new NotSupportedException();

        public int ReadInto(byte[] buffer, int offset, int length, int timeoutMs) => throw new NotSupportedException();

        public UsbReadResult ReadPacket(byte[] buffer, int offset, int length, int timeoutMs) =>
            throw new NotSupportedException();

        public int ReadInto(Span<byte> buffer, int timeoutMs) => throw new NotSupportedException();

        public long Write(byte[] data, int length) => throw Fail();

        public long Write(byte[] data, int length, int timeoutMs) => throw Fail();

        public long Write(byte[] data, int offset, int length, int timeoutMs) => throw Fail();

        public void WriteZlp(int timeoutMs) => throw Fail();

        public int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs) =>
            throw new NotSupportedException();

        public void SetInterfaceAltSetting(byte interfaceNumber, byte altSetting)
        {
        }

        public void SetConfiguration(byte configuration)
        {
        }

        public UsbReadResult ReadInterrupt(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs) =>
            throw new NotSupportedException();

        public long WriteInterrupt(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs) =>
            throw new NotSupportedException();

        public void Reset()
        {
        }

        public Task<byte[]> ReadAsync(int length, int timeoutMs, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> ReadIntoAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UsbReadResult> ReadPacketAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UsbReadResult> ReadPacketAsync(byte[] buffer, int offset, int length, int timeoutMs,
            IProgress<long>? progress, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> WriteAsync(byte[] data, int length, int timeoutMs,
            CancellationToken cancellationToken = default) => throw Fail();

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default) => throw Fail();

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, IProgress<long>? progress,
            CancellationToken cancellationToken = default) => throw Fail();

        public Task WriteZlpAsync(int timeoutMs, CancellationToken cancellationToken = default) => throw Fail();

        public Task<int> ControlTransferAsync(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length,
            int timeoutMs, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetInterfaceAltSettingAsync(byte interfaceNumber, byte altSetting,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetConfigurationAsync(byte configuration, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<UsbReadResult> ReadInterruptAsync(byte endpointAddress, byte[] buffer, int offset, int length,
            int timeoutMs, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<long> WriteInterruptAsync(byte endpointAddress, byte[] data, int offset, int length,
            int timeoutMs, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose()
        {
        }

        private Exception Fail()
        {
            WriteCount++;
            return new NotSupportedException("The factory must not send protocol bytes before initialization.");
        }
    }
}