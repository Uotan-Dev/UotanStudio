using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Signing;

/// <summary>
/// Produces JAR (v1) signature artifacts for an APK: MANIFEST.MF entry digests, the
/// signature file (.SF) and the PKCS#7 CMS ContentInfo (.RSA/.EC/.DSA). Pure managed,
/// AOT-compatible; CMS is built with the platform SignedCms implementation.
/// <para>生成 APK 的 JAR（v1）签名工件：MANIFEST.MF 条目摘要、签名文件（.SF）与 PKCS#7 CMS
/// ContentInfo（.RSA/.EC/.DSA）。AOT 兼容；CMS 使用平台 SignedCms 构建。</para>
/// </summary>
public static class ApkV1Signer
{
    /// <summary>Base name (without extension) of the signature files. 签名文件基础名（不含扩展名）。</summary>
    public const string DefaultSignerName = "UOTAN";

    private const int MaxLineBytes = 72;

    /// <summary>
    /// Builds MANIFEST.MF from entry digests. Each entry section carries the SHA-256
    /// digest of the entry's uncompressed content; directory entries must be excluded by the caller.
    /// <para>由条目摘要构建 MANIFEST.MF。每个条目段携带条目未压缩内容的 SHA-256 摘要；目录条目由调用方排除。</para>
    /// </summary>
    /// <param name="entries">Entry names and their SHA-256 content digests. 条目名与对应的 SHA-256 内容摘要。</param>
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
    /// Builds the signature file (.SF). The main section carries the digest of the whole
    /// MANIFEST.MF (the verification path preferred by Android) and, when v2/v3 is enabled,
    /// the X-Android-APK-Signed attribute protecting those schemes from stripping.
    /// <para>构建签名文件（.SF）。主段携带整个 MANIFEST.MF 的摘要（Android 优先校验路径）；
    /// 当启用 v2/v3 时附带 X-Android-APK-Signed 属性，防止对应方案被剥离。</para>
    /// </summary>
    /// <param name="manifestBytes">Complete MANIFEST.MF bytes. 完整的 MANIFEST.MF 字节。</param>
    /// <param name="protectedSchemeIds">APK signature scheme IDs to protect (e.g. 2 and 3). 要保护的 APK 签名方案 ID（如 2 和 3）。</param>
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
        // Include each manifest section, including its terminating blank line, as apksig does.
        // 与 apksig 一致，对包含末尾空行的每个清单段计算摘要。
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
                ("SHA-256-Digest", Convert.ToBase64String(SHA256.HashData(section)))]);
            start += end + 4;
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Signs the signature file with the user's custom key, producing a PKCS#7 CMS
    /// ContentInfo in DER encoding (detached, like apksig). Uses the platform
    /// SignedCms implementation, AOT-compatible and cross-platform.
    /// <para>使用用户自定义密钥对签名文件签名，产出 DER 编码的 PKCS#7 CMS ContentInfo（分离 SF，与 apksig 一致）。
    /// 使用平台 SignedCms 实现，AOT 兼容且跨平台。</para>
    /// </summary>
    public static byte[] SignSignatureFile(byte[] sfBytes, ApkSignerKeyMaterial keyMaterial)
    {
        // Merge the user's private key into the certificate so SignedCms can sign.
        // 把用户私钥合并进证书，供 SignedCms 签名使用。
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
    public static byte[] Sha256(byte[] content)
        => SHA256.HashData(content);

    /// <summary>
    /// Computes the SHA-256 digest of a stream (whole stream, from current position to end).
    /// <para>计算流的 SHA-256 摘要（从当前位置到流末尾）。</para>
    /// </summary>
    public static byte[] Sha256(Stream content)
        => SHA256.HashData(content);

    // ---- JAR manifest text formatting -------------------------------------------------

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

        // Sections are separated by a fully blank line.
        // 段落之间以完全空行分隔。
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

        // First line up to 72 bytes, continuation lines start with a space and carry 71 more.
        // 首行至多 72 字节，续行以空格开头并携带 71 字节。
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