using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Flash-page control gating: a UNISOC endpoint only answers storage questions once the link reports FDL2, so the
/// info read and the partition-table read must stay closed before that stage even though the flash action is
/// already gated on it.
/// <para>烧录页面控件门控：紫光展锐端点只在链路报告 FDL2 后才应答存储问题，因此在该阶段之前信息读取与分区表读取
/// 必须保持关闭，即使烧录动作已经按该条件门控。</para>
/// </summary>
public class FlashControlGateUnisocTests
{
    private static FlashGateContext Unisoc(bool fdl2, bool hasPackage = true) => new(
        Mode: FlashDeviceMode.Unisoc,
        Kind: FlashDeviceKind.Unisoc,
        Stage: RockchipStage.Unknown,
        IsSpacemiTFastboot: false,
        HasAdbSu: false,
        IsUnisocFdl2: fdl2,
        HasUnisocPackage: hasPackage);

    [Fact]
    public void InfoRead_UnisocBeforeFdl2_IsClosed()
    {
        var gate = FlashControlGate.InfoRead("ReadInfoButton");
        Assert.False(gate.IsOffered(Unisoc(fdl2: false)));
        Assert.True(gate.IsOffered(Unisoc(fdl2: true)));
    }

    [Fact]
    public void PartitionTableRead_UnisocBeforeFdl2_IsClosed()
    {
        var gate = FlashControlGate.PartitionTableRead("ReadPartitionTableButton");
        Assert.False(gate.IsOffered(Unisoc(fdl2: false)));
        Assert.True(gate.IsOffered(Unisoc(fdl2: true)));
    }

    [Fact]
    public void SharedFlash_UnisocNeedsBothFdl2AndPackage()
    {
        var gate = FlashControlGate.SharedFlash("FlashButton");
        Assert.False(gate.IsOffered(Unisoc(fdl2: false)));
        Assert.False(gate.IsOffered(Unisoc(fdl2: true, hasPackage: false)));
        Assert.True(gate.IsOffered(Unisoc(fdl2: true)));
    }
}