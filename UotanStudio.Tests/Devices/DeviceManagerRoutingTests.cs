using System.IO.Ports;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Mirroring;
using UotanStudio.ViewModels.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class DeviceManagerRoutingTests
{
    [Theory]
    [InlineData(DeviceProtocolKind.Adb)]
    [InlineData(DeviceProtocolKind.Hdc)]
    public void ResolveDirectUsbOpenKey_ServerPresent_RejectsStaleDirectTarget(DeviceProtocolKind protocol)
    {
        if (protocol == DeviceProtocolKind.Adb && !DebugBridgeRoute.UseAdbServer() ||
            protocol == DeviceProtocolKind.Hdc && !DebugBridgeRoute.UseHdcServer())
            Assert.Skip("The corresponding debug bridge server is not running.");

        var device = new DetectedDevice
        {
            Key = protocol == DeviceProtocolKind.Adb ? "adb:stale" : "hdc:stale",
            Serial = "stale",
            Protocol = protocol,
            Transport = DeviceTransportKind.Usb
        };
        Assert.Throws<IOException>(() => UotanStudio.App.DeviceMonitor.ResolveDirectUsbOpenKey(device));
    }

    [Fact]
    public void OpenAdbLease_ServerOriginTarget_NeverFallsBackToUsb()
    {
        var device = new DetectedDevice
        {
            Key = "adb:stale",
            Serial = "stale",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb
        };

        Assert.Throws<IOException>(() => UotanStudio.App.DeviceMonitor.ResolveDirectUsbOpenKey(device));
        if (DebugBridgeRoute.UseAdbServer())
        {
            var opened = UotanStudio.App.DeviceMonitor.OpenAdbLease(device);
            Assert.NotNull(opened);
            opened.Value.Lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        else
        {
            Assert.Throws<IOException>(() => UotanStudio.App.DeviceMonitor.OpenAdbLease(device));
        }
    }

    [Fact]
    public void ProbeAdb_StaleServerOriginTarget_ReturnsNotReady()
    {
        var device = new DetectedDevice
        {
            Key = "adb:stale",
            Serial = Guid.NewGuid().ToString("N"),
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb,
            State = "device"
        };

        Assert.False(UotanStudio.App.DeviceMonitor.ProbeAdb(device));
    }

    [Fact]
    public async Task DisconnectAdbAsync_NonNetworkTarget_RejectsBeforeServerAccess()
    {
        var device = new DetectedDevice
        {
            Key = "usb:test",
            Serial = "test",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb
        };

        await Assert.ThrowsAsync<ArgumentException>(() => UotanStudio.App.DeviceMonitor.DisconnectAdbAsync(device));
    }

    [Fact]
    public async Task HdcShellAsync_WrongProtocol_RejectsBeforeCommunication()
    {
        var device = new DetectedDevice
        {
            Key = "adb:test",
            Serial = "test",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb
        };

        await Assert.ThrowsAsync<ArgumentException>(() => UotanStudio.App.DeviceMonitor.HdcShellAsync(
            device, "echo test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RequireSerialTarget_SelectedPortMissing_RejectsBeforeConnection()
    {
        var options = new SerialConnectionOptions
        {
            PortName = "selected",
            BaudRate = 115200,
            Handshake = Handshake.None
        };

        Assert.Throws<IOException>(() => DeviceMonitorService.RequireSerialTarget(options, ["other"]));
        DeviceMonitorService.RequireSerialTarget(options, ["selected"]);
    }

    [Fact]
    public void StartMirror_NonAdbTarget_RejectsBeforeDeployment()
    {
        var device = new DetectedDevice
        {
            Key = "edl:test",
            Serial = "test",
            Protocol = DeviceProtocolKind.Edl,
            Transport = DeviceTransportKind.Usb
        };

        Assert.Throws<InvalidOperationException>(() =>
            UotanStudio.App.DeviceMonitor.StartMirror(device, "missing.jar", new ScrcpyServerOptions()));
    }

    [Fact]
    public async Task OpenProtocolSession_MismatchedUsbTarget_RejectsBeforeUsbOpen()
    {
        var device = new DetectedDevice
        {
            Key = "fastboot:test",
            Serial = "test",
            Protocol = DeviceProtocolKind.Fastboot,
            Transport = DeviceTransportKind.Usb
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            UotanStudio.App.DeviceMonitor.RunEdlAsync(device, _ => Task.FromResult(true), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            UotanStudio.App.DeviceMonitor.RunRkdlAsync(device, _ => Task.FromResult(true), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CoalesceSameEndpoint_DuplicatedServerTarget_PreservesReadyStateAndDistinctDevices()
    {
        static DetectedDevice Target(string key, string serial, string state) => new()
        {
            Key = key,
            Serial = serial,
            State = state,
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb,
            VendorId = 0x18D1,
            ProductId = 0xD00D
        };
        var devices = DeviceMonitorService.CoalesceSameEndpoint([
            Target("adb:one", "one", "offline"),
            Target("adb:two", "two", "device"),
            Target("adb:one", "one", "online")
        ]);

        Assert.Equal(2, devices.Count);
        Assert.Equal("online", Assert.Single(devices, device => device.Key == "adb:one").State);
        Assert.Contains(devices, device => device.Key == "adb:two");
    }

    [Fact]
    public void GroupPhysicalDevices_SharedIdentity_MergesProtocolsButNotMatchingUsbIds()
    {
        static DetectedDevice Target(string key, string serial, DeviceProtocolKind protocol, string? physicalId) => new()
        {
            Key = key,
            Serial = serial,
            Protocol = protocol,
            Transport = DeviceTransportKind.Usb,
            VendorId = 0x18D1,
            ProductId = 0xD00D,
            PhysicalId = physicalId
        };

        var groups = DeviceMonitorService.GroupPhysicalDevices([
            Target("adb:one", "one", DeviceProtocolKind.Adb, "usb:one"),
            Target("hdc:one", "one", DeviceProtocolKind.Hdc, "usb:one"),
            Target("adb:two", "two", DeviceProtocolKind.Adb, "usb:two")
        ]);

        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Count);
        Assert.Single(groups[1]);
    }

    [Fact]
    public void PhysicalUsbId_SharedContainer_MergesDifferentInterfacesWithoutCollapsingOtherDevices()
    {
        var shared = Guid.NewGuid();
        static DetectedDevice Endpoint(string key, DeviceProtocolKind protocol, string? physicalId) => new()
        {
            Key = key,
            Serial = key,
            Protocol = protocol,
            Transport = DeviceTransportKind.Usb,
            PhysicalId = physicalId
        };
        var adb = new UsbDeviceInfo { VendorId = 0x22D9, ProductId = 0x2769, ContainerId = shared };
        var hdc = new UsbDeviceInfo { VendorId = 0x12D1, ProductId = 0x5000, ContainerId = shared };
        var other = new UsbDeviceInfo { VendorId = 0x12D1, ProductId = 0x5000, ContainerId = Guid.NewGuid() };

        var groups = DeviceMonitorService.GroupPhysicalDevices([
            Endpoint("adb:one", DeviceProtocolKind.Adb, DeviceMonitorService.PhysicalUsbId(adb, null)),
            Endpoint("hdc:one", DeviceProtocolKind.Hdc, DeviceMonitorService.PhysicalUsbId(hdc, null)),
            Endpoint("hdc:two", DeviceProtocolKind.Hdc, DeviceMonitorService.PhysicalUsbId(other, null))
        ]);

        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Count);
        Assert.Single(groups[1]);
    }

    [Fact]
    public void FindServerUsbIdentity_ExactPnpInstanceSerial_UsesUniquePhysicalContainer()
    {
        var container = Guid.NewGuid();
        var infos = new UsbDeviceInfo[]
        {
            new()
            {
                VendorId = 0x22D9, ProductId = 0x2769,
                InterfaceClass = 0xFF, InterfaceSubClass = 0x42, InterfaceProtocol = 0x01,
                InterfaceMetadataObserved = true,
                DeviceInstanceId = @"USB\VID_22D9&PID_2769\B845EBDF",
                ContainerId = container
            },
            new()
            {
                VendorId = 0x22D9, ProductId = 0x2769,
                InterfaceClass = 0xFF, InterfaceSubClass = 0x42, InterfaceProtocol = 0x01,
                InterfaceMetadataObserved = true,
                DeviceInstanceId = @"USB\VID_22D9&PID_2769\OTHER",
                ContainerId = Guid.NewGuid()
            }
        };

        Assert.Equal($"usb-container:{container:N}", DeviceMonitorService.FindServerUsbIdentity(
            infos, "B845EBDF", DeviceProtocolKind.Adb));
        Assert.Null(DeviceMonitorService.FindServerUsbIdentity(infos, "missing", DeviceProtocolKind.Adb));
        Assert.Null(DeviceMonitorService.FindServerUsbIdentity(infos, "B845EBDF", DeviceProtocolKind.Hdc));
        infos[1].DeviceInstanceId = infos[0].DeviceInstanceId;
        Assert.Null(DeviceMonitorService.FindServerUsbIdentity(infos, "B845EBDF", DeviceProtocolKind.Adb));
    }

    [Fact]
    public void DeviceInfo_SelectedConnection_ChangesTransactionTargetWithinOneEntry()
    {
        var adb = new DetectedDevice
        {
            Key = "adb:one",
            Serial = "one",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb,
            PhysicalId = "usb:one"
        };
        var hdc = new DetectedDevice
        {
            Key = "hdc:one",
            Serial = "one",
            Protocol = DeviceProtocolKind.Hdc,
            Transport = DeviceTransportKind.Usb,
            PhysicalId = "usb:one"
        };
        var entry = new DeviceInfo([adb, hdc]);

        Assert.True(entry.HasMultipleConnections);
        Assert.Same(adb, entry.Device);
        entry.SelectedConnection = hdc;
        Assert.Same(hdc, entry.Device);
        Assert.True(entry.IsHdc);
        Assert.Equal("usb:one", entry.Key);

        var refreshedHdc = new DetectedDevice
        {
            Key = "hdc:one",
            Serial = "one",
            Protocol = DeviceProtocolKind.Hdc,
            Transport = DeviceTransportKind.Usb,
            PhysicalId = "usb:one"
        };
        var refreshedEntry = new DeviceInfo([adb, refreshedHdc]);

        refreshedEntry.SelectedConnection = hdc;

        Assert.Same(refreshedHdc, refreshedEntry.Device);
        Assert.True(refreshedEntry.IsHdc);

        var unrelated = new DetectedDevice
        {
            Key = "adb:other",
            Serial = "other",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb,
            PhysicalId = "usb:other"
        };
        refreshedEntry.SelectedConnection = unrelated;

        Assert.Same(refreshedHdc, refreshedEntry.Device);
    }

}