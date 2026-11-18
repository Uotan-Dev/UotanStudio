using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

public sealed class TegraControlGateTests
{
    [Theory]
    [InlineData("bootloader")]
    [InlineData("recovery")]
    [InlineData("forced-recovery")]
    public void IsTegraRebootTarget_KernelImplementedTargets_AreAccepted(string target)
    {
        Assert.True(FlashViewModel.IsTegraRebootTarget(target));
        Assert.Contains(target, FlashViewModel.TegraRebootTargets, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("rcm")]
    [InlineData("fastboot")]
    [InlineData("download")]
    [InlineData("")]
    [InlineData(null)]
    public void IsTegraRebootTarget_UnoimplementedTargets_AreRejected(string? target)
    {
        Assert.False(FlashViewModel.IsTegraRebootTarget(target));
    }

    [Fact]
    public void TegraRebootTargets_DeclaresExactlyThreeTargets()
    {
        Assert.Equal(3, FlashViewModel.TegraRebootTargets.Count);
    }

    [Fact]
    public void ModeOf_TegraProtocol_MapsToTheTegraSection()
    {
        Assert.Equal(FlashDeviceMode.Tegra, FlashDeviceUiPolicy.ModeOf(DeviceProtocolKind.Tegra));
    }

    [Fact]
    public void KindOf_TegraProtocol_IsTheRestrictedTegraKind()
    {
        Assert.Equal(FlashDeviceKind.Tegra, FlashDeviceUiPolicy.KindOf(DeviceProtocolKind.Tegra));
    }

    [Fact]
    public void SectionState_NvidiaIdentifier_RoundTripsThroughCapture()
    {
        var expanded = FlashSectionState.Capture(
            edl: false, mtk: false, rockchip: false, image: false, nvidia: true);

        Assert.Contains(FlashSectionState.Nvidia, expanded);
        Assert.True(FlashSectionState.IsExpanded(expanded, FlashSectionState.Nvidia));
        Assert.True(FlashSectionState.IsExpanded(expanded, "NVIDIA"));
        Assert.False(FlashSectionState.IsExpanded(expanded, FlashSectionState.Edl));
    }

    [Fact]
    public void SectionState_NvidiaCollapsed_IsNotReportedExpanded()
    {
        var expanded = FlashSectionState.Capture(
            edl: true, mtk: false, rockchip: false, image: false);

        Assert.DoesNotContain(FlashSectionState.Nvidia, expanded);
        Assert.False(FlashSectionState.IsExpanded(expanded, FlashSectionState.Nvidia));
    }

    [Fact]
    public void SectionState_NvidiaSitsBetweenUnisocAndImage()
    {
        var expanded = FlashSectionState.Capture(
            edl: false, mtk: false, rockchip: false, image: true, unisoc: true, nvidia: true);

        var unisocIndex = expanded.IndexOf(FlashSectionState.Unisoc);
        var nvidiaIndex = expanded.IndexOf(FlashSectionState.Nvidia);
        var imageIndex = expanded.IndexOf(FlashSectionState.Image);

        Assert.True(unisocIndex < nvidiaIndex, "NVIDIA must follow UNISOC.");
        Assert.True(nvidiaIndex < imageIndex, "NVIDIA must precede the image section.");
    }
}