using UotanStudio.Comm.Transports.Usb.Backend.Windows;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class WinUsbDiscoveryTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Windows registry API is required.", SkipUnless = nameof(IsWindows))]
    public void ReadDeviceInterfaceGuids_MultipleMissingNodes_BuildsFallbackMapOnce()
    {
        var firstId = @"UotanStudio.Tests\" + Guid.NewGuid().ToString("N");
        var secondId = @"UotanStudio.Tests\" + Guid.NewGuid().ToString("N");
        var firstGuid = Guid.NewGuid();
        var secondGuid = Guid.NewGuid();
        var builds = 0;
        var map = new Lazy<Dictionary<string, List<Guid>>>(() =>
        {
            builds++;
            return new(StringComparer.OrdinalIgnoreCase)
            {
                [firstId] = [firstGuid, firstGuid],
                [secondId] = [secondGuid]
            };
        });

        Assert.Equal([firstGuid], WinUSBFinder.ReadDeviceInterfaceGuids(firstId.ToUpperInvariant(), map));
        Assert.Equal([secondGuid], WinUSBFinder.ReadDeviceInterfaceGuids(secondId, map));
        Assert.Empty(WinUSBFinder.ReadDeviceInterfaceGuids(firstId + "-missing", map));
        Assert.Equal([firstGuid], WinUSBFinder.ReadDeviceInterfaceGuids(firstId, map));
        Assert.Equal(1, builds);
    }

    [Fact(Skip = "Windows registry API is required.", SkipUnless = nameof(IsWindows))]
    public void ReadDeviceInterfaceGuids_SeparateScans_ObserveUpdatedFallbackMap()
    {
        var instanceId = @"UotanStudio.Tests\" + Guid.NewGuid().ToString("N");
        var original = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var current = original;
        Dictionary<string, List<Guid>> BuildMap() => new(StringComparer.OrdinalIgnoreCase)
        {
            [instanceId] = [current]
        };
        var firstScan = new Lazy<Dictionary<string, List<Guid>>>(BuildMap);
        Assert.Equal([original], WinUSBFinder.ReadDeviceInterfaceGuids(instanceId, firstScan));

        current = replacement;
        var secondScan = new Lazy<Dictionary<string, List<Guid>>>(BuildMap);

        Assert.Equal([original], WinUSBFinder.ReadDeviceInterfaceGuids(instanceId, firstScan));
        Assert.Equal([replacement], WinUSBFinder.ReadDeviceInterfaceGuids(instanceId, secondScan));
    }
}