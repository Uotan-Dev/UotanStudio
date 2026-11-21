using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;
using Xunit;

namespace UotanStudio.Tests.Usb;

/// <summary>
/// Verifies that the USB inventory cache seeds once and applies later events to only the touched slot.
/// <para>验证 USB 清单缓存只播种一次，后续事件只更新被触及的槽位。</para>
/// </summary>
public sealed class UsbEnumerationSnapshotTests
{
    [Fact]
    public void FirstPassSeedsCache_AndEmptyHintPassReusesIt()
    {
        var snapshot = new UsbEnumerationSnapshot();
        var first = Device("path-1", 0x1111, 0x2222);
        var second = Device("path-2", 0x3333, 0x4444);
        var fullCalls = 0;

        IReadOnlyList<UsbDeviceInfo> Full()
        {
            fullCalls++;
            return [first, second];
        }

        var seeded = snapshot.Apply([], _ => throw new InvalidOperationException("No probe expected"), Full);
        var cached = snapshot.Apply([], _ => throw new InvalidOperationException("No probe expected"), Full);

        Assert.Equal(2, seeded.Count);
        Assert.Equal(2, cached.Count);
        Assert.Equal(1, fullCalls);
    }

    [Fact]
    public void RemovalHint_RemovesOnlyTheNamedDevice()
    {
        var snapshot = new UsbEnumerationSnapshot();
        var first = Device("path-1", 0x1111, 0x2222);
        var second = Device("path-2", 0x3333, 0x4444);
        snapshot.Apply([], _ => UsbDeviceProbeResult.Unhandled, () => [first, second]);

        var result = snapshot.Apply(
            [new UsbDeviceProbeHint { Kind = UsbDeviceChangeKind.Removed, DevicePath = "path-1" }],
            _ => throw new InvalidOperationException("Removal must not probe"),
            () => throw new InvalidOperationException("A precise removal must not enumerate"));

        var device = Assert.Single(result);
        Assert.Equal("path-2", device.DevicePath);
    }

    [Fact]
    public void ArrivalHint_ProbesOnlyTheNamedDevice()
    {
        var snapshot = new UsbEnumerationSnapshot();
        var existing = Device("path-1", 0x1111, 0x2222);
        var arrived = Device("path-2", 0x3333, 0x4444);
        snapshot.Apply([], _ => UsbDeviceProbeResult.Unhandled, () => [existing]);
        var probeCalls = 0;

        var result = snapshot.Apply(
            [new UsbDeviceProbeHint { Kind = UsbDeviceChangeKind.Added, DevicePath = "path-2" }],
            _ =>
            {
                probeCalls++;
                return UsbDeviceProbeResult.Found(arrived);
            },
            () => throw new InvalidOperationException("A precise arrival must not enumerate"));

        Assert.Equal(1, probeCalls);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, device => device.DevicePath == "path-1");
        Assert.Contains(result, device => device.DevicePath == "path-2");
    }

    [Fact]
    public void UnhandledProbe_FallsBackToFullEnumeration()
    {
        var snapshot = new UsbEnumerationSnapshot();
        var first = Device("path-1", 0x1111, 0x2222);
        var replacement = Device("path-2", 0x3333, 0x4444);
        var fullCalls = 0;

        IReadOnlyList<UsbDeviceInfo> Full()
        {
            fullCalls++;
            return fullCalls == 1 ? [first] : [replacement];
        }

        snapshot.Apply([], _ => UsbDeviceProbeResult.Unhandled, Full);
        var result = snapshot.Apply(
            [new UsbDeviceProbeHint { Kind = UsbDeviceChangeKind.Added, PhysicalPortPath = "macos-port:00000001" }],
            _ => UsbDeviceProbeResult.Unhandled,
            Full);

        Assert.Equal(2, fullCalls);
        var device = Assert.Single(result);
        Assert.Equal("path-2", device.DevicePath);
    }

    [Fact]
    public void AmbiguousVidPidHint_FallsBackToFullEnumeration()
    {
        var snapshot = new UsbEnumerationSnapshot();
        var first = Device("path-1", 0x1111, 0x2222);
        var second = Device("path-2", 0x1111, 0x2222);
        var replacement = Device("path-3", 0x3333, 0x4444);
        var fullCalls = 0;

        IReadOnlyList<UsbDeviceInfo> Full()
        {
            fullCalls++;
            return fullCalls == 1 ? [first, second] : [replacement];
        }

        snapshot.Apply([], _ => UsbDeviceProbeResult.Unhandled, Full);
        var result = snapshot.Apply(
            [new UsbDeviceProbeHint { Kind = UsbDeviceChangeKind.Removed, VendorId = 0x1111, ProductId = 0x2222 }],
            _ => throw new InvalidOperationException("Ambiguous hint must not probe"),
            Full);

        Assert.Equal(2, fullCalls);
        var device = Assert.Single(result);
        Assert.Equal("path-3", device.DevicePath);
    }

    private static UsbDeviceInfo Device(string path, ushort vendorId, ushort productId) => new()
    {
        ApiName = "test",
        DevicePath = path,
        VendorId = vendorId,
        ProductId = productId,
        SerialNumber = "serial-" + path
    };
}