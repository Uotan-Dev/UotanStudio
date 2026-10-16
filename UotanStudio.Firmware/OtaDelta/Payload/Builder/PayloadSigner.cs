using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.OtaDelta.Payload.Models;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.OtaDelta.Payload.Builder;

/// <summary>
/// Re-signs an existing payload.bin without rebuilding its partitions: the header, manifest and data blob are
/// carried over byte-for-byte except for the manifest signature bookkeeping, and both signature slots are
/// recomputed with RSA SHA-256 PKCS#1 v1.5 exactly as <see cref="PayloadBuilder"/> produces them during a
/// signed build (metadata signature covers header plus manifest; payload signature covers header, manifest and
/// the operation data region).
/// <para>在不重建分区的前提下为既有 payload.bin 重新签名：头部、清单与数据区逐字节原样迁移（仅清单内签名记账
/// 字段随之更新），两个签名槽按 <see cref="PayloadBuilder"/> 签名构建的同一语义以 RSA SHA-256 PKCS#1 v1.5
/// 重算（元数据签名覆盖头部加清单；载荷签名覆盖头部、清单与操作数据区）。</para>
/// </summary>
/// <remarks>
/// Works on unsigned payloads (first signing), on payloads signed with a key of the same modulus size, and on
/// payloads whose key modulus changes; when the modulus changes, the manifest and metadata-signature lengths
/// shift and the operation data offsets stay valid because they are relative to the blob base. Output is
/// published atomically through a sibling temporary file. Re-signing deliberately caps the manifest at
/// 64 MiB, tighter than the 512 MiB read-path ceiling, because the whole manifest is materialized in memory
/// and real manifests stay orders of magnitude below it.
/// <para>可用于未签名载荷（首次签名）、同模数密钥重签与更换模数密钥的重签；模数变化时清单与元数据签名长度
/// 随之偏移，而操作数据偏移相对 blob 基址，仍然有效。输出经同目录临时文件原子发布。重签刻意把清单上限
/// 收紧到 64 MiB（读取路径为 512 MiB），因为整份清单会驻留内存，而真实清单远小于该值。</para>
/// </remarks>
internal static class PayloadSigner
{
    /// <summary>Re-signs a payload file with the given private key. <para>使用给定私钥为载荷文件重新签名。</para></summary>
    /// <param name="payloadPath">
    /// Existing signed or unsigned payload; opened for reading only and never modified.
    /// <para>既有已签或未签载荷；只读打开，绝不原地修改。</para>
    /// </param>
    /// <param name="signingKey">
    /// RSA key with a 2048- or 4096-bit modulus; ownership stays with the caller and it is never disposed here.
    /// <para>模数为 2048 或 4096 位的 RSA 密钥；所有权归调用方，本方法绝不释放。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination file, replaced atomically only after a complete re-sign; may be the same path as
    /// <paramref name="payloadPath"/>. <para>目标文件，仅在完整重签后原子替换；可与 <paramref name="payloadPath"/> 相同。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels before and during the copy; a cancelled re-sign never touches the existing output.
    /// <para>在复制前后响应取消；被取消的重签不会触及既有输出。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// A path is empty or whitespace, or the key modulus is neither 2048 nor 4096 bits.
    /// <para>路径为空或仅含空白，或密钥模数既非 2048 也非 4096 位。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="payloadPath"/>, <paramref name="signingKey"/> or <paramref name="outputPath"/> is
    /// <see langword="null"/>. <para><paramref name="payloadPath"/>、<paramref name="signingKey"/> 或
    /// <paramref name="outputPath"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="payloadPath"/> does not exist. <para><paramref name="payloadPath"/> 不存在。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The input is not a structurally consistent major-version-2 payload, or its declared signature region
    /// does not match the file size. <para>输入不是结构一致的主版本 2 载荷，或其声明的签名区域与文件大小不符。</para>
    /// </exception>
    public static void Resign(string payloadPath, RSA signingKey, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.KeySize is not (2048 or 4096))
        {
            throw new ArgumentException("Payload signing requires RSA 2048 or 4096.", nameof(signingKey));
        }

        if (!File.Exists(payloadPath))
        {
            throw new FileNotFoundException($"Payload file not found: {payloadPath}", nameof(payloadPath));
        }

        var destination = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = TempFileHelper.CreateAdjacentPath(destination);
        try
        {
            using (var input = File.OpenRead(payloadPath))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ResignCore(input, output, signingKey, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            AtomicPublish.Publish(temporary, destination);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>Re-signs a payload read from a seekable stream into a writable stream. <para>从可定位流读取载荷并重签名到可写流。</para></summary>
    /// <param name="payload">
    /// Seekable payload source read from position <c>0</c>; left open. <para>从位置 <c>0</c> 读取的可定位载荷源；保持打开。</para>
    /// </param>
    /// <param name="signingKey">
    /// RSA key with a 2048- or 4096-bit modulus; not disposed here. <para>模数为 2048 或 4096 位的 RSA 密钥；不在此释放。</para>
    /// </param>
    /// <param name="output">
    /// Writable sink receiving the resigned payload from its current position; left open.
    /// <para>从其当前位置接收重签载荷的可写接收流；保持打开。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels during the data copy. <para>在数据复制期间响应取消。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// A stream is unusable or the key modulus is neither 2048 nor 4096 bits. <para>流不可用，或密钥模数既非 2048 也非 4096 位。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The input is not a structurally consistent major-version-2 payload. <para>输入不是结构一致的主版本 2 载荷。</para>
    /// </exception>
    public static void Resign(Stream payload, RSA signingKey, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(signingKey);
        ArgumentNullException.ThrowIfNull(output);
        if (signingKey.KeySize is not (2048 or 4096))
        {
            throw new ArgumentException("Payload signing requires RSA 2048 or 4096.", nameof(signingKey));
        }

        if (!payload.CanSeek || !payload.CanRead)
        {
            throw new ArgumentException("The payload stream must be readable and seekable.", nameof(payload));
        }

        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable.", nameof(output));
        }

        payload.Position = 0;
        ResignCore(payload, output, signingKey, cancellationToken);
    }

    private static void ResignCore(Stream input, Stream output, RSA signingKey, CancellationToken cancellationToken)
    {
        var header = PayloadHeader.ReadFrom(input);
        if (header.ManifestLength > 64L * 1024 * 1024)
        {
            throw new InvalidDataException("Payload manifest exceeds the 64 MiB re-sign ceiling.");
        }

        var manifestBytes = new byte[(int)header.ManifestLength];
        input.ReadExactly(manifestBytes);
        var oldMetadataSignature = new byte[header.MetadataSignatureLength];
        input.ReadExactly(oldMetadataSignature);
        var manifest = DeltaArchiveManifest.Parser.ParseFrom(manifestBytes);
        var blobStart = input.Position;
        var blobLength = checked(input.Length - blobStart);
        if (blobLength < 0 || manifest.SignaturesSize > (ulong)blobLength)
        {
            throw new InvalidDataException("Payload signature region exceeds the file.");
        }

        var dataLength = checked(blobLength - (long)manifest.SignaturesSize);
        var signatureSize = SerializeSignature(new byte[signingKey.KeySize / 8]).Length;
        manifest.SignaturesOffset = checked((ulong)dataLength);
        manifest.SignaturesSize = checked((ulong)signatureSize);
        if (manifest.CalculateSize() > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("Payload manifest exceeds 64 MiB.");
        }

        var newManifestBytes = manifest.ToByteArray();
        if (dataLength == 0)
        {
            using var padded = new MemoryStream();
            padded.Write(newManifestBytes);
            padded.Write([0x20, 0x00]);
            newManifestBytes = padded.ToArray();
        }

        var newHeader = PayloadHeaderBuilder.BuildHeader((ulong)newManifestBytes.Length, checked((uint)signatureSize));
        using (var hash = Sha256.CreateAccumulator())
        {
            hash.Append(newHeader);
            hash.Append(newManifestBytes);
            var metadataSignature = SerializeSignature(signingKey.SignHash(hash.GetCurrentHash(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(newHeader);
            output.Write(newManifestBytes);
            output.Write(metadataSignature);

            input.Position = blobStart;
            var buffer = new byte[64 * 1024];
            var remaining = dataLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                if (count == 0)
                {
                    throw new EndOfStreamException("Payload ended before its declared data region.");
                }

                hash.Append(buffer.AsSpan(0, count));
                output.Write(buffer, 0, count);
                remaining -= count;
            }

            var payloadSignature = SerializeSignature(signingKey.SignHash(hash.GetHashAndReset(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(payloadSignature);
        }

        output.Flush();
    }

    private static byte[] SerializeSignature(byte[] signature)
    {
        using var nested = new MemoryStream();
        using (var writer = new CodedOutputStream(nested, leaveOpen: true))
        {
            writer.WriteTag(2, WireFormat.WireType.LengthDelimited);
            writer.WriteBytes(ByteString.CopyFrom(signature));
            writer.WriteTag(3, WireFormat.WireType.Fixed32);
            writer.WriteFixed32((uint)signature.Length);
        }

        using var result = new MemoryStream();
        using (var writer = new CodedOutputStream(result, leaveOpen: true))
        {
            writer.WriteTag(1, WireFormat.WireType.LengthDelimited);
            writer.WriteBytes(ByteString.CopyFrom(nested.ToArray()));
        }

        return result.ToArray();
    }
}