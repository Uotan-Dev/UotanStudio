using UotanStudio.Services.Devices;
using UotanStudio.Services.Workspace;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class DeviceShellServiceTests
{
    [Theory]
    [InlineData("device", true)]
    [InlineData("online", true)]
    [InlineData("offline", false)]
    [InlineData("unauthorized", false)]
    public void IsAdbReadyState_ServerAndDirectStates_AcceptsOnlyReady(string state, bool expected)
        => Assert.Equal(expected, DeviceShellService.IsAdbReadyState(state));

    private static DetectedDevice Target(DeviceProtocolKind protocol) => new()
    {
        Key = "target",
        Serial = "target",
        Protocol = protocol,
        Transport = DeviceTransportKind.Usb
    };

    [Theory]
    [InlineData(DeviceProtocolKind.Adb)]
    [InlineData(DeviceProtocolKind.Hdc)]
    public void RequireServerTarget_ServerAbsent_RejectsManagedShell(DeviceProtocolKind protocol)
        => Assert.Throws<NotSupportedException>(() =>
            DeviceShellService.RequireServerTarget(Target(protocol), false, false));

    [Theory]
    [InlineData(DeviceProtocolKind.Adb)]
    [InlineData(DeviceProtocolKind.Hdc)]
    public void RequireServerTarget_TargetNotReady_RejectsManagedShell(DeviceProtocolKind protocol)
        => Assert.Throws<IOException>(() =>
            DeviceShellService.RequireServerTarget(Target(protocol), true, false));

    [Theory]
    [InlineData(DeviceProtocolKind.Adb)]
    [InlineData(DeviceProtocolKind.Hdc)]
    public void RequireServerTarget_ReadyServerTarget_AllowsManagedShell(DeviceProtocolKind protocol)
        => DeviceShellService.RequireServerTarget(Target(protocol), true, true);

    [Fact]
    public void RequireServerTarget_OtherProtocol_RejectsManagedShell()
        => Assert.Throws<ArgumentException>(() =>
            DeviceShellService.RequireServerTarget(Target(DeviceProtocolKind.Fastboot), true, true));

    [Theory]
    [InlineData(DeviceProtocolKind.Adb)]
    [InlineData(DeviceProtocolKind.Hdc)]
    public void CreateDeviceShellProfile_SelectedTarget_LabelsInProcessShell(DeviceProtocolKind protocol)
    {
        var profile = WorkspacePanelService.CreateDeviceShellProfile(Target(protocol));
        Assert.Equal("Device shell", profile.Name);
        Assert.Empty(profile.Arguments);
    }
}