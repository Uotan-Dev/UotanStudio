using UotanStudio.Comm.Protocols.Tegra;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class TegraFlashPlanTests
{
    [Fact]
    public void Options_MissingConfirmation_IsRejectedBeforeAnyWireIo()
    {
        var options = CreateValidOptions();
        options.FlashConfirmation = string.Empty;

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("FLASH", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_WrongConfirmation_IsRejected()
    {
        var options = CreateValidOptions();
        options.FlashConfirmation = "flash";

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Options_UnknownChip_IsRejected()
    {
        var options = CreateValidOptions();
        options.Chip = TegraChip.Unknown;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Options_MissingMiniloader_IsRejected()
    {
        var options = CreateValidOptions();
        options.MiniloaderPath = null;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Plan_BootloaderBeforeBct_IsRejected()
    {
        var plan = TegraFlashPlan.CreateUnvalidated(TegraChip.Tegra114,
        [
            new TegraFlashStep(TegraFlashStage.RcmHandshake, "rcm", null, null, true),
            new TegraFlashStep(TegraFlashStage.BootloaderDownload, "bootloader", null, 1024, true),
            new TegraFlashStep(TegraFlashStage.BctDownload, "bct", null, 1024, true)
        ]);

        Assert.Throws<InvalidOperationException>(plan.Validate);
    }

    [Fact]
    public void Plan_StagesOutOfOrder_AreRejected()
    {
        var plan = TegraFlashPlan.CreateUnvalidated(TegraChip.Tegra114,
        [
            new TegraFlashStep(TegraFlashStage.BctDownload, "bct", null, 1024, true),
            new TegraFlashStep(TegraFlashStage.RcmHandshake, "rcm", null, null, true)
        ]);

        Assert.Throws<InvalidOperationException>(plan.Validate);
    }

    [Fact]
    public void Plan_TerminalPartitionWrite_IsRejected()
    {
        var plan = TegraFlashPlan.CreateUnvalidated(TegraChip.Tegra114,
        [
            new TegraFlashStep(TegraFlashStage.RcmHandshake, "rcm", null, null, true),
            new TegraFlashStep(TegraFlashStage.PartitionWrite, "system", null, 1024, true)
        ]);

        Assert.Throws<InvalidOperationException>(plan.Validate);
    }

    [Fact]
    public void Plan_EmptyStepList_IsRejected()
    {
        var plan = TegraFlashPlan.CreateUnvalidated(TegraChip.Tegra114, []);

        Assert.Throws<InvalidOperationException>(plan.Validate);
    }

    [Fact]
    public void Bundle_GenerationToken_MatchesTheVendorFileNaming()
    {
        Assert.Equal("tegra114", TegraBootloaderBundle.GetGenerationToken(TegraChip.Tegra114));
        Assert.Equal("tegra124", TegraBootloaderBundle.GetGenerationToken(TegraChip.Tegra124));
        Assert.Equal("tegra30", TegraBootloaderBundle.GetGenerationToken(TegraChip.Tegra30));
        Assert.Equal("tegra20", TegraBootloaderBundle.GetGenerationToken(TegraChip.Tegra20));
        Assert.Null(TegraBootloaderBundle.GetGenerationToken(TegraChip.Unknown));
    }

    private static TegraFlashOptions CreateValidOptions()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uotan-tegra-plan-tests");
        return new TegraFlashOptions
        {
            FlashConfirmation = TegraFlashOptions.RequiredConfirmation,
            Chip = TegraChip.Tegra114,
            MiniloaderPath = System.IO.Path.Combine(directory, "tegra114-miniloader.bin"),
            BctPath = System.IO.Path.Combine(directory, "tegra114-bct.bin")
        };
    }
}