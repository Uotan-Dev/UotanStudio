using System.Collections.Concurrent;
using System.Reflection;
using UotanStudio.Comm.Transports.Udp;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports;

/// <summary>
/// mDNS device model and discovery helpers: <see cref="MdnsDevice"/> surface plus the internal
/// AdbMdnsDiscovery service-type matching and accumulator→device projection (exercised via
/// reflection because the seams are internal; no live multicast traffic is sent).
/// <para>mDNS 设备模型与发现辅助：<see cref="MdnsDevice"/> 对外字段，以及内部
/// AdbMdnsDiscovery 的服务类型匹配与累积→设备投影（通过反射访问内部接缝；不发送真实组播流量）。</para>
/// </summary>
public class MdnsDiscoveryTests
{
    [Fact]
    public void MdnsDevice_ExposesParsedFields()
    {
        var device = new MdnsDevice(
            "ALN-AL00._adb-tls-connect._tcp.local",
            "ALN-AL00.local",
            5555,
            ["192.168.1.10", "fe80::1"],
            new Dictionary<string, string> { ["model"] = "Pixel", ["serial"] = "ABC123" });

        Assert.Equal("ALN-AL00._adb-tls-connect._tcp.local", device.Instance);
        Assert.Equal("ALN-AL00.local", device.HostName);
        Assert.Equal(5555, device.Port);
        Assert.Equal(new[] { "192.168.1.10", "fe80::1" }, device.Addresses);
        Assert.Equal("Pixel", device.Properties["model"]);
        Assert.Contains("ALN-AL00", device.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MdnsDevice_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new MdnsDevice(null!, null, 1, Array.Empty<string>(), new Dictionary<string, string>()));
        Assert.Throws<ArgumentNullException>(() => new MdnsDevice("x", null, 1, null!, new Dictionary<string, string>()));
        Assert.Throws<ArgumentNullException>(() => new MdnsDevice("x", null, 1, Array.Empty<string>(), null!));
    }

    [Theory]
    [InlineData("ALN._adb-tls-connect._tcp.local", true)]
    [InlineData("ALN._adb._tcp.local", true)]
    [InlineData("printer._ipp._tcp.local", false)]
    [InlineData("_adb-tls-connect._tcp.local", true)]
    public void IsAdbService_MatchesTlsAndLegacyServiceTypes(string name, bool expected)
    {
        var method = DiscoveryType.GetMethod("IsAdbService", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(expected, (bool)method.Invoke(null, [name])!);
    }

    [Fact]
    public void BuildDevices_JoinsAddressViaSrvHostAndDefaultPort()
    {
        var acc = CreateAccumulator();
        SetAccumulator(acc, isAdb: true, port: 0, hostName: "phone.local");
        var props = GetProperties(acc);
        props["model"] = "Pixel 8";
        props["serial"] = "SN001";

        var byInstance = CreateInstanceMap();
        AddToMap(byInstance, "phone._adb-tls-connect._tcp.local", acc);

        var addresses = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        addresses["phone.local"] = "10.0.0.42";

        var result = BuildDevices(byInstance, addresses);

        var device = Assert.Single(result);
        Assert.Equal("phone._adb-tls-connect._tcp.local", device.Instance);
        Assert.Equal("phone.local", device.HostName);
        // No SRV port -> default adb port 5555.
        // 无 SRV 端口 -> 默认 adb 端口 5555。
        Assert.Equal(5555, device.Port);
        Assert.Equal("10.0.0.42", Assert.Single(device.Addresses));
        Assert.Equal("Pixel 8", device.Properties["model"]);
    }

    [Fact]
    public void BuildDevices_NonAdbInstancesAreDropped()
    {
        var acc = CreateAccumulator();
        SetAccumulator(acc, isAdb: false, port: 1234, hostName: "other.local");

        var byInstance = CreateInstanceMap();
        AddToMap(byInstance, "printer._ipp._tcp.local", acc);

        var result = BuildDevices(byInstance, new ConcurrentDictionary<string, string>());

        Assert.Empty(result);
    }

    [Fact]
    public void BuildDevices_ResolvesAddressByInstanceNameWhenNoHostName()
    {
        var acc = CreateAccumulator();
        SetAccumulator(acc, isAdb: true, port: 5555, hostName: null);

        var byInstance = CreateInstanceMap();
        AddToMap(byInstance, "phone._adb._tcp.local", acc);

        var addresses = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        addresses["phone._adb._tcp.local"] = "192.168.0.77";

        var device = Assert.Single(BuildDevices(byInstance, addresses));

        Assert.Equal("192.168.0.77", Assert.Single(device.Addresses));
    }

    [Fact]
    public void BuildDevices_UnresolvableAddress_YieldsEmptyAddressList()
    {
        var acc = CreateAccumulator();
        SetAccumulator(acc, isAdb: true, port: 5555, hostName: null);

        var byInstance = CreateInstanceMap();
        AddToMap(byInstance, "phone._adb._tcp.local", acc);

        var device = Assert.Single(BuildDevices(byInstance, new ConcurrentDictionary<string, string>()));

        Assert.Empty(device.Addresses);
    }

    private static IReadOnlyList<MdnsDevice> BuildDevices(object byInstance, ConcurrentDictionary<string, string> addresses)
    {
        var method = DiscoveryType.GetMethod("BuildDevices", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (IReadOnlyList<MdnsDevice>)method.Invoke(null, [byInstance, addresses])!;
    }

    private static object CreateAccumulator()
        => Activator.CreateInstance(AccumulatorType, nonPublic: true)!;

    private static void SetAccumulator(object acc, bool isAdb, int port, string? hostName)
    {
        AccumulatorType.GetProperty("IsAdb")!.SetValue(acc, isAdb);
        AccumulatorType.GetProperty("Port")!.SetValue(acc, port);
        AccumulatorType.GetProperty("HostName")!.SetValue(acc, hostName);
    }

    private static IDictionary<string, string> GetProperties(object acc)
        => (IDictionary<string, string>)AccumulatorType.GetProperty("Properties")!.GetValue(acc)!;

    private static object CreateInstanceMap()
        => Activator.CreateInstance(typeof(ConcurrentDictionary<,>).MakeGenericType(typeof(string), AccumulatorType))!;

    private static void AddToMap(object map, string key, object acc)
        => map.GetType().GetMethod("TryAdd")!.Invoke(map, [key, acc]);

    private static Type DiscoveryType =>
        Type.GetType("UotanStudio.Comm.Transports.Udp.AdbMdnsDiscovery, UotanStudio.Comm", throwOnError: true)!;

    private static Type AccumulatorType =>
        DiscoveryType.GetNestedType("DeviceAccumulator", BindingFlags.NonPublic)!;
}
