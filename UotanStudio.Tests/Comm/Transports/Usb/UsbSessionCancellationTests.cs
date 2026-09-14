using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;
using UotanStudio.Comm.Transports.Usb.Core;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class UsbSessionCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetCancelledWhileWaiting_ReleasesPreviouslyAcquiredGates(bool control)
    {
        var device = new BlockingDevice();
        var session = new UsbDeviceSession("test", UsbApiKind.Custom, device);
        var recovered = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        Task blocker = control
            ? session.ControlTransferAsync(default, null, 0, 0, 1000, deadline.Token)
            : session.WriteAsync(new byte[1], 0, 1, 1000, deadline.Token);
        try
        {
            await device.Entered.Task.WaitAsync(deadline.Token);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            // Reset acquires the read gate (and, for control, the write gate) synchronously
            // before returning an incomplete task waiting on the occupied direction.
            var reset = session.ResetAsync(cancel.Token);
            Assert.False(reset.IsCompleted);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset);
            Assert.Equal(0, device.ResetCount);
            device.Release.TrySetResult();
            await blocker;
            await session.ResetAsync(deadline.Token);
            Assert.Equal(1, device.ResetCount);
            recovered = true;
        }
        finally
        {
            device.Release.TrySetResult();
            await blocker;
            // A regression must fail on the bounded second reset, not hang test teardown.
            if (recovered) session.Dispose();
            else device.Dispose();
        }
    }

    private sealed class BlockingDevice : UsbDevice
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ResetCount { get; private set; }
        protected override string BackendName => "test";
        protected override bool IsOpen => true;
        internal override bool IsHandleOpen => true;
        protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs) => UsbChunkResult.Timeout(0);
        protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs) => UsbChunkResult.Timeout(0);
        public override byte[] Read(int length) => [];
        public override long Write(byte[] data, int length) => length;
        public override int GetSerialNumber() => 0;
        public override int CreateHandle() => 0;
        public override void Reset() => ResetCount++;
        public override Task ResetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reset(); return Task.CompletedTask;
        }
        public override async Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return length;
        }
        public override async Task<int> ControlTransferAsync(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return length;
        }
        public override void Dispose() { }
    }
}