using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class FirehoseProgramZlpTests
{
    [Theory]
    [InlineData(true, 512, 512, true)]
    [InlineData(true, 1048576, 512, true)]
    [InlineData(true, 500, 512, false)]
    [InlineData(true, 1024, 4096, false)]
    [InlineData(true, 512, 0, false)]
    [InlineData(false, 512, 512, false)]
    public void ShouldTerminateBlockWithZlp_FollowsUpstreamCondition(
        bool zlpAwareHost, int blockBytes, int sectorSizeBytes, bool expected)
    {
        Assert.Equal(expected, UotanStudio.Comm.Protocols.Edl.FirehoseProtocol.ShouldTerminateBlockWithZlp(
            zlpAwareHost, blockBytes, sectorSizeBytes));
    }

    [Fact]
    public async Task UsbDeviceTransport_Session_ExposesWriteZlpToProtocolLayers()
    {
        var session = new ZlpRecordingSession();
        using var transport = new UsbDeviceTransport(session);

        await transport.Session.WriteZlpAsync(1000, TestContext.Current.CancellationToken);

        Assert.Equal(1, session.ZlpCalls);
        Assert.Equal(1000, session.LastZlpTimeoutMs);
    }

    private sealed class ZlpRecordingSession : IUsbDeviceSession
    {
        public int ZlpCalls { get; private set; }

        public int LastZlpTimeoutMs { get; private set; }

        public int DefaultTimeoutMs => 5000;

        public UsbDeviceInfo DeviceInfo => new();

        public byte EndpointIn => 0x81;

        public byte EndpointOut => 0x01;

        public byte[] Read(int length) => throw new NotSupportedException();

        public byte[] Read(int length, int timeoutMs) => throw new NotSupportedException();

        public int ReadInto(byte[] buffer, int offset, int length) => throw new NotSupportedException();

        public int ReadInto(byte[] buffer, int offset, int length, int timeoutMs) => throw new NotSupportedException();

        public int ReadInto(Span<byte> buffer, int timeoutMs) => throw new NotSupportedException();

        public UsbReadResult ReadPacket(byte[] buffer, int offset, int length, int timeoutMs)
            => throw new NotSupportedException();

        public long Write(byte[] data, int length) => throw new NotSupportedException();

        public long Write(byte[] data, int length, int timeoutMs) => throw new NotSupportedException();

        public long Write(byte[] data, int offset, int length, int timeoutMs) => throw new NotSupportedException();

        public void WriteZlp(int timeoutMs)
        {
            ZlpCalls++;
            LastZlpTimeoutMs = timeoutMs;
        }

        public int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
            => throw new NotSupportedException();

        public void SetInterfaceAltSetting(byte interfaceNumber, byte altSetting) => throw new NotSupportedException();

        public void SetConfiguration(byte configuration) => throw new NotSupportedException();

        public UsbReadResult ReadInterrupt(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs)
            => throw new NotSupportedException();

        public long WriteInterrupt(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs)
            => throw new NotSupportedException();

        public void Reset() => throw new NotSupportedException();

        public Task<byte[]> ReadAsync(int length, int timeoutMs, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> ReadIntoAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UsbReadResult> ReadPacketAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UsbReadResult> ReadPacketAsync(byte[] buffer, int offset, int length, int timeoutMs, IProgress<long>? progress, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> WriteAsync(byte[] data, int length, int timeoutMs, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, IProgress<long>? progress, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task WriteZlpAsync(int timeoutMs, CancellationToken cancellationToken = default)
        {
            ZlpCalls++;
            LastZlpTimeoutMs = timeoutMs;
            return Task.CompletedTask;
        }

        public Task<int> ControlTransferAsync(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetInterfaceAltSettingAsync(byte interfaceNumber, byte altSetting, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetConfigurationAsync(byte configuration, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UsbReadResult> ReadInterruptAsync(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> WriteInterruptAsync(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ResetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}