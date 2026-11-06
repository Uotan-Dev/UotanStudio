using UotanStudio.Comm.Protocols.SpacemiT;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

/// <summary>
/// The overall bar during a SpacemiT run: the storage section has one denominator for every row it writes, so the bar
/// follows the bytes, while the boot-chain section stages two unrelated files and hands over twice, so it scrolls for
/// the whole section instead of restarting at zero for each staged file.
/// <para>SpacemiT 运行时的总进度条：烧录段落对它写入的每一行有共同的分母，因此按字节推进；引导链段落暂存两个互不相关的
/// 文件并两次移交，因此整个段落滚动显示，而不是在每个暂存文件上从零重新开始。</para>
/// </summary>
public sealed class FlashSpacemiTProgressTests
{
    private static readonly IReadOnlyDictionary<string, FlashPartitionEntry> NoRows =
        new Dictionary<string, FlashPartitionEntry>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void StagedTransfer_KeepsTheBootChainBarScrolling()
    {
        var model = new FlashViewModel
        {
            IsOverallIndeterminate = true,
            OverallPercent = 0,
            OverallText = "boot chain"
        };

        model.ApplySpacemiTProgress(
            new SpacemiTFlashProgress("stage", null, 50, 100, "staging factory/FSBL.bin (1024 bytes)"),
            NoRows, scrolls: true);

        Assert.True(model.IsOverallIndeterminate);
        Assert.Equal(0d, model.OverallPercent);

        // The transport's own diagnostic line never reaches the caption: the section keeps its localized wording, and the
        // bar must not fall back to a determinate 50% it would then restart from zero for U-Boot.
        // 传输层的诊断行不会进入说明文本：该段落保持自己的本地化措辞，进度条也不能退回到一个随后会在 U-Boot 上从零重来的 50%。
        Assert.Equal("boot chain", model.OverallText);
    }

    [Fact]
    public void StagedTransfer_OnTheStorageSectionMovesTheBarWithTheBytes()
    {
        var model = new FlashViewModel { IsOverallIndeterminate = true };

        model.ApplySpacemiTProgress(
            new SpacemiTFlashProgress("stage", null, 25, 100, "staging u-boot.itb (2048 bytes)"),
            NoRows, scrolls: false);

        Assert.False(model.IsOverallIndeterminate);
        Assert.Equal(25d, model.OverallPercent);
        Assert.Equal("staging u-boot.itb (2048 bytes)", model.OverallText);
    }

    [Fact]
    public void StagedTransferWithoutATotal_StaysScrollingOnEitherSection()
    {
        var bootChain = new FlashViewModel { IsOverallIndeterminate = true };
        var storage = new FlashViewModel { IsOverallIndeterminate = false };

        var report = new SpacemiTFlashProgress("stage", null, 4096, null, "staging");
        bootChain.ApplySpacemiTProgress(report, NoRows, scrolls: true);
        storage.ApplySpacemiTProgress(report, NoRows, scrolls: false);

        Assert.True(bootChain.IsOverallIndeterminate);
        Assert.True(storage.IsOverallIndeterminate);
        Assert.Equal(0d, storage.OverallPercent);
    }

    [Fact]
    public void PhaseReportWithoutBytes_LeavesTheBootChainBarScrolling()
    {
        var model = new FlashViewModel { IsOverallIndeterminate = true, OverallPercent = 0 };

        model.ApplySpacemiTProgress(
            new SpacemiTFlashProgress("action", null, null, null,
                "continue: device leaves fastboot, waiting for re-enumeration"),
            NoRows, scrolls: true);

        Assert.True(model.IsOverallIndeterminate);
        Assert.Equal(0d, model.OverallPercent);
    }
}