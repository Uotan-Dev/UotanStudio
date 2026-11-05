using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Covers the EDL reboot-target plan: which targets are a bare power command, which need a bootloader
/// control block, and which power value each sends. These are pure decisions, so they are checked without
/// any device.
/// <para>覆盖 EDL 重启目标计划：哪些目标是单纯的 power 命令、哪些需要引导控制块，以及各自发送哪个 power 值。
/// 这些是纯决策，因此无需设备即可校验。</para>
/// </summary>
public sealed class EdlModeSwitchPlannerTests
{
    [Theory]
    [InlineData(EdlRebootTarget.System, "reset", null)]
    [InlineData(EdlRebootTarget.PowerOff, "off", null)]
    [InlineData(EdlRebootTarget.Edl, "reset_to_edl", null)]
    [InlineData(EdlRebootTarget.Recovery, "reset", "misc_torecovery.img")]
    [InlineData(EdlRebootTarget.Fastbootd, "reset", "misc_tofastbootd.img")]
    public void Plan_EveryTarget_CarriesItsPowerValueAndBootImage(
        EdlRebootTarget target, string expectedPower, string? expectedImage)
    {
        var plan = EdlModeSwitchPlanner.Plan(target);

        Assert.Equal(target, plan.Target);
        Assert.Equal(expectedPower, plan.PowerValue);
        Assert.Equal(expectedImage, plan.BootloaderControlImage);
    }

    [Theory]
    [InlineData(EdlRebootTarget.System, RebootOption.Reset)]
    [InlineData(EdlRebootTarget.PowerOff, RebootOption.PowerOff)]
    [InlineData(EdlRebootTarget.Edl, RebootOption.ResetToEdl)]
    public void ToRebootOption_TargetWithBarePower_MapsOntoTheProtocolOption(
        EdlRebootTarget target, RebootOption expected)
        => Assert.Equal(expected, EdlModeSwitchPlanner.ToRebootOption(target));

    [Theory]
    [InlineData(EdlRebootTarget.Recovery)]
    [InlineData(EdlRebootTarget.Fastbootd)]
    public void ToRebootOption_TargetNeedingBootImage_IsRefusedInsteadOfDowngraded(EdlRebootTarget target)
        => Assert.Throws<ArgumentOutOfRangeException>(() => EdlModeSwitchPlanner.ToRebootOption(target));

    [Fact]
    public void Plan_UndefinedTarget_IsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => EdlModeSwitchPlanner.Plan((EdlRebootTarget)99));

    [Fact]
    public void Plan_BootImageTargets_AreExactlyTheTwoNonSystemFirmwareModes()
    {
        var withImage = Enum.GetValues<EdlRebootTarget>()
            .Select(EdlModeSwitchPlanner.Plan)
            .Where(plan => plan.BootloaderControlImage is not null)
            .Select(plan => plan.Target)
            .ToArray();

        Assert.Equal([EdlRebootTarget.Recovery, EdlRebootTarget.Fastbootd], withImage);
    }
}