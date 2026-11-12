using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Shell;

/// <summary>
/// Verifies the row states of a USB tree node that offer the device access configuration and vendor driver
/// installation actions: the install action stays available while no driver is bound and a bundled package
/// covers the device, and it disappears as soon as a driver is bound.
/// <para>验证 USB 树节点提供设备访问配置与厂商驱动安装操作的行状态：设备尚未绑定驱动且捆绑包覆盖时保留
/// 安装操作，一旦绑定驱动即消失。</para>
/// </summary>
public sealed class UsbTreeNodeDriverActionTests
{
    private static UsbTreeNodeViewModel CreateNode()
    {
        var node = new UsbTopologyNode
        {
            InstanceId = "USB\\VID_2207&PID_350B\\5&1A2B3C4D&0&1",
            Kind = UsbTopologyNodeKind.Device,
            HardwareIds = ["USB\\VID_2207&PID_350B&REV_0302", "USB\\VID_2207&PID_350B"]
        };
        var capabilities = new UsbTopologyCapabilities { Backend = UsbTopologyBackendKind.WindowsPnp };
        return new UsbTreeNodeViewModel(node, capabilities, [], depth: 0);
    }

    [Fact]
    public void CanInstallDriver_DriverlessNodeWithMatchedPackage_IsTrue()
    {
        var node = CreateNode();
        node.ShowDriverInstallAction = true;
        node.IsDriverInstalled = false;
        node.MatchedDriverPackageId = "rockusb";

        Assert.True(node.CanInstallDriver);
        Assert.False(node.CanConfigureDevice);
    }

    [Fact]
    public void CanInstallDriver_NodeWithBoundDriver_IsFalse()
    {
        var node = CreateNode();
        node.ShowDriverInstallAction = true;
        node.IsDriverInstalled = true;
        node.MatchedDriverPackageId = "rockusb";

        Assert.False(node.CanInstallDriver);
    }

    [Fact]
    public void CanInstallDriver_NodeWithoutMatchedPackageOrAction_IsFalse()
    {
        var node = CreateNode();
        node.ShowDriverInstallAction = true;
        node.IsDriverInstalled = false;
        node.MatchedDriverPackageId = null;
        Assert.False(node.CanInstallDriver);

        node.MatchedDriverPackageId = "rockusb";
        node.ShowDriverInstallAction = false;
        Assert.False(node.CanInstallDriver);
        Assert.False(node.CanConfigureDevice);
    }

    [Fact]
    public void CanConfigureDevice_ConfiguredNodeWithDriverInstallAction_KeepsIndependentStates()
    {
        var node = CreateNode();
        node.ShowDriverAction = true;
        node.ShowDriverInstallAction = true;
        node.IsDriverInstalled = true;
        node.MatchedDriverPackageId = "rockusb";

        Assert.False(node.CanInstallDriver);
        Assert.True(node.CanConfigureDevice);

        node.IsConfigured = true;
        Assert.False(node.CanConfigureDevice);
        Assert.True(node.CanUnconfigureDevice);
        Assert.False(node.CanInstallDriver);
    }
}