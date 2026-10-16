using System.Collections.Concurrent;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Routing;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed class DeviceServerWatchStateTests
{
    private const int ProbeAdbPort = 40537;
    private const int ProbeHdcPort = 48710;

    [Fact]
    public async Task RunServerWatchAsync_UnknownAfterListening_KeepsStateAndReleasesVanishedLease()
    {
        var endpoint = ServerEndpoint("adb-server", "adb-server", "serial-1");
        var source = new ScriptedSource("adb-server", index => index == 1 ? [endpoint] : []);
        await using var controller = CreateController(source);
        await using var lease = controller.AdoptSession(endpoint, new object());
        var events = new List<(bool Adb, bool Hdc)>();
        var removed = new List<DeviceEndpoint>();
        controller.DebugBridgeAvailabilityChanged += (adb, hdc) => events.Add((adb, hdc));
        controller.DeviceRemoved += removed.Add;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = new ScriptedProbe(
            [
                (DebugBridgeProbeOutcome.Listening, DebugBridgeProbeOutcome.Refused),
                (DebugBridgeProbeOutcome.Unknown, DebugBridgeProbeOutcome.Refused),
            ],
            cancellation);

        await controller.RunServerWatchAsync(ProbeAdbPort, ProbeHdcPort, probe.ProbeAsync, TimeSpan.FromMilliseconds(1), cancellation.Token);

        Assert.Equal([(true, false)], events);
        Assert.Equal(2, source.CallCount);
        Assert.Equal([endpoint], removed);
    }

    [Fact]
    public async Task RunServerWatchAsync_RefusedAfterListening_RaisesFlipAndSkipsReleasePass()
    {
        var endpoint = ServerEndpoint("adb-server", "adb-server", "serial-1");
        var source = new ScriptedSource("adb-server", _ => [endpoint]);
        await using var controller = CreateController(source);
        await using var lease = controller.AdoptSession(endpoint, new object());
        var events = new List<(bool Adb, bool Hdc)>();
        var removed = new List<DeviceEndpoint>();
        controller.DebugBridgeAvailabilityChanged += (adb, hdc) => events.Add((adb, hdc));
        controller.DeviceRemoved += removed.Add;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = new ScriptedProbe(
            [
                (DebugBridgeProbeOutcome.Listening, DebugBridgeProbeOutcome.Refused),
                (DebugBridgeProbeOutcome.Refused, DebugBridgeProbeOutcome.Refused),
            ],
            cancellation);

        await controller.RunServerWatchAsync(ProbeAdbPort, ProbeHdcPort, probe.ProbeAsync, TimeSpan.FromMilliseconds(1), cancellation.Token);

        Assert.Equal([(true, false), (false, false)], events);
        Assert.Equal(1, source.CallCount);
        Assert.Empty(removed);
    }

    [Fact]
    public async Task RunServerWatchAsync_UnknownFirstPass_TreatsServerAsAbsentAndKeepsLease()
    {
        var endpoint = ServerEndpoint("adb-server", "adb-server", "serial-1");
        var source = new ScriptedSource("adb-server", _ => []);
        await using var controller = CreateController(source);
        await using var lease = controller.AdoptSession(endpoint, new object());
        var events = new List<(bool Adb, bool Hdc)>();
        var removed = new List<DeviceEndpoint>();
        controller.DebugBridgeAvailabilityChanged += (adb, hdc) => events.Add((adb, hdc));
        controller.DeviceRemoved += removed.Add;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = new ScriptedProbe(
            [(DebugBridgeProbeOutcome.Unknown, DebugBridgeProbeOutcome.Unknown)],
            cancellation);

        await controller.RunServerWatchAsync(ProbeAdbPort, ProbeHdcPort, probe.ProbeAsync, TimeSpan.FromMilliseconds(1), cancellation.Token);

        Assert.Empty(events);
        Assert.Equal(0, source.CallCount);
        Assert.Empty(removed);
    }

    [Fact]
    public async Task RunServerWatchAsync_MultipleLeasesPerSource_DiscoversSourceOncePerPass()
    {
        var first = ServerEndpoint("adb-server", "adb-server", "serial-1");
        var second = ServerEndpoint("adb-server", "adb-server", "serial-2");
        var hdc = ServerEndpoint("hdc-server", "hdc-server", "serial-3");
        var adbSource = new ScriptedSource("adb-server", _ => [first, second]);
        var hdcSource = new ScriptedSource("hdc-server", _ => [hdc]);
        await using var controller = CreateController(adbSource, hdcSource);
        await using var firstLease = controller.AdoptSession(first, new object());
        await using var secondLease = controller.AdoptSession(second, new object());
        await using var hdcLease = controller.AdoptSession(hdc, new object());
        var removed = new List<DeviceEndpoint>();
        controller.DeviceRemoved += removed.Add;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = new ScriptedProbe(
            [
                (DebugBridgeProbeOutcome.Listening, DebugBridgeProbeOutcome.Listening),
                (DebugBridgeProbeOutcome.Listening, DebugBridgeProbeOutcome.Listening),
            ],
            cancellation);

        await controller.RunServerWatchAsync(ProbeAdbPort, ProbeHdcPort, probe.ProbeAsync, TimeSpan.FromMilliseconds(1), cancellation.Token);

        Assert.Equal(2, adbSource.CallCount);
        Assert.Equal(2, hdcSource.CallCount);
        Assert.Empty(removed);
    }

    [Fact]
    public async Task RunServerWatchAsync_SameAddressDifferentTransport_ReleasesLease()
    {
        var endpoint = ServerEndpoint("adb-server", "adb-server", "serial-1");
        var foreignTransport = new DeviceEndpoint
        {
            Source = "adb-server",
            Transport = "direct-tcp",
            Address = "serial-1",
            Route = "server",
        };
        var source = new ScriptedSource("adb-server", _ => [foreignTransport]);
        await using var controller = CreateController(source);
        await using var lease = controller.AdoptSession(endpoint, new object());
        var removed = new List<DeviceEndpoint>();
        controller.DeviceRemoved += removed.Add;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = new ScriptedProbe(
            [(DebugBridgeProbeOutcome.Listening, DebugBridgeProbeOutcome.Refused)],
            cancellation);

        await controller.RunServerWatchAsync(ProbeAdbPort, ProbeHdcPort, probe.ProbeAsync, TimeSpan.FromMilliseconds(1), cancellation.Token);

        Assert.Equal([endpoint], removed);
    }

    [Fact]
    public async Task RunServerWatchAsync_AvailabilitySubscriberThrows_ReleasePassStillRunsAndErrorIsLogged()
    {
        var endpoint = ServerEndpoint("adb-server", "adb-server", "serial-1");
        var source = new ScriptedSource("adb-server", _ => []);
        await using var controller = CreateController(source);
        await using var lease = controller.AdoptSession(endpoint, new object());
        controller.DebugBridgeAvailabilityChanged += (_, _) => throw new InvalidOperationException("subscriber failure");
        var removed = new List<DeviceEndpoint>();
        controller.DeviceRemoved += removed.Add;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = new ScriptedProbe(
            [(DebugBridgeProbeOutcome.Listening, DebugBridgeProbeOutcome.Refused)],
            cancellation);
        var logger = new RecordingLogger();
        var previous = UsbTrace.Logger;
        UsbTrace.Logger = logger;
        try
        {
            await controller.RunServerWatchAsync(ProbeAdbPort, ProbeHdcPort, probe.ProbeAsync, TimeSpan.FromMilliseconds(1), cancellation.Token);
        }
        finally
        {
            UsbTrace.Logger = previous;
        }

        Assert.Equal(1, source.CallCount);
        Assert.Equal([endpoint], removed);
        Assert.Contains(logger.Messages, message => message.Contains("Debug bridge availability subscriber failed: InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunServerWatchAsync_RemovalSubscriberThrows_BothLeasesReleasedAndErrorIsLogged()
    {
        var first = ServerEndpoint("adb-server", "adb-server", "serial-1");
        var second = ServerEndpoint("adb-server", "adb-server", "serial-2");
        var source = new ScriptedSource("adb-server", _ => []);
        await using var controller = CreateController(source);
        await using var firstLease = controller.AdoptSession(first, new object());
        await using var secondLease = controller.AdoptSession(second, new object());
        var removed = new List<DeviceEndpoint>();
        controller.DeviceRemoved += candidate =>
        {
            removed.Add(candidate);
            throw new InvalidOperationException("subscriber failure");
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = new ScriptedProbe(
            [(DebugBridgeProbeOutcome.Listening, DebugBridgeProbeOutcome.Refused)],
            cancellation);
        var logger = new RecordingLogger();
        var previous = UsbTrace.Logger;
        UsbTrace.Logger = logger;
        try
        {
            await controller.RunServerWatchAsync(ProbeAdbPort, ProbeHdcPort, probe.ProbeAsync, TimeSpan.FromMilliseconds(1), cancellation.Token);
        }
        finally
        {
            UsbTrace.Logger = previous;
        }

        Assert.Equal(1, source.CallCount);
        Assert.Equal([first, second], removed);
        Assert.Contains(logger.Messages, message => message.Contains("Device removal subscriber failed: InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunServerWatchAsync_AdbAvailabilitySink_ReceivesRetainedStateEachPass()
    {
        await using var controller = CreateController(new ScriptedSource("adb-server", _ => []));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = new ScriptedProbe(
            [
                (DebugBridgeProbeOutcome.Listening, DebugBridgeProbeOutcome.Refused),
                (DebugBridgeProbeOutcome.Unknown, DebugBridgeProbeOutcome.Refused),
                (DebugBridgeProbeOutcome.Refused, DebugBridgeProbeOutcome.Refused),
            ],
            cancellation);
        var received = new List<bool>();

        await controller.RunServerWatchAsync(ProbeAdbPort, ProbeHdcPort, probe.ProbeAsync,
            TimeSpan.FromMilliseconds(1), cancellation.Token, received.Add);

        Assert.Equal([true, true, false], received);
    }

    [Fact]
    public async Task RunServerWatchAsync_HdcAvailabilitySink_ReceivesRetainedStateEachPass()
    {
        await using var controller = CreateController(new ScriptedSource("hdc-server", _ => []));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = new ScriptedProbe(
            [
                (DebugBridgeProbeOutcome.Refused, DebugBridgeProbeOutcome.Listening),
                (DebugBridgeProbeOutcome.Refused, DebugBridgeProbeOutcome.Unknown),
                (DebugBridgeProbeOutcome.Refused, DebugBridgeProbeOutcome.Refused),
            ],
            cancellation);
        var received = new List<bool>();

        await controller.RunServerWatchAsync(ProbeAdbPort, ProbeHdcPort, probe.ProbeAsync,
            TimeSpan.FromMilliseconds(1), cancellation.Token, null, received.Add);

        Assert.Equal([true, true, false], received);
    }

    [Fact]
    public async Task RunServerWatchAsync_NoAvailabilitySink_PassesLeaveGlobalParameterUnchanged()
    {
        DebugBridgeRoute.InitializeAdbServerProbe();
        var before = DebugBridgeRoute.AdbServerAvailable;
        await using var controller = CreateController(new ScriptedSource("adb-server", _ => []));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = new ScriptedProbe(
            [(DebugBridgeProbeOutcome.Listening, DebugBridgeProbeOutcome.Refused)],
            cancellation);

        await controller.RunServerWatchAsync(ProbeAdbPort, ProbeHdcPort, probe.ProbeAsync,
            TimeSpan.FromMilliseconds(1), cancellation.Token);

        Assert.Equal(before, DebugBridgeRoute.AdbServerAvailable);
    }

    private static DeviceEndpoint ServerEndpoint(string source, string transport, string address)
        => new() { Source = source, Transport = transport, Address = address, Route = "server" };

    private static DeviceController CreateController(params IDeviceDiscoverySource[] sources)
        => new(sources, new NullOpener());

    private sealed class ScriptedSource(string name, Func<int, IReadOnlyList<DeviceEndpoint>> results) : IDeviceDiscoverySource
    {
        private int _calls;

        public string Name => name;

        public int CallCount => Volatile.Read(ref _calls);

        public Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _calls);
            return Task.FromResult(results(index));
        }
    }

    private sealed class ScriptedProbe(
        (DebugBridgeProbeOutcome Adb, DebugBridgeProbeOutcome Hdc)[] passes,
        CancellationTokenSource cancellation)
    {
        private int _completedPasses;

        public Task<DebugBridgeProbeOutcome> ProbeAsync(int port, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (port == ProbeAdbPort)
            {
                if (_completedPasses >= passes.Length)
                {
                    cancellation.Cancel();
                    return Task.FromResult(DebugBridgeProbeOutcome.Unknown);
                }

                return Task.FromResult(passes[_completedPasses].Adb);
            }

            var outcome = passes[Math.Min(_completedPasses, passes.Length - 1)].Hdc;
            Interlocked.Increment(ref _completedPasses);
            return Task.FromResult(outcome);
        }
    }

    private sealed class NullOpener : IDeviceTransportOpener
    {
        public Task<IAsyncDisposable> OpenAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
            => Task.FromResult<IAsyncDisposable>(new NullTransport());

        private sealed class NullTransport : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLogger : IUsbLogger
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => [.. _messages];

        public void Log(string message) => _messages.Enqueue(message);
    }
}