using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

/// <summary>
/// USB backend abstraction pure-logic tests (no real hardware): provider registry
/// (<see cref="UsbApiRegistry"/>), device filter matching (<see cref="UsbDeviceFilter"/>),
/// identity-key building/parsing (<see cref="UsbDeviceIdentity"/>) and the monitoring
/// snapshot/deduplication logic (<see cref="UsbDeviceMonitor"/>). Real backend I/O is out
/// of scope and intentionally not exercised here.
/// <para>USB 后端抽象层纯逻辑测试（无真实硬件）：提供器注册表（<see cref="UsbApiRegistry"/>）、
/// 设备过滤匹配（<see cref="UsbDeviceFilter"/>）、身份键构建/解析（<see cref="UsbDeviceIdentity"/>）
/// 与监控快照/去重逻辑（<see cref="UsbDeviceMonitor"/>）。真实后端 I/O 不在范围内，刻意不测试。</para>
/// </summary>
public class UsbApiRegistryTests
{
    // ============== UsbApiRegistry ==============

    private sealed class FakeProvider(string name, UsbApiKind kind = UsbApiKind.Custom) : IUsbApiProvider
    {
        public string ApiName { get; } = name;
        public UsbApiKind ApiKind { get; } = kind;
        public bool IsSupportedOnCurrentPlatform => true;
        public IReadOnlyList<UsbDeviceInfo> EnumerateDeviceInfos(UsbDeviceFilter? filter = null) => [];
        public IReadOnlyList<IUsbDeviceSession> EnumerateDeviceSessions(UsbDeviceFilter? filter = null) => [];
        public UsbApiCapabilities GetCapabilities() =>
            new() { ApiName = ApiName, ApiKind = ApiKind, IsSupportedOnCurrentPlatform = true };
    }

    [Fact]
    public void Registry_RegisterAndTryCreate_RoundTrips()
    {
        var registry = new UsbApiRegistry();
        registry.Register("test-backend", () => new FakeProvider("test-backend"));

        Assert.True(registry.TryCreate("test-backend", out var provider));
        Assert.Equal("test-backend", provider!.ApiName);
        Assert.Equal(UsbApiKind.Custom, provider.ApiKind);
    }

    [Fact]
    public void Registry_TryCreate_UnknownName_ReturnsFalse()
    {
        var registry = new UsbApiRegistry();
        Assert.False(registry.TryCreate("missing", out var provider));
        Assert.Null(provider);
    }

    [Fact]
    public void Registry_Register_RejectsInvalidArguments()
    {
        var registry = new UsbApiRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register("  ", () => new FakeProvider("x")));
        Assert.Throws<ArgumentNullException>(() => registry.Register("x", null!));
    }

    [Fact]
    public void Registry_Register_IsCaseInsensitiveAndLastWins()
    {
        var registry = new UsbApiRegistry();
        registry.Register("Backend", () => new FakeProvider("first"));
        registry.Register("backend", () => new FakeProvider("second"));

        Assert.Equal(["backend"], registry.GetApiNames());
        Assert.True(registry.TryCreate("BACKEND", out var provider));
        Assert.Equal("second", provider!.ApiName);
    }

    [Fact]
    public void Registry_GetApiNames_ReturnsSortedNames()
    {
        var registry = new UsbApiRegistry();
        registry.Register("zulu", () => new FakeProvider("zulu"));
        registry.Register("alpha", () => new FakeProvider("alpha"));
        registry.Register("Mike", () => new FakeProvider("mike"));

        Assert.Equal(["alpha", "Mike", "zulu"], registry.GetApiNames());
    }

    [Fact]
    public void Registry_CreateAll_InstantiatesEveryFactory()
    {
        var registry = new UsbApiRegistry();
        registry.Register("a", () => new FakeProvider("a"));
        registry.Register("b", () => new FakeProvider("b"));

        var all = registry.CreateAll();
        Assert.Equal(["a", "b"], all.Select(p => p.ApiName).OrderBy(n => n));
    }

    [Fact]
    public void Registry_ProviderEvents_FireOnRegister()
    {
        var registry = new UsbApiRegistry();
        var nameEvents = new List<string>();
        var providerEvents = new List<string>();
        registry.ProviderNameRegistered += name => nameEvents.Add(name);
        registry.ProviderRegistered += provider => providerEvents.Add(provider.ApiName);

        registry.Register("evt-backend", () => new FakeProvider("evt-backend"));

        Assert.Equal(["evt-backend"], nameEvents);
        Assert.Equal(["evt-backend"], providerEvents);
    }

    [Fact]
    public void Registry_CreateDefault_RegistersNativeLibusbHarmony()
    {
        var registry = UsbApiRegistry.CreateDefault();

        Assert.Equal(3, registry.GetApiNames().Count);
        Assert.True(registry.TryCreate("native", out var native));
        Assert.NotNull(native);
        Assert.True(registry.TryCreate("libusb", out _));
        Assert.True(registry.TryCreate("harmony", out _));
    }

    // ============== UsbDeviceFilter ==============

    private static UsbDeviceInfo Device(
        ushort vid = 0x1234,
        ushort pid = 0x5678,
        string? serial = null,
        string? path = null,
        UsbApiKind kind = UsbApiKind.Auto,
        byte? ifClass = null,
        byte? ifSub = null,
        byte? ifProtocol = null,
        IReadOnlyList<UsbInterfaceInfo>? interfaces = null)
        => new()
        {
            VendorId = vid,
            ProductId = pid,
            SerialNumber = serial,
            DevicePath = path ?? string.Empty,
            SourceApiKind = kind,
            InterfaceClass = ifClass,
            InterfaceSubClass = ifSub,
            InterfaceProtocol = ifProtocol,
            Interfaces = interfaces ?? []
        };

    private static UsbInterfaceInfo Interface(byte number, params byte[] endpoints)
        => new()
        {
            InterfaceNumber = number,
            Endpoints = endpoints.Select(e => new UsbEndpointInfo { EndpointAddress = e }).ToList()
        };

    [Fact]
    public void Filter_Empty_MatchesAnything()
    {
        var filter = new UsbDeviceFilter();
        Assert.True(filter.Matches(Device()));
        Assert.True(filter.Matches(Device(vid: 0x9999, pid: 0x0001, ifClass: 0xFF)));
    }

    [Fact]
    public void Filter_VendorProductAndInterface_MatchAndMismatch()
    {
        var filter = new UsbDeviceFilter
        {
            VendorId = 0x1234,
            ProductId = 0x5678,
            InterfaceClass = 0xFF,
            InterfaceSubClass = 0x42,
            InterfaceProtocol = 0x01
        };

        Assert.True(filter.Matches(Device(ifClass: 0xFF, ifSub: 0x42, ifProtocol: 0x01)));
        Assert.False(filter.Matches(Device(pid: 0x9999, ifClass: 0xFF, ifSub: 0x42, ifProtocol: 0x01)));
        Assert.False(filter.Matches(Device(ifClass: 0xFF, ifSub: 0x00, ifProtocol: 0x01)));
        Assert.False(filter.Matches(Device(ifClass: 0xFF, ifSub: 0x42, ifProtocol: 0x02)));
    }

    [Fact]
    public void Filter_SerialNumber_IsCaseInsensitive()
    {
        var filter = new UsbDeviceFilter { SerialNumber = "SN-ABC123" };
        Assert.True(filter.Matches(Device(serial: "sn-abc123")));
        Assert.False(filter.Matches(Device(serial: "OTHER")));
        Assert.False(filter.Matches(Device(serial: null)));
    }

    [Fact]
    public void Filter_DevicePathContains_IsCaseInsensitive()
    {
        var filter = new UsbDeviceFilter { DevicePathContains = "vid_1234" };
        Assert.True(filter.Matches(Device(path: @"\\.\usb#vid_1234&pid_5678")));
        Assert.False(filter.Matches(Device(path: @"\\.\usb#vid_9999")));
        Assert.False(filter.Matches(Device()));
    }

    [Fact]
    public void Filter_SourceApiKind_MatchAndMismatch()
    {
        var filter = new UsbDeviceFilter { SourceApiKind = UsbApiKind.Native };
        Assert.True(filter.Matches(Device(kind: UsbApiKind.Native)));
        Assert.False(filter.Matches(Device(kind: UsbApiKind.LibUsbDotNet)));
    }

    [Fact]
    public void Filter_InterfaceNumber_RequiresPresentInterface()
    {
        var filter = new UsbDeviceFilter { InterfaceNumber = 2 };
        Assert.True(filter.Matches(Device(interfaces: [Interface(0), Interface(2)])));
        Assert.False(filter.Matches(Device(interfaces: [Interface(0), Interface(1)])));
        Assert.False(filter.Matches(Device()));
    }

    [Fact]
    public void Filter_InterfaceNumbers_AllMustBePresent()
    {
        var filter = new UsbDeviceFilter { InterfaceNumbers = [0, 1] };
        Assert.True(filter.Matches(Device(interfaces: [Interface(0), Interface(1)])));
        Assert.False(filter.Matches(Device(interfaces: [Interface(0)])));
    }

    [Fact]
    public void Filter_EndpointAddresses_RequireMatchingEndpoints()
    {
        var filter = new UsbDeviceFilter { EndpointAddressIn = 0x82, EndpointAddressOut = 0x02 };
        Assert.True(filter.Matches(Device(interfaces: [Interface(0, 0x82, 0x02)])));
        Assert.False(filter.Matches(Device(interfaces: [Interface(0, 0x81, 0x01)])));
        Assert.False(filter.Matches(Device(interfaces: [Interface(0, 0x82)])));
    }

    // ============== UsbDeviceIdentity ==============

    [Fact]
    public void Identity_BuildKey_IncludesAllSegments()
    {
        var info = Device(vid: 0x1234, pid: 0x5678, serial: "ABC", path: "p1", kind: UsbApiKind.Native,
            ifClass: 0xFF, ifSub: 0x42, ifProtocol: 0x01);
        info.ApiName = "native";

        var key = UsbDeviceIdentity.BuildKey(info);
        Assert.Equal("native|Native|1234|5678|FF|42|01|ABC|p1", key);
    }

    [Fact]
    public void Identity_BuildKey_EmptyOptionalFields_BecomeEmptySegments()
    {
        var key = UsbDeviceIdentity.BuildKey(Device(vid: 0x0001, pid: 0x0002));
        Assert.Equal("|Auto|0001|0002|||||", key);
    }

    [Fact]
    public void Identity_BuildPhysicalKey_IgnoresApiAndPath()
    {
        var native = Device(vid: 0x1234, pid: 0x5678, serial: "ABC", path: "path-a", kind: UsbApiKind.Native,
            ifClass: 0xFF, ifSub: 0x42, ifProtocol: 0x01);
        var libusb = Device(vid: 0x1234, pid: 0x5678, serial: "ABC", path: "path-b", kind: UsbApiKind.LibUsbDotNet,
            ifClass: 0xFF, ifSub: 0x42, ifProtocol: 0x01);

        Assert.Equal(
            UsbDeviceIdentity.BuildPhysicalKey(native),
            UsbDeviceIdentity.BuildPhysicalKey(libusb));
        // 物理键基于 VID/PID/接口类+子类/序列号；接口协议号被有意排除，使同一接口上的
        // 协议变化由监视器上报为元数据变化（Changed）而非移除+新增。
        // The physical key is based on VID/PID/interface class+subclass/serial; the interface
        // PROTOCOL is deliberately excluded so a protocol change on the same interface is reported
        // by the monitor as a metadata change (Changed) instead of a remove+add.
        Assert.Equal("1234|5678|FF|42|ABC", UsbDeviceIdentity.BuildPhysicalKey(native));

        // A different serial must produce a different physical key.
        // 序列号不同必须产生不同的物理键。
        Assert.NotEqual(
            UsbDeviceIdentity.BuildPhysicalKey(Device(serial: "ABC", ifClass: 0xFF, ifSub: 0x42, ifProtocol: 0x01)),
            UsbDeviceIdentity.BuildPhysicalKey(Device(serial: "XYZ", ifClass: 0xFF, ifSub: 0x42, ifProtocol: 0x01)));
    }

    [Fact]
    public void Identity_TryParseKeyFilter_RoundTrips()
    {
        var info = Device(vid: 0x1234, pid: 0x5678, serial: "ABC", path: "p1", kind: UsbApiKind.Native,
            ifClass: 0xFF, ifSub: 0x42, ifProtocol: 0x01);
        info.ApiName = "native";

        var key = UsbDeviceIdentity.BuildKey(info);
        var filter = UsbDeviceIdentity.TryParseKeyFilter(key);

        Assert.NotNull(filter);
        Assert.Equal((ushort?)0x1234, filter.VendorId);
        Assert.Equal((ushort?)0x5678, filter.ProductId);
        Assert.Equal((byte?)0xFF, filter.InterfaceClass);
        Assert.Equal((byte?)0x42, filter.InterfaceSubClass);
        Assert.Equal((byte?)0x01, filter.InterfaceProtocol);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("only|two|segments")]
    public void Identity_TryParseKeyFilter_InvalidKey_ReturnsNull(string key)
    {
        Assert.Null(UsbDeviceIdentity.TryParseKeyFilter(key));
    }

    // ============== UsbDeviceMonitor pure logic ==============

    [Fact]
    public void Monitor_BuildMap_DeduplicatesAcrossBackends()
    {
        var samePhysicalDevice = new[]
        {
            Device(vid: 0x1234, pid: 0x5678, serial: "ABC", path: "native-path", kind: UsbApiKind.Native,
                ifClass: 0xFF, ifSub: 0x42, ifProtocol: 0x01),
            Device(vid: 0x1234, pid: 0x5678, serial: "ABC", path: "libusb-path", kind: UsbApiKind.LibUsbDotNet,
                ifClass: 0xFF, ifSub: 0x42, ifProtocol: 0x01)
        };

        var map = UsbDeviceMonitor.BuildMap(samePhysicalDevice);
        Assert.Single(map);

        var differentSerial = new[]
        {
            Device(vid: 0x1234, pid: 0x5678, serial: "ABC", ifClass: 0xFF, ifSub: 0x42, ifProtocol: 0x01),
            Device(vid: 0x1234, pid: 0x5678, serial: "DEF", ifClass: 0xFF, ifSub: 0x42, ifProtocol: 0x01)
        };
        Assert.Equal(2, UsbDeviceMonitor.BuildMap(differentSerial).Count);
    }

    [Fact]
    public void Monitor_BuildIdentityKey_IsPhysicalKey()
    {
        var info = Device(vid: 0x1111, pid: 0x2222, serial: "SN1", path: "p", kind: UsbApiKind.Native,
            ifClass: 0xEE, ifSub: 0x01, ifProtocol: 0x02);
        Assert.Equal(UsbDeviceIdentity.BuildPhysicalKey(info), UsbDeviceMonitor.BuildIdentityKey(info));
    }

    [Fact]
    public void Monitor_ComputeChanges_DetectsAddedAndRemoved()
    {
        var a = Device(vid: 1, pid: 1, serial: "A");
        var b = Device(vid: 2, pid: 2, serial: "B");
        var last = UsbDeviceMonitor.BuildMap([a]);
        var current = UsbDeviceMonitor.BuildMap([b]);

        var changes = UsbDeviceMonitor.ComputeChanges(current, last);
        Assert.NotNull(changes);
        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.Kind == UsbDeviceChangeKind.Added && c.Device.SerialNumber == "B");
        Assert.Contains(changes, c => c.Kind == UsbDeviceChangeKind.Removed && c.Device.SerialNumber == "A");
    }

    [Fact]
    public void Monitor_ComputeChanges_MetadataChange_ReportsChanged()
    {
        var before = Device(vid: 1, pid: 1, serial: "A", ifClass: 0xFF, ifSub: 0x42, ifProtocol: 0x01);
        var after = Device(vid: 1, pid: 1, serial: "A", ifClass: 0xFF, ifSub: 0x42, ifProtocol: 0x02);

        var changes = UsbDeviceMonitor.ComputeChanges(
            UsbDeviceMonitor.BuildMap([after]),
            UsbDeviceMonitor.BuildMap([before]));

        var changed = Assert.Single(changes!);
        Assert.Equal(UsbDeviceChangeKind.Changed, changed.Kind);
        Assert.Equal((byte?)0x02, changed.Device.InterfaceProtocol);
    }

    [Fact]
    public void Monitor_ComputeChanges_NoChange_ReturnsNull()
    {
        var a = Device(vid: 1, pid: 1, serial: "A");
        var changes = UsbDeviceMonitor.ComputeChanges(
            UsbDeviceMonitor.BuildMap([a]),
            UsbDeviceMonitor.BuildMap([a]));

        Assert.Null(changes);
    }

    [Fact]
    public void Monitor_Constructor_FiresInitialSnapshot()
    {
        var devices = new[] { Device(vid: 1, pid: 1, serial: "A"), Device(vid: 2, pid: 2, serial: "B") };
        List<UsbDeviceChange>? initial = null;

        using var monitor = new UsbDeviceMonitor(
            () => devices,
            changes => initial = [.. changes],
            _ => throw new InvalidOperationException("no errors expected"),
            TimeSpan.FromDays(1),
            fireInitialSnapshot: true);

        Assert.NotNull(initial);
        Assert.Equal(2, initial.Count);
        Assert.All(initial, c => Assert.Equal(UsbDeviceChangeKind.Added, c.Kind));
        Assert.Equal(["A", "B"], initial.Select(c => c.Device.SerialNumber));
    }

    [Fact]
    public void Monitor_Constructor_EnumeratorThrows_InvokesOnError()
    {
        var boom = new IOException("enumerate failed");
        Exception? observed = null;
        List<UsbDeviceChange>? initial = null;

        using var monitor = new UsbDeviceMonitor(
            () => throw boom,
            changes => initial = [.. changes],
            error => observed = error,
            TimeSpan.FromDays(1),
            fireInitialSnapshot: true);

        Assert.Same(boom, observed);
        Assert.Null(initial);
    }
}
