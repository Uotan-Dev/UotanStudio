using UotanStudio.Comm.Transports.Usb;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Usb;

public sealed class UsbStreamWriteTests
{
    [Fact]
    public void Write_EndpointAcceptsEveryByte_CompletesWithoutThrowing()
    {
        var session = new ShortWriteSession(accepted: 4);
        var stream = new UsbStream(session);
        byte[] payload = [1, 2, 3, 4];

        stream.Write(payload, 0, payload.Length);

        Assert.Equal(1, session.WriteCalls);
        Assert.Equal(4, session.LastRequestedLength);
    }

    [Fact]
    public void Write_EndpointAcceptsFewerBytes_ThrowsIOException()
    {
        var session = new ShortWriteSession(accepted: 2);
        var stream = new UsbStream(session);
        byte[] payload = [1, 2, 3, 4];

        var error = Assert.Throws<IOException>(() => stream.Write(payload, 0, payload.Length));

        Assert.Contains("2 of 4 bytes", error.Message, StringComparison.Ordinal);
    }

    private sealed class ShortWriteSession(int accepted) : IUsbDeviceSession
    {
        public int WriteCalls { get; private set; }

        public int LastRequestedLength { get; private set; }

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

        public long Write(byte[] data, int length) => Write(data, 0, length, DefaultTimeoutMs);

        public long Write(byte[] data, int length, int timeoutMs) => Write(data, 0, length, timeoutMs);

        public long Write(byte[] data, int offset, int length, int timeoutMs)
        {
            WriteCalls++;
            LastRequestedLength = length;
            return Math.Min(accepted, length);
        }

        public void WriteZlp(int timeoutMs) => throw new NotSupportedException();

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
            => throw new NotSupportedException();

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