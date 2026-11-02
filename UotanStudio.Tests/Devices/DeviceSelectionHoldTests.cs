using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Checks that a scan keeps the selected device across a mode switch that replaces its endpoint, so a Rockchip
/// maskrom board loading usbplug never moves the selection to an unrelated device.
/// <para>校验扫描在替换设备端点的模式切换中保持选中设备，使瑞芯微 maskrom 板加载 usbplug 时不会把选择移到
/// 无关设备上。</para>
/// </summary>
public class DeviceSelectionHoldTests
{
    [Fact]
    public void ApplySelectionAfterScan_SelectedEndpointStillPresent_KeepsSelection()
    {
        using var viewModel = new DeviceViewModel(monitorDevices: false);
        var target = Create("usb:maskrom", "maskrom");
        var other = Create("usb:unrelated", "device");
        viewModel.Devices.Add(target);
        viewModel.Devices.Add(other);
        viewModel.SelectedDevice = target;

        viewModel.ApplySelectionAfterScan(target.Key);

        Assert.Same(target, viewModel.SelectedDevice);
    }

    [Fact]
    public void ApplySelectionAfterScan_SelectedEndpointGoneWithoutHold_FallsBackToFirstDevice()
    {
        using var viewModel = new DeviceViewModel(monitorDevices: false);
        var other = Create("usb:unrelated", "device");
        viewModel.Devices.Add(other);
        viewModel.SelectedDevice = Create("usb:maskrom", "maskrom");

        viewModel.ApplySelectionAfterScan("usb:maskrom");

        Assert.Same(other, viewModel.SelectedDevice);
    }

    [Fact]
    public void ApplySelectionAfterScan_SelectedEndpointGoneDuringHold_KeepsTheSwitchingSelection()
    {
        using var viewModel = new DeviceViewModel(monitorDevices: false);
        var target = Create("usb:maskrom", "maskrom");
        var other = Create("usb:unrelated", "device");
        viewModel.Devices.Add(target);
        viewModel.Devices.Add(other);
        viewModel.SelectedDevice = target;
        viewModel.BeginSelectionHold();
        viewModel.Devices.Remove(target);

        viewModel.ApplySelectionAfterScan(target.Key);

        Assert.Same(target, viewModel.SelectedDevice);
    }

    [Fact]
    public void ApplySelectionAfterScan_ReplacementEnumeratedDuringHold_SelectsTheReplacement()
    {
        using var viewModel = new DeviceViewModel(monitorDevices: false);
        var target = Create("usb:maskrom", "maskrom");
        var other = Create("usb:unrelated", "device");
        viewModel.Devices.Add(target);
        viewModel.Devices.Add(other);
        viewModel.SelectedDevice = target;
        viewModel.BeginSelectionHold();
        viewModel.Devices.Remove(target);
        viewModel.ApplySelectionAfterScan(target.Key);
        var replacement = Create("usb:usbplug", "usbplug");
        viewModel.Devices.Add(replacement);

        viewModel.ApplySelectionAfterScan(target.Key);

        Assert.Same(replacement, viewModel.SelectedDevice);
    }

    [Fact]
    public void ApplySelectionAfterScan_TwoEndpointsAppearDuringHold_KeepsWaiting()
    {
        using var viewModel = new DeviceViewModel(monitorDevices: false);
        var target = Create("usb:maskrom", "maskrom");
        viewModel.Devices.Add(target);
        viewModel.SelectedDevice = target;
        viewModel.BeginSelectionHold();
        viewModel.Devices.Remove(target);
        viewModel.ApplySelectionAfterScan(target.Key);
        viewModel.Devices.Add(Create("usb:usbplug", "usbplug"));
        viewModel.Devices.Add(Create("usb:unrelated", "device"));

        viewModel.ApplySelectionAfterScan(target.Key);

        Assert.Same(target, viewModel.SelectedDevice);
    }

    [Fact]
    public void ApplySelectionAfterScan_HoldExpiredWithoutReplacement_FallsBackToFirstDevice()
    {
        var clock = new ManualClock();
        using var viewModel = new DeviceViewModel(monitorDevices: false, clock: clock);
        var target = Create("usb:maskrom", "maskrom");
        var other = Create("usb:unrelated", "device");
        viewModel.Devices.Add(target);
        viewModel.Devices.Add(other);
        viewModel.SelectedDevice = target;
        viewModel.BeginSelectionHold();
        viewModel.Devices.Remove(target);
        clock.Advance(TimeSpan.FromSeconds(31));

        viewModel.ApplySelectionAfterScan(target.Key);

        Assert.Same(other, viewModel.SelectedDevice);
    }

    [Fact]
    public void SelectDevice_AfterHoldWasArmed_EndsTheHoldSoLaterScansSelectNormally()
    {
        using var viewModel = new DeviceViewModel(monitorDevices: false);
        var target = Create("usb:maskrom", "maskrom");
        var other = Create("usb:unrelated", "device");
        viewModel.Devices.Add(target);
        viewModel.Devices.Add(other);
        viewModel.SelectedDevice = target;
        viewModel.BeginSelectionHold();
        viewModel.SelectDevice(other);
        viewModel.Devices.Remove(target);

        viewModel.ApplySelectionAfterScan(target.Key);

        Assert.Same(other, viewModel.SelectedDevice);
    }

    [Fact]
    public void ApplySelectionAfterScan_NoSelection_SelectsTheFirstDevice()
    {
        using var viewModel = new DeviceViewModel(monitorDevices: false);
        var first = Create("usb:first", "device");
        viewModel.Devices.Add(first);
        viewModel.Devices.Add(Create("usb:second", "fastboot"));

        viewModel.ApplySelectionAfterScan(null);

        Assert.Same(first, viewModel.SelectedDevice);
    }

    private static DeviceInfo Create(string key, string state) => new(new DetectedDevice
    {
        Key = key,
        Serial = key,
        Protocol = DeviceProtocolKind.Rkdl,
        Transport = DeviceTransportKind.Usb,
        State = state
    });

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}