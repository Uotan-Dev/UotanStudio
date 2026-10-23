using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class HdcSessionRoutingTests
{
    [Fact]
    public void Resolve_ServerListsTarget_ReturnsServerRoute()
        => Assert.Equal(HdcSessionRoute.Server, HdcSessionRouting.Resolve(
            serverOnly: false, serverAvailable: true, serverListsKey: true));

    [Fact]
    public void Resolve_ServerAbsent_ReturnsDirectRoute()
        => Assert.Equal(HdcSessionRoute.Direct, HdcSessionRouting.Resolve(
            serverOnly: false, serverAvailable: false, serverListsKey: false));

    [Fact]
    public void Resolve_ServerPresentWithoutTargetKey_ReturnsDirectRoute()
        => Assert.Equal(HdcSessionRoute.Direct, HdcSessionRouting.Resolve(
            serverOnly: false, serverAvailable: true, serverListsKey: false));

    [Fact]
    public void Resolve_ServerOnlyWithServer_ReturnsServerRoute()
        => Assert.Equal(HdcSessionRoute.Server, HdcSessionRouting.Resolve(
            serverOnly: true, serverAvailable: true, serverListsKey: false));

    [Fact]
    public void Resolve_ServerOnlyWithoutServer_ThrowsInsteadOfFallingBackToDirect()
    {
        var error = Assert.Throws<IOException>(() =>
        {
            _ = HdcSessionRouting.Resolve(
                serverOnly: true, serverAvailable: false, serverListsKey: false);
        });

        Assert.Contains("ServerOnly", error.Message, StringComparison.Ordinal);
    }
}