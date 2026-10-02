using System.Net;
using System.Net.Sockets;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Routing;
using Xunit;

namespace UotanStudio.Tests.Comm.Routing;

public sealed class AdbServerAvailabilityTests
{
    [Fact]
    public void UseAdbServer_ParameterFlipped_ReflectsGlobalParameterWithoutProbing()
    {
        var previous = DebugBridgeRoute.AdbServerAvailable;
        try
        {
            DebugBridgeRoute.SetAdbServerAvailable(true);
            Assert.True(DebugBridgeRoute.UseAdbServer());
            DebugBridgeRoute.SetAdbServerAvailable(false);
            Assert.False(DebugBridgeRoute.UseAdbServer());
            Assert.False(DebugBridgeRoute.AdbServerAvailable);
        }
        finally
        {
            DebugBridgeRoute.SetAdbServerAvailable(previous);
        }
    }

    [Fact]
    public void AdbServerOwns_ParameterFalseForDefaultEndpoint_ReturnsFalse()
    {
        var previous = DebugBridgeRoute.AdbServerAvailable;
        try
        {
            DebugBridgeRoute.SetAdbServerAvailable(false);
            Assert.False(DebugBridgeSessionOpener.AdbServerOwns("uotan-availability-absent-flag-serial"));
        }
        finally
        {
            DebugBridgeRoute.SetAdbServerAvailable(previous);
        }
    }

    [Fact]
    public async Task InitializeAdbServerProbe_DefaultEndpointConclusive_WritesMatchingParameter()
    {
        DebugBridgeRoute.InvalidateProbe(DebugBridgeEndpoints.ServerHost, DebugBridgeEndpoints.AdbServerPort);
        var outcome = await DebugBridgeRoute.TryProbeAsync(DebugBridgeEndpoints.ServerHost,
            DebugBridgeEndpoints.AdbServerPort, TestContext.Current.CancellationToken);
        if (outcome == DebugBridgeProbeOutcome.Unknown)
        {
            Assert.Skip("The default adb endpoint probe did not conclude on this machine.");
        }

        DebugBridgeRoute.SetAdbServerAvailable(outcome != DebugBridgeProbeOutcome.Listening);
        DebugBridgeRoute.InitializeAdbServerProbe();

        Assert.Equal(outcome == DebugBridgeProbeOutcome.Listening, DebugBridgeRoute.AdbServerAvailable);
    }

    [Fact]
    public void UseAdbServer_CustomEndpointWithListener_ProbesLiveForThatEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.True(DebugBridgeRoute.UseAdbServer("127.0.0.1", port));
        }
        finally
        {
            DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);
        }
    }

    [Fact]
    public void UseAdbServer_CustomRefusedEndpoint_ReturnsFalse()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);

        Assert.False(DebugBridgeRoute.UseAdbServer("127.0.0.1", port));
    }

    [Fact]
    public async Task TryProbeAsync_CustomEndpointListener_LeavesGlobalParameterUnchanged()
    {
        DebugBridgeRoute.InitializeAdbServerProbe();
        var before = DebugBridgeRoute.AdbServerAvailable;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.Equal(DebugBridgeProbeOutcome.Listening,
                await DebugBridgeRoute.TryProbeAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
            Assert.Equal(before, DebugBridgeRoute.AdbServerAvailable);
        }
        finally
        {
            DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);
        }
    }
}