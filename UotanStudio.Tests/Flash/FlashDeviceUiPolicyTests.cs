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
    public void IsAvailable_NoDeviceSelected_DisablesOnlyDeviceOperations()
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
        string[] ungated = ["PackagePathBox", "PartitionGrid", "ClearLogButton", "PartitionSearchBox"];

        foreach (var name in ungated)
        {
            Assert.True(FlashDeviceUiPolicy.IsAvailable(name, FlashDeviceMode.None));
            foreach (var mode in SelectedModes)
            {
                Assert.True(FlashDeviceUiPolicy.IsAvailable(name, mode), $"{name} in {mode}");
            }
        }

        Assert.False(FlashDeviceUiPolicy.IsAvailable("FlashSelectedButton", FlashDeviceMode.None));
        Assert.False(FlashDeviceUiPolicy.IsAvailable("RebootMenuButton", FlashDeviceMode.None));
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

    [Fact]
    public void FlashPartitionEntry_EditableCommandAndName_StayInSyncWithStep()
    {
        var entry = new FlashPartitionEntry
        {
            Step = new("flash", "boot", "boot.img", 1024),
            Name = "boot",
            Command = "flash"
        };

        entry.Name = "vendor_boot";
        entry.Command = "wipe_super";

        Assert.Equal("vendor_boot", entry.Step?.Partition);
        Assert.Equal("wipe_super", entry.Step?.Command);
        Assert.Equal("wipe_super", entry.EffectiveCommand);
    }

    [Fact]
    public void FlashPartitionEntry_HasFileName_TracksFileNameChanges()
    {
        var entry = new FlashPartitionEntry();
        var changedProperties = new List<string?>();
        entry.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        entry.FileName = "boot.img";
        Assert.True(entry.HasFileName);
        Assert.Contains(nameof(FlashPartitionEntry.HasFileName), changedProperties);

        changedProperties.Clear();
        entry.FileName = string.Empty;

        Assert.False(entry.HasFileName);
        Assert.Contains(nameof(FlashPartitionEntry.HasFileName), changedProperties);
    }
}