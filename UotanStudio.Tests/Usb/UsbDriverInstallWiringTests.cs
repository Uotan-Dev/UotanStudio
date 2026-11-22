using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Drivers;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Usb;

/// <summary>
/// Verifies the row-level wiring of the vendor driver install action: the package that covers a node is
/// resolved from the node's real reported ids, and the button only becomes available once that package is
/// known, the row keeps the action and no driver is bound yet.
/// <para>验证厂商驱动安装操作的行级接线：覆盖节点的包由节点真实上报的 ID 解析，只有在包已解析、行保留该
/// 操作且尚未绑定驱动时按钮才可用。</para>
/// </summary>
public sealed class UsbDriverInstallWiringTests
{
    private static readonly UsbTopologyCapabilities WindowsCapabilities = new()
    {
        Backend = UsbTopologyBackendKind.WindowsPnp,
        SupportsDriverInfo = true,
        SupportsDriverInfPath = true
    };

    [Fact]
    public void InstallAction_UnisocDownloadNode_ResolvesPackageAndOffersButton()
    {
        var viewModel = CreateViewModel([@"USB\VID_1782&PID_4D00"]);

        Assert.True(viewModel.ShowDriverInstallAction);
        Assert.Equal("unisoc-spreadtrum", DriverPackageCatalog.FindMatch(
            [.. viewModel.Node.HardwareIds, .. viewModel.Node.CompatibleIds])?.Id);

        viewModel.MatchedDriverPackageId = "unisoc-spreadtrum";

        Assert.True(viewModel.CanInstallDriver);
    }

    [Fact]
    public void InstallAction_UnisocAdbFunctionNode_ResolvesPackage()
    {
        var viewModel = CreateViewModel([@"USB\VID_1782&PID_5A5B&MI_01"]);

        var matched = DriverPackageCatalog.FindMatch(
            [.. viewModel.Node.HardwareIds, .. viewModel.Node.CompatibleIds]);

        Assert.Equal("unisoc-spreadtrum", matched?.Id);
    }

    [Fact]
    public void InstallAction_UnmatchedNode_KeepsButtonHidden()
    {
        var viewModel = CreateViewModel([@"USB\VID_1234&PID_5678"]);

        var matched = DriverPackageCatalog.FindMatch(
            [.. viewModel.Node.HardwareIds, .. viewModel.Node.CompatibleIds]);
        viewModel.MatchedDriverPackageId = matched?.Id;

        Assert.True(viewModel.ShowDriverInstallAction);
        Assert.Null(matched);
        Assert.False(viewModel.CanInstallDriver);
    }

    [Fact]
    public void InstallAction_AlreadyBoundDevice_KeepsButtonHidden()
    {
        var viewModel = CreateViewModel([@"USB\VID_1782&PID_4D00"]);
        viewModel.MatchedDriverPackageId = "unisoc-spreadtrum";
        viewModel.IsDriverInstalled = true;

        Assert.False(viewModel.CanInstallDriver);
    }

    [Fact]
    public void InstallAction_WithoutWindowsDriverInfo_WithholdsAction()
    {
        var capabilities = new UsbTopologyCapabilities { Backend = UsbTopologyBackendKind.LibUsb };
        var node = new UsbTopologyNode
        {
            InstanceId = @"USB\VID_1782&PID_4D00\5&2A1B3C4D&0&1",
            Kind = UsbTopologyNodeKind.Device,
            HardwareIds = [@"USB\VID_1782&PID_4D00"]
        };
        var viewModel = new UsbTreeNodeViewModel(node, capabilities, [], depth: 0)
        {
            ShowDriverInstallAction = false,
            MatchedDriverPackageId = "unisoc-spreadtrum"
        };

        Assert.False(viewModel.CanInstallDriver);
    }

    private static UsbTreeNodeViewModel CreateViewModel(IReadOnlyList<string> hardwareIds)
    {
        var node = new UsbTopologyNode
        {
            InstanceId = @"USB\VID_1782&PID_4D00\5&2A1B3C4D&0&1",
            Kind = UsbTopologyNodeKind.Device,
            HardwareIds = hardwareIds
        };
        return new UsbTreeNodeViewModel(node, WindowsCapabilities, [], depth: 0)
        {
            ShowDriverInstallAction = true
        };
    }
}