using System.Reflection;
using System.Runtime.InteropServices;
using LibUsbDotNet;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.LibUsb;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class LibUsbTransferTests
{
    [Theory]
    [InlineData(123, 123)]
    [InlineData(-1, LibUsbTransfer.IndefiniteNativeTimeoutMs)]
    [InlineData(0, 5000)]
    [InlineData(-2, 5000)]
    public void Timeout_IsPerTransferAndUsesLibrarySentinels(int input, int native)
        => Assert.Equal(native, LibUsbTransfer.NativeTimeout(input));

    [Fact]
    public void NativeTimeout_InfiniteSentinel_NeverMapsToNativeNoTimeoutZero()
        => Assert.NotEqual(0, LibUsbTransfer.NativeTimeout(-1));

    [Theory]
    [InlineData(Error.Success, 0, false, true)]
    [InlineData(Error.Success, 3, false, true)]
    [InlineData(Error.Success, 8, false, false)]
    [InlineData(Error.Timeout, 0, true, false)]
    [InlineData(Error.Timeout, 3, true, false)]
    public void ReadResult_PreservesZlpShortPacketAndPartialTimeout(Error error, int count, bool timeout, bool shortPacket)
    {
        var result = LibUsbTransfer.ReadResult(error, count, 8);
        Assert.Equal(count, result.Count);
        Assert.Equal(timeout, result.IsTimeout);
        Assert.Equal(shortPacket, result.IsShortPacket);
    }

    [Theory]
    [InlineData(Error.Pipe)]
    [InlineData(Error.Overflow)]
    [InlineData(Error.Io)]
    [InlineData(Error.Interrupted)]
    [InlineData(Error.Access)]
    public void NativeFailure_IsNotAnOrdinaryTimeout(Error error)
    {
        var ex = Assert.Throws<UsbTransferException>(() => LibUsbTransfer.ReadResult(error, 0, 8));
        Assert.Equal((int)error, ex.NativeErrorCode);
        Assert.Equal("libusb", ex.Backend);
    }

    [Fact]
    public void DisconnectAndInvalidCounts_AreRejected()
    {
        Assert.Throws<UsbDeviceDisconnectedException>(() => LibUsbTransfer.ReadResult(Error.NoDevice, 0, 8));
        Assert.Throws<InvalidDataException>(() => LibUsbTransfer.ReadResult(Error.Success, -1, 8));
        Assert.Throws<InvalidDataException>(() => LibUsbTransfer.ReadResult(Error.Success, 9, 8));
        Assert.Equal(Error.Pipe, LibUsbTransfer.FromStatus(TransferStatus.Stall));
        Assert.Equal(Error.Timeout, LibUsbTransfer.FromStatus(TransferStatus.TimedOut));
        Assert.Equal(Error.Interrupted, LibUsbTransfer.FromStatus(TransferStatus.Cancelled));
    }

    [Fact]
    public void ControlSetup_UsesLittleEndianAndOnlyTheRequestedSlice()
    {
        var setup = new UsbSetupPacket { RequestType = 0x40, Request = 2, Value = 0x1234, Index = 0x5678, Length = 2 };
        Assert.Equal(Convert.FromHexString("4002341278560200AABB"), LibUsbDevice.BuildControlBuffer(setup, [9, 0xaa, 0xbb, 8], 1, 2));
        setup.RequestType = 0xc0;
        Assert.Equal(Convert.FromHexString("C0023412785602000000"), LibUsbDevice.BuildControlBuffer(setup, [9, 0xaa, 0xbb, 8], 1, 2));
        Assert.Throws<ArgumentException>(() => LibUsbDevice.BuildControlBuffer(setup, new byte[3], 0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => LibUsbDevice.BuildControlBuffer(default, null, 1, 0));
    }

    [Theory]
    [InlineData(TransferType.Control, 0)]
    [InlineData(TransferType.Interrupt, 0x81)]
    public async Task Cancellation_RetainsTransferAndHandleUntilNativeCompletion(TransferType type, byte endpoint)
    {
        var native = new FakeApi();
        using var handle = new TestHandle();
        using var cancel = new CancellationTokenSource();
        var task = LibUsbTransfer.ExecuteAsync(handle, endpoint, type, new byte[16], -1, cancel.Token, native);
        Assert.Equal((byte)type, native.Type);
        Assert.Equal(endpoint, native.Endpoint);
        Assert.Equal((uint)LibUsbTransfer.IndefiniteNativeTimeoutMs, native.Timeout);
        handle.Dispose();
        await cancel.CancelAsync();
        Assert.Equal(1, native.Cancels);
        Assert.False(task.IsCompleted);
        Assert.Equal(0, native.Frees);
        Assert.Equal(0, handle.Releases);
        native.Complete(TransferStatus.Cancelled, 0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, native.Frees);
        Assert.Equal(1, handle.Releases);
    }

    [Fact]
    public async Task Timeout_IsNotRetried_AndCompletionBeforeCancellationIsSafe()
    {
        var native = new FakeApi();
        using var handle = new TestHandle();
        using var cancel = new CancellationTokenSource();
        var task = LibUsbTransfer.ExecuteAsync(handle, 0, TransferType.Control, new byte[8], 37, cancel.Token, native);
        Assert.Equal(37u, native.Timeout);
        native.Complete(TransferStatus.TimedOut, 0);
        Assert.Equal(Error.Timeout, (await task).Error);
        await cancel.CancelAsync();
        Assert.Equal(0, native.Cancels);
        Assert.Equal(1, native.Submits);
        Assert.Equal(1, native.Frees);
    }

    [Fact]
    public async Task SubmitFailureAndPreCancellation_DoNotLeakOrSubmitAgain()
    {
        var native = new FakeApi { SubmitError = Error.NoDevice };
        using var handle = new TestHandle();
        await Assert.ThrowsAsync<UsbDeviceDisconnectedException>(() => LibUsbTransfer.ExecuteAsync(handle, 0, TransferType.Control,
            new byte[8], 50, TestContext.Current.CancellationToken, native));
        Assert.Equal(1, native.Frees);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LibUsbTransfer.ExecuteAsync(handle, 0, TransferType.Control,
            new byte[8], 50, cancel.Token, native));
        Assert.Equal(1, native.Submits);
    }

    [Fact]
    public async Task CompletionRacingCancellation_ReleasesExactlyOnce()
    {
        for (var i = 0; i < 100; i++)
        {
            var native = new FakeApi();
            using var handle = new TestHandle();
            using var cancel = new CancellationTokenSource();
            var transfer = LibUsbTransfer.ExecuteAsync(handle, 0x81, TransferType.Interrupt,
                new byte[8], 1000, cancel.Token, native);
            await Task.WhenAll(Task.Run(() => native.Complete(TransferStatus.Completed, 8), TestContext.Current.CancellationToken),
                Task.Run(() => cancel.Cancel(), TestContext.Current.CancellationToken));
            try { Assert.Equal(8, (await transfer).Count); }
            catch (OperationCanceledException) { Assert.True(cancel.IsCancellationRequested); }
            handle.Dispose();
            Assert.Equal(1, native.Frees);
            Assert.Equal(1, handle.Releases);
            Assert.InRange(native.Cancels, 0, 1);
        }
    }

    [Fact]
    public async Task Dispose_ReadChunkGateHeld_WaitsUntilOwnerReleases()
    {
        var device = new LibUsbDevice();
        var readGate = GetChunkGate(device, "_readChunkGate");
        var writeGate = GetChunkGate(device, "_writeChunkGate");
        Assert.True(await readGate.WaitAsync(0, TestContext.Current.CancellationToken));
        var disposing = Task.Run(device.Dispose, TestContext.Current.CancellationToken);
        var completedFirst = await Task.WhenAny(disposing, Task.Delay(250, TestContext.Current.CancellationToken));
        Assert.NotSame(disposing, completedFirst);
        readGate.Release();
        await disposing;
        Assert.Throws<ObjectDisposedException>(() => { readGate.Wait(0, TestContext.Current.CancellationToken); });
        Assert.Throws<ObjectDisposedException>(() => { writeGate.Wait(0, TestContext.Current.CancellationToken); });
    }

    [Fact]
    public void Dispose_IdleDevice_DisposesChunkGatesAndStaysRepeatable()
    {
        var device = new LibUsbDevice();
        device.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { GetChunkGate(device, "_readChunkGate").Wait(0, TestContext.Current.CancellationToken); });
        Assert.Throws<ObjectDisposedException>(() => { GetChunkGate(device, "_writeChunkGate").Wait(0, TestContext.Current.CancellationToken); });
        device.Dispose();
    }

    private static SemaphoreSlim GetChunkGate(LibUsbDevice device, string fieldName) =>
        (SemaphoreSlim)typeof(LibUsbDevice).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(device)!;

    private sealed class TestHandle : SafeHandle
    {
        internal int Releases { get; private set; }
        internal TestHandle() : base(IntPtr.Zero, true) => SetHandle((IntPtr)1);
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() { Releases++; return true; }
    }

    private sealed class FakeApi : LibUsbTransfer.INativeApi
    {
        private IntPtr _pointer;
        internal Error SubmitError { get; init; }
        internal int Submits { get; private set; }
        internal int Cancels { get; private set; }
        internal int Frees { get; private set; }
        internal byte Type { get; private set; }
        internal byte Endpoint { get; private set; }
        internal uint Timeout { get; private set; }
        public IntPtr Allocate()
        {
            _pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Transfer>());
            Marshal.StructureToPtr(default(Transfer), _pointer, false);
            return _pointer;
        }
        public Error Submit(IntPtr pointer)
        {
            Submits++;
            var transfer = Marshal.PtrToStructure<Transfer>(pointer);
            Type = transfer.Type; Endpoint = transfer.Endpoint; Timeout = transfer.Timeout;
            return SubmitError;
        }
        public Error Cancel(IntPtr pointer) { Cancels++; return Error.Success; }
        public void Free(IntPtr pointer) { Frees++; Marshal.FreeHGlobal(pointer); _pointer = IntPtr.Zero; }
        internal void Complete(TransferStatus status, int count)
        {
            var transfer = Marshal.PtrToStructure<Transfer>(_pointer);
            transfer.Status = status; transfer.ActualLength = count;
            Marshal.StructureToPtr(transfer, _pointer, false);
            Marshal.GetDelegateForFunctionPointer<LibUsbTransfer.CompletedCallback>(transfer.Callback)(_pointer);
        }
    }
}