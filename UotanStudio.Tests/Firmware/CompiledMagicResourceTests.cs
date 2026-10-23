using UotanStudio.Firmware.Core.Magic;
using UotanStudio.Firmware.Core.Magic.Generated;
using UotanStudio.Hash.Digests;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the compiled magic rule table resource: the embedded zstd frame must match the generator's recorded sizes,
/// decode to the declared length, and hash to the recorded digest before the engine ever reads it.
/// <para>守护编译型魔数规则表资源：内嵌 zstd 帧必须与生成器记录的尺寸一致，解码后长度与摘要必须匹配，引擎才会读取它。</para>
/// </summary>
public class CompiledMagicResourceTests
{
    private const string ResourceName = "UotanStudio.Firmware.Core.Magic.rules.zst";

    [Fact]
    public void EmbeddedResource_CompiledMagicDataFrame_IsEmbeddedWithTheDeclaredLength()
    {
        var assembly = typeof(CompiledMagicEngine).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);

        Assert.NotNull(stream);
        Assert.Equal(CompiledMagicData.CompressedByteLength, checked((int)stream!.Length));
    }

    [Fact]
    public void Rules_EmbeddedFrame_DecodesToTheDeclaredLengthAndDigest()
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