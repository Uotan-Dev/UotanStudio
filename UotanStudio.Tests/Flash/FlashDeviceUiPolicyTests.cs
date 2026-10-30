using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

public sealed class FlashDeviceUiPolicyTests
{
    private static readonly FlashDeviceMode[] SelectedModes =
        [FlashDeviceMode.Shared, FlashDeviceMode.Edl, FlashDeviceMode.Mtk, FlashDeviceMode.Rkdl];

    [Fact]
    public void ModeOf_SelectedProtocol_ReturnsModeThatOffersItsSection()
    {
        Assert.Equal(FlashDeviceMode.None, FlashDeviceUiPolicy.ModeOf(null));
        Assert.Equal(FlashDeviceMode.Shared, FlashDeviceUiPolicy.ModeOf(DeviceProtocolKind.Fastboot));
        Assert.Equal(FlashDeviceMode.Shared, FlashDeviceUiPolicy.ModeOf(DeviceProtocolKind.Adb));
        Assert.Equal(FlashDeviceMode.Shared, FlashDeviceUiPolicy.ModeOf(DeviceProtocolKind.Hdc));
        Assert.Equal(FlashDeviceMode.Shared, FlashDeviceUiPolicy.ModeOf(DeviceProtocolKind.Ios));
        Assert.Equal(FlashDeviceMode.Edl, FlashDeviceUiPolicy.ModeOf(DeviceProtocolKind.Edl));
        Assert.Equal(FlashDeviceMode.Mtk, FlashDeviceUiPolicy.ModeOf(DeviceProtocolKind.Mtk));
        Assert.Equal(FlashDeviceMode.Rkdl, FlashDeviceUiPolicy.ModeOf(DeviceProtocolKind.Rkdl));
    }

    [Fact]
    public void IsAvailable_NoDeviceSelected_DisablesEveryGatedControl()
    {
        Assert.NotEmpty(FlashDeviceUiPolicy.GatedControls);

        foreach (var name in FlashDeviceUiPolicy.GatedControls)
        {
            Assert.False(FlashDeviceUiPolicy.IsAvailable(name, FlashDeviceMode.None));
        }
    }

    [Fact]
    public void IsAvailable_EverySelectedMode_EnablesSomeGatedControl()
    {
        foreach (var mode in SelectedModes)
        {
            Assert.Contains(FlashDeviceUiPolicy.GatedControls,
                name => FlashDeviceUiPolicy.IsAvailable(name, mode));
        }
    }

    [Fact]
    public void IsAvailable_SharedControls_StayAvailableInEverySelectedMode()
    {
        string[] shared = ["PackagePathBox", "PartitionGrid", "FlashSelectedButton", "RebootMenuButton"];

        foreach (var name in shared)
        {
            Assert.False(FlashDeviceUiPolicy.IsAvailable(name, FlashDeviceMode.None));
            foreach (var mode in SelectedModes)
            {
                Assert.True(FlashDeviceUiPolicy.IsAvailable(name, mode), $"{name} in {mode}");
            }
        }
    }

    [Fact]
    public void IsAvailable_ProtocolSection_IsOfferedOnlyToItsOwnMode()
    {
        var sections = new (string Name, FlashDeviceMode Owner)[]
        {
            ("EdlPane", FlashDeviceMode.Edl),
            ("MtkPane", FlashDeviceMode.Mtk),
            ("RkPane", FlashDeviceMode.Rkdl)
        };

        foreach (var (name, owner) in sections)
        {
            foreach (var mode in SelectedModes)
            {
                Assert.Equal(mode == owner, FlashDeviceUiPolicy.IsAvailable(name, mode));
            }

            Assert.False(FlashDeviceUiPolicy.IsAvailable(name, FlashDeviceMode.None));
        }
    }

    [Fact]
    public void GatedControls_ListEveryControlOnceAndEachStaysReachableInSomeMode()
    {
        var gated = FlashDeviceUiPolicy.GatedControls;

        Assert.Equal(gated.Count, gated.Distinct(StringComparer.Ordinal).Count());
        foreach (var name in gated)
        {
            Assert.Contains(SelectedModes, mode => FlashDeviceUiPolicy.IsAvailable(name, mode));
        }
    }

    [Fact]
    public void IsAvailable_ControlOutsideTheTable_IsNotDeviceGated()
    {
        Assert.True(FlashDeviceUiPolicy.IsAvailable("RootVersionBox", FlashDeviceMode.None));
        Assert.True(FlashDeviceUiPolicy.IsAvailable("EdlProgrammerPickBox", FlashDeviceMode.Shared));
    }
}