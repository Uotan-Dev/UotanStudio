using System.Text.RegularExpressions;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

public sealed class FlashControlGateTests
{
    private static readonly FlashDeviceMode[] SelectedModes =
    [
        FlashDeviceMode.Shared, FlashDeviceMode.Edl, FlashDeviceMode.Mtk, FlashDeviceMode.Rkdl,
        FlashDeviceMode.Fastboot, FlashDeviceMode.Sunxi
    ];

    private static FlashGateContext Context(FlashDeviceMode mode,
        FlashDeviceKind kind = FlashDeviceKind.Unknown, RockchipStage stage = RockchipStage.Unknown,
        bool spacemiTFastboot = false, bool adbSu = false, bool hasSelectedPartitions = false) =>
        new(mode, kind, stage, spacemiTFastboot, adbSu, hasSelectedPartitions);

    [Fact]
    public void ModeOf_SelectedProtocol_ReturnsModeThatOffersItsSection()
    {
        Assert.Equal(FlashDeviceMode.None, FlashDeviceUiPolicy.ModeOf(null));
        Assert.Equal(FlashDeviceMode.Fastboot, FlashDeviceUiPolicy.ModeOf(DeviceProtocolKind.Fastboot));
        Assert.Equal(FlashDeviceMode.Shared, FlashDeviceUiPolicy.ModeOf(DeviceProtocolKind.Adb));
        Assert.Equal(FlashDeviceMode.Shared, FlashDeviceUiPolicy.ModeOf(DeviceProtocolKind.Hdc));
        Assert.Equal(FlashDeviceMode.Shared, FlashDeviceUiPolicy.ModeOf(DeviceProtocolKind.Ios));
        Assert.Equal(FlashDeviceMode.Edl, FlashDeviceUiPolicy.ModeOf(DeviceProtocolKind.Edl));
        Assert.Equal(FlashDeviceMode.Mtk, FlashDeviceUiPolicy.ModeOf(DeviceProtocolKind.Mtk));
        Assert.Equal(FlashDeviceMode.Rkdl, FlashDeviceUiPolicy.ModeOf(DeviceProtocolKind.Rkdl));
        Assert.Equal(FlashDeviceMode.Sunxi, FlashDeviceUiPolicy.ModeOf(DeviceProtocolKind.Sunxi));
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
        Assert.Equal(FlashDeviceKind.Sunxi, FlashDeviceUiPolicy.KindOf(DeviceProtocolKind.Sunxi));
    }

    [Fact]
    public void SharedFlash_RefusesNoDeviceHdcAndAllwinner()
    {
        var gate = FlashControlGate.SharedFlash("FlashSelectedButton");

        Assert.False(gate.IsOffered(Context(FlashDeviceMode.None)));
        foreach (var mode in SelectedModes.Where(mode => mode is not (FlashDeviceMode.Sunxi or FlashDeviceMode.None)))
        {
            Assert.True(gate.IsOffered(Context(mode)), mode.ToString());
        }

        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Hdc)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Sunxi, FlashDeviceKind.Sunxi)));
    }

    [Fact]
    public void SharedFlash_RequiresAConfirmedAdbRootShell()
    {
        var gate = FlashControlGate.SharedFlash("FlashSelectedButton");

        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Adb)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Adb, adbSu: true)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Fastboot)));
    }

    [Fact]
    public void SharedFlash_RefusesAProvenMaskRomButKeepsAnUnprovenStage()
    {
        var gate = FlashControlGate.SharedFlash("FlashSelectedButton");

        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Rkdl, stage: RockchipStage.Maskrom)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Rkdl, stage: RockchipStage.Loader)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Rkdl)),
            "a board whose stage no evidence settles must still be flashable");
    }

    [Fact]
    public void SharedStorage_IsOfferedOnlyToUnrestrictedProtocolKinds()
    {
        var gate = FlashControlGate.SharedStorage("ReadbackSelectedButton");

        Assert.False(gate.IsOffered(Context(FlashDeviceMode.None)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Shared)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Edl)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Mtk)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Rkdl, stage: RockchipStage.Loader)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Rkdl, stage: RockchipStage.Maskrom)));

        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Adb, adbSu: true)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Hdc)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Sunxi, FlashDeviceKind.Sunxi)));
    }

    [Fact]
    public void SharedAny_StaysOnEveryRockchipStageBecauseItNeverTouchesStorage()
    {
        var gate = FlashControlGate.SharedAny("ImagePane");

        foreach (var mode in SelectedModes)
        {
            Assert.True(gate.IsOffered(Context(mode)), mode.ToString());
        }

        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Rkdl, stage: RockchipStage.Maskrom)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.None)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Adb)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Hdc)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Sunxi, FlashDeviceKind.Sunxi)));
    }

    [Fact]
    public void SlotSwitch_IsOfferedToEveryModeExceptRockchip()
    {
        var gate = FlashControlGate.SlotSwitch("SwitchSlotButton");

        Assert.False(gate.IsOffered(Context(FlashDeviceMode.None)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Shared)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Edl)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Mtk)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Rkdl)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Rkdl, stage: RockchipStage.Maskrom)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Rkdl, stage: RockchipStage.Loader)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Adb)));
    }

    [Fact]
    public void LoaderOnly_IsOfferedToEveryLoaderCapableMode()
    {
        var gate = FlashControlGate.LoaderOnly("LoadBootButton");

        Assert.False(gate.IsOffered(Context(FlashDeviceMode.None)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Fastboot)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot)));

        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Edl)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Mtk)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Rkdl)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Rkdl, stage: RockchipStage.Maskrom)),
            "the load-boot entry must stay reachable on Rockchip without a loader because loading one is its purpose");
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Rkdl, stage: RockchipStage.Loader)));
    }

    [Fact]
    public void Section_IsOfferedOnlyToItsOwnMode()
    {
        var gates = new (FlashControlGate Gate, FlashDeviceMode Owner)[]
        {
            (FlashControlGate.Section("EdlPane", FlashDeviceMode.Edl), FlashDeviceMode.Edl),
            (FlashControlGate.Section("MtkPane", FlashDeviceMode.Mtk), FlashDeviceMode.Mtk),
            (FlashControlGate.Section("RkPane", FlashDeviceMode.Rkdl), FlashDeviceMode.Rkdl),
            (FlashControlGate.Section("SunxiPane", FlashDeviceMode.Sunxi), FlashDeviceMode.Sunxi)
        };

        foreach (var (gate, owner) in gates)
        {
            foreach (var mode in SelectedModes)
            {
                Assert.Equal(mode == owner, gate.IsOffered(Context(mode)));
            }

            Assert.False(gate.IsOffered(Context(FlashDeviceMode.None)));
        }
    }

    [Fact]
    public void RockchipSection_FollowsTheStageEvidence()
    {
        var gate = FlashControlGate.Section("RkPane", FlashDeviceMode.Rkdl);

        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Rkdl, stage: RockchipStage.Maskrom)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Rkdl, stage: RockchipStage.Loader)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Rkdl)),
            "an unproven stage keeps the commands the device itself can refuse");
    }

    [Fact]
    public void SpacemiT_OpensOnlyForTheGadgetItself()
    {
        var gate = FlashControlGate.SpacemiT("SpacemiTFlashButton");

        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot,
            spacemiTFastboot: true)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Adb, spacemiTFastboot: true)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Hdc, spacemiTFastboot: true)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Edl, spacemiTFastboot: true)));
    }

    [Fact]
    public void Sunxi_IsOfferedOnlyToAllwinnerEndpoints()
    {
        var gate = FlashControlGate.Sunxi("SunxiBootFesButton");

        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Sunxi, FlashDeviceKind.Sunxi)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Sunxi)));
        foreach (var mode in SelectedModes.Where(mode => mode != FlashDeviceMode.Sunxi))
        {
            Assert.False(gate.IsOffered(Context(mode)), mode.ToString());
        }

        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Adb)));
    }

    [Fact]
    public void BusyOnly_IsOfferedWithoutADeviceAndOnlyClosedByTheBusyState()
    {
        var gate = FlashControlGate.BusyOnly("DisableVbmetaBox");

        foreach (var mode in SelectedModes.Append(FlashDeviceMode.None))
        {
            Assert.True(gate.IsOffered(Context(mode)), mode.ToString());
        }

        Assert.True(gate.BusyDisabled);
    }

    [Fact]
    public void PageActions_CarryTheBusyConditionWithoutADeviceCondition()
    {
        foreach (var name in new[]
                 {
                     "ChooseFileButton", "ChooseFolderButton", "GetPackageButton", "AddPartitionButton",
                     "ChooseRootPackageButton"
                 })
        {
            var gate = FlashControlGate.BusyOnly(name);
            Assert.True(gate.IsOffered(Context(FlashDeviceMode.None)), name);
            Assert.True(gate.BusyDisabled, name);
        }
    }

    [Fact]
    public void DeletePartitions_NeedsASelectedRowAndClosesWhileBusy()
    {
        var gate = FlashControlGate.SelectionRequired("DeletePartitionsButton");

        Assert.False(gate.IsOffered(Context(FlashDeviceMode.None)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Shared)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Shared, hasSelectedPartitions: true)));
        Assert.True(gate.BusyDisabled);
    }

    [Fact]
    public void SectionHeader_StaysUsableForEveryDevice()
    {
        var gate = FlashControlGate.SectionHeader("EdlExpander");

        foreach (var mode in SelectedModes.Append(FlashDeviceMode.None))
        {
            Assert.True(gate.IsOffered(Context(mode)), mode.ToString());
        }

        Assert.False(gate.BusyDisabled);
    }

    [Fact]
    public void FlashPage_ClosesUnmatchedActionsWithoutHidingAnything()
    {
        var directory = Path.Combine(RepositoryRoot(), "UotanStudio", "ViewModels", "Flash");
        var gateSource = File.ReadAllText(Path.Combine(directory, "FlashControlGate.cs"));

        Assert.DoesNotContain("IsVisible", gateSource, StringComparison.Ordinal);

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            var source = File.ReadAllText(file);
            Assert.False(source.Contains(".IsVisible =", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} hides a control instead of disabling it.");
        }
    }

    [Fact]
    public void EveryClickableNamedControl_CarriesItsOwnGateOrAnExplicitExemption()
    {
        var registered = RegisteredGateNames();
        var clickable = ClickableNamedControls();
        Assert.NotEmpty(clickable);
        Assert.NotEmpty(registered);

        var exempt = new HashSet<string>(StringComparer.Ordinal)
        {
            // 日志区永远可读可复制，取消按钮由 SetBusy 单独控制。
            "ClearLogButton",
            "CancelFlashButton"
        };

        foreach (var name in clickable)
        {
            if (exempt.Contains(name))
            {
                continue;
            }

            Assert.True(registered.Contains(name),
                $"{name} is clickable but carries no FlashControlGate and is not an explicit exemption.");
        }
    }

    [Fact]
    public void EveryRegisteredGate_NamesAControlThatExistsInTheView()
    {
        var view = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "UotanStudio", "Views", "Shell", "FlashView.axaml"));
        var registered = RegisteredGateNames();
        Assert.NotEmpty(registered);

        foreach (var name in registered)
        {
            Assert.Contains($"Name=\"{name}\"", view, StringComparison.Ordinal);
        }
    }

    private static HashSet<string> RegisteredGateNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var pattern = new Regex(@"RegisterGate\(""([A-Za-z0-9_]+)""", RegexOptions.Compiled);
        var directory = Path.Combine(RepositoryRoot(), "UotanStudio", "ViewModels", "Flash");
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                names.Add(match.Groups[1].Value);
            }
        }

        return names;
    }

    private static HashSet<string> ClickableNamedControls()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "UotanStudio", "Views", "Shell", "FlashView.axaml"));
        var element = new Regex(@"<(?:Button|DropDownButton|HyperlinkButton)\b([^>]*)>", RegexOptions.Compiled);
        var name = new Regex(@"Name=""([A-Za-z0-9_]+)""", RegexOptions.Compiled);
        var click = new Regex(@"\bClick=""", RegexOptions.Compiled);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in element.Matches(xaml))
        {
            var attributes = match.Groups[1].Value;
            if (!click.IsMatch(attributes))
            {
                continue;
            }

            var nameMatch = name.Match(attributes);
            if (nameMatch.Success)
            {
                names.Add(nameMatch.Groups[1].Value);
            }
        }

        return names;
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

    [Fact]
    public void Gate_BusyFlagIsHeldByTheControlThatRequestsIt()
    {
        Assert.True(FlashControlGate.SharedFlash("FlashSelectedButton", busyDisabled: true).BusyDisabled);
        Assert.False(FlashControlGate.SharedFlash("FlashSelectedButton").BusyDisabled);
        Assert.True(FlashControlGate.SharedStorage("ReadInfoButton", busyDisabled: true).BusyDisabled);
        Assert.True(FlashControlGate.Section("EdlPane", FlashDeviceMode.Edl, busyDisabled: true).BusyDisabled);
        Assert.False(FlashControlGate.Sunxi("SunxiPane").BusyDisabled);
    }

    [Fact]
    public void AlwaysEnabledControls_CoverTheLogAreaAndNeverTheCancelEntry()
    {
        Assert.Contains("LogBox", FlashViewModel.AlwaysEnabledControls);
        Assert.Contains("ClearLogButton", FlashViewModel.AlwaysEnabledControls);
        Assert.DoesNotContain("CancelFlashButton", FlashViewModel.AlwaysEnabledControls);
    }

    [Fact]
    public void IsLoaderStageLoadable_UnprovenRockchipStage_KeepsTheBootDownloadAvailable()
    {
        Assert.True(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Rkdl, EdlProtocolState.Unknown, string.Empty, RockchipStage.Unknown),
            "an unproven stage must keep the boot download the maskrom recovery path needs");
        Assert.True(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Rkdl, EdlProtocolState.Unknown, string.Empty, RockchipStage.Maskrom));
        Assert.False(FlashDeviceUiPolicy.IsLoaderStageLoadable(
            DeviceProtocolKind.Rkdl, EdlProtocolState.Unknown, string.Empty, RockchipStage.Loader));
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