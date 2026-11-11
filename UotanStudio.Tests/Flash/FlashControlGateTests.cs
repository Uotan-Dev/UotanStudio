using System.Text.RegularExpressions;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.SpacemiT;
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

    /// <summary>
    /// The seven option check boxes of the shared action pane, in page order.
    /// <para>通用操作区的七个选项复选框，按页面顺序排列。</para>
    /// </summary>
    private static readonly string[] OptionCheckBoxNames =
    [
        "DisableVbmetaBox", "AddRootBox", "EraseDataBox", "RebootAfterBox",
        "ShowAllPartitionsBox", "DeleteCowBox", "SpacemiTVerifyBox"
    ];

    private static FlashGateContext Context(FlashDeviceMode mode,
        FlashDeviceKind kind = FlashDeviceKind.Unknown, RockchipStage stage = RockchipStage.Unknown,
        bool spacemiTFastboot = false, bool adbSu = false, bool hasSelectedPartitions = false,
        SpacemiTBootStage fastbootStage = SpacemiTBootStage.Unknown) =>
        new(mode, kind, stage, spacemiTFastboot, adbSu, hasSelectedPartitions, fastbootStage);

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
    public void InfoRead_OpensOnFastbootBecauseThatEndpointAnswersGetvar()
    {
        var gate = FlashControlGate.InfoRead("ReadInfoButton");

        Assert.False(gate.IsOffered(Context(FlashDeviceMode.None)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Shared)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Edl)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Mtk)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Rkdl, stage: RockchipStage.Loader)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Rkdl, stage: RockchipStage.Maskrom)));

        // Both fastboot stages can be read: the generic gadget through the standard branch, the SpacemiT gadget through
        // the branch its stage selects.
        // 两个 fastboot 阶段都可读取：通用 gadget 走标准分支，进迭时空 gadget 走其阶段选定的分支。
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot,
            spacemiTFastboot: true, fastbootStage: SpacemiTBootStage.BromFdl)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot,
            spacemiTFastboot: true, fastbootStage: SpacemiTBootStage.Uboot)));

        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Adb, adbSu: true)),
            "the page has no information branch for ADB");
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Hdc)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Sunxi, FlashDeviceKind.Sunxi)));
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
    public void LoaderOnly_OffersTheBootChainToTheSpacemiTGadget()
    {
        var gate = FlashControlGate.LoaderOnly("LoadBootButton");

        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot,
            spacemiTFastboot: true)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot,
            spacemiTFastboot: true, fastbootStage: SpacemiTBootStage.BromFdl)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot,
            spacemiTFastboot: true, fastbootStage: SpacemiTBootStage.Uboot)),
            "a gadget already running U-Boot has no boot chain left to send");
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot)));
    }

    [Fact]
    public void PartitionTableRead_IsOfferedOnlyWhereFastbootEnumeratesTheTable()
    {
        var gate = FlashControlGate.PartitionTableRead("ReadPartitionTableButton", busyDisabled: true);

        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot,
            spacemiTFastboot: true, fastbootStage: SpacemiTBootStage.Uboot)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot,
            spacemiTFastboot: true, fastbootStage: SpacemiTBootStage.BromFdl)),
            "the BootROM download mode has no partition table to read");

        // Every other protocol reads its table through the action its own section owns.
        // 其它协议都经各自分类拥有的动作读取分区表。
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.None)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Shared)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Edl)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Mtk)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Rkdl, stage: RockchipStage.Loader)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Sunxi, FlashDeviceKind.Sunxi)));
        Assert.True(gate.BusyDisabled);
    }

    [Fact]
    public void SharedActions_FormAFixedTwoByFourButtonMatrix()
    {
        var view = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "UotanStudio", "Views", "Shell", "FlashView.axaml"));
        var block = ActionMatrix(view);

        Assert.Matches(new Regex("RowDefinitions=\"Auto,Auto,Auto,Auto\"", RegexOptions.Compiled), block);

        var cells = new Dictionary<(int Row, int Column), string>();
        // The lookahead keeps "<DropDownButton.Flyout>" out of the element scan; it is a property element, not a cell.
        // 前瞻把 "<DropDownButton.Flyout>" 排除在元素扫描之外：它是属性元素而不是单元格。
        foreach (Match button in new Regex(
                     "<(?:Button|DropDownButton)(?=[\\s>])([^>]*)>", RegexOptions.Compiled).Matches(block))
        {
            var attributes = button.Groups[1].Value;
            var name = Regex.Match(attributes, "Name=\"([A-Za-z0-9_]+)\"").Groups[1].Value;
            var row = int.Parse(Regex.Match(attributes, "Grid[.]Row=\"(\\d+)\"").Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            var column = int.Parse(Regex.Match(attributes, "Grid[.]Column=\"(\\d+)\"").Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(cells.TryAdd((row, column), name), $"{name} shares a cell with another entry");
        }

        Assert.Equal(8, cells.Count);
        Assert.All(cells.Keys, cell => Assert.InRange(cell.Row, 0, 3));
        Assert.All(cells.Keys, cell => Assert.InRange(cell.Column, 0, 1));
        for (var row = 0; row < 4; row++)
        {
            Assert.True(cells.ContainsKey((row, 0)), $"row {row} has no left entry");
            Assert.True(cells.ContainsKey((row, 1)), $"row {row} has no right entry");
        }

        // Row by row, left to right: send the boot program, flash the selected rows, read the device's partition table,
        // read the device, read the selected partitions back, erase them, switch the slot and cancel.
        // 逐行从左到右：发送引导程序、刷入选中分区、读取设备分区表、读取设备信息、回读选中分区、擦除选中分区、切换槽位、取消。
        Assert.Equal("LoadBootButton", cells[(0, 0)]);
        Assert.Equal("FlashSelectedButton", cells[(0, 1)]);
        Assert.Equal("ReadPartitionTableButton", cells[(1, 0)]);
        Assert.Equal("ReadInfoButton", cells[(1, 1)]);
        Assert.Equal("ReadbackSelectedButton", cells[(2, 0)]);
        Assert.Equal("EraseSelectedButton", cells[(2, 1)]);
        Assert.Equal("SwitchSlotButton", cells[(3, 0)]);
        Assert.Equal("CancelFlashButton", cells[(3, 1)]);
    }

    /// <summary>
    /// Reads the shared-action button matrix out of the page: the stretch grid that hosts the eight fixed action
    /// cells, whose bounds end at the first closing tag after its opening one.
    /// <para>从页面中取出通用动作按钮矩阵：承载八个固定动作单元格的拉伸网格，其范围终止于起始标签之后的第一个闭合标签。</para>
    /// </summary>
    /// <param name="view">The page markup. <para>页面标记。</para></param>
    /// <returns>The matrix markup. <para>矩阵标记。</para></returns>
    private static string ActionMatrix(string view)
    {
        const string start = "<Grid RowDefinitions=\"Auto,Auto,Auto,Auto\" ColumnDefinitions=\"*,*\"";
        var index = view.IndexOf(start, StringComparison.Ordinal);
        Assert.True(index >= 0, "the shared action matrix is missing from the page");
        var end = view.IndexOf("</Grid>", index, StringComparison.Ordinal);
        Assert.True(end > index, "the shared action matrix is not closed");
        return view[index..end];
    }

    [Fact]
    public void SharedFlash_ClosesWhileTheSpacemiTGadgetStillSitsInTheBootromMode()
    {
        var gate = FlashControlGate.SharedFlash("FlashSelectedButton");

        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot,
            spacemiTFastboot: true, fastbootStage: SpacemiTBootStage.BromFdl)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot,
            spacemiTFastboot: true, fastbootStage: SpacemiTBootStage.Uboot)),
            "the U-Boot stage can still write partitions");
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot,
            spacemiTFastboot: true)),
            "an unproven stage must not lock the flash action");
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot)));
    }

    [Fact]
    public void OptionCheckBoxes_CarryNoDeviceCondition()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "UotanStudio", "ViewModels", "Flash", "FlashViewModel.Gating.cs"));

        foreach (var name in OptionCheckBoxNames)
        {
            Assert.Contains($"RegisterGate(\"{name}\", FlashControlGate.BusyOnly(\"{name}\"))", source,
                StringComparison.Ordinal);

            var gate = FlashControlGate.BusyOnly(name);
            foreach (var mode in SelectedModes.Append(FlashDeviceMode.None))
            {
                Assert.True(gate.IsOffered(Context(mode)), $"{name} @ {mode}");
            }
        }
    }

    [Fact]
    public void Readback_OffersSpacemiTFastbootOnlyWithSelectedRows()
    {
        var gate = FlashControlGate.Readback("ReadbackSelectedButton", busyDisabled: true);

        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot,
            spacemiTFastboot: true, hasSelectedPartitions: true)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot,
            spacemiTFastboot: true, hasSelectedPartitions: true, fastbootStage: SpacemiTBootStage.Uboot)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot,
            spacemiTFastboot: true, hasSelectedPartitions: false)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot,
            spacemiTFastboot: true, hasSelectedPartitions: true, fastbootStage: SpacemiTBootStage.BromFdl)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot,
            hasSelectedPartitions: true)));
    }

    [Fact]
    public void Readback_KeepsSharedStorageBehaviorForOtherDevices()
    {
        var gate = FlashControlGate.Readback("ReadbackSelectedButton", busyDisabled: true);

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
        Assert.True(gate.BusyDisabled);
    }

    [Fact]
    public void SpacemiTGates_CloseWhileThePageIsBusy()
    {
        Assert.True(FlashControlGate.BusyOnly("SpacemiTVerifyBox").BusyDisabled);
        Assert.True(FlashControlGate.Readback("ReadbackSelectedButton", busyDisabled: true).BusyDisabled);
        Assert.False(FlashControlGate.Readback("ReadbackSelectedButton").BusyDisabled);
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
    public void RootPackagePicker_StaysAvailableOnEveryConnectedDevice()
    {
        var gate = FlashControlGate.FlashOptionAnyDevice("ChooseRootPackageButton", busyDisabled: true);

        foreach (var mode in SelectedModes)
        {
            Assert.True(gate.IsOffered(Context(mode)), mode.ToString());
        }

        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Adb)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Shared, FlashDeviceKind.Hdc)));
        Assert.True(gate.IsOffered(Context(FlashDeviceMode.Fastboot, FlashDeviceKind.Fastboot, spacemiTFastboot: true)));
        Assert.False(gate.IsOffered(Context(FlashDeviceMode.None)), "the root package needs a device to be applied to");
        Assert.True(gate.BusyDisabled);
    }

    [Fact]
    public void OptionGroups_ShowThemselvesFromTheirOwnCheckBox()
    {
        var view = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "UotanStudio", "Views", "Shell", "FlashView.axaml"));

        Assert.Matches(
            new Regex("<StackPanel Name=\"AvbOptionsPanel\"[^>]*IsVisible=\"\\{Binding #DisableVbmetaBox[.]IsChecked\\}\"",
                RegexOptions.Compiled), view);
        Assert.Matches(
            new Regex("<StackPanel Name=\"RootOptionsPanel\"[^>]*IsVisible=\"\\{Binding #AddRootBox[.]IsChecked\\}\"",
                RegexOptions.Compiled), view);
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