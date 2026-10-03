using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Serial;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed class EdlSerialFallbackDeviceTests
{
    public static bool SerialDeviceEnabled =>
        Environment.GetEnvironmentVariable("UOTAN_EDL_SERIAL_DEVICE") == "1" && OperatingSystem.IsWindows();

    [Fact(Skip = "Requires UOTAN_EDL_SERIAL_DEVICE=1 on Windows with a QDLoader-bound EDL target attached.", SkipUnless = nameof(SerialDeviceEnabled))]
    public async Task OpenAsync_QdLoaderBoundEdlEndpoint_FallsBackToSerialAndProbesProtocol()
    {
        await using var controller = DeviceControllerFactory.CreateDefault();
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var endpoint = Assert.Single(snapshot.Endpoints,
            candidate => candidate.Route == "direct-usb" && candidate.Protocol == "edl");

        var lease = await controller.OpenAsync(snapshot, endpoint.Id, DeviceSessionKind.Edl,
            TestContext.Current.CancellationToken);
        try
        {
            var transport = Assert.IsType<OwnedDeviceTransport<SerialTransport>>(lease.Transport);
            Assert.True(transport.Value.IsConnected);
            Assert.True(lease.TryGetSession<EdlSession>(out var session));
            var state = await session.ProbeProtocolAsync(TestContext.Current.CancellationToken);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"EDL probe over '{transport.Value.PortName}' reported {state}.");
            Assert.NotEqual(EdlProtocolState.Unknown, state);
            Assert.Equal(state, session.ProtocolState);
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }
}