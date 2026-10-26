using System.Text.Json;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class AdbRemoteRoutingTests
{
    [Fact]
    public void TryNormalizeNetworkAddress_AdbBareHost_AppliesDefault5555()
    {
        var accepted = DeviceMonitorService.TryNormalizeNetworkAddress(
            DeviceProtocolKind.Adb, "192.168.1.5", out var address);

        Assert.True(accepted);
        Assert.Equal("192.168.1.5:5555", address);
    }

    [Fact]
    public void TryNormalizeNetworkAddress_AdbExplicitPort_KeepsPort()
    {
        var accepted = DeviceMonitorService.TryNormalizeNetworkAddress(
            DeviceProtocolKind.Adb, "192.168.1.5:37177", out var address);

        Assert.True(accepted);
        Assert.Equal("192.168.1.5:37177", address);
    }

    [Fact]
    public void TryNormalizeNetworkAddress_HdcBareHost_IsRejected()
    {
        var accepted = DeviceMonitorService.TryNormalizeNetworkAddress(
            DeviceProtocolKind.Hdc, "192.168.1.5", out var address);

        Assert.False(accepted);
        Assert.Equal(string.Empty, address);
    }

    [Fact]
    public void TryNormalizeNetworkAddress_HdcExplicitPort_IsAccepted()
    {
        var accepted = DeviceMonitorService.TryNormalizeNetworkAddress(
            DeviceProtocolKind.Hdc, "192.168.1.5:8710", out var address);

        Assert.True(accepted);
        Assert.Equal("192.168.1.5:8710", address);
    }

    [Fact]
    public void TryNormalizeNetworkAddress_HdcAddressOver31Bytes_IsRejected()
    {
        var accepted = DeviceMonitorService.TryNormalizeNetworkAddress(
            DeviceProtocolKind.Hdc, "abcdefghijklmnopqrstuvwxyz1234:8710", out _);

        Assert.False(accepted);
    }

    [Fact]
    public void ManualNetworkTargetState_Unverified_ReportsOffline()
        => Assert.Equal("offline", DeviceMonitorService.ManualNetworkTargetState(false));

    [Fact]
    public void ManualNetworkTargetState_Verified_ReportsDevice()
        => Assert.Equal("device", DeviceMonitorService.ManualNetworkTargetState(true));

    [Fact]
    public void IsTlsRequiredFailure_DirectAndWrappedCause_ReturnsTrue()
    {
        var direct = new AdbTlsRequiredException("The peer requires TLS.");
        var wrapped = new TimeoutException("Handshake failed.", direct);

        Assert.True(DeviceMonitorService.IsTlsRequiredFailure(direct));
        Assert.True(DeviceMonitorService.IsTlsRequiredFailure(wrapped));
        Assert.False(DeviceMonitorService.IsTlsRequiredFailure(new IOException("plaintext failure")));
    }

    [Fact]
    public void HasAdbNetworkHost_ServerTargetOnSameHost_HidesRememberedDirectEndpoint()
    {
        var server = new DetectedDevice
        {
            Key = "adb:192.168.1.144:36695",
            Serial = "192.168.1.144:36695",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Tcp,
            State = "device"
        };

        Assert.True(DeviceMonitorService.HasAdbNetworkHost([server], "192.168.1.144:43407"));
        Assert.False(DeviceMonitorService.HasAdbNetworkHost([server], "192.168.1.150:43407"));
    }

    [Fact]
    public void HasAdbNetworkHost_InstanceKeyedTarget_MatchesThroughItsNetworkAlias()
    {
        var server = new DetectedDevice
        {
            Key = "adb:adb-phone._adb-tls-connect._tcp",
            Serial = "adb-phone._adb-tls-connect._tcp",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Tcp,
            NetworkAlias = "192.168.1.144:36695",
            State = "device"
        };

        Assert.True(DeviceMonitorService.HasAdbNetworkHost([server], "192.168.1.144:43407"));
    }

    [Fact]
    public void HasAdbNetworkHost_OfflineServerTarget_LeavesTheRememberedEndpointVisible()
    {
        var offline = new DetectedDevice
        {
            Key = "adb:192.168.1.144:36695",
            Serial = "192.168.1.144:36695",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Tcp,
            State = "offline"
        };

        Assert.False(DeviceMonitorService.HasAdbNetworkHost([offline], "192.168.1.144:43407"));
    }

    [Fact]
    public void HasAdbNetworkHost_UsbRowWithHostShapedSerial_IsIgnored()
    {
        var usb = new DetectedDevice
        {
            Key = "usb:1-3",
            Serial = "192.168.1.144:43407",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb,
            State = "device"
        };

        Assert.False(DeviceMonitorService.HasAdbNetworkHost([usb], "192.168.1.144:43407"));
    }

    [Fact]
    public void AppSettings_OldAdbOnlyPayload_PreservesAdbTargetAndLeavesHdcTargetsEmpty()
    {
        const string json = """
            {
              "pairedNetworkTargets": [
                { "address": "192.168.1.5:37177", "requiresTls": true }
              ]
            }
            """;

        var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;

        var target = Assert.Single(settings.PairedNetworkTargets);
        Assert.Equal("192.168.1.5:37177", target.Address);
        Assert.True(target.RequiresTls);
        Assert.Empty(settings.PairedHdcNetworkTargets);
    }
}