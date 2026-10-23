using UotanStudio.Compress;
using UotanStudio.Compress.Codecs.Zstd;
using UotanStudio.Firmware.Core.Magic.Generated;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.Core.Magic;

/// <summary>
/// Loads the embedded compiled magic rule table: one zstd frame decoded into memory on first use and verified against
/// the length and SHA-256 the generator recorded.
/// <para>装载内嵌的编译型规则表：首次使用时把一个 zstd 帧解码进内存，并对照生成器记录的长度与 SHA-256 校验。</para>
/// </summary>
internal static class CompiledMagicRules
{
    private const string ResourceName = "UotanStudio.Firmware.Core.Magic.rules.zst";
    private const int MaximumCompressedBytes = 4 * 1024 * 1024;

    private static readonly Lazy<byte[]> Decoded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Serialized rule-table bytes; decoded once per process and shared afterwards.
    /// <para>序列化规则表字节；每个进程只解码一次，之后共享。</para>
    /// </summary>
    /// <exception cref="InvalidDataException">The embedded frame is missing, truncated, corrupt, or does not match the
    /// generator's declared length and digest.
    /// <para>内嵌帧缺失、被截断、损坏，或与生成器声明长度、摘要不符。</para></exception>
    internal static byte[] Rules => Decoded.Value;

    private static byte[] Load()
    {
        var compressed = ReadFrame();
        byte[] rules;
        try
        {
            rules = ZstdBlockCodec.Decompress(compressed, CompiledMagicData.RulesByteLength);
        }
        catch (CompressionException error)
        {
            throw new InvalidDataException(
                $"The embedded compiled magic rule frame is not a valid zstd frame of {CompiledMagicData.RulesByteLength} bytes. "
                + $"内嵌的编译型魔数规则帧不是 {CompiledMagicData.RulesByteLength} 字节的有效 zstd 帧。",
                error);
        }

        var digest = Sha256.ComputeHexLower(rules);
        if (!string.Equals(digest, CompiledMagicData.RulesSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The embedded compiled magic rule table hashes to {digest} instead of {CompiledMagicData.RulesSha256}. "
                + $"内嵌的编译型魔数规则表摘要为 {digest}，与记录的 {CompiledMagicData.RulesSha256} 不符。");
        }

        return rules;
    }

    private static byte[] ReadFrame()
    {
        var assembly = typeof(CompiledMagicRules).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException(
                $"The compiled magic rule resource '{ResourceName}' is missing from {assembly.GetName().Name}. "
                + $"程序集 {assembly.GetName().Name} 中缺少编译型魔数规则资源 '{ResourceName}'。");

        if (stream.Length is <= 0 or > MaximumCompressedBytes)
        {
            throw new InvalidDataException(
                $"The compiled magic rule resource '{ResourceName}' declares {stream.Length} bytes, which is outside the accepted range. "
                + $"编译型魔数规则资源 '{ResourceName}' 声明 {stream.Length} 字节，超出可接受范围。");
        }

        var compressed = new byte[checked((int)stream.Length)];
        stream.ReadExactly(compressed);
        if (compressed.Length != CompiledMagicData.CompressedByteLength)
        {
            throw new InvalidDataException(
                $"The compiled magic rule resource is {compressed.Length} bytes while the generator recorded {CompiledMagicData.CompressedByteLength}. "
                + $"编译型魔数规则资源为 {compressed.Length} 字节，与生成器记录的 {CompiledMagicData.CompressedByteLength} 字节不符。");
        }

        return compressed;
    }
}