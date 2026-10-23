using System.Text.Json;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Routing;
using UotanStudio.Services;
using Xunit;

namespace UotanStudio.Tests.Devices;

[CollectionDefinition("Debug bridge route", DisableParallelization = true)]
public sealed class DebugBridgeRouteCollection;

/// <summary>
/// Covers the single route gate introduced for ADB/HDC: the fixed mode, the immutable availability
/// snapshot and its epoch, the direct-USB guard, and the lease draining/conflict rules.
/// <para>覆盖 ADB/HDC 的唯一路由门控：固定模式、不可变可用性快照及其 epoch、直连 USB 守卫，
/// 以及租约清理与冲突规则。</para>
/// </summary>
[Collection("Debug bridge route")]
public sealed class DebugBridgeRouteTests : IDisposable
{
    public DebugBridgeRouteTests() => DebugBridgeRoute.ResetForTests();

    public void Dispose() => DebugBridgeRoute.ResetForTests();

    [Fact]
    public void ConfigureMode_DifferentModeAfterFix_IsRejected()
    {
        DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.Auto);
        DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.Auto);

        Assert.Throws<InvalidOperationException>(() =>
            DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.ServerOnly));
        Assert.Equal(DebugBridgeRouteMode.Auto, DebugBridgeRoute.Mode);
    }

    [Fact]
    public void SuppressDirectUsb_ServerOnly_HidesBothProtocols()
    {
        DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.ServerOnly);

        Assert.True(DebugBridgeRoute.SuppressDirectUsb("adb"));
        Assert.True(DebugBridgeRoute.SuppressDirectUsb("hdc"));
        Assert.False(DebugBridgeRoute.SuppressDirectUsb("fastboot"));
    }

    [Fact]
    public void SuppressDirectUsb_DirectOnly_NeverHidesEvenWhenSnapshotIsLive()
    {
        DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.DirectOnly);
        DebugBridgeRoute.SetAvailabilityForTests(true, true);

        Assert.False(DebugBridgeRoute.SuppressDirectUsb("adb"));
        Assert.False(DebugBridgeRoute.SuppressDirectUsb("hdc"));
        Assert.False(DebugBridgeRoute.UseAdbServer());
        Assert.False(DebugBridgeRoute.UseHdcServer());
    }

    [Fact]
    public void SuppressDirectUsb_Auto_FollowsSnapshot()
    {
        DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.Auto);
        DebugBridgeRoute.SetAvailabilityForTests(true, false);

        Assert.True(DebugBridgeRoute.SuppressDirectUsb("adb"));
        Assert.False(DebugBridgeRoute.SuppressDirectUsb("hdc"));

        DebugBridgeRoute.SetAvailabilityForTests(false, false);

        Assert.False(DebugBridgeRoute.SuppressDirectUsb("adb"));
    }

    [Fact]
    public void StartupFrozen_RetainsStartupSnapshot()
    {
        DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.StartupFrozen);
        DebugBridgeRoute.SetAvailabilityForTests(true, false);
        var epoch = DebugBridgeRoute.Current.Epoch;

        DebugBridgeRoute.ApplyServerWatch(false, false);
        DebugBridgeRoute.SetAvailabilityForTests(false, true);

        var current = DebugBridgeRoute.Current;
        Assert.True(current.AdbServerAvailable);
        Assert.False(current.HdcServerAvailable);
        Assert.Equal(epoch, current.Epoch);
    }

    [Fact]
    public void Auto_WatchAdvancesEpochOnlyWhenSnapshotChanges()
    {
        DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.Auto);
        DebugBridgeRoute.SetAvailabilityForTests(false, false);
        var first = DebugBridgeRoute.Current.Epoch;

        DebugBridgeRoute.ApplyServerWatch(false, false);
        Assert.Equal(first, DebugBridgeRoute.Current.Epoch);

        DebugBridgeRoute.ApplyServerWatch(true, false);
        Assert.Equal(first + 1, DebugBridgeRoute.Current.Epoch);
        Assert.True(DebugBridgeRoute.Current.AdbServerAvailable);
    }

    [Fact]
    public void RequireDirectUsb_ServerOnly_RejectsDebugInterface()
    {
        DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.ServerOnly);

        Assert.Throws<IOException>(() => DebugBridgeSessionOpener.RequireDirectUsb(0xff, 0x42, 1, "SERIAL"));
        Assert.Throws<IOException>(() => DebugBridgeSessionOpener.RequireDirectUsb(0xff, 0x50, 1, "KEY"));
    }

    [Fact]
    public void RequireDirectUsb_NonDebugInterface_IsIgnored()
    {
        DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.ServerOnly);

        DebugBridgeSessionOpener.RequireDirectUsb(0xff, 0x00, 1, "SERIAL");
        DebugBridgeSessionOpener.RequireDirectUsb(0x08, 0x06, 0x50, "SERIAL");
    }

    [Fact]
    public void RequireDirectUsb_StartupFrozenWithPresentSnapshot_RejectsAdb()
    {
        DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.StartupFrozen);
        DebugBridgeRoute.SetAvailabilityForTests(true, false);

        Assert.Throws<IOException>(() => DebugBridgeSessionOpener.RequireDirectUsb(0xff, 0x42, 1, "SERIAL"));
    }

    [Fact]
    public void RequireDirectUsb_AutoWithoutServer_AllowsDebugInterface()
    {
        DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.Auto);
        DebugBridgeRoute.SetAvailabilityForTests(false, false);

        DebugBridgeSessionOpener.RequireDirectUsb(0xff, 0x42, 1, "SERIAL");
        DebugBridgeSessionOpener.RequireDirectUsb(0xff, 0x50, 1, "KEY");
    }

    [Fact]
    public async Task ServerWatch_FirstInconclusivePass_KeepsSeededState()
    {
        DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.Auto);
        DebugBridgeRoute.SetAvailabilityForTests(true, false);
        await using var controller = new DeviceController([], new NoopOpener());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var observed = new List<(bool Adb, bool Hdc)>();

        await controller.RunServerWatchAsync(DebugBridgeEndpoints.AdbServerPort,
            DebugBridgeEndpoints.HdcServerPort,
            static (_, _) => Task.FromResult(DebugBridgeProbeOutcome.Unknown),
            TimeSpan.FromMilliseconds(10), cancellation.Token,
            (adb, hdc) =>
            {
                observed.Add((adb, hdc));
                cancellation.Cancel();
            });

        Assert.NotEmpty(observed);
        Assert.Equal((true, false), observed[0]);
    }

    [Fact]
    public async Task ServerWatch_RefusedServer_ReleasesItsLease()
    {
        DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.Auto);
        DebugBridgeRoute.SetAvailabilityForTests(false, false);
        await using var controller = new DeviceController([], new NoopOpener());
        var endpoint = new DeviceEndpoint
        {
            Source = "adb-server@127.0.0.1:5037",
            Transport = "adb-server",
            Address = "SERVER-TARGET",
            Serial = "SERVER-TARGET",
            Protocol = "adb",
            Route = "server",
        };
        using var lease = controller.AdoptSession(endpoint, new object());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var removed = new TaskCompletionSource<DeviceEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.DeviceRemoved += removedEndpoint =>
        {
            removed.TrySetResult(removedEndpoint);
            cancellation.Cancel();
        };

        await controller.RunServerWatchAsync(DebugBridgeEndpoints.AdbServerPort,
            DebugBridgeEndpoints.HdcServerPort,
            static (_, _) => Task.FromResult(DebugBridgeProbeOutcome.Refused),
            TimeSpan.FromMilliseconds(10), cancellation.Token);

        var removedEndpoint = await removed.Task.WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Equal(endpoint.Id, removedEndpoint.Id);
    }

    [Fact]
    public async Task Open_StaleRouteEpoch_IsRejected()
    {
        DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.Auto);
        DebugBridgeRoute.SetAvailabilityForTests(false, false);
        var source = new StaticDiscoverySource("usb",
        [
            new DeviceEndpoint
            {
                Source = "usb",
                Transport = "winusb",
                Address = "USB-KEY",
                Serial = "SERIAL",
                Protocol = "fastboot",
                Route = "direct-usb",
                RouteEpoch = DebugBridgeRoute.Current.Epoch,
            }
        ]);
        await using var controller = new DeviceController([source], new NoopOpener());
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var id = snapshot.Endpoints[0].Id;

        DebugBridgeRoute.SetAvailabilityForTests(true, false);

        await Assert.ThrowsAsync<IOException>(() =>
            controller.OpenAsync(snapshot, id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Open_OtherRouteAlreadyHoldsDevice_IsRejected()
    {
        DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.Auto);
        DebugBridgeRoute.SetAvailabilityForTests(false, false);
        var source = new StaticDiscoverySource("usb",
        [
            new DeviceEndpoint
            {
                Source = "usb",
                Transport = "winusb",
                Address = "USB-KEY",
                Serial = "SHARED-SERIAL",
                Protocol = "adb",
                Route = "direct-usb",
                RouteEpoch = DebugBridgeRoute.Current.Epoch,
            }
        ]);
        await using var controller = new DeviceController([source], new NoopOpener());
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var serverEndpoint = new DeviceEndpoint
        {
            Source = "adb-server@127.0.0.1:5037",
            Transport = "adb-server",
            Address = "SHARED-SERIAL",
            Serial = "SHARED-SERIAL",
            Protocol = "adb",
            Route = "server",
        };
        using var lease = controller.AdoptSession(serverEndpoint, new object());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.OpenAsync(snapshot, snapshot.Endpoints[0].Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AppSettings_RouteMode_RoundTrips()
    {
        var settings = new AppSettings
        {
            DeviceCommunication = new Comm.DeviceCommunicationConfiguration
            {
                RouteMode = DebugBridgeRouteMode.StartupFrozen
            }
        };

        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        var roundTripped = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);

        Assert.NotNull(roundTripped);
        Assert.Equal(DebugBridgeRouteMode.StartupFrozen, roundTripped.DeviceCommunication.RouteMode);
    }

    [Fact]
    public void DeviceCommunicationConfiguration_UndefinedRouteMode_FallsBackToAuto()
    {
        var configuration = new Comm.DeviceCommunicationConfiguration
        {
            RouteMode = (DebugBridgeRouteMode)99
        };

        Assert.Equal(DebugBridgeRouteMode.Auto, configuration.RouteMode);
    }

    private sealed class NoopOpener : IDeviceTransportOpener
    {
        public Task<IAsyncDisposable> OpenAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
            => Task.FromResult<IAsyncDisposable>(new NoopDisposable());
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StaticDiscoverySource(string name, IReadOnlyList<DeviceEndpoint> endpoints)
        : IDeviceDiscoverySource
    {
        public string Name => name;

        public Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
            => Task.FromResult(endpoints);
    }
}