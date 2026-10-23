using System.Net;
using System.Net.Sockets;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Transports.Tcp;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class HdcRemoteProtocolTests
{
    [Fact(Timeout = 20000)]
    public async Task TcpTransport_IdleRead_TimesOutThenReturnsArrivedBytesAndEndOfStream()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
            using var transport = new HdcTcpTransport("127.0.0.1", port, 2000);
            using var server = await accepted;
            var buffer = new byte[11];

            var timeout = Assert.Throws<TimeoutException>(() =>
            {
                _ = transport.Read(buffer, 0, buffer.Length, 200);
            });
            Assert.Contains("HDC daemon", timeout.Message, StringComparison.Ordinal);

            await server.GetStream().WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
            Assert.Equal(3, transport.Read(buffer, 0, buffer.Length, 2000));
            Assert.Equal(new byte[] { 1, 2, 3 }, buffer[..3]);

            server.Client.Shutdown(SocketShutdown.Send);
            Assert.Equal(0, transport.Read(buffer, 0, buffer.Length, 2000));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("system", "")]
    [InlineData("bootloader", "bootloader")]
    [InlineData("recovery", "recovery")]
    public void RebootArgument_UsesTheDaemonWhitelistWithoutLeadingDash(string? mode, string expected)
        => Assert.Equal(expected, HdcDeviceServices.RebootArgument(mode));

    [Fact]
    public void RebootArgument_UnsupportedMode_IsRejected()
        => Assert.Throws<NotSupportedException>(() =>
        {
            _ = HdcDeviceServices.RebootArgument("edl");
        });

    [Fact]
    public void Capabilities_RoundTrip_PreservesAuthorizationAndSupportFeatures()
    {
        var encoded = HdcCapabilities.Encode(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["authtype"] = "1",
            ["supportfeatures"] = "Ver: 3.2.0f,tcp,win,heartbeat",
        });

        var decoded = HdcCapabilities.Decode(encoded);

        Assert.Equal("1", decoded["authtype"]);
        Assert.Equal("Ver: 3.2.0f,tcp,win,heartbeat", decoded["supportfeatures"]);
    }

    [Fact]
    public void Capabilities_DecodeDaemonAuthStatus_UsesSixteenByteTags()
    {
        var encoded = HdcCapabilities.Encode(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["emgmsg"] = "[E000003]:The device unauthorized.",
            ["daemonauthstatus"] = "DAEMON_UNAUTH",
        });

        var decoded = HdcCapabilities.Decode(encoded);

        Assert.Equal("DAEMON_UNAUTH", decoded["daemonauthstatus"]);
        Assert.StartsWith("[E000003]", decoded["emgmsg"], StringComparison.Ordinal);
    }

    [Fact]
    public void ParseVerboseTargets_KeepsFullNameAndIgnoresNonTargetRows()
    {
        const string output =
            "[Empty]\n" +
            "usb-key\t\tUSB\tConnected\tDevice Name With Spaces\n" +
            "tcp-key\t\tTCP\tUnauthorized\tPhone\n" +
            "uart-key\t\tUART\tOffline\tModule A\n" +
            "malformed\n";

        var targets = HdcTargetInfo.Parse(output);

        Assert.Equal(3, targets.Count);
        Assert.Equal("Device Name With Spaces", targets.Single(target => target.ConnectKey == "usb-key").Name);
        Assert.Equal("Unauthorized", targets.Single(target => target.ConnectKey == "tcp-key").State);
        Assert.Equal("UART", targets.Single(target => target.ConnectKey == "uart-key").Transport);
    }

    [Fact]
    public void ParseVerboseTargets_FailLine_IsRejected()
        => Assert.Throws<IOException>(() =>
        {
            _ = HdcTargetInfo.Parse("[Fail]server is busy\n");
        });

    [Fact]
    public void HdcTargetListOwns_OfflineAndUnauthorizedTargets_RemainServerOwned()
    {
        IReadOnlyList<HdcTargetInfo> targets =
        [
            new("192.0.2.10:8710", "TCP", "Offline", "phone"),
            new("usb-key", "USB", "Unauthorized", "tablet"),
        ];

        Assert.True(DebugBridgeSessionOpener.HdcTargetListOwns(targets, "192.0.2.10:8710"));
        Assert.True(DebugBridgeSessionOpener.HdcTargetListOwns(targets, "usb-key"));
        Assert.False(DebugBridgeSessionOpener.HdcTargetListOwns(targets, "192.0.2.11:8710"));
        Assert.True(DebugBridgeSessionOpener.HdcTargetListOwns(targets, null));
    }
}