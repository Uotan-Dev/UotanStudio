using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.AndroidMeta.Apk.Signing;

/// <summary>
/// Produces JAR (v1) signature artifacts for an APK: MANIFEST.MF entry digests, the
/// signature file (.SF) and the PKCS#7 CMS ContentInfo (.RSA/.EC/.DSA). Pure managed,
/// AOT-compatible; CMS is built with the platform SignedCms implementation.
/// <para>生成 APK 的 JAR（v1）签名工件：MANIFEST.MF 条目摘要、签名文件（.SF）与 PKCS#7 CMS
/// ContentInfo（.RSA/.EC/.DSA）。AOT 兼容；CMS 使用平台 SignedCms 构建。</para>
/// </summary>
internal static class ApkV1Signer
{
    /// <summary>Base name (without extension) of the signature files.
    /// <para>签名文件基础名（不含扩展名）。</para></summary>
    public const string DefaultSignerName = "UOTAN";

    private const int MaxLineBytes = 72;

    /// <summary>
    /// Builds MANIFEST.MF from entry digests. Each entry section carries the SHA-256
    /// digest of the entry's uncompressed content; directory entries must be excluded by the caller.
    /// <para>由条目摘要构建 MANIFEST.MF。每个条目段携带条目未压缩内容的 SHA-256 摘要；目录条目由调用方排除。</para>
    /// </summary>
    /// <param name="entries">Entry names and their SHA-256 content digests.
    /// <para>条目名与对应的 SHA-256 内容摘要。</para></param>
    /// <returns>UTF-8 MANIFEST.MF bytes: main section followed by one Name/SHA-256-Digest section per
    /// entry, CRLF line endings with 72-byte continuation wrapping.
    /// <para>UTF-8 编码的 MANIFEST.MF 字节：主段之后每个条目一个 Name/SHA-256-Digest 段，行尾为 CRLF，
    /// 超过 72 字节按续行折行。</para></returns>
    public static byte[] BuildManifest(IReadOnlyList<(string Name, byte[] Sha256Digest)> entries)
    {
        var main = new List<(string Name, string Value)>
        {
            ("Manifest-Version", "1.0"),
            ("Created-By", "1.0 (UotanStudio)"),
        };
        using var buffer = new MemoryStream();
        WriteSection(buffer, main, individualSections: null);
        foreach (var (name, digest) in entries)
        {
            var section = new List<(string, string)>
            {
                ("Name", name),
                ("SHA-256-Digest", Convert.ToBase64String(digest)),
            };
            WriteSection(buffer, null, section);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Builds MANIFEST.MF and the matching .SF signature file in one pass over the entry digests:
    /// each manifest section is hashed once as it is produced instead of being split and rehashed
    /// from the finished manifest.
    /// <para>对条目摘要只遍历一次即生成 MANIFEST.MF 与配套的 .SF 签名文件：每个清单段在产出时即完成一次
    /// 哈希，不再从成品清单切分后重算。</para>
    /// </summary>
    /// <param name="entries">Entry names and their SHA-256 content digests.
    /// <para>条目名与对应的 SHA-256 内容摘要。</para></param>
    /// <param name="protectedSchemeIds">APK signature scheme IDs to protect (e.g. 2 and 3).
    /// <para>要保护的 APK 签名方案 ID（如 2 和 3）。</para></param>
    /// <returns>The manifest bytes and the signature file bytes, byte-identical to
    /// <see cref="BuildManifest"/> followed by <see cref="BuildSignatureFile"/> for the same input.
    /// <para>清单字节与签名文件字节，与对同一输入分别调用 <see cref="BuildManifest"/> 和
    /// <see cref="BuildSignatureFile"/> 的结果逐字节一致。</para></returns>
    internal static (byte[] Manifest, byte[] SignatureFile) BuildManifestAndSignatureFile(
        IReadOnlyList<(string Name, byte[] Sha256Digest)> entries,
        IReadOnlyList<int> protectedSchemeIds)
    {
        using var manifestBuffer = new MemoryStream();
        using var manifestHash = Hash.Digests.Sha256.CreateAccumulator();
        WriteHashed(manifestBuffer, manifestHash,
            BuildSectionBytes(
                [
                    ("Manifest-Version", "1.0"),
                    ("Created-By", "1.0 (UotanStudio)"),
                ],
                individualSections: null));

        var sections = new List<(string Name, byte[] Digest)>(entries.Count);
        foreach (var (name, digest) in entries)
        {
            var section = BuildSectionBytes(null,
            [
                ("Name", name),
                ("SHA-256-Digest", Convert.ToBase64String(digest)),
            ]);
            sections.Add((ExtractSectionName(section), Hash.Digests.Sha256.ComputeHash(section)));
            WriteHashed(manifestBuffer, manifestHash, section);
        }

        var manifestBytes = manifestBuffer.ToArray();
        using var signatureBuffer = new MemoryStream();
        var main = new List<(string Name, string Value)>
        {
            ("Signature-Version", "1.0"),
            ("Created-By", "1.0 (UotanStudio)"),
        };
        if (protectedSchemeIds.Count > 0)
        {
            main.Add(("X-Android-APK-Signed", string.Join(", ", protectedSchemeIds)));
        }

        main.Add(("SHA-256-Digest-Manifest", Convert.ToBase64String(manifestHash.GetCurrentHash())));
        WriteSection(signatureBuffer, main, individualSections: null);
        foreach (var (name, digest) in sections)
        {
            WriteSection(signatureBuffer, null, [("Name", name), ("SHA-256-Digest", Convert.ToBase64String(digest))]);
        }

        return (manifestBytes, signatureBuffer.ToArray());
    }

    private static byte[] BuildSectionBytes(
        IReadOnlyList<(string, string)>? mainAttributes,
        IReadOnlyList<(string, string)>? individualSections)
    {
        using var buffer = new MemoryStream();
        WriteSection(buffer, mainAttributes, individualSections);
        return buffer.ToArray();
    }

    private static void WriteHashed(Stream output, DigestAccumulator hash, byte[] bytes)
    {
        output.Write(bytes);
        hash.Append(bytes);
    }

    private static string ExtractSectionName(byte[] section)
    {
        var unfolded = System.Text.Encoding.UTF8.GetString(section).Replace("\r\n ", "", StringComparison.Ordinal);
        var nameEnd = unfolded.IndexOf("\r\n", StringComparison.Ordinal);
        if (!unfolded.StartsWith("Name: ", StringComparison.Ordinal) || nameEnd < 6)
            throw new InvalidDataException("Missing JAR section name. JAR 清单段缺少名称。");
        return unfolded[6..nameEnd];
    }

    /// <summary>Builds the v1 signature file (.SF) for the given manifest bytes.
    /// <para>为给定清单字节构建 v1 签名文件（.SF）。</para></summary>
    /// <param name="manifestBytes">Complete MANIFEST.MF bytes.<para>完整的 MANIFEST.MF 字节。</para></param>
    /// <param name="protectedSchemeIds">APK signature scheme IDs to protect (e.g. 2 and 3).
    /// <para>要保护的 APK 签名方案 ID（如 2 和 3）。</para></param>
    /// <returns>UTF-8 .SF bytes: main section with SHA-256-Digest-Manifest plus, per manifest section, a Name/SHA-256-Digest pair mirroring the manifest order.
    /// <para>UTF-8 的 .SF 字节：含 SHA-256-Digest-Manifest 的主段，随后按清单顺序为每个清单段生成 Name/SHA-256-Digest 对。</para></returns>
    /// <exception cref="InvalidDataException">The manifest is malformed: a section lacks the leading <c>Name: </c> attribute or the trailing blank line.
    /// <para>清单格式无效：某段缺少开头的 <c>Name: </c> 属性或结尾空行。</para></exception>
    public static byte[] BuildSignatureFile(byte[] manifestBytes, IReadOnlyList<int> protectedSchemeIds)
    {
        var main = new List<(string Name, string Value)>
        {
            ("Signature-Version", "1.0"),
            ("Created-By", "1.0 (UotanStudio)"),
        };
        if (protectedSchemeIds.Count > 0)
        {
            main.Add(("X-Android-APK-Signed", string.Join(", ", protectedSchemeIds)));
        }

        main.Add(("SHA-256-Digest-Manifest", Convert.ToBase64String(Sha256(manifestBytes))));

        using var buffer = new MemoryStream();
        WriteSection(buffer, main, individualSections: null);
        var start = manifestBytes.AsSpan().IndexOf("\r\n\r\n"u8);
        start = start < 0 ? manifestBytes.Length : start + 4;
        while (start < manifestBytes.Length)
        {
            var end = manifestBytes.AsSpan(start).IndexOf("\r\n\r\n"u8);
            if (end < 0) throw new InvalidDataException("Invalid JAR manifest section. JAR 清单段无效。");
            var section = manifestBytes.AsSpan(start, end + 4);
            var unfolded = System.Text.Encoding.UTF8.GetString(section).Replace("\r\n ", "", StringComparison.Ordinal);
            var nameEnd = unfolded.IndexOf("\r\n", StringComparison.Ordinal);
            if (!unfolded.StartsWith("Name: ", StringComparison.Ordinal) || nameEnd < 6)
                throw new InvalidDataException("Missing JAR section name. JAR 清单段缺少名称。");
            WriteSection(buffer, null, [("Name", unfolded[6..nameEnd]),
                ("SHA-256-Digest", Convert.ToBase64String(Hash.Digests.Sha256.ComputeHash(section)))]);
            start += end + 4;
        }
        return buffer.ToArray();
    }

    /// <summary>Signs the signature file with the user's custom key, producing a detached PKCS#7 CMS ContentInfo in DER encoding. Uses the platform SignedCms implementation, AOT-compatible and cross-platform.
    /// <para>使用用户自定义密钥对签名文件签名，产出 DER 编码的分离式 PKCS#7 CMS ContentInfo。 使用平台 SignedCms 实现，AOT 兼容且跨平台。</para></summary>
    /// <param name="sfBytes">Complete signature file bytes to sign.<para>待签名的完整签名文件字节。</para></param>
    /// <param name="keyMaterial">Private key and leaf certificate used for the CMS signature.
    /// <para>用于 CMS 签名的私钥与叶子证书。</para></param>
    /// <returns>DER-encoded PKCS#7 CMS ContentInfo with a detached SHA-256 signature over <paramref name="sfBytes"/>.
    /// <para>DER 编码的 PKCS#7 CMS ContentInfo，携带对 <paramref name="sfBytes"/> 的分离式 SHA-256 签名。</para></returns>
    /// <exception cref="NotSupportedException">The key material carries neither an RSA nor an ECDSA key.
    /// <para>密钥材料既不是 RSA 也不是 ECDSA 私钥。</para></exception>
    public static byte[] SignSignatureFile(byte[] sfBytes, ApkSignerKeyMaterial keyMaterial)
    {
        X509Certificate2 certificateWithKey = keyMaterial.Key switch
        {
            RSA rsa => keyMaterial.Certificate.CopyWithPrivateKey(rsa),
            ECDsa ec => keyMaterial.Certificate.CopyWithPrivateKey(ec),
            _ => throw new NotSupportedException(
                $"Key type {keyMaterial.Key.GetType().Name} is not supported for v1 signing; expected RSA or ECDSA. 密钥类型 {keyMaterial.Key.GetType().Name} 不支持 v1 签名，期望 RSA 或 ECDSA。"),
        };

        try
        {
            var contentInfo = new ContentInfo(sfBytes);
            var signedCms = new SignedCms(contentInfo, detached: true);
            var cmsSigner = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificateWithKey)
            {
                IncludeOption = X509IncludeOption.EndCertOnly,
                DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            };
            signedCms.ComputeSignature(cmsSigner, silent: true);
            return signedCms.Encode();
        }
        finally
        {
            certificateWithKey.Dispose();
        }
    }

    /// <summary>
    /// Returns the signature block file extension for the key type (.RSA/.EC/.DSA).
    /// <para>按键类型返回签名块文件扩展名（.RSA/.EC/.DSA）。</para>
    /// </summary>
    /// <param name="key">Signing key whose type selects the extension.
    /// <para>由密钥类型决定扩展名的签名密钥。</para></param>
    /// <returns><c>".RSA"</c> for RSA, <c>".EC"</c> for ECDSA, <c>".DSA"</c> for DSA.
    /// <para>RSA 返回 <c>".RSA"</c>，ECDSA 返回 <c>".EC"</c>，DSA 返回 <c>".DSA"</c>。</para></returns>
    /// <exception cref="NotSupportedException">The key type has no defined extension.
    /// <para>该密钥类型没有对应扩展名。</para></exception>
    public static string SignatureBlockExtension(AsymmetricAlgorithm key) => key switch
    {
        RSA => ".RSA",
        ECDsa => ".EC",
        DSA => ".DSA",
        _ => throw new NotSupportedException(
            $"Key type {key.GetType().Name} has no signature block extension. 密钥类型 {key.GetType().Name} 没有对应的签名块扩展名。"),
    };

    /// <summary>
    /// Computes the SHA-256 digest of entry content (convenience wrapper).
    /// <para>计算条目内容的 SHA-256 摘要（便捷封装）。</para>
    /// </summary>
    /// <param name="content">Bytes to hash. <para>待摘要的字节。</para></param>
    /// <returns>The 32-byte SHA-256 digest. <para>32 字节的 SHA-256 摘要。</para></returns>
    public static byte[] Sha256(byte[] content)
        => Hash.Digests.Sha256.ComputeHash(content);

    /// <summary>
    /// Computes the SHA-256 digest of a stream (whole stream, from current position to end).
    /// <para>计算流的 SHA-256 摘要（从当前位置到流末尾）。</para>
    /// </summary>
    /// <param name="content">Readable stream; not consumed past its end and not closed.
    /// <para>可读流；读到末尾即止，且不被关闭。</para></param>
    /// <returns>The 32-byte SHA-256 digest. <para>32 字节的 SHA-256 摘要。</para></returns>
    public static byte[] Sha256(Stream content)
        => Hash.Digests.Sha256.ComputeHash(content);

    /// <summary>
    /// Writes one manifest section: main section header lines or a named individual section,
    /// terminated by a blank line; long lines wrap at 72 bytes with a single-space continuation.
    /// <para>写入一个清单段：主段头部或具名独立段，以空行结束；超过 72 字节的行按 JAR 规范以单空格续行折行。</para>
    /// </summary>
    private static void WriteSection(Stream output, IReadOnlyList<(string, string)>? mainAttributes, IReadOnlyList<(string, string)>? individualSections)
    {
        if (mainAttributes is not null)
        {
            foreach (var (name, value) in mainAttributes)
            {
                WriteAttribute(output, $"{name}: {value}");
            }
        }
        else if (individualSections is not null)
        {
            foreach (var (name, value) in individualSections)
            {
                WriteAttribute(output, $"{name}: {value}");
            }
        }

        output.Write("\r\n"u8);
    }

    private static void WriteAttribute(Stream output, string line)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        if (bytes.Length <= MaxLineBytes)
        {
            output.Write(bytes);
            output.Write("\r\n"u8);
            return;
        }

        var offset = 0;
        var first = true;
        while (offset < bytes.Length)
        {
            var take = first ? MaxLineBytes : MaxLineBytes - 1;
            take = Math.Min(take, bytes.Length - offset);
            if (!first)
            {
                output.Write(" "u8);
            }

            output.Write(bytes.AsSpan(offset, take));
            output.Write("\r\n"u8);
            offset += take;
            first = false;
        }
    }
}