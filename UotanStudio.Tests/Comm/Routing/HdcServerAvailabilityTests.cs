using System.Net;
using System.Net.Sockets;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Routing;
using Xunit;

namespace UotanStudio.Tests.Comm.Routing;

public sealed class HdcServerAvailabilityTests
{
    [Fact]
    public void UseHdcServer_ParameterFlipped_ReflectsGlobalParameterWithoutProbing()
    {
        var previous = DebugBridgeRoute.HdcServerAvailable;
        try
        {
            DebugBridgeRoute.SetHdcServerAvailable(true);
            Assert.True(DebugBridgeRoute.UseHdcServer());
            DebugBridgeRoute.SetHdcServerAvailable(false);
            Assert.False(DebugBridgeRoute.UseHdcServer());
            Assert.False(DebugBridgeRoute.HdcServerAvailable);
        }
        finally
        {
            DebugBridgeRoute.SetHdcServerAvailable(previous);
        }
    }

    [Fact]
    public void HdcServerOwns_ParameterFalseForDefaultEndpoint_ReturnsFalse()
    {
        var previous = DebugBridgeRoute.HdcServerAvailable;
        try
        {
            DebugBridgeRoute.SetHdcServerAvailable(false);
            Assert.False(DebugBridgeSessionOpener.HdcServerOwns("uotan-availability-absent-flag-key"));
        }
        finally
        {
            DebugBridgeRoute.SetHdcServerAvailable(previous);
        }
    }

    [Fact]
    public async Task InitializeHdcServerProbe_DefaultEndpointConclusive_WritesMatchingParameter()
    {
        var defaultPort = DebugBridgeEndpoints.HdcServerPort;
        DebugBridgeRoute.InvalidateProbe(DebugBridgeEndpoints.ServerHost, defaultPort);
        var outcome = await DebugBridgeRoute.TryProbeAsync(DebugBridgeEndpoints.ServerHost,
            defaultPort, TestContext.Current.CancellationToken);
        if (outcome == DebugBridgeProbeOutcome.Unknown)
        {
            Assert.Skip("The default HDC endpoint probe did not conclude on this machine.");
        }

        DebugBridgeRoute.SetHdcServerAvailable(outcome != DebugBridgeProbeOutcome.Listening);
        DebugBridgeRoute.InitializeHdcServerProbe();

        Assert.Equal(outcome == DebugBridgeProbeOutcome.Listening, DebugBridgeRoute.HdcServerAvailable);
    }

    [Fact]
    public void UseHdcServer_CustomEndpointWithListener_ProbesLiveForThatEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.True(DebugBridgeRoute.UseHdcServer("127.0.0.1", port));
        }
        finally
        {
            DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);
        }
    }

    [Fact]
    public void UseHdcServer_CustomRefusedEndpoint_ReturnsFalse()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);

        Assert.False(DebugBridgeRoute.UseHdcServer("127.0.0.1", port));
    }

    [Fact]
    public async Task TryProbeAsync_CustomHdcEndpointListener_LeavesGlobalParameterUnchanged()
    {
        DebugBridgeRoute.InitializeHdcServerProbe();
        var before = DebugBridgeRoute.HdcServerAvailable;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.Equal(DebugBridgeProbeOutcome.Listening,
                await DebugBridgeRoute.TryProbeAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
            Assert.Equal(before, DebugBridgeRoute.HdcServerAvailable);
        }
        finally
        {
            DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);
        }
    }
}