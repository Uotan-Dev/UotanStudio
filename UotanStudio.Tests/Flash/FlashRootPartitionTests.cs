using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

public sealed class FlashRootPartitionTests
{
    [Fact]
    public void RootBootPartitionNames_MixedRows_ReturnsBootNamesWithoutDuplicates()
    {
        FlashPartitionEntry[] rows =
        [
            Row("boot_a"),
            Row("system"),
            Row("vendor_boot"),
            Row("boot_b"),
            Row("init_boot"),
            Row("super"),
            Row("BOOT_X"),
            Row("boot_a")
        ];

        var names = FlashViewModel.RootBootPartitionNames(rows);

        string[] expected = ["boot_a", "vendor_boot", "boot_b", "init_boot", "BOOT_X"];
        Assert.Equal(expected, names);
    }

    [Fact]
    public void RootBootPartitionNames_NoBootRows_ReturnsEmpty()
    {
        var names = FlashViewModel.RootBootPartitionNames([Row("system"), Row("vbmeta")]);

        Assert.Empty(names);
    }

    [Fact]
    public void FindRootPartitionEntry_QueuedFlashRow_ReturnsThatRow()
    {
        var boot = Row("boot_a");
        FlashPartitionEntry[] selected = [Row("system"), boot, Row("boot_b")];

        var found = FlashViewModel.FindRootPartitionEntry(selected, "BOOT_A");

        Assert.Same(boot, found);
    }

    [Fact]
    public void FindRootPartitionEntry_TargetNotInSelection_ReturnsNull()
    {
        FlashPartitionEntry[] selected = [Row("boot_a"), Row("system")];

        var found = FlashViewModel.FindRootPartitionEntry(selected, "init_boot");

        Assert.Null(found);
    }

    [Fact]
    public void FindRootPartitionEntry_BlankTarget_ReturnsNull()
    {
        FlashPartitionEntry[] selected = [Row("boot_a")];

        Assert.Null(FlashViewModel.FindRootPartitionEntry(selected, null));
        Assert.Null(FlashViewModel.FindRootPartitionEntry(selected, "   "));
    }

    [Fact]
    public void FindRootPartitionEntry_NonFlashCommand_ReturnsNull()
    {
        var reboot = new FlashPartitionEntry
        {
            Name = "boot",
            Step = new FlashStep("reboot", "boot"),
            IsSelected = true,
            CanSelect = true
        };

        Assert.Null(FlashViewModel.FindRootPartitionEntry([reboot], "boot"));
    }

    private static FlashPartitionEntry Row(string name) => new()
    {
        Name = name,
        IsSelected = true,
        CanSelect = true
    };
}