using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;
using UotanStudio.Comm.Transports.Usb.Core;
using Xunit;

namespace UotanStudio.Tests.Usb;

public sealed class UsbDeviceSessionDisposalTests
{
    [Fact]
    public void Read_AfterSessionDisposal_SurfacesDeviceErrorInsteadOfDisposedGate()
    {
        var device = new ClosingDevice();
        var session = new UsbDeviceSession("test", UsbApiKind.Native, device);
        session.Dispose();

        Assert.Throws<DeviceClosedException>(() => session.Read(4));
        Assert.True(device.SawRead);
    }

    [Fact]
    public void Write_AfterSessionDisposal_SurfacesDeviceErrorInsteadOfDisposedGate()
    {
        var device = new ClosingDevice();
        var session = new UsbDeviceSession("test", UsbApiKind.Native, device);
        session.Dispose();
        byte[] payload = [1, 2, 3, 4];

        Assert.Throws<DeviceClosedException>(() => session.Write(payload, payload.Length));
        Assert.True(device.SawWrite);
    }

    [Fact]
    public void ControlTransfer_AfterSessionDisposal_SurfacesDeviceErrorInsteadOfDisposedGate()
    {
        var device = new ClosingDevice();
        var session = new UsbDeviceSession("test", UsbApiKind.Native, device);
        session.Dispose();

        Assert.Throws<DeviceClosedException>(() => session.ControlTransfer(new UsbSetupPacket(), null, 0, 0, 100));
        Assert.True(device.SawControl);
    }

    private sealed class DeviceClosedException : Exception;

    private sealed class ClosingDevice : UsbDevice
    {
        private bool _disposed;

        public bool SawRead { get; private set; }

        public bool SawWrite { get; private set; }

        public bool SawControl { get; private set; }

        protected override string BackendName => "Test";

        protected override bool IsOpen => !_disposed;

        public override byte[] Read(int length)
        {
            SawRead = true;
            if (_disposed) throw new DeviceClosedException();
            return new byte[length];
        }

        public override long Write(byte[] data, int length)
        {
            SawWrite = true;
            if (_disposed) throw new DeviceClosedException();
            return length;
        }

        public override int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
        {
            SawControl = true;
            if (_disposed) throw new DeviceClosedException();
            return length;
        }

        protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
            => UsbChunkResult.Success(0);

        protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs)
            => UsbChunkResult.Success(length);

        public override int GetSerialNumber() => 0;

        public override int CreateHandle() => 0;

        public override void Reset()
        {
        }

        public override void Dispose() => _disposed = true;
    }

    private sealed class BlockingDevice : UsbDevice
    {
        private readonly ManualResetEventSlim _releaseRead = new(false);
        private readonly ManualResetEventSlim _enteredRead = new(false);

        public bool SawRead { get; private set; }

        public bool SawWrite { get; private set; }

        public bool SawControl { get; private set; }

        protected override string BackendName => "Test";

        protected override bool IsOpen => true;

        public void WaitForRead() => _enteredRead.Wait(TimeSpan.FromSeconds(5));

        public void ReleaseRead() => _releaseRead.Set();

        public override byte[] Read(int length)
        {
            SawRead = true;
            _enteredRead.Set();
            _releaseRead.Wait(TimeSpan.FromSeconds(5));
            return new byte[length];
        }

        public override long Write(byte[] data, int length)
        {
            SawWrite = true;
            return length;
        }

        public override int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
        {
            SawControl = true;
            return length;
        }

        protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
            => UsbChunkResult.Success(0);

        protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs)
            => UsbChunkResult.Success(length);

        public override int GetSerialNumber() => 0;

        public override int CreateHandle() => 0;

        public override void Reset()
        {
        }

        public override void Dispose()
        {
        }
    }

    [Fact]
    public async Task Dispose_WhileReadIsInFlight_ReleasesGateWithoutDisposedException()
    {
        var device = new BlockingDevice();
        var session = new UsbDeviceSession("test", UsbApiKind.Native, device);
        var read = Task.Run(() => session.Read(4));
        device.WaitForRead();

        session.Dispose();
        device.ReleaseRead();

        Assert.Equal(4, (await read).Length);
        Assert.True(device.SawRead);
    }
}