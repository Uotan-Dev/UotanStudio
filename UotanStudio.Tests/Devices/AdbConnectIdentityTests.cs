using UotanStudio.Comm.Protocols.Adb.Pairing;
using UotanStudio.Comm.Transports.Udp;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Covers the device identity the official adb chain uses after pairing: the instance name adbd registers
/// (`persist.adb.wifi.guid`) identifies the secure connect service, and an instance carrying it is matched without an
/// address match while it outranks instances only identified by address.
/// <para>覆盖官方 adb 链路在配对后使用的设备身份：adbd 注册的实例名（`persist.adb.wifi.guid`）标识安全连接服务，
/// 携带它的实例无需地址匹配即命中，且优先于只能靠地址识别的实例。</para>
/// </summary>
public sealed class AdbConnectIdentityTests
{
    private const string Registered = "adb-6a082bda-lZgxIP";

    private static readonly IReadOnlyDictionary<string, string> Properties = new Dictionary<string, string>();

    [Fact]
    public void DescribesDevice_InstanceCarriesRegisteredGuid_MatchesOnAnotherAddress()
    {
        var device = new MdnsDevice($"{Registered}._adb-tls-connect._tcp.local", "phone.local", 44065,
            ["192.0.2.77"], Properties, AdbMdnsDiscovery.AdbTlsService);

        Assert.True(AdbMdnsDiscovery.DescribesDevice(device, Registered, "192.0.2.40"));
    }

    [Fact]
    public void DescribesDevice_OnlyAddressMatches_StillMatches()
    {
        var device = new MdnsDevice("adb-other._adb-tls-connect._tcp.local", "phone.local", 44065,
            ["192.0.2.40"], Properties, AdbMdnsDiscovery.AdbTlsService);

        Assert.True(AdbMdnsDiscovery.DescribesDevice(device, Registered, "192.0.2.40"));
    }

    [Fact]
    public void DescribesDevice_NeitherIdentityMatches_IsFalse()
    {
        var device = new MdnsDevice("adb-other._adb-tls-connect._tcp.local", "other.local", 44065,
            ["192.0.2.99"], Properties, AdbMdnsDiscovery.AdbTlsService);

        Assert.False(AdbMdnsDiscovery.DescribesDevice(device, Registered, "192.0.2.40"));
    }

    [Fact]
    public void SelectConnectPorts_RegisteredInstanceOutranksNewerAddressMatch()
    {
        var newer = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var registered = new MdnsDevice($"{Registered}._adb-tls-connect._tcp.local", "phone.local", 44065,
            ["192.0.2.77"], Properties, AdbMdnsDiscovery.AdbTlsService, older);
        var addressMatch = new MdnsDevice("adb-other._adb-tls-connect._tcp.local", "other.local", 42568,
            ["192.0.2.40"], Properties, AdbMdnsDiscovery.AdbTlsService, newer);

        var ports = AdbQrPairingService.SelectConnectPorts([addressMatch, registered], Registered, "192.0.2.40",
            "phone.local");

        Assert.Equal([44065, 42568], ports);
    }

    [Fact]
    public void SelectConnectPorts_WithoutInstanceName_KeepsAddressOrdering()
    {
        var registered = new MdnsDevice($"{Registered}._adb-tls-connect._tcp.local", "phone.local", 44065,
            ["192.0.2.77"], Properties, AdbMdnsDiscovery.AdbTlsService);
        var addressMatch = new MdnsDevice("adb-other._adb-tls-connect._tcp.local", "other.local", 42568,
            ["192.0.2.40"], Properties, AdbMdnsDiscovery.AdbTlsService);

        Assert.Equal([42568], AdbQrPairingService.SelectConnectPorts([registered, addressMatch], "192.0.2.40",
            "other.local"));
    }
}