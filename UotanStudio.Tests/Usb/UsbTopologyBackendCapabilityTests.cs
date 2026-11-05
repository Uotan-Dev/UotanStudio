using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.LibUsb;
using UotanStudio.Comm.Transports.Usb.Backend.Linux;
using UotanStudio.Comm.Transports.Usb.Backend.MacOS;
using UotanStudio.Comm.Transports.Usb.Backend.Windows;
using UotanStudio.Models;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Usb;

/// <summary>
/// Verifies that each topology backend declares exactly what its data source can report, and that the node
/// view model renders "not provided by this backend" for fields the backend cannot obtain in principle
/// instead of the "none" reserved for values the backend really reported as empty.
/// <para>验证每个拓扑后端声明的能力与其数据源的真实能力一致，且节点视图模型对后端原理上拿不到的字段显示
/// 「该后端不提供」，而不是把「后端确实上报且为空」才用的「无」用在上面。</para>
/// </summary>
public sealed class UsbTopologyBackendCapabilityTests
{
    private const string NotProvidedKey = "UsbTree_NotProvided";
    private const string NoneKey = "UsbTree_None";

    [Fact]
    public void Windows_Capabilities_DeclareEveryDiagnosticFeature()
    {
        var capabilities = WindowsUsbTopologyProvider.Instance.Capabilities;

        Assert.True(capabilities.SupportsDriverInfo);
        Assert.True(capabilities.SupportsInterfaceNodes);
        Assert.True(capabilities.SupportsHostControllerNodes);
        Assert.True(capabilities.SupportsDeviceInterfaceGuids);
        Assert.True(capabilities.SupportsProblemCodes);
        Assert.True(capabilities.SupportsClassGuid);
        Assert.True(capabilities.SupportsDriverInfPath);
        Assert.True(capabilities.SupportsContainerId);
        Assert.True(capabilities.SupportsCompatibleIds);
    }

    [Fact]
    public void Linux_Capabilities_DeclareModaliasCompatibleIds()
    {
        var capabilities = LinuxSysfsTopologyProvider.Instance.Capabilities;

        Assert.True(capabilities.SupportsDriverInfo);
        Assert.True(capabilities.SupportsInterfaceNodes);
        Assert.True(capabilities.SupportsHostControllerNodes);
        Assert.True(capabilities.SupportsCompatibleIds);
        Assert.False(capabilities.SupportsDeviceInterfaceGuids);
        Assert.False(capabilities.SupportsProblemCodes);
        Assert.False(capabilities.SupportsClassGuid);
        Assert.False(capabilities.SupportsDriverInfPath);
        Assert.False(capabilities.SupportsContainerId);
    }

    [Fact]
    public void MacOS_Capabilities_DeclareDriverClassWithoutWindowsConcepts()
    {
        var capabilities = MacIoRegistryTopologyProvider.Instance.Capabilities;

        Assert.True(capabilities.SupportsDriverInfo);
        Assert.True(capabilities.SupportsInterfaceNodes);
        Assert.True(capabilities.SupportsHostControllerNodes);
        Assert.False(capabilities.SupportsDeviceInterfaceGuids);
        Assert.False(capabilities.SupportsProblemCodes);
        Assert.False(capabilities.SupportsClassGuid);
        Assert.False(capabilities.SupportsDriverInfPath);
        Assert.False(capabilities.SupportsContainerId);
        Assert.False(capabilities.SupportsCompatibleIds);
    }

    [Fact]
    public void LibUsb_Capabilities_DeclareOnlyDescriptorFeatures()
    {
        var capabilities = LibUsbTopologyProvider.Instance.Capabilities;

        Assert.True(capabilities.SupportsInterfaceNodes);
        Assert.False(capabilities.SupportsDriverInfo);
        Assert.False(capabilities.SupportsHostControllerNodes);
        Assert.False(capabilities.SupportsDeviceInterfaceGuids);
        Assert.False(capabilities.SupportsProblemCodes);
        Assert.False(capabilities.SupportsClassGuid);
        Assert.False(capabilities.SupportsDriverInfPath);
        Assert.False(capabilities.SupportsContainerId);
        Assert.False(capabilities.SupportsCompatibleIds);
    }

    [Fact]
    public void MacShape_ReportsIoClassAsDriverServiceAndRegistryLocation()
    {
        var node = MacIoRegistryTopology.Shape(new MacIoRegistryEntry
        {
            InstanceId = "IOService:/AppleACPIPlatformExpert/PCI0/XHC1@14000000",
            IOClass = "AppleUSBHostDevice",
            VendorId = 0x18D1,
            ProductId = 0x4EE0,
            BcdDevice = 0x0414,
            Speed = "High"
        }, null);

        Assert.Equal("AppleUSBHostDevice", node.ClassName);
        Assert.Equal("AppleUSBHostDevice", node.DriverService);
        Assert.Equal(["ioreg:IOService:/AppleACPIPlatformExpert/PCI0/XHC1@14000000"], node.LocationPaths);
    }

    [Fact]
    public void DetailRows_LibUsbCapabilities_EmitOnlyProvidedEntries()
    {
        var capabilities = new UsbTopologyCapabilities
        {
            Backend = UsbTopologyBackendKind.LibUsb,
            SupportsInterfaceNodes = true
        };
        var node = new UsbTopologyNode
        {
            InstanceId = "libusb-port:1:2",
            Kind = UsbTopologyNodeKind.Device
        };
        var viewModel = CreateViewModel(capabilities, node);

        var labels = viewModel.DetailRows.Select(row => row.Label).ToArray();

        Assert.Equal([
            Localized("UsbTree_Detail_InstanceId"),
            Localized("UsbTree_Detail_Parent"),
            Localized("UsbTree_Detail_Kind"),
            Localized("UsbTree_Detail_Vendor"),
            Localized("UsbTree_Detail_Speed"),
            Localized("UsbTree_Detail_Interface"),
            Localized("UsbTree_Detail_Location"),
            Localized("UsbTree_Detail_HardwareIds")
        ], labels);
    }

    [Fact]
    public void DetailRows_WindowsCapabilities_EmitEveryEntry()
    {
        var capabilities = new UsbTopologyCapabilities
        {
            Backend = UsbTopologyBackendKind.WindowsPnp,
            SupportsDriverInfo = true,
            SupportsInterfaceNodes = true,
            SupportsHostControllerNodes = true,
            SupportsDeviceInterfaceGuids = true,
            SupportsProblemCodes = true,
            SupportsClassGuid = true,
            SupportsDriverInfPath = true,
            SupportsContainerId = true,
            SupportsCompatibleIds = true
        };
        var node = new UsbTopologyNode
        {
            InstanceId = "USB\\VID_18D1&PID_4EE0\\ABC",
            Kind = UsbTopologyNodeKind.Device
        };
        var viewModel = CreateViewModel(capabilities, node);

        var labels = viewModel.DetailRows.Select(row => row.Label).ToArray();

        Assert.Equal([
            Localized("UsbTree_Detail_InstanceId"),
            Localized("UsbTree_Detail_Parent"),
            Localized("UsbTree_Detail_Kind"),
            Localized("UsbTree_Detail_Vendor"),
            Localized("UsbTree_Detail_Speed"),
            Localized("UsbTree_Detail_Class"),
            Localized("UsbTree_Detail_ClassGuid"),
            Localized("UsbTree_Detail_DriverService"),
            Localized("UsbTree_Detail_DriverInf"),
            Localized("UsbTree_Detail_Interface"),
            Localized("UsbTree_Detail_Problem"),
            Localized("UsbTree_Detail_Container"),
            Localized("UsbTree_Detail_Location"),
            Localized("UsbTree_Detail_HardwareIds"),
            Localized("UsbTree_Detail_CompatibleIds"),
            Localized("UsbTree_Detail_InterfaceGuids")
        ], labels);
    }

    [Fact]
    public void NodeTexts_WithoutGatingCapability_RenderNotProvided()
    {
        var capabilities = new UsbTopologyCapabilities { Backend = UsbTopologyBackendKind.LibUsb };
        var node = new UsbTopologyNode
        {
            InstanceId = "libusb-port:1:2",
            Kind = UsbTopologyNodeKind.Device,
            ClassGuid = TestGuid(1),
            ContainerId = TestGuid(2),
            CompatibleIds = ["usb:v18D1p4EE0"]
        };
        var viewModel = CreateViewModel(capabilities, node);

        Assert.Equal(Localized(NotProvidedKey), viewModel.ClassGuidText);
        Assert.Equal(Localized(NotProvidedKey), viewModel.ContainerIdText);
        Assert.Equal(Localized(NotProvidedKey), viewModel.CompatibleIdsText);
    }

    [Fact]
    public void NodeTexts_WithGatingCapability_RenderValueOrNone()
    {
        var capabilities = new UsbTopologyCapabilities
        {
            Backend = UsbTopologyBackendKind.WindowsPnp,
            SupportsClassGuid = true,
            SupportsContainerId = true,
            SupportsCompatibleIds = true
        };
        var node = new UsbTopologyNode
        {
            InstanceId = "USB\\VID_18D1&PID_4EE0\\ABC",
            Kind = UsbTopologyNodeKind.Device,
            ClassGuid = TestGuid(1),
            ContainerId = TestGuid(2),
            CompatibleIds = ["USB\\Class_00&SubClass_00&Prot_00"]
        };
        var viewModel = CreateViewModel(capabilities, node);

        Assert.Equal(TestGuid(1).ToString("D"), viewModel.ClassGuidText);
        Assert.Equal(TestGuid(2).ToString("D"), viewModel.ContainerIdText);
        Assert.Equal("USB\\Class_00&SubClass_00&Prot_00", viewModel.CompatibleIdsText);

        var bare = CreateViewModel(capabilities, new UsbTopologyNode
        {
            InstanceId = "USB\\VID_18D1&PID_4EE0\\ABC",
            Kind = UsbTopologyNodeKind.Device
        });
        Assert.Equal(Localized(NoneKey), bare.ClassGuidText);
        Assert.Equal(Localized(NoneKey), bare.ContainerIdText);
        Assert.Equal(Localized(NoneKey), bare.CompatibleIdsText);
    }

    private static UsbTreeNodeViewModel CreateViewModel(UsbTopologyCapabilities capabilities,
        UsbTopologyNode node) => new(node, capabilities, [], depth: 0);

    private static Guid TestGuid(byte seed) => new($"00000000-0000-0000-0000-{seed:D12}");

    private static string Localized(string key) => LanguageService.Instance.GetString(key);
}