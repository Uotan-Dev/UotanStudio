using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.Linux;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class LinuxSysfsTopologyTests
{
    public static bool SupportsSysfsTree => OperatingSystem.IsLinux() &&
        Directory.Exists(LinuxSysfsTopology.DefaultDevicesRoot);

    public static bool SupportsColonInDirectoryNames => !OperatingSystem.IsWindows();

    public static bool CanCreateSymlinks => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    [Fact]
    public void ReadFlatNodes_TempSysfsTreeWithoutInterfaceDirectories_ChainsDevicesToRootHub()
    {
        using var tree = new SysfsTree();
        tree.AddDirectory("usb1",
            ("busnum", "1"),
            ("devnum", "1"),
            ("speed", "480"),
            ("bDeviceClass", "09"),
            ("product", "xHCI Host Controller"),
            ("manufacturer", "Linux 6.9 xhci-hcd"),
            ("idVendor", "0000"),
            ("idProduct", "0000"),
            ("bcdDevice", "1.10"),
            ("bNumConfigurations", "1"),
            ("driver", "usb"));
        tree.AddDirectory("1-2",
            ("busnum", "1"),
            ("devnum", "13"),
            ("idVendor", "22d9"),
            ("idProduct", "2769"),
            ("bcdDevice", "5.04"),
            ("serial", "UOTAN1234567"),
            ("manufacturer", "Uotan Device"),
            ("product", "Uotan ADB Interface Device"),
            ("speed", "480"),
            ("bDeviceClass", "00"),
            ("bNumConfigurations", "2"),
            ("driver", "usb"));
        tree.AddDirectory("1-2.3",
            ("busnum", "1"),
            ("devnum", "14"),
            ("idVendor", "04e5"),
            ("idProduct", "0610"),
            ("speed", "12"),
            ("bDeviceClass", "09"),
            ("driver", "hub"),
            ("product", "USB Hub"));

        var nodes = ShapeAll(LinuxSysfsTopology.ReadFlatNodes(tree.Root));
        var rooted = UsbTopologyTree.Flatten(UsbTopologyTree.Build(nodes));
        var rootHub = Entry(nodes, "usb1");
        var device = Entry(nodes, "1-2");
        var nested = Entry(nodes, "1-2.3");

        Assert.Equal(3, nodes.Count);
        Assert.Equal(nodes.Count, rooted.Count);
        Assert.Same(rootHub, rooted[0]);
        Assert.Same(device, rooted[1]);
        Assert.Same(nested, rooted[2]);
        Assert.Null(rootHub.ParentInstanceId);
        Assert.Equal(rootHub.InstanceId, device.ParentInstanceId);
        Assert.Equal(device.InstanceId, nested.ParentInstanceId);
        Assert.Equal(UsbTopologyNodeKind.RootHub, rootHub.Kind);
        Assert.Equal(UsbTopologyNodeKind.Device, device.Kind);
        Assert.Equal(UsbTopologyNodeKind.Hub, nested.Kind);
        Assert.Equal((ushort)0x22D9, device.VendorId);
        Assert.Equal((ushort)0x2769, device.ProductId);
        Assert.Equal("5.04", device.Revision);
        Assert.Equal("UOTAN1234567", device.SerialNumber);
        Assert.Equal("Uotan Device", device.ManufacturerName);
        Assert.Equal("Uotan ADB Interface Device", device.Description);
        Assert.Equal("usb", device.ClassName);
        Assert.Equal("usb", device.DriverService);
        Assert.Equal(UsbDeviceSpeed.High, device.Speed);
        Assert.Equal(UsbDeviceSpeed.Full, nested.Speed);
        Assert.Equal(["usb:v22D9p2769"], device.HardwareIds);
        Assert.Contains("usbfs:/dev/bus/usb/001/013", device.LocationPaths);
        Assert.DoesNotContain(device.LocationPaths,
            path => path.Contains("bNumConfigurations", StringComparison.Ordinal));
        Assert.Null(device.InterfaceNumber);
        Assert.Null(device.Protocol);
    }

    [Fact]
    public void ReadFlatNodes_TempSysfsTreeWithoutAttributeFiles_DegradesToNullsWithoutThrowing()
    {
        using var tree = new SysfsTree();
        tree.AddDirectory("usb1");
        tree.AddDirectory("1-2");
        tree.AddDirectory("1-2.3");

        var nodes = ShapeAll(LinuxSysfsTopology.ReadFlatNodes(tree.Root));
        var rooted = UsbTopologyTree.Flatten(UsbTopologyTree.Build(nodes));
        var rootHub = Entry(nodes, "usb1");
        var device = Entry(nodes, "1-2");
        var nested = Entry(nodes, "1-2.3");

        Assert.Equal(3, nodes.Count);
        Assert.Equal(nodes.Count, rooted.Count);
        Assert.Same(rootHub, rooted[0]);
        Assert.Same(device, rooted[1]);
        Assert.Same(nested, rooted[2]);
        Assert.Null(rootHub.ParentInstanceId);
        Assert.Equal(rootHub.InstanceId, device.ParentInstanceId);
        Assert.Equal(device.InstanceId, nested.ParentInstanceId);
        Assert.Equal(UsbTopologyNodeKind.RootHub, rootHub.Kind);
        Assert.Equal(UsbTopologyNodeKind.Device, device.Kind);
        Assert.Null(device.VendorId);
        Assert.Null(device.ProductId);
        Assert.Null(device.Revision);
        Assert.Null(device.SerialNumber);
        Assert.Null(device.Description);
        Assert.Null(device.ManufacturerName);
        Assert.Null(device.ClassName);
        Assert.Null(device.DriverService);
        Assert.Null(device.InterfaceNumber);
        Assert.Null(device.InterfaceClass);
        Assert.Null(device.InterfaceSubClass);
        Assert.Null(device.InterfaceProtocol);
        Assert.Null(device.Protocol);
        Assert.Empty(device.LocationPaths);
        Assert.Equal(UsbDeviceSpeed.Unknown, device.Speed);
    }

    [Fact]
    public void ReadFlatNodes_SysfsRootHoldingForeignDirectoryNames_ReturnsNoNodes()
    {
        using var tree = new SysfsTree();
        tree.AddDirectory("notes");
        tree.AddDirectory("usb");
        tree.AddDirectory("usb-port");
        tree.AddDirectory("1-");
        tree.AddDirectory("1-0x");

        Assert.Empty(LinuxSysfsTopology.ReadFlatNodes(tree.Root));
    }

    [Fact]
    public void ReadFlatNodes_AbsentSysfsRoot_ReturnsNoNodes()
    {
        var missing = Path.Combine(Path.GetTempPath(), "linux-sysfs-" + Guid.NewGuid().ToString("N"));

        Assert.False(Directory.Exists(missing));
        Assert.Empty(LinuxSysfsTopology.ReadFlatNodes(missing));
    }

    [Fact]
    public void ReadFlatNodes_SysfsTreeWithInterfaceDirectories_ChainsRootHubDeviceAndInterface()
    {
        Assert.SkipUnless(SupportsColonInDirectoryNames,
            "Windows cannot create directory names holding the sysfs interface separator ':', so interface layering is asserted through the pure shaping tests on that platform.");
        using var tree = new SysfsTree();
        tree.AddDirectory("usb1",
            ("busnum", "1"),
            ("devnum", "1"),
            ("speed", "480"),
            ("bDeviceClass", "09"),
            ("product", "xHCI Host Controller"),
            ("driver", "usb"));
        tree.AddDirectory("1-2",
            ("busnum", "1"),
            ("devnum", "13"),
            ("idVendor", "22d9"),
            ("idProduct", "2769"),
            ("bcdDevice", "5.04"),
            ("serial", "UOTAN1234567"),
            ("manufacturer", "Uotan Device"),
            ("product", "Uotan ADB Interface Device"),
            ("speed", "480"),
            ("bDeviceClass", "00"),
            ("bNumConfigurations", "2"),
            ("driver", "usb"));
        tree.AddDirectory("1-2:1.0",
            ("bInterfaceClass", "ff"),
            ("bInterfaceSubClass", "42"),
            ("bInterfaceProtocol", "01"),
            ("interface", "ADB Interface"),
            ("driver", "usbfs"));

        var nodes = ShapeAll(LinuxSysfsTopology.ReadFlatNodes(tree.Root));
        var rooted = UsbTopologyTree.Flatten(UsbTopologyTree.Build(nodes));
        var rootHub = Entry(nodes, "usb1");
        var device = Entry(nodes, "1-2");
        var function = Entry(nodes, "1-2:1.0");

        Assert.Equal(3, nodes.Count);
        Assert.Equal(nodes.Count, rooted.Count);
        Assert.Same(rootHub, rooted[0]);
        Assert.Same(device, rooted[1]);
        Assert.Same(function, rooted[2]);
        Assert.Equal(device.InstanceId, function.ParentInstanceId);
        Assert.Equal(UsbTopologyNodeKind.Function, function.Kind);
        Assert.Equal((byte)0, function.InterfaceNumber);
        Assert.Equal((byte?)0xFF, function.InterfaceClass);
        Assert.Equal((byte?)0x42, function.InterfaceSubClass);
        Assert.Equal((byte?)0x01, function.InterfaceProtocol);
        Assert.Equal((ushort)0x22D9, function.VendorId);
        Assert.Equal((ushort)0x2769, function.ProductId);
        Assert.Equal("UOTAN1234567", function.SerialNumber);
        Assert.Equal(UsbDeviceSpeed.High, function.Speed);
        Assert.Equal("ADB Interface", function.Description);
        Assert.Equal("usbfs", function.ClassName);
        Assert.Equal("adb", function.Protocol);
    }

    [Fact]
    public void Shape_InterfaceWithAdbTriple_ClassifiesAdbProtocol()
    {
        var device = new LinuxSysfsEntry
        {
            InstanceId = "/sys/bus/usb/devices/1-2",
            Name = "1-2",
            IdVendor = "22d9",
            IdProduct = "2769",
            BcdDevice = "5.04",
            Serial = "UOTAN1234567",
            Speed = "480"
        };
        var function = new LinuxSysfsEntry
        {
            InstanceId = "/sys/bus/usb/devices/1-2:1.0",
            Name = "1-2:1.0",
            ParentInstanceId = device.InstanceId,
            BInterfaceClass = "ff",
            BInterfaceSubClass = "42",
            BInterfaceProtocol = "01",
            InterfaceDescription = "ADB Interface"
        };

        var shaped = Shape(function, device);

        Assert.Equal(UsbTopologyNodeKind.Function, shaped.Kind);
        Assert.Equal((byte)0, shaped.InterfaceNumber);
        Assert.Equal((byte?)0xFF, shaped.InterfaceClass);
        Assert.Equal((byte?)0x42, shaped.InterfaceSubClass);
        Assert.Equal((byte?)0x01, shaped.InterfaceProtocol);
        Assert.Equal((ushort)0x22D9, shaped.VendorId);
        Assert.Equal("adb", shaped.Protocol);
        Assert.Equal(UsbDeviceSpeed.High, shaped.Speed);
        Assert.Equal(["usb:v22D9p2769icFFisc42ip01in00"], shaped.HardwareIds);
        Assert.Equal(["usb:v22D9p2769icFFisc42ip01"], shaped.CompatibleIds);
    }

    [Fact]
    public void Shape_InterfaceWithoutParentIdentity_LeavesVendorAndProtocolNull()
    {
        var function = new LinuxSysfsEntry
        {
            InstanceId = "/sys/bus/usb/devices/4-1:2.7",
            Name = "4-1:2.7",
            BInterfaceClass = "ff",
            BInterfaceSubClass = "42",
            BInterfaceProtocol = "01"
        };

        var shaped = Shape(function);

        Assert.Equal(UsbTopologyNodeKind.Function, shaped.Kind);
        Assert.Null(shaped.VendorId);
        Assert.Null(shaped.Protocol);
        Assert.Equal((byte)7, shaped.InterfaceNumber);
    }

    [Fact]
    public void Shape_DeviceClassNine_YieldsHubKind()
    {
        var hub = Shape(new LinuxSysfsEntry
        {
            InstanceId = "/sys/bus/usb/devices/2-1",
            Name = "2-1",
            IdVendor = "04e5",
            IdProduct = "0610",
            BDeviceClass = "09"
        });

        Assert.Equal(UsbTopologyNodeKind.Hub, hub.Kind);
    }

    [Fact]
    public void Shape_BoundHubModuleOnClassZeroDevice_YieldsHubKind()
    {
        var hub = Shape(new LinuxSysfsEntry
        {
            InstanceId = "/sys/bus/usb/devices/2-1",
            Name = "2-1",
            IdVendor = "2109",
            IdProduct = "2817",
            BDeviceClass = "00",
            Driver = "hub"
        });

        Assert.Equal(UsbTopologyNodeKind.Hub, hub.Kind);
    }

    [Fact]
    public void Shape_ClassZeroDeviceWithStorageModule_YieldsDeviceKind()
    {
        var device = Shape(new LinuxSysfsEntry
        {
            InstanceId = "/sys/bus/usb/devices/3-1",
            Name = "3-1",
            IdVendor = "13fe",
            IdProduct = "5400",
            BDeviceClass = "00",
            Driver = "uas"
        });

        Assert.Equal(UsbTopologyNodeKind.Device, device.Kind);
        Assert.Equal("uas", device.ClassName);
    }

    [Fact]
    public void Shape_InterfaceOfHubDevice_KeepsFunctionRoleAndStorageTriple()
    {
        var device = new LinuxSysfsEntry
        {
            InstanceId = "/sys/bus/usb/devices/1-3",
            Name = "1-3",
            IdVendor = "058f",
            IdProduct = "6387",
            BDeviceClass = "09",
            Speed = "480"
        };
        var function = new LinuxSysfsEntry
        {
            InstanceId = "/sys/bus/usb/devices/1-3:1.0",
            Name = "1-3:1.0",
            ParentInstanceId = device.InstanceId,
            BInterfaceClass = "08",
            BInterfaceSubClass = "06",
            BInterfaceProtocol = "50"
        };

        var hub = Shape(device);
        var massStorage = Shape(function, device);

        Assert.Equal(UsbTopologyNodeKind.Hub, hub.Kind);
        Assert.Equal(["usb:v058Fp6387"], hub.HardwareIds);
        Assert.Equal(UsbTopologyNodeKind.Function, massStorage.Kind);
        Assert.Equal((byte?)0x08, massStorage.InterfaceClass);
        Assert.Equal((ushort)0x058F, massStorage.VendorId);
        Assert.Equal(UsbDeviceSpeed.High, massStorage.Speed);
        Assert.Null(massStorage.Protocol);
        Assert.Equal(["usb:v058Fp6387ic08isc06ip50in00"], massStorage.HardwareIds);
    }

    [Fact]
    public void Shape_HostControllerEntry_ReportsPciIdentityAndBoundModule()
    {
        var controller = Shape(new LinuxSysfsEntry
        {
            InstanceId = "/sys/devices/pci0000:00/0000:00:14.0",
            Name = "0000:00:14.0",
            IdVendor = "0x8086",
            IdProduct = "0x10c0",
            BDeviceClass = "0x0c0330",
            Driver = "xhci_pci"
        });

        Assert.Equal(UsbTopologyNodeKind.HostController, controller.Kind);
        Assert.Equal((ushort)0x8086, controller.VendorId);
        Assert.Equal((ushort)0x10C0, controller.ProductId);
        Assert.Equal("xhci_pci", controller.ClassName);
        Assert.Equal("xhci_pci", controller.DriverService);
        Assert.Equal(["pci:v00008086d000010C0"], controller.HardwareIds);
        Assert.Equal(UsbDeviceSpeed.Unknown, controller.Speed);
    }

    [Fact]
    public void Shape_InterfaceClassTextWiderThanOneByte_LeavesInterfaceClassNull()
    {
        var function = Shape(new LinuxSysfsEntry
        {
            InstanceId = "/sys/bus/usb/devices/1-2:1.0",
            Name = "1-2:1.0",
            BInterfaceClass = "0x0c0330"
        });

        Assert.Equal(UsbTopologyNodeKind.Function, function.Kind);
        Assert.Null(function.InterfaceClass);
        Assert.Empty(function.HardwareIds);
    }

    [Fact]
    public void Shape_RootHubCarryingHubDeviceClass_KeepsRootHubRole()
    {
        var rootHub = Shape(new LinuxSysfsEntry
        {
            InstanceId = "/sys/bus/usb/devices/usb1",
            Name = "usb1",
            BDeviceClass = "09",
            Speed = "5000"
        });

        Assert.Equal(UsbTopologyNodeKind.RootHub, rootHub.Kind);
        Assert.Equal(UsbDeviceSpeed.Super, rootHub.Speed);
    }

    [Theory]
    [InlineData("1.5", UsbDeviceSpeed.Low)]
    [InlineData("12", UsbDeviceSpeed.Full)]
    [InlineData("480", UsbDeviceSpeed.High)]
    [InlineData("5000", UsbDeviceSpeed.Super)]
    [InlineData("10000", UsbDeviceSpeed.SuperPlus)]
    [InlineData("40000", UsbDeviceSpeed.SuperPlus)]
    [InlineData("2", UsbDeviceSpeed.Low)]
    [InlineData("12.5", UsbDeviceSpeed.High)]
    [InlineData("0", UsbDeviceSpeed.Unknown)]
    [InlineData("-12", UsbDeviceSpeed.Unknown)]
    [InlineData("", UsbDeviceSpeed.Unknown)]
    [InlineData("not-a-rate", UsbDeviceSpeed.Unknown)]
    [InlineData(null, UsbDeviceSpeed.Unknown)]
    public void MapSpeed_KnownAndMalformedSysfsRates_MapToDocumentedBuckets(string? text,
        UsbDeviceSpeed expected)
        => Assert.Equal(expected, LinuxSysfsTopology.MapSpeed(text));

    [Theory]
    [InlineData("usb1", null)]
    [InlineData("usb20", null)]
    [InlineData("1-2", "usb1")]
    [InlineData("2-1", "usb2")]
    [InlineData("1-2.3", "1-2")]
    [InlineData("2-1.4.2", "2-1.4")]
    [InlineData("1-2:1.0", "1-2")]
    [InlineData("usb1:1.0", "usb1")]
    [InlineData("1:1.0", "usb1")]
    [InlineData("0000:00:14.0", null)]
    public void ParentDirectoryName_SysfsNamingConvention_DerivesExpectedParent(string name, string? expected)
        => Assert.Equal(expected, LinuxSysfsTopology.ParentDirectoryName(name));

    [Theory]
    [InlineData("usb1")]
    [InlineData("usb20")]
    [InlineData("1-2")]
    [InlineData("2-1.4.2")]
    [InlineData("1-2:1.0")]
    [InlineData("usb1:1.0")]
    [InlineData("1:1.0")]
    public void IsUsbDirectoryName_SysfsDeviceAndInterfaceNames_ReportsTrue(string name)
        => Assert.True(LinuxSysfsTopology.IsUsbDirectoryName(name));

    [Theory]
    [InlineData("notes")]
    [InlineData("usb")]
    [InlineData("usb-port")]
    [InlineData("1-")]
    [InlineData("1-0x")]
    [InlineData("1-2:")]
    [InlineData("0000:00:14.0")]
    [InlineData("00:14.0")]
    [InlineData("hub")]
    public void IsUsbDirectoryName_ForeignOrIncompleteNames_ReportsFalse(string name)
        => Assert.False(LinuxSysfsTopology.IsUsbDirectoryName(name));

    [Theory]
    [InlineData("0000:00:14.0", true)]
    [InlineData("00:14.0", true)]
    [InlineData("1-2:1.0", false)]
    [InlineData("usb1:1.0", false)]
    [InlineData("pci0000:00", false)]
    [InlineData("3f100000.usb", false)]
    public void IsPciDirectoryName_FunctionSlotsAndForeignNames_ReportsMembership(string name, bool expected)
        => Assert.Equal(expected, LinuxSysfsTopology.IsPciDirectoryName(name));

    [Fact]
    public void PciAncestorChain_ResolvedPathBelowPciBridge_ReturnsConsecutiveFunctions()
    {
        var chain = LinuxSysfsTopology.PciAncestorChain(
            "/sys/devices/pci0000:00/0000:00:1c.0/0000:01:00.0/usb2");

        Assert.Equal(
            [
                "/sys/devices/pci0000:00/0000:00:1c.0",
                "/sys/devices/pci0000:00/0000:00:1c.0/0000:01:00.0"
            ],
            chain);
    }

    [Fact]
    public void PciAncestorChain_PlatformControllerPath_ReturnsNoFunctions()
        => Assert.Empty(LinuxSysfsTopology.PciAncestorChain(
            "/sys/devices/platform/soc/3f100000.usb/usb1"));

    [Fact]
    public void ReadDriverName_RegularFileHoldingModuleName_ReportsModuleWithoutSymlink()
    {
        using var tree = new SysfsTree();
        var directory = tree.AddDirectory("1-2", ("driver", "uas"));

        Assert.Equal("uas", LinuxSysfsTopology.ReadDriverName(directory));
    }

    [Fact]
    public void ReadDriverName_DirectoryWithoutDriverEntry_ReportsNull()
    {
        using var tree = new SysfsTree();
        var directory = tree.AddDirectory("1-2", ("idVendor", "22d9"));

        Assert.Null(LinuxSysfsTopology.ReadDriverName(directory));
    }

    [Fact]
    public void ReadDriverName_DriverSymlinkIntoBusDirectory_ReportsTargetBasename()
    {
        Assert.SkipUnless(CanCreateSymlinks,
            "Symbolic links can only be created on a POSIX host; the regular-file driver form is covered by ReadDriverName_RegularFileHoldingModuleName_ReportsModuleWithoutSymlink.");
        using var tree = new SysfsTree();
        var module = tree.AddDirectory(Path.Combine("drivers", "uas"));
        var device = tree.AddDirectory("1-2", ("idVendor", "22d9"));
        var linkPath = Path.Combine(device, "driver");

        Assert.True(TryCreateSymlink(linkPath, module));
        Assert.NotNull(new DirectoryInfo(linkPath).LinkTarget);
        Assert.Equal("uas", LinuxSysfsTopology.ReadDriverName(device));
    }

    [Fact]
    public void IsAvailable_NonLinuxHost_ReportsFalse()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(),
            "The Linux availability rule is already exercised by the real sysfs smoke test.");

        Assert.False(LinuxSysfsTopologyProvider.Instance.IsAvailable);
    }

    [Fact]
    public void ReadFlatNodes_RealSysfsTree_KeepsEveryNodeInBuiltTree()
    {
        Assert.SkipUnless(SupportsSysfsTree,
            "A Linux host with a readable /sys/bus/usb/devices tree is required.");
        var nodes = ShapeAll(LinuxSysfsTopology.ReadFlatNodes(LinuxSysfsTopology.DefaultDevicesRoot));
        var rooted = UsbTopologyTree.Flatten(UsbTopologyTree.Build(nodes));

        Assert.Equal(nodes.Count, rooted.Count);
        Assert.All(nodes, node => Assert.False(string.IsNullOrWhiteSpace(node.InstanceId)));
        Assert.Equal(nodes.Count, nodes.Select(node => node.InstanceId).Distinct(StringComparer.Ordinal).Count());
    }

    private static IReadOnlyList<UsbTopologyNode> ShapeAll(IReadOnlyList<UsbTopologyNode> rawNodes)
        => [.. rawNodes.Select(UsbTopologyNodes.Create)];

    private static UsbTopologyNode Shape(LinuxSysfsEntry entry, LinuxSysfsEntry? parent = null)
        => UsbTopologyNodes.Create(LinuxSysfsTopology.Shape(entry, parent));

    private static UsbTopologyNode Entry(IReadOnlyList<UsbTopologyNode> nodes, string name)
        => nodes.First(node => ToPortablePath(node.InstanceId)
            .EndsWith("/" + name, StringComparison.Ordinal));

    private static string ToPortablePath(string path) => path.Replace('\\', '/');

    private static bool TryCreateSymlink(string linkPath, string target)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic link creation is unavailable here: " + error.Message);
            return false;
        }
    }

    private sealed class SysfsTree : IDisposable
    {
        internal SysfsTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "linux-sysfs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal string AddDirectory(string name, params (string Attribute, string Value)[] attributes)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            foreach (var (attribute, value) in attributes)
            {
                Write(path, attribute, value);
            }

            return path;
        }

        internal void Write(string directory, string name, string value)
            => File.WriteAllText(Path.Combine(directory, name), value + Environment.NewLine);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}