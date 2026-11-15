using System.Buffers.Binary;

namespace UotanStudio.Firmware.AndroidMeta.Apk.Signing;

/// <summary>
/// Parses the v2/v3 signer structures recorded inside an APK Signing Block value, mirroring the
/// layout the project's signer writes (length-prefixed sequences, little-endian).
/// <para>解析 APK 签名分块值中记录的 v2/v3 签名者结构，与本项目签名器写出的布局
/// （带长度前缀的序列，小端序）互为镜像。</para></summary>
public static class ApkV2V3SignatureReader
{
    /// <summary>
    /// One signer parsed from a v2/v3 block value.
    /// <para>从 v2/v3 分块值解析出的一个签名者。</para></summary>
    public sealed class BlockSigner
    {
        /// <summary>The signed-data chunk bytes the signatures are made over.
        /// <para>签名所覆盖的签名数据分块字节。</para></summary>
        public byte[] SignedData { get; init; } = [];

        /// <summary>Digest entries recorded in the signed data (algorithm ID and digest bytes).
        /// <para>签名数据中记录的摘要条目（算法 ID 与摘要字节）。</para></summary>
        public IReadOnlyList<(uint AlgorithmId, byte[] Digest)> Digests { get; init; } = [];

        /// <summary>Certificate chain bytes, leaf first. <para>证书链字节，叶子在前。</para></summary>
        public IReadOnlyList<byte[]> Certificates { get; init; } = [];

        /// <summary>Signature entries (algorithm ID and signature bytes).
        /// <para>签名条目（算法 ID 与签名字节）。</para></summary>
        public IReadOnlyList<(uint AlgorithmId, byte[] Signature)> Signatures { get; init; } = [];

        /// <summary>Recorded subject public key info bytes. <para>记录的主体公钥信息字节。</para></summary>
        public byte[] PublicKey { get; init; } = [];

        /// <summary>Declared minimum SDK (v3 only). <para>声明的最低 SDK（仅 v3）。</para></summary>
        public int? MinSdk { get; init; }

        /// <summary>Declared maximum SDK (v3 only). <para>声明的最高 SDK（仅 v3）。</para></summary>
        public int? MaxSdk { get; init; }

        /// <summary>Whether the signed data carries the anti-stripping protection attribute (0xbeeff00d).
        /// <para>签名数据是否携带防剥离保护属性（0xbeeff00d）。</para></summary>
        public bool HasProtectionAttribute { get; init; }
    }

    /// <summary>Parses the signer sequence of one v2/v3 block value. The value is a single
    /// <c>length-prefixed sequence of length-prefixed signers</c>, so the outer prefix is unwrapped
    /// before the individual signers are read.
    /// <para>解析单个 v2/v3 分块值的签名者序列。该值整体是一层"长度前缀的、由长度前缀签名者组成的序列"，
    /// 因此先剥掉外层长度前缀，再逐个读取签名者。</para></summary>
    /// <param name="blockValue">The value of the v2/v3 ID-value pair.
    /// <para>v2/v3 ID-值对的值部分。</para></param>
    /// <param name="v3">Whether the value follows the v3 signer layout (SDK fields between signed data and signatures).
    /// <para>该值是否遵循 v3 签名者布局（签名数据与签名之间带 SDK 字段）。</para></param>
    /// <returns>Signers in stored order. <para>按存储顺序的签名者。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="blockValue"/> is <see langword="null"/>.
    /// <para><paramref name="blockValue"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The outer sequence prefix is truncated, overruns the value, or does not describe the whole value; a signer length prefix is truncated or overruns its region.
    /// <para>外层序列前缀被截断、越过值边界或未覆盖整个值；某个签名者长度前缀被截断或越出其区域。</para></exception>
    public static List<BlockSigner> ParseSigners(byte[] blockValue, bool v3)
    {
        ArgumentNullException.ThrowIfNull(blockValue);
        if (blockValue.Length < 4)
        {
            throw new InvalidDataException("The v2/v3 block value is shorter than its outer sequence length prefix. v2/v3 分块值短于外层序列长度前缀。");
        }

        var sequenceLength = BinaryPrimitives.ReadUInt32LittleEndian(blockValue.AsSpan(0, 4));
        if (sequenceLength != blockValue.Length - 4)
        {
            throw new InvalidDataException(
                $"The v2/v3 block value's outer sequence length ({sequenceLength}) does not cover the value body ({blockValue.Length - 4} bytes). v2/v3 分块值的外层序列长度（{sequenceLength}）未覆盖值主体（{blockValue.Length - 4} 字节）。");
        }

        var signers = new List<BlockSigner>();
        foreach (var signerBytes in ReadSequence(blockValue[4..], requireExact: true))
        {
            signers.Add(ParseSigner(signerBytes, v3));
        }
        return signers;
    }

    /// <summary>Parses one signer structure.
    /// <para>解析单个签名者结构。</para></summary>
    private static BlockSigner ParseSigner(byte[] signer, bool v3)
    {
        var offset = 0;
        var signedData = ReadBlock(signer, ref offset, "signed data");
        int? minSdk = null;
        int? maxSdk = null;
        if (v3)
        {
            if (signer.Length - offset < 8) throw new InvalidDataException("v3 signer truncated before the SDK fields.");
            minSdk = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(signer.AsSpan(offset)));
            maxSdk = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(signer.AsSpan(offset + 4)));
            offset += 8;
        }
        var signatures = ReadBlock(signer, ref offset, "signatures");
        var publicKey = ReadBlock(signer, ref offset, "public key");

        var signedDataOffset = 0;
        var digestsSequence = ReadBlock(signedData, ref signedDataOffset, "digests");
        var certificatesSequence = ReadBlock(signedData, ref signedDataOffset, "certificates");
        byte[] attributesSequence;
        if (v3)
        {
            if (signedData.Length - signedDataOffset < 8) throw new InvalidDataException("v3 signed data truncated before the SDK fields.");
            minSdk = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(signedData.AsSpan(signedDataOffset)));
            maxSdk = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(signedData.AsSpan(signedDataOffset + 4)));
            signedDataOffset += 8;
        }
        attributesSequence = ReadBlock(signedData, ref signedDataOffset, "attributes");

        var digests = new List<(uint AlgorithmId, byte[] Digest)>();
        foreach (var entry in ReadSequence(digestsSequence, requireExact: false))
        {
            if (entry.Length < 4) throw new InvalidDataException("Digest entry shorter than the algorithm id.");
            var algorithmId = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(0, 4));
            var digestOffset = 4;
            digests.Add((algorithmId, ReadBlock(entry, ref digestOffset, "digest bytes")));
        }

        var signatureEntries = new List<(uint AlgorithmId, byte[] Signature)>();
        foreach (var entry in ReadSequence(signatures, requireExact: false))
        {
            if (entry.Length < 4) throw new InvalidDataException("Signature entry shorter than the algorithm id.");
            var algorithmId = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(0, 4));
            var signatureOffset = 4;
            signatureEntries.Add((algorithmId, ReadBlock(entry, ref signatureOffset, "signature bytes")));
        }

        var certificates = new List<byte[]>();
        foreach (var certificate in ReadSequence(certificatesSequence, requireExact: false))
        {
            certificates.Add(certificate);
        }

        return new BlockSigner
        {
            SignedData = signedData,
            Digests = digests,
            Certificates = certificates,
            Signatures = signatureEntries,
            PublicKey = publicKey,
            MinSdk = minSdk,
            MaxSdk = maxSdk,
            HasProtectionAttribute = ContainsProtectionAttribute(attributesSequence)
        };
    }

    /// <summary>Checks the attribute sequence for the anti-stripping protection id.
    /// <para>检查属性序列中的防剥离保护 ID。</para></summary>
    private static bool ContainsProtectionAttribute(byte[] attributesSequence)
    {
        foreach (var attribute in ReadSequence(attributesSequence, requireExact: false))
        {
            if (attribute.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(attribute.AsSpan(0, 4)) == 0xbeeff00d)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Reads one u32-length-prefixed block and advances the offset past it.
    /// <para>读取一个 u32 长度前缀块并把偏移推进到其后。</para></summary>
    private static byte[] ReadBlock(byte[] data, ref int offset, string name)
    {
        if (data.Length - offset < 4) throw new InvalidDataException($"Truncated {name} length prefix.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        if (length > data.Length - offset - 4) throw new InvalidDataException($"{name} block overruns its region.");
        var block = data[(offset + 4)..(offset + 4 + (int)length)];
        offset += 4 + (int)length;
        return block;
    }

    /// <summary>Reads the remaining region as a sequence of u32-length-prefixed entries.
    /// <para>把剩余区域读作 u32 长度前缀条目的序列。</para></summary>
    private static List<byte[]> ReadSequence(byte[] data, bool requireExact)
    {
        var entries = new List<byte[]>();
        var offset = 0;
        while (data.Length - offset >= 4)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            if (length > data.Length - offset - 4)
            {
                if (requireExact) throw new InvalidDataException("Sequence entry overruns its region.");
                break;
            }
            entries.Add(data[(offset + 4)..(offset + 4 + (int)length)]);
            offset += 4 + (int)length;
        }
        return entries;
    }
}