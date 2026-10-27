using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using UotanStudio.Firmware.Core.Text;

namespace UotanStudio.Firmware.BootSecurity.Avb.Signing;

public static partial class AvbSigner
{
    /// <summary>
    /// Rebuilds and signs a VBMeta image with a verbatim descriptor table, preserving descriptor kinds the typed
    /// signer does not model.
    /// <para>使用原样的描述符表重建并签名 VBMeta 镜像，保留类型化签名器未建模的描述符种类。</para>
    /// </summary>
    /// <param name="vbmetaPath">Source VBMeta supplying required version, rollback, flags, release string and the header slots; must exist and carry the <c>AVB0</c> magic.
    /// <para>提供必需版本、回滚、标志、发布字符串与头部槽位的源 VBMeta；必须存在且带 <c>AVB0</c> 魔数。</para></param>
    /// <param name="key">RSA private key with a 2048, 4096 or 8192-bit modulus; the caller keeps ownership.
    /// <para>模数为 2048、4096 或 8192 位的 RSA 私钥；所有权归调用方。</para></param>
    /// <param name="outputPath">Destination published atomically after the generated signature validates; must differ from <paramref name="vbmetaPath"/>.
    /// <para>生成的签名通过校验后原子发布的目标路径；必须与 <paramref name="vbmetaPath"/> 不同。</para></param>
    /// <param name="descriptors">Descriptor table serialized verbatim into the auxiliary block; at most 16 MiB.
    /// <para>原样序列化进辅助块的描述符表；至多 16 MiB。</para></param>
    /// <param name="cancellationToken">Cancels the header read and output publication.
    /// <para>取消头部读取与输出发布。</para></param>
    /// <returns>Success with the algorithm and descriptor count, or a failure result explaining rejected input.
    /// <para>包含算法与描述符数量的成功结果，或说明拒绝原因的失败结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="descriptors"/> is <see langword="null"/>.
    /// <para><paramref name="key"/> 或 <paramref name="descriptors"/> 为 <see langword="null"/>。</para></exception>
    public static async Task<AvbSignResult> SignVbmetaWithDescriptorTableAsync(
        string vbmetaPath,
        RSA key,
        string outputPath,
        AvbDescriptorTable descriptors,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(vbmetaPath))
        {
            return Fail("vbmetaPath must be provided. 必须提供 vbmetaPath。");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return Fail("outputPath must be provided. 必须提供 outputPath。");
        }

        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(descriptors);

        try
        {
            if (!File.Exists(vbmetaPath))
            {
                return Fail($"vbmeta source file '{vbmetaPath}' does not exist. vbmeta 源文件 '{vbmetaPath}' 不存在。");
            }

            if (PathsEqual(vbmetaPath, outputPath))
            {
                return Fail("Output path must differ from the source vbmeta. 输出路径不能与源 vbmeta 相同。");
            }

            var descriptorBytes = descriptors.ToArray();
            if (descriptorBytes.Length > 16 * 1024 * 1024)
            {
                return Fail("The descriptor table exceeds the 16 MiB limit. 描述符表超过 16 MiB 上限。");
            }

            var keyBits = key.KeySize;
            var algorithmType = GetAlgorithmType(keyBits);
            var headerBytes = await ReadHeaderBytesAsync(vbmetaPath, cancellationToken).ConfigureAwait(false);
            if (headerBytes == null || !headerBytes.AsSpan(0, 4).SequenceEqual(MagicBytes))
            {
                return Fail($"'{vbmetaPath}' is not a valid AVB0 VBMeta image. '{vbmetaPath}' 不是有效的 AVB0 VBMeta 镜像。");
            }

            var requiredMajor = BinaryPrimitives.ReadUInt32BigEndian(headerBytes.AsSpan(4));
            var requiredMinor = BinaryPrimitives.ReadUInt32BigEndian(headerBytes.AsSpan(8));
            var rollbackIndex = BinaryPrimitives.ReadUInt64BigEndian(headerBytes.AsSpan(112));
            var flags = BinaryPrimitives.ReadUInt32BigEndian(headerBytes.AsSpan(120));
            var rollbackIndexLocation = BinaryPrimitives.ReadUInt32BigEndian(headerBytes.AsSpan(124));
            var releaseString = NulText.ReadCString(headerBytes.AsSpan(128, 48), Encoding.ASCII);
            if (string.IsNullOrEmpty(releaseString))
            {
                releaseString = ReleaseString;
            }

            var vbmeta = BuildSignedVbmetaCore(
                descriptorBytes, key, keyBits, algorithmType, rollbackIndex, flags, rollbackIndexLocation,
                releaseString, requiredMajor, requiredMinor);
            await PublishVbmetaAsync(outputPath, vbmeta, cancellationToken).ConfigureAwait(false);

            return new AvbSignResult
            {
                Success = true,
                OutputPath = outputPath,
                AlgorithmType = algorithmType,
                AlgorithmName = AvbSignatureVerifier.GetAlgorithmName(algorithmType),
                DescriptorCount = descriptors.Entries.Count,
                OutputSize = vbmeta.Length,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }
}