using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed class DeviceLeaseStreamTrackingTests
{
    private static readonly DeviceEndpoint Endpoint = new()
    {
        Source = "adb",
        Transport = "server",
        Address = "serial-stream"
    };

    [Fact]
    public async Task DisposeAsync_RegisteredStreams_ClosedBeforeSessionAndTransport()
    {
        var order = new List<string>();
        var transport = new LoggingResource(order, "transport");
        var session = new LoggingResource(order, "session");
        var released = 0;
        var lease = new DeviceLease(Endpoint, transport, session, () => released++);
        lease.RegisterStream(new LoggingResource(order, "stream-a"));
        lease.RegisterStream(new LoggingResource(order, "stream-b"));

        await lease.DisposeAsync();

        Assert.Equal(["stream-a", "stream-b", "session", "transport"], order);
        Assert.Equal(1, released);
    }

    [Fact]
    public async Task DisposeAsync_TrackedStreamFailure_StillClosesRemainingSessionAndTransport()
    {
        var order = new List<string>();
        var released = 0;
        var lease = new DeviceLease(
            Endpoint, new LoggingResource(order, "transport"), new LoggingResource(order, "session"), () => released++);
        lease.RegisterStream(new LoggingResource(order, "failing", failOnDispose: true));
        lease.RegisterStream(new LoggingResource(order, "second"));

        await lease.DisposeAsync();

        Assert.Equal(["failing", "second", "session", "transport"], order);
        Assert.Equal(1, released);
    }

    [Fact]
    public async Task RegisterStream_AfterTeardownStarted_ThrowsObjectDisposed()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new DeviceLease(Endpoint, new GatedResource(gate.Task), session: null, () => { });
        var teardown = lease.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => lease.RegisterStream(new LoggingResource([], "late")));

        gate.SetResult();
        await teardown;
    }

    [Fact]
    public async Task StreamRegistrationHandle_Disposed_UnregistersWithoutClosingStream()
    {
        var order = new List<string>();
        var stream = new LoggingResource(order, "stream");
        var lease = new DeviceLease(Endpoint, new LoggingResource(order, "transport"), session: null, () => { });
        var registration = lease.RegisterStream(stream);

        registration.Dispose();
        await lease.DisposeAsync();

        Assert.Equal(["transport"], order);
        Assert.Equal(0, stream.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCalls_TeardownRunsOnceAndStreamsCloseOnce()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new GatedStream(gate.Task);
        var transport = new CountingResource();
        var released = 0;
        var lease = new DeviceLease(Endpoint, transport, session: null, () => Interlocked.Increment(ref released));
        lease.RegisterStream(stream);

        var first = lease.DisposeAsync();
        var second = lease.DisposeAsync();
        gate.SetResult();
        await first;
        await second;

        Assert.Equal(1, stream.DisposeCount);
        Assert.Equal(1, transport.DisposeCount);
        Assert.Equal(1, released);
    }

    [Fact]
    public async Task RegisterStream_NullStream_ThrowsArgumentNull()
    {
        var lease = new DeviceLease(
            Endpoint, new LoggingResource([], "transport"), session: null, () => { });

        Assert.Throws<ArgumentNullException>(() => lease.RegisterStream(null!));
        await lease.DisposeAsync();
    }

    private sealed class LoggingResource(List<string> order, string name, bool failOnDispose = false) : IAsyncDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref _disposeCount) == 1)
            {
                lock (order)
                {
                    order.Add(name);
                }
            }

            return failOnDispose ? ValueTask.FromException(new IOException("the stream refused to close.")) : default;
        }
    }

    private sealed class GatedResource(Task gate) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await gate.ConfigureAwait(false);
    }

    private sealed class GatedStream(Task gate) : IAsyncDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            await gate.ConfigureAwait(false);
        }
    }

    private sealed class CountingResource : IAsyncDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return default;
        }
    }
}