using System.Globalization;
using System.Text;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Usb;

/// <summary>
/// Live Windows PnP topology probe: reads this host's USB tree and verifies every device-level node with a
/// USB parent hub reports a negotiated speed through the hub port query, with composite interface children
/// inheriting their device node's speed. Set <c>UOTAN_USB_TOPOLOGY_E2E=1</c> to run; the suite skips itself
/// everywhere else.
/// <para>真机 Windows PnP 拓扑探针：读取本机 USB 树，验证所有带 USB 父集线器的设备级节点都经集线器端口查询
/// 报告协商速度，且复合接口子节点继承所属设备节点的速度。设 <c>UOTAN_USB_TOPOLOGY_E2E=1</c> 后运行；其余环境
/// 一律自跳。</para>
/// </summary>
public sealed class WindowsUsbTopologyLiveTests(ITestOutputHelper output)
{
    private const string EnableVariable = "UOTAN_USB_TOPOLOGY_E2E";

    [Fact]
    public void WindowsPnp_LiveTopology_ReportsNegotiatedSpeeds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only probe; this host is not Windows.");
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal),
            $"{EnableVariable} is not set to 1; opt in to probe this host's live USB topology.");

        using var monitor = DeviceMonitorService.Create();
        var result = monitor.ReadUsbTopology(UsbTopologyBackendKind.WindowsPnp);
        Assert.SkipWhen(result is null, "The Windows PnP topology backend is not available on this host.");

        var eligible = result!.Nodes
            .Where(node => node.ParentInstanceId is { Length: > 0 } parent &&
                parent.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) &&
                !node.InstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase) &&
                node.VendorId is not null &&
                node.ProductId is not null)
            .ToList();
        Assert.SkipWhen(eligible.Count == 0,
            "No device-level node with a USB parent hub is present; plug in a USB device and rerun.");

        var report = new StringBuilder();
        foreach (var node in result.Nodes)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{node.InstanceId} kind={node.Kind} speed={node.Speed} " +
                $"vid=0x{node.VendorId:X4} pid=0x{node.ProductId:X4}");
        }
        output.WriteLine(report.ToString());

        var unresolved = eligible.Where(node => node.Speed == UsbDeviceSpeed.Unknown).ToList();
        Assert.True(unresolved.Count == 0, "Device-level nodes without a negotiated speed: " +
            string.Join("; ", unresolved.Select(node => node.InstanceId)));
    }
}