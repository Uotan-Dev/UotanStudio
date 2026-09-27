using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Devices;
using UotanStudio.Tests.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class EdlConnectionCacheLivenessTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly List<ScriptedCommTransport> Transports = [];
        internal readonly EdlConnectionCache Cache;
        internal int Released;
        private Func<DeviceLease, bool>? probe;

        internal Fixture(bool useConstructorProbe = false)
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
            }, () => { }, useConstructorProbe ? lease => CurrentProbe(lease) : null);
        }

        internal Func<DeviceLease, bool>? Probe
        {
            get => probe;
            set
            {
                probe = value;
                Cache.LeaseCurrentProbe = CurrentProbe;
            }
        }

        private bool CurrentProbe(DeviceLease lease) => probe is null || probe(lease);

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
    public async Task RunAsync_ProbeReportsGenerationMismatch_RetiresParkedLeaseAndOpensFresh()
    {
        using var fixture = new Fixture();
        var device = Device();
        await fixture.Cache.ProbeAsync(device, TestContext.Current.CancellationToken);
        var parkedTransport = fixture.Transports[0];
        fixture.Probe = _ => false;

        await fixture.Cache.RunAsync(device, _ => Task.FromResult(true), TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Transports.Count);
        Assert.NotSame(parkedTransport, fixture.Transports[1]);
        Assert.Equal(2, fixture.Released);
    }

    [Fact]
    public async Task RunAsync_ProbeReportsGenerationMatch_ReusesParkedLease()
    {
        using var fixture = new Fixture();
        var device = Device();
        await fixture.Cache.ProbeAsync(device, TestContext.Current.CancellationToken);
        fixture.Probe = _ => true;

        await fixture.Cache.RunAsync(device, _ => Task.FromResult(true), TestContext.Current.CancellationToken);

        Assert.Single(fixture.Transports);
        Assert.Equal(1, fixture.Released);
    }

    [Fact]
    public async Task GetState_ProbeMismatchAfterParkedRetirement_ReportsFreshStateAfterReopen()
    {
        using var fixture = new Fixture(useConstructorProbe: true);
        var device = Device();
        Assert.Equal(EdlProtocolState.Sahara, await fixture.Cache.ProbeAsync(device, TestContext.Current.CancellationToken));
        fixture.Probe = _ => false;

        Assert.Equal(EdlProtocolState.Sahara, await fixture.Cache.ProbeAsync(device, TestContext.Current.CancellationToken));

        Assert.Equal(2, fixture.Transports.Count);
        Assert.Equal(1, fixture.Released);
    }

    [Fact]
    public async Task RunAsync_DisconnectedTypedLeaseWithoutProbe_IsRetiredAndReopened()
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
    public async Task RunAsync_DisconnectedLeaseWithKeepProbe_StillRetiredBecauseConnectionMustBeLive()
    {
        using var fixture = new Fixture();
        var device = Device();
        await fixture.Cache.ProbeAsync(device, TestContext.Current.CancellationToken);
        fixture.Transports[0].Disconnect();
        fixture.Probe = _ => true;

        await fixture.Cache.RunAsync(device, session =>
        {
            Assert.True(session.IsConnected);
            return Task.FromResult(true);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Transports.Count);
        Assert.Equal(2, fixture.Released);
    }

    [Fact]
    public void TransportGeneration_SameValue_EqualAndDifferentValue_NotEqual()
    {
        Assert.Equal(new TransportGeneration(4), new TransportGeneration(4));
        Assert.NotEqual(new TransportGeneration(4), new TransportGeneration(5));
    }
}