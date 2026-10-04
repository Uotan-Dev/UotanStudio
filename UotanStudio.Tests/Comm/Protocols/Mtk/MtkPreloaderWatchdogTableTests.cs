using UotanStudio.Comm.Protocols.Mtk;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed class MtkPreloaderWatchdogTableTests
{
    [Theory]
    [InlineData(0x2625)]
    [InlineData(0x2523)]
    [InlineData(0x7682)]
    [InlineData(0x7686)]
    [InlineData(0x5932)]
    public void Resolve_FixedAddressChips_ReturnTheSingleRecordedWordWrite(ushort hardwareCode)
    {
        var rules = MtkPreloaderWatchdogTable.Resolve(hardwareCode, 0);

        var rule = Assert.Single(rules);
        Assert.Equal(MtkPreloaderWatchdogOperation.Write16, rule.Operation);
        Assert.Equal(0xA205_0000u, rule.Address);
        Assert.Equal(0x2200u, rule.Value);
    }

    [Theory]
    [InlineData(0xCA02)]
    [InlineData(0xCB01)]
    public void Resolve_Mtk6261Mt2503Revisions_ReturnRguClearIrqAvoidsAndRemapStep(uint hardwareVersion)
    {
        var rules = MtkPreloaderWatchdogTable.Resolve(0x6261, hardwareVersion);

        Assert.Equal(4, rules.Count);
        Assert.Equal(MtkPreloaderWatchdogOperation.Write16, rules[0].Operation);
        Assert.Equal(0xA003_0000u, rules[0].Address);
        Assert.Equal(0x2200u, rules[0].Value);
        Assert.Equal((0x8307_0008u, 0xABCDu), (rules[1].Address, rules[1].Value));
        Assert.Equal((0x8307_0010u, 0x0003u), (rules[2].Address, rules[2].Value));
        Assert.Equal(MtkPreloaderWatchdogOperation.ReadOrWrite32, rules[3].Operation);
        Assert.Equal(0xA051_0000u, rules[3].Address);
        Assert.Equal(0x2u, rules[3].Value);
    }

    [Fact]
    public void Resolve_Mtk6261OtherRevision_KeepsOnlyTheUncommentedRguClear()
    {
        var rules = MtkPreloaderWatchdogTable.Resolve(0x6261, 0x1234);

        var rule = Assert.Single(rules);
        Assert.Equal(MtkPreloaderWatchdogOperation.Write16, rule.Operation);
        Assert.Equal(0xA003_0000u, rule.Address);
    }

    [Fact]
    public void Resolve_Mt6575Family_UsesSixteenBitIoOnTheRecordedBase()
    {
        var rules = MtkPreloaderWatchdogTable.Resolve(0x6575, 0);

        var rule = Assert.Single(rules);
        Assert.Equal(MtkPreloaderWatchdogOperation.Write16, rule.Operation);
        Assert.Equal(0xC000_0000u, rule.Address);
        Assert.Equal(0x2264u, rule.Value);
        Assert.Equal(MtkPreloaderWatchdogTable.Resolve(0x6577, 0), rules);
    }

    [Fact]
    public void Resolve_Mt6592_AppendsTheAdditionalWatchdogRegister()
    {
        var rules = MtkPreloaderWatchdogTable.Resolve(0x6592, 0);

        Assert.Equal(2, rules.Count);
        Assert.Equal((MtkPreloaderWatchdogOperation.Write32, 0x1000_7000u, 0x2200_0064u),
            (rules[0].Operation, rules[0].Address, rules[0].Value));
        Assert.Equal((MtkPreloaderWatchdogOperation.Write32, 0x1000_0500u, 0x2200_0000u),
            (rules[1].Operation, rules[1].Address, rules[1].Value));
    }

    [Theory]
    [InlineData(0x6571, 0x2200_0000u)]
    [InlineData(0x0321, 0x2200_0000u)]
    [InlineData(0x0598, 0x2200_0064u)]
    [InlineData(0x6580, 0x2200_0064u)]
    [InlineData(0x6583, 0x2200_0064u)]
    public void Resolve_DefaultBranchChips_DeriveTheValueFromTheRecordedBase(ushort hardwareCode,
        uint expectedValue)
    {
        var rules = MtkPreloaderWatchdogTable.Resolve(hardwareCode, 0);

        var rule = Assert.Single(rules);
        Assert.Equal(MtkPreloaderWatchdogOperation.Write32, rule.Operation);
        Assert.Equal(expectedValue, rule.Value);
    }

    [Fact]
    public void Resolve_UnknownChip_ReturnsEmptyListSoTheCallerCanRefuse()
    {
        Assert.Empty(MtkPreloaderWatchdogTable.Resolve(0xF00D, 0));
    }

    [Theory]
    [InlineData(0x6580, 0x1000_1838u)]
    [InlineData(0x6582, 0x1000_2050u)]
    [InlineData(0x0766, 0x1001_A100u)]
    [InlineData(0x8172, 0x0120_2050u)]
    public void TryResolveResetRegisters_RecordedChips_ExposeTheMiscLockBase(ushort hardwareCode,
        uint expectedBase)
    {
        Assert.True(MtkPreloaderWatchdogTable.TryResolveResetRegisters(hardwareCode, out var baseAddress));
        Assert.Equal(expectedBase, baseAddress);
    }

    [Fact]
    public void TryResolveResetRegisters_ChipWithoutRecordedField_ReturnsFalseAndZero()
    {
        Assert.False(MtkPreloaderWatchdogTable.TryResolveResetRegisters(0xF00D, out var baseAddress));
        Assert.Equal(0u, baseAddress);
    }
}