using System.Text.Json;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class HdcRemoteRoutingTests
{
    [Fact]
    public void ResolveHdcServerUse_ServerListsManualTarget_UsesServer()
        => Assert.True(DeviceMonitorService.ResolveHdcServerUse(
            serverOnly: false, serverAvailable: true, serverListsKey: true));

    [Fact]
    public void ResolveHdcServerUse_ServerDoesNotListManualTarget_StaysDirect()
        => Assert.False(DeviceMonitorService.ResolveHdcServerUse(
            serverOnly: false, serverAvailable: true, serverListsKey: false));

    [Fact]
    public void ResolveHdcServerUse_ServerAbsent_StaysDirect()
        => Assert.False(DeviceMonitorService.ResolveHdcServerUse(
            serverOnly: false, serverAvailable: false, serverListsKey: false));

    [Fact]
    public void ResolveHdcServerUse_ServerOnlyWithoutServer_Throws()
    {
        Assert.Throws<IOException>(() =>
        {
            _ = DeviceMonitorService.ResolveHdcServerUse(
                serverOnly: true, serverAvailable: false, serverListsKey: false);
        });
    }

    [Fact]
    public void ResolveHdcServerUse_ServerOnlyWithServer_ReturnsTrue()
        => Assert.True(DeviceMonitorService.ResolveHdcServerUse(
            serverOnly: true, serverAvailable: true, serverListsKey: false));

    [Fact]
    public void AppSettings_HdcTargets_RoundTripThroughSourceContext()
    {
        var settings = new AppSettings
        {
            PairedNetworkTargets = [new PairedNetworkTarget("192.168.1.5:37177", true)],
            PairedHdcNetworkTargets = ["192.168.1.6:8710"],
        };

        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        var roundTripped = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;

        Assert.Contains("pairedHdcNetworkTargets", json);
        Assert.Equal(new[] { "192.168.1.6:8710" }, roundTripped.PairedHdcNetworkTargets);
        var target = Assert.Single(roundTripped.PairedNetworkTargets);
        Assert.Equal("192.168.1.5:37177", target.Address);
        Assert.True(target.RequiresTls);
    }
}