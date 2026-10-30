using UotanStudio.Assets;
using UotanStudio.Firmware.Core.Magic;
using UotanStudio.Firmware.Core.Magic.Generated;
using UotanStudio.Hash.Digests;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the compiled magic rule table asset: the shipped <c>firmware/magic</c> bundle must expose the rule table,
/// decode it to the length and digest the generator recorded, and load the declared top-level rule count.
/// <para>守护编译型魔数规则表资源：随包的 <c>firmware/magic</c> 资源包必须提供规则表，解出的长度与摘要必须与生成器记录一致，并装载声明数量的顶层规则。</para>
/// </summary>
public class CompiledMagicResourceTests
{
    [Fact]
    public void MagicRulesBundle_CompiledMagicDataEntry_ReadsToTheDeclaredLengthAndDigest()
    {
        var bundle = AssetCatalog.MagicRules;

        Assert.True(bundle.IsPresent, "The firmware/magic asset bundle is not shipped in this build.");
        Assert.Contains(bundle.Entries, entry => string.Equals(entry.Path, AssetNames.Magic.Rules, StringComparison.Ordinal));

        var payload = AssetStore.Read(bundle, AssetNames.Magic.Rules);

        Assert.Equal(CompiledMagicData.RulesByteLength, payload.Length);
        Assert.Equal(CompiledMagicData.RulesSha256, Sha256.ComputeHexLower(payload));
    }

    [Fact]
    public void Rules_LoadedTable_MatchesTheGeneratorRecordedLengthAndDigest()
    {
        var rules = CompiledMagicRules.Rules;

        Assert.Equal(CompiledMagicData.RulesByteLength, rules.Length);
        Assert.Equal(CompiledMagicData.RulesSha256, Sha256.ComputeHexLower(rules));
    }

    [Fact]
    public void Rules_RepeatedAccess_ReturnsTheSameDecodedBuffer()
    {
        Assert.Same(CompiledMagicRules.Rules, CompiledMagicRules.Rules);
    }

    [Fact]
    public void EnsureLoaded_GeneratedTable_LoadsTheDeclaredTopLevelRuleCount()
    {
        CompiledMagicEngine.EnsureLoaded();

        Assert.Equal(CompiledMagicData.RuleCount, CompiledMagicEngine.TopLevelCount);
    }
}