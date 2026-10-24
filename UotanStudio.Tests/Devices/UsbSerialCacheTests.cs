using UotanStudio.Comm.Transports.Usb.Backend.Windows;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class UsbSerialCacheTests : IDisposable
{
    private const string Path = @"\\?\usb#vid_18d1&pid_4ee7&mi_01#7&2c1f0b3a&0&0001#{guid}";
    private const string OtherPath = @"\\?\usb#vid_18d1&pid_4ee7&mi_02#7&2c1f0b3a&0&0002#{guid}";
    private const long Now = 1_000_000;

    public UsbSerialCacheTests() => UsbSerialCache.Clear();

    public void Dispose() => UsbSerialCache.Clear();

    [Fact]
    public void TryGet_AfterRememberSameInstance_ReturnsRememberedSerial()
    {
        UsbSerialCache.Remember(Path, "USB\\VID_18D1&PID_4EE7&MI_01\\6&1a2b&0&0001", "SERIAL0001", Now);

        Assert.True(UsbSerialCache.TryGet(Path, "USB\\VID_18D1&PID_4EE7&MI_01\\6&1a2b&0&0001", Now + 1_000, out var serial));
        Assert.Equal("SERIAL0001", serial);
    }

    [Fact]
    public void TryGet_AfterReplugDifferentInstance_DoesNotCarrySerialOver()
    {
        UsbSerialCache.Remember(Path, "USB\\VID_18D1&PID_4EE7&MI_01\\6&1a2b&0&0001", "SERIAL0001", Now);

        Assert.False(UsbSerialCache.TryGet(Path, "USB\\VID_18D1&PID_4EE7&MI_01\\7&3d4c&0&0001", Now + 1_000, out var serial));
        Assert.Null(serial);
    }

    [Fact]
    public void TryGet_EntryOlderThanLifetime_ReturnsFalse()
    {
        UsbSerialCache.Remember(Path, "instance", "SERIAL0001", Now);

        Assert.False(UsbSerialCache.TryGet(Path, "instance", Now + (long)TimeSpan.FromMinutes(31).TotalMilliseconds, out _));
    }

    [Fact]
    public void TryGet_BlankSerial_IsNotRemembered()
    {
        UsbSerialCache.Remember(Path, "instance", "   ", Now);

        Assert.False(UsbSerialCache.TryGet(Path, "instance", Now, out _));
    }

    [Fact]
    public void IsRecentlyUnavailable_AfterFailedRead_SuppressesTheSameScanOnly()
    {
        UsbSerialCache.RememberUnavailable(Path, "instance", Now);

        Assert.True(UsbSerialCache.IsRecentlyUnavailable(Path, "instance", Now + 500));
        Assert.False(UsbSerialCache.IsRecentlyUnavailable(Path, "instance", Now + 3_000));
        Assert.False(UsbSerialCache.IsRecentlyUnavailable(OtherPath, "instance", Now + 500));
    }

    [Fact]
    public void Remember_EntryWithoutInstance_StillMatchesAnyInstance()
    {
        UsbSerialCache.Remember(Path, null, "SERIAL0001", Now);

        Assert.True(UsbSerialCache.TryGet(Path, "any-instance", Now + 1_000, out var serial));
        Assert.Equal("SERIAL0001", serial);
    }
}