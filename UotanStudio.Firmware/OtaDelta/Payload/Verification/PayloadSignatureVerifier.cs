using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.OtaDelta.Payload.Models;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.OtaDelta.Payload.Verification;

/// <summary>
/// Reports both RSA signature slots of an A/B payload independently.
/// <para>独立报告 A/B 载荷的两个 RSA 签名槽。</para>
/// </summary>
public sealed class PayloadSignatureVerifyResult
{
    /// <summary>Whether a metadata signature block was present and verified.
    /// <para>元数据签名块是否存在并通过校验。</para></summary>
    /// <value><see langword="true"/> when the slot verifies, <see langword="false"/> when it fails, and <see langword="null"/> when the payload carries none.
    /// <para>槽位校验通过为 <see langword="true"/>，失败为 <see langword="false"/>，载荷未携带时为 <see langword="null"/>。</para></value>
    public bool? MetadataSignatureIsValid { get; init; }

    /// <summary>Whether the payload signatures section was present and verified.
    /// <para>载荷签名区是否存在并通过校验。</para></summary>
    /// <value><see langword="true"/> when the slot verifies, <see langword="false"/> when it fails, and <see langword="null"/> when the manifest declares none.
    /// <para>槽位校验通过为 <see langword="true"/>，失败为 <see langword="false"/>，清单未声明时为 <see langword="null"/>。</para></value>
    public bool? PayloadSignatureIsValid { get; init; }

    /// <summary>Whether at least one signature slot verified and no present slot failed.
    /// <para>至少一个签名槽通过且没有已存在槽位失败。</para></summary>
    /// <value>True for a fully verified signed payload. <para>完全验证通过的已签名载荷为 true。</para></value>
    public bool IsSigned => (MetadataSignatureIsValid == true || PayloadSignatureIsValid == true)
        && MetadataSignatureIsValid != false && PayloadSignatureIsValid != false;

    /// <summary>Diagnostic for a failed parse or verification; <see langword="null"/> on success.
    /// <para>解析或校验失败的诊断；成功时为 <see langword="null"/>。</para></summary>
    /// <value>The failure text. <para>失败文本。</para></value>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Verifies the metadata and payload RSA signatures carried by an Android A/B payload without extracting any
/// partition image.
/// <para>在不提取任何分区镜像的前提下验证 Android A/B 载荷携带的元数据与载荷 RSA 签名。</para>
/// </summary>
/// <remarks>
/// The metadata slot covers the 24-byte CrAU header plus the manifest; the payload slot covers those bytes plus
/// every operation data byte before the signatures section. Both slots are serialized as a nested protobuf
/// whose field 1 carries field 2 (signature bytes) and field 3 (signature length).
/// <para>元数据槽覆盖 24 字节 CrAU 头部与清单；载荷槽覆盖这些字节以及签名区之前的全部操作数据。
/// 两个槽都序列化为嵌套 protobuf：字段 1 内含字段 2（签名字节）与字段 3（签名长度）。</para>
/// </remarks>
public static class PayloadSignatureVerifier
{
    private const int HeaderSize = 24;
    private const long MaximumManifestBytes = 64L * 1024 * 1024;
    private const long MaximumSignatureBytes = 4L * 1024 * 1024;

    /// <summary>Verifies both signature slots with an RSA public key.
    /// <para>使用 RSA 公钥验证两个签名槽。</para></summary>
    /// <param name="payloadPath">Existing payload file; opened read-only and never modified.
    /// <para>既有载荷文件；以只读方式打开，绝不修改。</para></param>
    /// <param name="publicKey">RSA public key matching the signing certificate; the caller keeps ownership.
    /// <para>与签名证书匹配的 RSA 公钥；所有权归调用方。</para></param>
    /// <param name="cancellationToken">Cancels file reads and hashing. <para>取消文件读取与哈希。</para></param>
    /// <returns>Slot-by-slot results; malformed or unsigned payloads are reported instead of thrown.
    /// <para>逐槽结果；畸形或未签名载荷以结果报告而不抛出。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="payloadPath"/> is empty or whitespace.
    /// <para><paramref name="payloadPath"/> 为空或仅含空白。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="publicKey"/> is <see langword="null"/>.
    /// <para><paramref name="publicKey"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FileNotFoundException">The payload does not exist. <para>载荷不存在。</para></exception>
    public static async Task<PayloadSignatureVerifyResult> VerifyAsync(
        string payloadPath,
        RSA publicKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadPath);
        ArgumentNullException.ThrowIfNull(publicKey);
        if (!File.Exists(payloadPath))
        {
            throw new FileNotFoundException("Payload file was not found.", payloadPath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var input = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length < HeaderSize)
            {
                return Fail("The payload is shorter than its CrAU header.");
            }

            var headerBytes = new byte[HeaderSize];
            await input.ReadExactlyAsync(headerBytes, cancellationToken).ConfigureAwait(false);
            var header = PayloadHeader.FromBytes(headerBytes);
            if (header.ManifestLength > MaximumManifestBytes)
            {
                return Fail("The payload manifest exceeds the 64 MiB verification limit.");
            }

            var manifestBytes = new byte[(int)header.ManifestLength];
            await input.ReadExactlyAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
            var metadataSignature = new byte[header.MetadataSignatureLength];
            if (metadataSignature.Length > 0)
            {
                await input.ReadExactlyAsync(metadataSignature, cancellationToken).ConfigureAwait(false);
            }

            var manifest = DeltaArchiveManifest.Parser.ParseFrom(manifestBytes);
            bool? metadataIsValid = null;
            string? error = null;
            if (metadataSignature.Length > 0)
            {
                var rawSignature = ParseSignature(metadataSignature);
                if (rawSignature is null)
                {
                    metadataIsValid = false;
                    error = "The metadata signature block is not a valid AVB-style signature message.";
                }
                else
                {
                    using var hash = Sha256.CreateAccumulator();
                    hash.Append(headerBytes);
                    hash.Append(manifestBytes);
                    metadataIsValid = publicKey.VerifyHash(
                        hash.GetHashAndReset(), rawSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    if (!metadataIsValid.Value)
                    {
                        error = "The metadata signature does not verify with the supplied public key.";
                    }
                }
            }

            bool? payloadIsValid = null;
            var signaturesSize = manifest.SignaturesSize;
            if (signaturesSize > 0)
            {
                if (signaturesSize > MaximumSignatureBytes)
                {
                    payloadIsValid = false;
                    error ??= "The payload signatures section exceeds the 4 MiB verification limit.";
                }
                else
                {
                    var dataStart = HeaderSize + (long)header.ManifestLength + header.MetadataSignatureLength;
                    var dataLength = checked((long)manifest.SignaturesOffset);
                    if (dataLength < 0 || dataLength > input.Length - dataStart - (long)signaturesSize)
                    {
                        payloadIsValid = false;
                        error ??= "The payload signatures section is outside the file.";
                    }
                    else
                    {
                        input.Position = dataStart + dataLength;
                        var signatureBlob = new byte[(int)signaturesSize];
                        await input.ReadExactlyAsync(signatureBlob, cancellationToken).ConfigureAwait(false);
                        var rawSignature = ParseSignature(signatureBlob);
                        if (rawSignature is null)
                        {
                            payloadIsValid = false;
                            error ??= "The payload signatures section is not a valid signature message.";
                        }
                        else
                        {
                            using var hash = Sha256.CreateAccumulator();
                            hash.Append(headerBytes);
                            hash.Append(manifestBytes);
                            input.Position = dataStart;
                            var buffer = new byte[65536];
                            var remaining = dataLength;
                            while (remaining > 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var count = await input.ReadAsync(
                                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                                    cancellationToken).ConfigureAwait(false);
                                if (count == 0)
                                {
                                    return Fail("The payload ended inside its declared data region.");
                                }

                                hash.Append(buffer.AsSpan(0, count));
                                remaining -= count;
                            }

                            payloadIsValid = publicKey.VerifyHash(
                                hash.GetHashAndReset(), rawSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                            if (!payloadIsValid.Value)
                            {
                                error ??= "The payload signature does not verify with the supplied public key.";
                            }
                        }
                    }
                }
            }

            if (metadataIsValid is null && payloadIsValid is null)
            {
                return new PayloadSignatureVerifyResult
                {
                    ErrorMessage = "The payload carries no metadata or payload signature.",
                };
            }

            return new PayloadSignatureVerifyResult
            {
                MetadataSignatureIsValid = metadataIsValid,
                PayloadSignatureIsValid = payloadIsValid,
                ErrorMessage = error,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or CryptographicException
            or InvalidProtocolBufferException or ArgumentException or OverflowException
            or PayloadException)
        {
            return Fail(ex.Message);
        }
    }

    private static byte[]? ParseSignature(ReadOnlySpan<byte> serialized)
    {
        try
        {
            using var outer = new CodedInputStream(serialized.ToArray());
            while (true)
            {
                var tag = outer.ReadTag();
                if (tag == 0)
                {
                    return null;
                }

                if (tag != 10)
                {
                    return null;
                }

                using var inner = new CodedInputStream(outer.ReadBytes().ToByteArray());
                while (true)
                {
                    var innerTag = inner.ReadTag();
                    if (innerTag == 0)
                    {
                        return null;
                    }

                    if (innerTag == 18)
                    {
                        return inner.ReadBytes().ToByteArray();
                    }

                    if (innerTag == 29)
                    {
                        _ = inner.ReadFixed32();
                        continue;
                    }

                    return null;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static PayloadSignatureVerifyResult Fail(string message) => new()
    {
        MetadataSignatureIsValid = false,
        PayloadSignatureIsValid = false,
        ErrorMessage = message,
    };
}