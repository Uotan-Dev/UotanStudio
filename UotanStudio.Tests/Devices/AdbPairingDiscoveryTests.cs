using System.Collections.Concurrent;
using System.Net;
using Makaretu.Dns;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Adb.Pairing;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Udp;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Covers the QR pairing discovery fixes: server mDNS alias keys, service-type filtering, address
/// normalization and credential validation.
/// <para>覆盖扫码配对发现修复：server mDNS 别名键、服务类型筛选、地址规范化与凭据校验。</para>
/// </summary>
public sealed class AdbPairingDiscoveryTests
{
    [Fact]
    public void BuildAliasMap_InstanceOnlyServerRow_MatchesFullServerSerial()
    {
        var entries = new[]
        {
            new AdbServerMdnsEntry("adb-ABC", "_adb-tls-connect._tcp", "192.0.2.31", 37177)
        };

        var aliases = AdbServerDiscoverySource.BuildAliasMap(entries);

        Assert.Equal("192.0.2.31:37177",
            AdbServerDiscoverySource.ResolveAlias(aliases, "adb-ABC._adb-tls-connect._tcp"));
    }

    [Fact]
    public void ResolveAlias_InstanceOnlyMap_StripsServiceSuffix()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["adb-XYZ"] = "192.0.2.32:40000"
        };

        Assert.Equal("192.0.2.32:40000",
            AdbServerDiscoverySource.ResolveAlias(aliases, "adb-XYZ._adb-tls-connect._tcp"));
    }

    [Fact]
    public void BuildDevices_RequiredConnectService_ExcludesPairingAndLegacyInstances()
    {
        var pairing = new DomainName("ADB_WIFI_pair._adb-tls-pairing._tcp.local");
        var connect = new DomainName("ADB_WIFI_conn._adb-tls-connect._tcp.local");
        var legacy = new DomainName("adb-legacy._adb._tcp.local");
        var host = new DomainName("phone.local");
        var message = new Message();
        message.Answers.Add(new PTRRecord
        {
            Name = new DomainName("_adb-tls-pairing._tcp.local"),
            DomainName = pairing
        });
        message.Answers.Add(new PTRRecord
        {
            Name = new DomainName("_adb-tls-connect._tcp.local"),
            DomainName = connect
        });
        message.Answers.Add(new PTRRecord
        {
            Name = new DomainName("_adb._tcp.local"),
            DomainName = legacy
        });
        message.AdditionalRecords.Add(new SRVRecord { Name = pairing, Target = host, Port = 45678 });
        message.AdditionalRecords.Add(new SRVRecord { Name = connect, Target = host, Port = 37177 });
        message.AdditionalRecords.Add(new SRVRecord { Name = legacy, Target = host, Port = 5555 });
        message.AdditionalRecords.Add(new ARecord
        {
            Name = host,
            Address = IPAddress.Parse("192.0.2.40")
        });

        var byInstance = new ConcurrentDictionary<string, AdbMdnsDiscovery.DeviceAccumulator>(
            StringComparer.OrdinalIgnoreCase);
        var addresses = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AdbMdnsDiscovery.Accumulate(message, byInstance, addresses);

        Assert.Equal(3, AdbMdnsDiscovery.BuildDevices(byInstance, addresses, static _ => true).Count);
        var connectDevice = Assert.Single(AdbMdnsDiscovery.BuildDevices(byInstance, addresses,
            static _ => true, AdbMdnsDiscovery.AdbTlsService));
        Assert.Equal(connect.ToString(), connectDevice.Instance);
        Assert.Equal(37177, connectDevice.Port);
        var legacyDevice = Assert.Single(AdbMdnsDiscovery.BuildDevices(byInstance, addresses,
            static _ => true, AdbMdnsDiscovery.AdbLegacyService));
        Assert.Equal(legacy.ToString(), legacyDevice.Instance);
        Assert.True(AdbMdnsDiscovery.IsType(connectDevice, AdbMdnsDiscovery.AdbTlsService));
        Assert.False(AdbMdnsDiscovery.IsType(connectDevice, AdbMdnsDiscovery.AdbTlsPairingService));
        Assert.True(AdbMdnsDiscovery.IsType(legacyDevice, AdbMdnsDiscovery.AdbLegacyService));
    }

    [Theory]
    [InlineData("192.0.2.31", "192.0.2.31", true)]
    [InlineData("192.0.2.31", "::ffff:192.0.2.31", true)]
    [InlineData("::ffff:192.0.2.31", "192.0.2.31", true)]
    [InlineData("192.0.2.31", "192.0.2.32", false)]
    [InlineData("2001:db8::1", "2001:db8::1", true)]
    [InlineData("2001:db8::1", "192.0.2.31", false)]
    [InlineData("PHONE.LOCAL", "phone.local", true)]
    public void SameAddress_ComparesTextAndAddressFamilies(string candidate, string address, bool expected)
        => Assert.Equal(expected, AdbMdnsDiscovery.SameAddress(candidate, address));

    [Theory]
    [InlineData("", "safePassword")]
    [InlineData("bad;name", "safePassword")]
    [InlineData("bad:name", "safePassword")]
    [InlineData("bad\\name", "safePassword")]
    [InlineData("bad\"name", "safePassword")]
    [InlineData("bad.name", "safePassword")]
    [InlineData("ADB_WIFI_good\u0001name", "safePassword")]
    [InlineData("ADB_WIFI_good-name", "")]
    [InlineData("ADB_WIFI_good-name", "bad;password")]
    [InlineData("ADB_WIFI_good-name", "bad:password")]
    [InlineData("ADB_WIFI_good-name", "bad\\password")]
    [InlineData("ADB_WIFI_good-name", "bad\"password")]
    [InlineData("ADB_WIFI_good-name", "bad\u0001password")]
    public void BuildPayload_UnsafeCredential_IsRejected(string serviceName, string password)
        => Assert.Throws<ArgumentException>(() => AdbQrPairing.BuildPayload(
            new AdbQrPairingCredentials(serviceName, password)));

    [Fact]
    public void BuildPayload_SafeCredentials_ProduceDocumentedToken()
    {
        var payload = AdbQrPairing.BuildPayload(new AdbQrPairingCredentials(
            "ADB_WIFI_abcdefghijklmn-abcdef", "abcDEF123"));

        Assert.Equal("WIFI:T:ADB;S:ADB_WIFI_abcdefghijklmn-abcdef;P:abcDEF123;;", payload);
    }

    [Fact]
    public void MatchesInstanceLabel_BareQrField_MatchesBrowsedInstance()
        => Assert.True(AdbMdnsDiscovery.MatchesInstanceLabel(
            "ADB_WIFI_abc._adb-tls-pairing._tcp.local", "ADB_WIFI_abc"));

    [Fact]
    public void MatchesInstanceLabel_DottedExpectedLabel_NeverMatches()
        => Assert.False(AdbMdnsDiscovery.MatchesInstanceLabel(
            "ADB_WIFI_abc._adb-tls-pairing._tcp.local", "ADB_WIFI_abc.extra"));

    [Fact]
    public void SelectConnectPorts_SameHostNameOverDifferentAddressFamilies_PrefersHostNameMatch()
    {
        IReadOnlyDictionary<string, string> properties = new Dictionary<string, string>();
        var candidates = new[]
        {
            new MdnsDevice("adb-other._adb-tls-connect._tcp.local", "other.local", 40404, ["192.0.2.99"],
                properties, AdbMdnsDiscovery.AdbTlsService),
            new MdnsDevice("adb-phone._adb-tls-connect._tcp.local", "phone.local", 37177, ["fe80::1"],
                properties, AdbMdnsDiscovery.AdbTlsService),
            new MdnsDevice("adb-phone._adb-tls-connect._tcp.local", "phone.local", 37177, ["192.0.2.40"],
                properties, AdbMdnsDiscovery.AdbTlsService)
        };

        var ports = AdbQrPairingService.SelectConnectPorts(candidates, "192.0.2.40", "phone.local");

        Assert.Equal([37177], ports);
        Assert.Equal(37177, AdbQrPairingService.SelectConnectPort(candidates, "192.0.2.40", "phone.local"));
    }

    [Fact]
    public void SelectConnectPorts_PairingInstanceOnSameAddress_IsIgnored()
    {
        IReadOnlyDictionary<string, string> properties = new Dictionary<string, string>();
        var candidates = new[]
        {
            new MdnsDevice("ADB_WIFI_x._adb-tls-pairing._tcp.local", "phone.local", 45678, ["192.0.2.40"],
                properties, AdbMdnsDiscovery.AdbTlsPairingService),
            new MdnsDevice("adb-x._adb-tls-connect._tcp.local", "phone.local", 37177, ["192.0.2.40"],
                properties, AdbMdnsDiscovery.AdbTlsService)
        };

        Assert.Equal([37177], AdbQrPairingService.SelectConnectPorts(candidates, "192.0.2.40", "phone.local"));
    }

    [Fact]
    public async Task BuildDevices_ReAdvertisedInstance_CarriesTheLatestObservationTime()
    {
        var connect = new DomainName("adb-phone._adb-tls-connect._tcp.local");
        var host = new DomainName("phone.local");
        var message = new Message();
        message.Answers.Add(new PTRRecord
        {
            Name = new DomainName("_adb-tls-connect._tcp.local"),
            DomainName = connect
        });
        message.AdditionalRecords.Add(new SRVRecord { Name = connect, Target = host, Port = 37177 });
        message.AdditionalRecords.Add(new ARecord { Name = host, Address = IPAddress.Parse("192.0.2.40") });
        var byInstance = new ConcurrentDictionary<string, AdbMdnsDiscovery.DeviceAccumulator>(
            StringComparer.OrdinalIgnoreCase);
        var addresses = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AdbMdnsDiscovery.Accumulate(message, byInstance, addresses);
        var first = Assert.Single(AdbMdnsDiscovery.BuildDevices(byInstance, addresses, static _ => true,
            AdbMdnsDiscovery.AdbTlsService));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        AdbMdnsDiscovery.Accumulate(message, byInstance, addresses);
        var second = Assert.Single(AdbMdnsDiscovery.BuildDevices(byInstance, addresses, static _ => true,
            AdbMdnsDiscovery.AdbTlsService));

        Assert.True(second.LastSeenUtc > first.LastSeenUtc);
    }

    [Fact]
    public void SelectConnectPorts_StaleAndFreshInstances_PrefersTheNewestAdvertisement()
    {
        IReadOnlyDictionary<string, string> properties = new Dictionary<string, string>();
        var stale = new MdnsDevice("adb-phone-old._adb-tls-connect._tcp.local", "phone.local", 37177,
            ["192.0.2.40"], properties, AdbMdnsDiscovery.AdbTlsService,
            new DateTime(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc));
        var fresh = new MdnsDevice("adb-phone-new._adb-tls-connect._tcp.local", "phone.local", 41111,
            ["192.0.2.40"], properties, AdbMdnsDiscovery.AdbTlsService,
            new DateTime(2026, 10, 25, 10, 0, 5, DateTimeKind.Utc));

        var ports = AdbQrPairingService.SelectConnectPorts([stale, fresh], "192.0.2.40", "phone.local");

        Assert.Equal([41111, 37177], ports);
        Assert.Equal(41111, AdbQrPairingService.SelectConnectPort([stale, fresh], "192.0.2.40", "phone.local"));
    }

    [Fact]
    public void SelectConnectPorts_SameHostNameOnAnotherAddress_TriesTheAddressMatchFirst()
    {
        IReadOnlyDictionary<string, string> properties = new Dictionary<string, string>();
        var sameHostNameElsewhere = new MdnsDevice("adb-other._adb-tls-connect._tcp.local", "phone.local", 40404,
            ["192.0.2.99"], properties, AdbMdnsDiscovery.AdbTlsService);
        var addressMatch = new MdnsDevice("adb-phone._adb-tls-connect._tcp.local", "phone.local", 37177,
            ["192.0.2.40"], properties, AdbMdnsDiscovery.AdbTlsService);

        var ports = AdbQrPairingService.SelectConnectPorts([sameHostNameElsewhere, addressMatch],
            "192.0.2.40", "phone.local");

        Assert.Equal([37177, 40404], ports);
    }

    [Fact]
    public void SelectConnectPort_UnrelatedDevice_ReturnsZero()
    {
        IReadOnlyDictionary<string, string> properties = new Dictionary<string, string>();
        var candidates = new[]
        {
            new MdnsDevice("adb-phone._adb-tls-connect._tcp.local", "phone.local", 37177, ["192.0.2.40"],
                properties, AdbMdnsDiscovery.AdbTlsService)
        };

        Assert.Equal(0, AdbQrPairingService.SelectConnectPort(candidates, "198.51.100.7", "tablet.local"));
        Assert.False(AdbQrPairingService.IsSameDevice(candidates[0], "198.51.100.7", "tablet.local"));
        Assert.True(AdbQrPairingService.IsSameDevice(candidates[0], "198.51.100.7", "PHONE.local."));
    }

    [Fact]
    public void MatchServerConnection_AutoConnectedInstanceKeyedTarget_ResolvesTheDialableAddress()
    {
        var targets = new[] { new AdbTargetInfo("adb-ABC._adb-tls-connect._tcp", "device", "TCP") };
        var table = new[]
        {
            new AdbServerMdnsEntry("adb-ABC", "_adb-tls-connect._tcp", "192.0.2.31", 37177)
        };

        var match = AdbQrPairingService.MatchServerConnection(targets, table, "192.0.2.31");

        Assert.NotNull(match);
        Assert.Equal("adb-ABC._adb-tls-connect._tcp", match.Serial);
        Assert.Equal("192.0.2.31:37177", match.Address);
    }

    [Fact]
    public void MatchServerConnection_ExplicitEndpointTarget_IsRecognizedWithoutTheTable()
    {
        var targets = new[] { new AdbTargetInfo("192.0.2.31:37177", "device", "TCP") };

        var match = AdbQrPairingService.MatchServerConnection(targets, [], "192.0.2.31");

        Assert.Equal("192.0.2.31:37177", match?.Address);
        Assert.Equal("192.0.2.31:37177", match?.Serial);
    }

    [Fact]
    public void MatchServerConnection_DualStackTableRow_MatchesThePairingIpv4Address()
    {
        var targets = new[] { new AdbTargetInfo("adb-ABC._adb-tls-connect._tcp", "device", "TCP") };
        var table = new[]
        {
            new AdbServerMdnsEntry("adb-ABC", "_adb-tls-connect._tcp", "::ffff:192.0.2.31", 37177)
        };

        var match = AdbQrPairingService.MatchServerConnection(targets, table, "192.0.2.31");

        Assert.Equal("[::ffff:192.0.2.31]:37177", match?.Address);
    }

    [Fact]
    public void MatchServerConnection_OtherDevice_IsIgnored()
    {
        var targets = new[] { new AdbTargetInfo("adb-XYZ._adb-tls-connect._tcp", "device", "TCP") };
        var table = new[]
        {
            new AdbServerMdnsEntry("adb-XYZ", "_adb-tls-connect._tcp", "192.0.2.77", 40222)
        };

        Assert.Null(AdbQrPairingService.MatchServerConnection(targets, table, "192.0.2.31"));
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("")]
    public void MatchServerConnection_UnusableState_IsIgnored(string state)
    {
        var targets = new[] { new AdbTargetInfo("192.0.2.31:37177", state, "TCP") };

        Assert.Null(AdbQrPairingService.MatchServerConnection(targets, [], "192.0.2.31"));
    }

    [Fact]
    public void MatchServerConnection_DeviceState_IsPreferredOverUnauthorized()
    {
        var targets = new[]
        {
            new AdbTargetInfo("192.0.2.31:37177", "unauthorized", "TCP"),
            new AdbTargetInfo("adb-ABC._adb-tls-connect._tcp", "device", "TCP")
        };
        var table = new[]
        {
            new AdbServerMdnsEntry("adb-ABC", "_adb-tls-connect._tcp", "192.0.2.31", 37177)
        };

        var match = AdbQrPairingService.MatchServerConnection(targets, table, "192.0.2.31");

        Assert.Equal("adb-ABC._adb-tls-connect._tcp", match?.Serial);
        Assert.Equal("192.0.2.31:37177", match?.Address);
    }
}

/// <summary>
/// Covers the route-mode guard that prevents QR pairing from silently falling back to direct SPAKE2
/// when the process is fixed to server-only.
/// <para>覆盖固定为仅 server 模式时阻止扫码配对静默回退直连 SPAKE2 的路由门控。</para>
/// </summary>
[Collection("Debug bridge route")]
public sealed class AdbQrPairingRouteTests : IDisposable
{
    public AdbQrPairingRouteTests() => DebugBridgeRoute.ResetForTests();

    public void Dispose() => DebugBridgeRoute.ResetForTests();

    [Fact]
    public async Task PairAsync_ServerOnlyWithoutServer_RejectsBeforeDiscovery()
    {
        DebugBridgeRoute.ConfigureMode(DebugBridgeRouteMode.ServerOnly);
        DebugBridgeRoute.SetAvailabilityForTests(adbAvailable: false, hdcAvailable: false);
        var credentials = new AdbQrPairingCredentials("ADB_WIFI_abcdefghijklmn-abcdef", "abcDEF123");

        await Assert.ThrowsAsync<IOException>(() => AdbQrPairingService.PairAsync(
            credentials,
            discoveryTimeout: TimeSpan.FromMilliseconds(1),
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(DebugBridgeRouteMode.Auto, true)]
    [InlineData(DebugBridgeRouteMode.ServerOnly, false)]
    [InlineData(DebugBridgeRouteMode.DirectOnly, true)]
    [InlineData(DebugBridgeRouteMode.StartupFrozen, true)]
    public void AllowsInProcessPairing_FollowsRouteMode(DebugBridgeRouteMode mode, bool expected)
        => Assert.Equal(expected, AdbQrPairingService.AllowsInProcessPairing(mode));
}