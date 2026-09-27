using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Devices;
using UotanStudio.Tests.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class EdlConnectionCacheTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly List<ScriptedCommTransport> Transports = [];
        internal readonly EdlConnectionCache Cache;
        internal int Released;

        internal Fixture()
        {
            Cache = new EdlConnectionCache((device, token) =>
            {
                token.ThrowIfCancellationRequested();
                var transport = new ScriptedCommTransport();
                transport.Enqueue(EdlProtocolProbeTests.Hello());
                Transports.Add(transport);
                var endpoint = new DeviceEndpoint { Source = "test", Transport = "usb", Address = device.Key };
                return Task.FromResult(new DeviceLease(endpoint, new AsyncHandle(),
                    new EdlSession(transport), () => Released++));
            }, () => { });
        }

        public void Dispose() => Cache.Dispose();
    }

    private sealed class AsyncHandle : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static DetectedDevice Device(string key = "edl:a") => new()
    {
        Key = key,
        Serial = key,
        Protocol = DeviceProtocolKind.Edl,
        Transport = DeviceTransportKind.Usb
    };

    [Fact]
    public async Task RunAsync_AfterProbe_ReusesLeaseAndReleasesIt()
    {
        using var fixture = new Fixture();
        var device = Device();
        Assert.Equal(EdlProtocolState.Sahara, await fixture.Cache.ProbeAsync(device, TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Released);

        await fixture.Cache.RunAsync(device, async session =>
        {
            var buffer = new byte[48];
            Assert.Equal(48, await session.Transport.ReadAsync(buffer, 0, 48, 100, TestContext.Current.CancellationToken));
            Assert.Equal(EdlProtocolProbeTests.Hello(), buffer);
            return true;
        }, TestContext.Current.CancellationToken);

        Assert.Single(fixture.Transports);
        Assert.Equal(1, fixture.Released);
    }

    [Fact]
    public async Task ProbeAsync_OperationBusy_ReturnsStateWithoutConcurrentReads()
    {
        using var fixture = new Fixture();
        var device = Device();
        await fixture.Cache.ProbeAsync(device, TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = fixture.Cache.RunAsync(device, async _ =>
        {
            started.SetResult();
            await finish.Task.WaitAsync(TestContext.Current.CancellationToken);
            return true;
        }, TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            var reads = fixture.Transports[0].ReadCount;
            Assert.Equal(EdlProtocolState.Sahara, await fixture.Cache.ProbeAsync(device, TestContext.Current.CancellationToken));
            Assert.Equal(reads, fixture.Transports[0].ReadCount);
        }
        finally
        {
            finish.TrySetResult();
            await operation;
        }
    }

    [Fact]
    public async Task RetainDevices_RemovedEndpoint_ReleasesLeaseAndClearsOnlyItsState()
    {
        using var fixture = new Fixture();
        await fixture.Cache.ProbeAsync(Device("a"), TestContext.Current.CancellationToken);
        await fixture.Cache.ProbeAsync(Device("b"), TestContext.Current.CancellationToken);
        fixture.Cache.RetainDevices(new HashSet<string> { "b" });
        Assert.Equal(1, fixture.Released);
        Assert.Equal(EdlProtocolState.Unknown, fixture.Cache.GetState("a"));
        Assert.Equal(EdlProtocolState.Sahara, fixture.Cache.GetState("b"));
        fixture.Cache.Dispose();
        Assert.Equal(2, fixture.Released);
    }

    [Fact]
    public async Task RunAsync_DisconnectedParkedLease_OpensFreshConnection()
    {
        using var fixture = new Fixture();
        var device = Device();
        await fixture.Cache.ProbeAsync(device, TestContext.Current.CancellationToken);
        fixture.Transports[0].Disconnect();

        await fixture.Cache.RunAsync(device, session =>
        {
            Assert.True(session.IsConnected);
            return Task.FromResult(true);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Transports.Count);
        Assert.Equal(2, fixture.Released);
    }

    [Fact]
    public async Task RunAsync_RemovedDuringOperation_DefersDisposalUntilOperationEnds()
    {
        using var fixture = new Fixture();
        var device = Device();
        await fixture.Cache.ProbeAsync(device, TestContext.Current.CancellationToken);
        await fixture.Cache.RunAsync(device, session =>
        {
            fixture.Cache.RetainDevices(new HashSet<string>());
            Assert.Equal(0, fixture.Released);
            Assert.True(session.IsConnected);
            return Task.FromResult(true);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Released);
        Assert.Equal(EdlProtocolState.Unknown, fixture.Cache.GetState(device.Key));
    }
}