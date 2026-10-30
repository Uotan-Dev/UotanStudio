using UotanStudio.Assets;
using UotanStudio.Firmware.Core.Magic.Generated;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.Core.Magic;

/// <summary>
/// Loads the compiled magic rule table from the shipped <c>firmware/magic</c> asset bundle: the table is decoded once
/// per process and cross-checked against the length and SHA-256 the generator recorded.
/// <para>从随包的 <c>firmware/magic</c> 资源包装载编译型规则表：每个进程只解码一次，并对照生成器记录的长度与
/// SHA-256 交叉校验。</para>
/// </summary>
internal static class CompiledMagicRules
{
    private static readonly Lazy<byte[]> Decoded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Serialized rule-table bytes; decoded once per process and shared afterwards.
    /// <para>序列化规则表字节；每个进程只解码一次，之后共享。</para>
    /// </summary>
    /// <exception cref="InvalidDataException">The shipped frame is missing, truncated, corrupt, or the decoded table
    /// does not match <see cref="CompiledMagicData.RulesByteLength"/> and
    /// <see cref="CompiledMagicData.RulesSha256"/>.
    /// <para>随包帧缺失、被截断、损坏，或解码后的规则表与 <see cref="CompiledMagicData.RulesByteLength"/>、
    /// <see cref="CompiledMagicData.RulesSha256"/> 不符。</para></exception>
    /// <exception cref="KeyNotFoundException">This build ships no <c>firmware/magic</c> entry for the rule table.
    /// <para>本次构建未随包 <c>firmware/magic</c> 规则表条目。</para></exception>
    internal static byte[] Rules => Decoded.Value;

    private static byte[] Load()
    {
        // The asset manifest owns storage-level integrity (frame shape, decoded length and digest); the generator
        // constants below pin format-level identity, so a frame produced by a different generator build cannot pass
        // unnoticed.
        // 资源清单负责存储层完整性（帧形态、解码长度与摘要）；下面的生成器常量锁定格式层同一性，
        // 其它生成器构建产出的帧无法蒙混通过。
        var rules = AssetStore.Read(AssetCatalog.MagicRules, AssetNames.Magic.Rules);
        if (rules.Length != CompiledMagicData.RulesByteLength)
        {
            throw new InvalidDataException(
                $"The compiled magic rule table '{AssetNames.Magic.Rules}' is {rules.Length} bytes while the generator recorded {CompiledMagicData.RulesByteLength}. "
                + $"编译型魔数规则表 '{AssetNames.Magic.Rules}' 为 {rules.Length} 字节，与生成器记录的 {CompiledMagicData.RulesByteLength} 字节不符。");
        }

        var digest = Sha256.ComputeHexLower(rules);
        if (!string.Equals(digest, CompiledMagicData.RulesSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The compiled magic rule table '{AssetNames.Magic.Rules}' hashes to {digest} instead of {CompiledMagicData.RulesSha256}. "
                + $"编译型魔数规则表 '{AssetNames.Magic.Rules}' 摘要为 {digest}，与生成器记录的 {CompiledMagicData.RulesSha256} 不符。");
        }

        return rules;
    }
}