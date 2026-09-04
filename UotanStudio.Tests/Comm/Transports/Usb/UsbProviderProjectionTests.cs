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
    private sealed class FakeUsbDevice : UsbDevice
    {
        public bool HandleCreationSucceeds { get; set; } = true;
        public int DisposeCount { get; private set; }

        protected override string BackendName => "fake";
        protected override bool IsOpen => false;
        internal override bool IsHandleOpen => false;
        protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs) => UsbChunkResult.Timeout(0);
        protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs) => UsbChunkResult.Timeout(0);

        public override byte[] Read(int length) => [];
        public override long Write(byte[] data, int length) => 0;
        public override int GetSerialNumber() => 0;
        public override int CreateHandle() => HandleCreationSucceeds ? 0 : 1;
        public override void Reset() { }
        public override void Dispose() => DisposeCount++;
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

        // ToInfos disposes every backend device after projection.
        // ToInfos 投影后释放每个后端设备。
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
            [matching, filterMismatch, openFailure], filter);

        var session = Assert.Single(sessions);
        Assert.Equal("usb:1-1", session.DeviceInfo.DevicePath);
        Assert.Equal((byte?)0xFF, session.DeviceInfo.InterfaceClass);

        // The matching device is owned by the live session (not disposed).
        // 匹配设备归活动会话所有（未被释放）。
        Assert.Equal(0, matching.DisposeCount);

        // The filter-mismatch session and the failed-open device are both disposed.
        // 过滤不匹配的会话与打开失败的设备都被释放。
        Assert.Equal(1, filterMismatch.DisposeCount);
        Assert.Equal(1, openFailure.DisposeCount);

        foreach (var s in sessions)
        {
            s.Dispose();
        }
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