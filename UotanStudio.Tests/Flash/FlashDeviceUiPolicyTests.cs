using UotanStudio.Comm.Protocols.Edl;
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
        Assert.False(FlashDeviceUiPolicy.IsAvailable("SwitchSlotButton", FlashDeviceMode.None));
    }

    [Fact]
    public void IsAvailable_LoadBootButton_IsOfferedOnlyToLoaderCapableModes()
    {
        Assert.False(FlashDeviceUiPolicy.IsAvailable("LoadBootButton", FlashDeviceMode.None));
        Assert.False(FlashDeviceUiPolicy.IsAvailable("LoadBootButton", FlashDeviceMode.Shared));

        Assert.True(FlashDeviceUiPolicy.IsAvailable("LoadBootButton", FlashDeviceMode.Edl));
        Assert.True(FlashDeviceUiPolicy.IsAvailable("LoadBootButton", FlashDeviceMode.Mtk));
        Assert.True(FlashDeviceUiPolicy.IsAvailable("LoadBootButton", FlashDeviceMode.Rkdl));

        Assert.True(FlashDeviceUiPolicy.IsAvailable("LoadBootButton", FlashDeviceMode.Rkdl, isRockchipLoaderStage: false),
            "the load-boot entry must stay reachable on Rockchip without a loader because loading one is its purpose");
        Assert.True(FlashDeviceUiPolicy.IsAvailable("LoadBootButton", FlashDeviceMode.Rkdl, isRockchipLoaderStage: true));
    }

    [Fact]
    public void CanLoadBoot_RockchipUsbplugStage_LeavesLoadBootDisabled()
    {
        Assert.False(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Rkdl, EdlProtocolState.Unknown, string.Empty, isRockchipLoaderStage: true));
        Assert.True(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Rkdl, EdlProtocolState.Unknown, string.Empty, isRockchipLoaderStage: false));

        Assert.False(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Edl, EdlProtocolState.Firehose, string.Empty, isRockchipLoaderStage: true));
        Assert.True(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Mtk, EdlProtocolState.Unknown, string.Empty, isRockchipLoaderStage: true));
    }

    [Fact]
    public void IsAvailable_RockchipStage_SplitsStorageActionsFromInfoActions()
    {
        Assert.False(FlashDeviceUiPolicy.IsAvailable("SwitchSlotButton", FlashDeviceMode.Rkdl, isRockchipLoaderStage: false),
            "A/B slot switching is a fastboot operation no bootrom endpoint can answer");
        Assert.False(FlashDeviceUiPolicy.IsAvailable("SwitchSlotButton", FlashDeviceMode.Rkdl, isRockchipLoaderStage: true),
            "A/B slot switching is a fastboot operation no loader endpoint can answer");
        Assert.True(FlashDeviceUiPolicy.IsAvailable("SwitchSlotButton", FlashDeviceMode.Shared));

        Assert.False(FlashDeviceUiPolicy.IsAvailable("FlashSelectedButton", FlashDeviceMode.Rkdl, isRockchipLoaderStage: false),
            "maskrom storage must not receive a write");
        Assert.True(FlashDeviceUiPolicy.IsAvailable("FlashSelectedButton", FlashDeviceMode.Rkdl, isRockchipLoaderStage: true));
        Assert.False(FlashDeviceUiPolicy.IsAvailable("ReadbackSelectedButton", FlashDeviceMode.Rkdl, isRockchipLoaderStage: false));
        Assert.True(FlashDeviceUiPolicy.IsAvailable("ReadbackSelectedButton", FlashDeviceMode.Rkdl, isRockchipLoaderStage: true));
        Assert.False(FlashDeviceUiPolicy.IsAvailable("RkPane", FlashDeviceMode.Rkdl, isRockchipLoaderStage: false));
        Assert.True(FlashDeviceUiPolicy.IsAvailable("RkPane", FlashDeviceMode.Rkdl, isRockchipLoaderStage: true));
    }

    [Fact]
    public void IsLoaderStageLoadable_ObservedStages_MatchWhatEachProtocolCanStillAccept()
    {
        Assert.False(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Edl, EdlProtocolState.Firehose, string.Empty));
        Assert.False(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Edl, EdlProtocolState.Streaming, string.Empty));
        Assert.True(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Edl, EdlProtocolState.Sahara, string.Empty));
        Assert.True(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Edl, EdlProtocolState.Unknown, string.Empty));

        Assert.False(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Mtk, EdlProtocolState.Unknown, "logging"));
        Assert.True(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Mtk, EdlProtocolState.Unknown, "brom"));

        Assert.True(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Rkdl, EdlProtocolState.Unknown, string.Empty));
        Assert.False(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Fastboot, EdlProtocolState.Unknown, string.Empty));

        Assert.True(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Rkdl, EdlProtocolState.Unknown, string.Empty, isRockchipLoaderStage: false),
            "the maskrom endpoint must keep accepting the loader download");
        Assert.False(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Rkdl, EdlProtocolState.Unknown, string.Empty, isRockchipLoaderStage: true),
            "the usbplug endpoint already runs the loader the boot download produced");
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
    public void IsAvailable_SectionExpanders_AreNotGated()
    {
        foreach (var section in new[] { "EdlExpander", "MtkExpander", "RkExpander", "ImageExpander" })
        {
            Assert.DoesNotContain(section, FlashDeviceUiPolicy.GatedControls);
            Assert.True(FlashDeviceUiPolicy.IsAvailable(section, FlashDeviceMode.None));
            foreach (var mode in SelectedModes)
            {
                Assert.True(FlashDeviceUiPolicy.IsAvailable(section, mode));
            }
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
    [Fact]
    public void KindOf_SelectedProtocol_RestrictsAdbFastbootAndHdc()
    {
        Assert.Equal(FlashDeviceKind.Unknown, FlashDeviceUiPolicy.KindOf(null));
        Assert.Equal(FlashDeviceKind.Adb, FlashDeviceUiPolicy.KindOf(DeviceProtocolKind.Adb));
        Assert.Equal(FlashDeviceKind.Fastboot, FlashDeviceUiPolicy.KindOf(DeviceProtocolKind.Fastboot));
        Assert.Equal(FlashDeviceKind.Unknown, FlashDeviceUiPolicy.KindOf(DeviceProtocolKind.Edl));
        Assert.Equal(FlashDeviceKind.Unknown, FlashDeviceUiPolicy.KindOf(DeviceProtocolKind.Mtk));
        Assert.Equal(FlashDeviceKind.Unknown, FlashDeviceUiPolicy.KindOf(DeviceProtocolKind.Rkdl));
        Assert.Equal(FlashDeviceKind.Hdc, FlashDeviceUiPolicy.KindOf(DeviceProtocolKind.Hdc));
    }

    [Fact]
    public void IsAvailable_AdbDevice_OffersOnlyTheSharedFlashAction()
    {
        Assert.True(FlashDeviceUiPolicy.IsAvailable("FlashSelectedButton", FlashDeviceMode.Shared,
            isRockchipLoaderStage: false, FlashDeviceKind.Adb));

        var refused = FlashDeviceUiPolicy.GatedControls
            .Where(name => name != "FlashSelectedButton")
            .ToArray();
        Assert.NotEmpty(refused);
        foreach (var name in refused)
        {
            Assert.False(FlashDeviceUiPolicy.IsAvailable(name, FlashDeviceMode.Shared,
                isRockchipLoaderStage: false, FlashDeviceKind.Adb), name);
        }
    }

    [Fact]
    public void IsAvailable_FastbootDevice_OffersOnlyTheSharedFlashAction()
    {
        Assert.True(FlashDeviceUiPolicy.IsAvailable("FlashSelectedButton", FlashDeviceMode.Shared,
            isRockchipLoaderStage: false, FlashDeviceKind.Fastboot));

        Assert.False(FlashDeviceUiPolicy.IsAvailable("ReadInfoButton", FlashDeviceMode.Shared,
            isRockchipLoaderStage: false, FlashDeviceKind.Fastboot));
        Assert.False(FlashDeviceUiPolicy.IsAvailable("ReadbackSelectedButton", FlashDeviceMode.Shared,
            isRockchipLoaderStage: false, FlashDeviceKind.Fastboot));
        Assert.False(FlashDeviceUiPolicy.IsAvailable("EraseSelectedButton", FlashDeviceMode.Shared,
            isRockchipLoaderStage: false, FlashDeviceKind.Fastboot));
        Assert.False(FlashDeviceUiPolicy.IsAvailable("LoadBootButton", FlashDeviceMode.Shared,
            isRockchipLoaderStage: false, FlashDeviceKind.Fastboot));
        Assert.False(FlashDeviceUiPolicy.IsAvailable("SwitchSlotButton", FlashDeviceMode.Shared,
            isRockchipLoaderStage: false, FlashDeviceKind.Fastboot));

        foreach (var section in new[] { "EdlPane", "MtkPane", "RkPane", "ImagePane" })
        {
            Assert.False(FlashDeviceUiPolicy.IsAvailable(section, FlashDeviceMode.Shared,
                isRockchipLoaderStage: false, FlashDeviceKind.Fastboot), section);
        }
    }

    [Fact]
    public void IsAvailable_HdcDevice_OffersNoPageControl()
    {
        var gated = FlashDeviceUiPolicy.GatedControls;
        Assert.NotEmpty(gated);

        foreach (var name in gated)
        {
            Assert.False(FlashDeviceUiPolicy.IsAvailable(name, FlashDeviceMode.Shared,
                isRockchipLoaderStage: false, FlashDeviceKind.Hdc), name);
            Assert.False(FlashDeviceUiPolicy.IsAvailable(name, FlashDeviceMode.Shared,
                isRockchipLoaderStage: true, FlashDeviceKind.Hdc), name);
        }
    }

    [Fact]
    public void IsAvailable_EdlMtkAndRkdlDevices_KeepEveryModeControlTheyOwned()
    {
        foreach (var (protocol, mode) in new (DeviceProtocolKind, FlashDeviceMode)[]
                 {
                     (DeviceProtocolKind.Edl, FlashDeviceMode.Edl),
                     (DeviceProtocolKind.Mtk, FlashDeviceMode.Mtk),
                     (DeviceProtocolKind.Rkdl, FlashDeviceMode.Rkdl)
                 })
        {
            var kind = FlashDeviceUiPolicy.KindOf(protocol);
            Assert.Equal(FlashDeviceKind.Unknown, kind);
            foreach (var name in FlashDeviceUiPolicy.GatedControls)
            {
                Assert.Equal(
                    FlashDeviceUiPolicy.IsAvailable(name, mode, isRockchipLoaderStage: true),
                    FlashDeviceUiPolicy.IsAvailable(name, mode, isRockchipLoaderStage: true, kind));
            }
        }
    }

    [Fact]
    public void RequiresAdbSu_OnlyTheSharedFlashActionWaitsForTheRootVerdict()
    {
        Assert.True(FlashDeviceUiPolicy.RequiresAdbSu("FlashSelectedButton"));
        Assert.False(FlashDeviceUiPolicy.RequiresAdbSu("ReadInfoButton"));
        Assert.False(FlashDeviceUiPolicy.RequiresAdbSu("EdlPane"));
    }

    [Fact]
    public void GatedControls_ListEverySectionPaneOnce()
    {
        foreach (var section in new[] { "EdlPane", "MtkPane", "RkPane", "ImagePane" })
        {
            Assert.Single(FlashDeviceUiPolicy.GatedControls, name => name == section);
        }
    }

    [Fact]
    public void BusyDisabledControls_KeepThePageScrollableAndTheLogUsable()
    {
        var busy = FlashDeviceUiPolicy.BusyDisabledControls;

        Assert.NotEmpty(busy);
        string[] mustStayUsable =
        [
            "PageScroll", "LogBox", "ClearLogButton", "LogOverallProgress",
            "PackageBar", "PackagePathBox", "ChooseFileButton", "PartitionGrid", "PartitionSearchBox"
        ];
        foreach (var name in mustStayUsable)
        {
            Assert.DoesNotContain(name, busy);
        }

        string[] mustTurnOff =
        [
            "LoadBootButton", "ReadInfoButton", "ReadbackSelectedButton", "FlashSelectedButton",
            "EraseSelectedButton", "SwitchSlotButton", "EdlPane", "MtkPane", "RkPane", "ImagePane"
        ];
        foreach (var name in mustTurnOff)
        {
            Assert.Contains(name, busy);
        }
    }

    [Fact]
    public void BusyDisabledControls_ListEveryControlOnceAndEachExistsInTheView()
    {
        var busy = FlashDeviceUiPolicy.BusyDisabledControls;
        Assert.Equal(busy.Count, busy.Distinct(StringComparer.Ordinal).Count());

        var view = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "UotanStudio", "Views", "Shell", "FlashView.axaml"));
        foreach (var name in busy)
        {
            Assert.Contains($"Name=\"{name}\"", view);
        }
    }

    [Fact]
    public void AlwaysEnabledControls_CoverTheLogAreaAndNeverTheCancelEntry()
    {
        Assert.Contains("LogBox", FlashViewModel.AlwaysEnabledControls);
        Assert.Contains("ClearLogButton", FlashViewModel.AlwaysEnabledControls);
        Assert.DoesNotContain("CancelFlashButton", FlashViewModel.AlwaysEnabledControls);
        Assert.DoesNotContain("CancelFlashButton", FlashDeviceUiPolicy.BusyDisabledControls);
        Assert.All(FlashViewModel.AlwaysEnabledControls,
            name => Assert.DoesNotContain(name, FlashDeviceUiPolicy.BusyDisabledControls));
    }

    private static string RepositoryRoot()
    {
        if (!UotanStudio.Tests.Firmware.Architecture.FirmwareContextDependencyScanner
                .TryFindRepositoryRoot(AppContext.BaseDirectory, out var root) || root is null)
        {
            throw new InvalidOperationException("The checked-out solution root was not found.");
        }

        return root;
    }
}