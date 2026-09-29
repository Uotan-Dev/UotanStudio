using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;
using UotanStudio.Comm.Transports.Usb.Core;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

/// <summary>
/// USB provider projection tests: converting backend <see cref="UsbDevice"/> instances into
/// device-info / session collections with filter application and disposal of non-matching
/// devices. Uses an in-process fake <see cref="UsbDevice"/> — no real hardware is touched.
/// <para>USB 提供器投影测试：将后端 <see cref="UsbDevice"/> 实例转换为设备信息/会话集合，
/// 应用过滤器并释放不匹配设备。使用进程内假 <see cref="UsbDevice"/>——不接触真实硬件。</para>
/// </summary>
public class UsbProviderProjectionTests
{
    private static string LibUsbKey(string serial, string path, string protocol = "01", string interfaceClass = "FF", string interfaceSubClass = "50")
        => $"libusb|LibUsbDotNet|12D1|5000|{interfaceClass}|{interfaceSubClass}|{protocol}|{serial}|{path}";

    [Theory]
    [InlineData("", "serial", "path1", "path1", "01", true)]
    [InlineData("serial", "other", "path1", "path1", "01", false)]
    [InlineData("", "serial", "path1", "path2", "01", false)]
    [InlineData("", "serial", "path1", "path1", "02", false)]
    [InlineData("", "serial", "", "", "01", false)]
    public void OpenIdentity_AllowsSerialEnrichmentOnlyOnSamePhysicalInterface(string beforeSerial, string afterSerial,
        string beforePath, string afterPath, string protocol, bool matches)
        => Assert.Equal(matches, UsbDeviceIdentity.MatchesOpenedKey(
            LibUsbKey(beforeSerial, beforePath),
            LibUsbKey(afterSerial, afterPath, protocol)));

    [Theory]
    [InlineData("path1", "path1", "01", "FF", "50", true)]
    [InlineData("path1", "path1", "02", "FF", "50", false)]
    [InlineData("path1", "path1", "01", "FE", "50", false)]
    [InlineData("path1", "path1", "01", "FF", "51", false)]
    [InlineData("path1", "path2", "01", "FF", "50", false)]
    public void OpenIdentity_SerialAbsentOnBothSides_MatchesOnlyOnSamePhysicalInterface(string beforePath, string afterPath,
        string protocol, string interfaceClass, string interfaceSubClass, bool matches)
        => Assert.Equal(matches, UsbDeviceIdentity.MatchesOpenedKey(
            LibUsbKey("", beforePath),
            LibUsbKey("", afterPath, protocol, interfaceClass, interfaceSubClass)));

    [Fact]
    public void OpenIdentity_UnlabelledDeviceKeyWithoutDevicePath_MatchesOnlyItself()
    {
        var pathless = LibUsbKey("", "");
        Assert.True(UsbDeviceIdentity.MatchesOpenedKey(pathless, pathless));
        Assert.False(UsbDeviceIdentity.MatchesOpenedKey(pathless, LibUsbKey("serial", "")));
        Assert.False(UsbDeviceIdentity.MatchesOpenedKey(pathless, LibUsbKey("", "path1")));
    }

    [Fact]
    public void OpenIdentity_SerialPresentOnBothSides_MatchesIgnoringDescriptorCasing()
        => Assert.True(UsbDeviceIdentity.MatchesOpenedKey(
            LibUsbKey("0123456789ABCDEF", "path1"),
            LibUsbKey("0123456789abcdef", "path1")));

    private sealed class FakeUsbDevice : UsbDevice
    {
        public bool HandleCreationSucceeds { get; set; } = true;
        public int DisposeCount { get; private set; }
        public int OpenCount { get; private set; }

        protected override string BackendName => "fake";
        protected override bool IsOpen => false;
        internal override bool IsHandleOpen => false;
        protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs) => UsbChunkResult.Timeout(0);
        protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs) => UsbChunkResult.Timeout(0);

        public override byte[] Read(int length) => [];
        public override long Write(byte[] data, int length) => 0;
        public override int GetSerialNumber() => 0;
        public override int CreateHandle() { OpenCount++; return HandleCreationSucceeds ? 0 : 1; }
        public override void Reset() { }
        public override void Dispose() => DisposeCount++;
    }

    private sealed class SessionProvider(IReadOnlyList<IUsbDeviceSession> sessions) : IUsbApiProvider
    {
        public string ApiName => "test-provider";
        public UsbApiKind ApiKind => UsbApiKind.Custom;
        public bool IsSupportedOnCurrentPlatform => true;
        public IReadOnlyList<UsbDeviceInfo> EnumerateDeviceInfos(UsbDeviceFilter? filter = null) => [];
        public IReadOnlyList<IUsbDeviceSession> EnumerateDeviceSessions(UsbDeviceFilter? filter = null) => sessions;
        public UsbApiCapabilities GetCapabilities() => new() { ApiName = ApiName, ApiKind = ApiKind, IsSupportedOnCurrentPlatform = true };
    }

    [Fact]
    public void OpenByKey_EnrichesSerial_AndDisposesAllOtherEagerSessions()
    {
        var first = Device("path1", 0x12d1, 0x5000, "one", 0xff, 0x50, 1);
        var middle = Device("path2", 0x12d1, 0x5000, "two", 0xff, 0x50, 1);
        var last = Device("path3", 0x12d1, 0x5000, "three", 0xff, 0x50, 1);
        var sessions = new[] { first, middle, last }.Select(device => new UsbDeviceSession("test-provider", UsbApiKind.Custom, device)).ToArray();
        var registry = new UsbApiRegistry(); registry.Register("test-provider", () => new SessionProvider(sessions));
        using var selected = new UsbCommunicationLayer(registry).OpenDeviceSessionByKey(sessions[1].DeviceInfo.DeviceKey.Replace("|two|", "||", StringComparison.Ordinal));
        Assert.Same(sessions[1], selected);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, middle.DisposeCount);
        Assert.Equal(1, last.DisposeCount);
    }

    private static FakeUsbDevice Device(
        string path,
        ushort vid,
        ushort pid,
        string? serial = null,
        byte? interfaceClass = null,
        byte? interfaceSubClass = null,
        byte? interfaceProtocol = null)
        => new()
        {
            DevicePath = path,
            VendorId = vid,
            ProductId = pid,
            SerialNumber = serial,
            InterfaceClass = interfaceClass,
            InterfaceSubClass = interfaceSubClass,
            InterfaceProtocol = interfaceProtocol
        };

    [Fact]
    public void ToInfos_ProjectsMatchingDevicesAndDisposesAll()
    {
        var matching = Device("usb:1-1", 0x1234, 0x5678, "SN1", 0xFF, 0x42, 0x01);
        var nonMatching = Device("usb:1-2", 0x1234, 0x9999, "SN2", 0xEE, 0x00, 0x00);

        var filter = new UsbDeviceFilter { VendorId = 0x1234, InterfaceClass = 0xFF };

        var infos = UsbProviderProjection.ToInfos("fake-api", UsbApiKind.Custom, [matching, nonMatching], filter);

        var info = Assert.Single(infos);
        Assert.Equal("fake-api", info.ApiName);
        Assert.Equal(UsbApiKind.Custom, info.SourceApiKind);
        Assert.Equal("FakeUsbDevice", info.SourceDeviceType);
        Assert.Equal("usb:1-1", info.DevicePath);
        Assert.Equal("SN1", info.SerialNumber);
        Assert.Equal((ushort?)0x1234, info.VendorId);
        Assert.Equal((ushort?)0x5678, info.ProductId);
        Assert.Equal((byte?)0xFF, info.InterfaceClass);
        Assert.Equal((byte?)0x42, info.InterfaceSubClass);
        Assert.Equal((byte?)0x01, info.InterfaceProtocol);
        Assert.False(string.IsNullOrEmpty(info.DeviceKey));

        Assert.Equal(1, matching.DisposeCount);
        Assert.Equal(1, nonMatching.DisposeCount);
    }

    [Fact]
    public void ToInfos_NoFilter_ReturnsAllDevices()
    {
        var first = Device("usb:1", 1, 1);
        var second = Device("usb:2", 2, 2);

        var infos = UsbProviderProjection.ToInfos("api", UsbApiKind.Auto, [first, second], filter: null);

        Assert.Equal(["usb:1", "usb:2"], infos.Select(i => i.DevicePath));
    }

    [Fact]
    public void ToSessions_OpensMatchingDeviceAndDisposesOthers()
    {
        var matching = Device("usb:1-1", 0x1234, 0x5678, "SN1", 0xFF, 0x42, 0x01);
        var filterMismatch = Device("usb:1-2", 0x1234, 0x9999, "SN2", 0xEE, 0x00, 0x00);
        var openFailure = Device("usb:1-3", 0x1234, 0x7777, "SN3", 0xFF, 0x42, 0x01);
        openFailure.HandleCreationSucceeds = false;

        var filter = new UsbDeviceFilter { InterfaceClass = 0xFF };

        var sessions = UsbProviderProjection.ToSessions("fake-api", UsbApiKind.Custom,
            [matching, filterMismatch, openFailure], filter, _ => { });

        var session = Assert.Single(sessions);
        Assert.Equal("usb:1-1", session.DeviceInfo.DevicePath);
        Assert.Equal((byte?)0xFF, session.DeviceInfo.InterfaceClass);

        Assert.Equal(0, matching.DisposeCount);

        Assert.Equal(1, filterMismatch.DisposeCount);
        Assert.Equal(1, openFailure.DisposeCount);

        foreach (var s in sessions)
        {
            s.Dispose();
        }
    }

    [Fact]
    public void ExactPath_IsFilteredBeforeOpeningSameModelPeers()
    {
        var selected = Device("usb:1", 1, 1);
        var other = Device("usb:10", 1, 1);
        var filter = new UsbDeviceFilter { DevicePathEquals = "usb:1" };
        var sessions = UsbProviderProjection.ToSessions("fake", UsbApiKind.Custom, [other, selected], filter, _ => { });
        Assert.Single(sessions).Dispose();
        Assert.Equal(1, selected.OpenCount);
        Assert.Equal(0, other.OpenCount);
    }

    [Fact]
    public void ServerOwnership_RejectsBeforeAnyUsbOpen()
    {
        var selected = Device("usb:1", 1, 1);
        Assert.Throws<IOException>(() => UsbProviderProjection.ToSessions("fake", UsbApiKind.Custom, [selected], null,
            _ => throw new IOException("Server owns target")));
        Assert.Equal(0, selected.OpenCount);
        Assert.Equal(1, selected.DisposeCount);
    }

    [Fact]
    public void UnlabelledDevices_KeepDistinctPhysicalIdentities()
    {
        var first = new UsbDeviceInfo { DevicePath = "usb:1", ApiName = "native", VendorId = 1, ProductId = 1 };
        var second = new UsbDeviceInfo { DevicePath = "usb:2", ApiName = "native", VendorId = 1, ProductId = 1 };
        Assert.NotEqual(UsbDeviceIdentity.BuildPhysicalKey(first), UsbDeviceIdentity.BuildPhysicalKey(second));
    }

    [Fact]
    public void UnlabelledDevice_MultipleInterfaceGuids_CollapseToOnePhysicalIdentity()
    {
        var viaClassGuid = new UsbDeviceInfo
        {
            DevicePath = @"\\?\usb#vid_18d1&pid_d00d#6b24c48f#{a5dcbf10-6530-11d2-901f-00c04fb951ed}",
            ApiName = "native",
            VendorId = 0x18D1,
            ProductId = 0xD00D,
            InterfaceClass = 0xFF,
            InterfaceSubClass = 0x42,
        };
        var viaLegacyGuid = new UsbDeviceInfo
        {
            DevicePath = @"\\?\usb#vid_18d1&pid_d00d#6b24c48f#{f72fe0d4-cbcb-407d-8814-9ed673d0dd6b}",
            ApiName = "native",
            VendorId = 0x18D1,
            ProductId = 0xD00D,
            InterfaceClass = 0xFF,
            InterfaceSubClass = 0x42,
        };
        Assert.Equal(UsbDeviceIdentity.BuildPhysicalKey(viaClassGuid), UsbDeviceIdentity.BuildPhysicalKey(viaLegacyGuid));

        var adbInterface = new UsbDeviceInfo
        {
            DevicePath = @"\\?\usb#vid_18d1&pid_d00d&mi_02#6b24c48f#{a5dcbf10-6530-11d2-901f-00c04fb951ed}",
            ApiName = "native",
            VendorId = 0x18D1,
            ProductId = 0xD00D,
            InterfaceClass = 0xFF,
            InterfaceSubClass = 0x42,
        };
        var mtpInterface = new UsbDeviceInfo
        {
            DevicePath = @"\\?\usb#vid_18d1&pid_d00d&mi_05#6b24c48f#{a5dcbf10-6530-11d2-901f-00c04fb951ed}",
            ApiName = "native",
            VendorId = 0x18D1,
            ProductId = 0xD00D,
            InterfaceClass = 0xEE,
            InterfaceSubClass = 0x01,
        };
        Assert.NotEqual(UsbDeviceIdentity.BuildPhysicalKey(adbInterface), UsbDeviceIdentity.BuildPhysicalKey(mtpInterface));
    }

    [Fact]
    public void ToSessions_NoFilter_KeepsAllOpenSessions()
    {
        var first = Device("usb:1", 1, 1);
        var second = Device("usb:2", 2, 2);

        var sessions = UsbProviderProjection.ToSessions("api", UsbApiKind.Auto, [first, second], filter: null);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(0, first.DisposeCount);
        Assert.Equal(0, second.DisposeCount);

        foreach (var s in sessions)
        {
            s.Dispose();
        }

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }
}