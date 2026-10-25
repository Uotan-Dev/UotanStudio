using System.Globalization;
using System.Net;
using System.Net.Sockets;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Transports.Tcp;
using UotanStudio.Mirroring;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

public sealed class OhscrcpyHdcIntegrationTests
{
    [Fact]
    public async Task ControlForward_RealDevice_ConnectsAndInjectsTouch()
    {
        var target = Environment.GetEnvironmentVariable("UOTAN_HDC_MIRROR_DIRECT");
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(target),
            "Set UOTAN_HDC_MIRROR_DIRECT to an HDC connect key or a direct HDC daemon host:port (for example 127.0.0.1:5555) to run this hardware test.");

        var requested = target!;
        HdcSession? session = null;
        HdcSessionHandle? lease = null;
        DeviceMonitorService? monitor = null;
        try
        {
            string connectKey;
            if (TryParseTcpEndpoint(requested, out var host, out var port))
            {
                session = new HdcSession(new HdcTcpTransport(host, port));
                session.Connect();
                connectKey = requested;
            }
            else
            {
                monitor = DeviceMonitorService.Create();
                var devices = monitor.Scan()
                    .Where(device => device.Protocol == DeviceProtocolKind.Hdc
                        && (string.Equals(device.Serial, requested, StringComparison.Ordinal)
                            || string.Equals(device.Key, requested, StringComparison.Ordinal)))
                    .ToArray();
                Assert.SkipUnless(
                    devices.Length > 0,
                    $"No HDC target '{requested}' was found; connect it and rerun the test.");
                var pair = HdcDirectConnection.Open(monitor, devices[0]);
                lease = pair.Lease;
                session = pair.Session;
                connectKey = devices[0].Serial;
            }

            var info = OhscrcpyServerManager.Probe(session, TestContext.Current.CancellationToken);
            Assert.True(
                info.UiTestVersion >= OhscrcpyConstants.MinimumUiTestVersion,
                $"UI Test {info.UiTestVersion} is older than the required {OhscrcpyConstants.MinimumUiTestVersion}.");
            Assert.False(
                string.IsNullOrWhiteSpace(info.Architecture),
                "The device did not report a CPU architecture.");

            var daemon = OhscrcpyServerManager.EnsureControlDaemon(
                session, info, _ => { }, TestContext.Current.CancellationToken);
            try
            {
                using var tunnel = new HdcForwardTunnel(
                    session,
                    connectKey,
                    info.ControlEndpoint,
                    0,
                    OhscrcpyConstants.ControlConnectTimeoutMs);
                using var control = new OhscrcpyControlChannel(session, connectKey, info, tunnel, _ => { });
                control.Connect();

                Assert.True(control.IsConnected, "The UI Test control channel did not connect.");
                Assert.True(control.DisplayWidth > 0, "The control handshake reported no display width.");
                Assert.True(control.DisplayHeight > 0, "The control handshake reported no display height.");

                var touchX = Math.Min(control.DisplayWidth / 4, control.DisplayWidth - 1);
                var touchY = Math.Min(control.DisplayHeight / 4, control.DisplayHeight - 1);
                control.SendGesture("touchDown", touchX, touchY);
                await Task.Delay(250, TestContext.Current.CancellationToken);
                control.SendGesture("touchUp", touchX, touchY);
                await Task.Delay(250, TestContext.Current.CancellationToken);

                Assert.True(control.IsConnected, "The control channel disconnected while injecting the touch pair.");
            }
            finally
            {
                OhscrcpyServerManager.StopControlDaemon(session, daemon, _ => { });
            }
        }
        finally
        {
            session?.Dispose();
            if (lease is not null) await lease.DisposeAsync();
            monitor?.Dispose();
        }
    }

    private static bool TryParseTcpEndpoint(string endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var separator = endpoint.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || endpoint.IndexOf(':', separator + 1) >= 0) return false;
        var hostText = endpoint[..separator];
        var portText = endpoint[(separator + 1)..];
        if (!IPAddress.TryParse(hostText, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !string.Equals(address.ToString(), hostText, StringComparison.Ordinal)
            || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port is < 1 or > 65535)
        {
            return false;
        }

        host = address.ToString();
        return true;
    }
}