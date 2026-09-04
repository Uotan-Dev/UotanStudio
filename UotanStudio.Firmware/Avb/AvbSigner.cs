using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;

namespace UotanStudio.Firmware.Avb;

/// <summary>
/// Describes one AVB hash descriptor to embed into a signed VBMeta image: the target partition
/// name, the salt and the expected digest (computed over <c>salt || partition_data</c> with SHA-256).
/// <para>描述要嵌入已签名 VBMeta 镜像的单个 AVB 哈希描述符：目标分区名、盐值与期望摘要
/// （对 <c>salt || partition_data</c> 计算 SHA-256）。</para>
/// </summary>
public sealed class AvbHashDescriptor
{
    /// <summary>
    /// Gets or sets the target partition name (ASCII, 1-255 bytes).
    /// <para>获取或设置目标分区名称（ASCII，1-255 字节）。</para>
    /// </summary>
    public string PartitionName { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the salt prefixed to the partition data before hashing.
    /// <para>获取或设置哈希前与分区数据拼接的盐值。</para>
    /// </summary>
    public byte[] Salt { get; init; } = [];

    /// <summary>
    /// Gets or sets the expected digest of <c>salt || partition_data</c> (SHA-256, 32 bytes).
    /// <para>获取或设置 <c>salt || partition_data</c> 的期望摘要（SHA-256，32 字节）。</para>
    /// </summary>
    public byte[] Digest { get; init; } = [];
}

/// <summary>
/// Result of an AVB signing operation.
/// <para>AVB 签名操作的结果。</para>
/// </summary>
public sealed class AvbSignResult
{
    /// <summary>
    /// Gets whether the signing operation succeeded.
    /// <para>获取签名操作是否成功。</para>
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets the error message when signing failed.
    /// <para>获取签名失败时的错误信息。</para>
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the output file path.
    /// <para>获取输出文件路径。</para>
    /// </summary>
    public string? OutputPath { get; init; }

    /// <summary>
    /// Gets the libavb algorithm type used (1 = SHA256_RSA2048, 2 = SHA256_RSA4096, 3 = SHA256_RSA8192).
    /// <para>获取使用的 libavb 算法类型（1 = SHA256_RSA2048，2 = SHA256_RSA4096，3 = SHA256_RSA8192）。</para>
    /// </summary>
    public uint AlgorithmType { get; init; }

    /// <summary>
    /// Gets the algorithm display name (e.g. "SHA256_RSA2048").
    /// <para>获取算法显示名称（如 "SHA256_RSA2048"）。</para>
    /// </summary>
    public string AlgorithmName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the number of hash descriptors embedded in the VBMeta image.
    /// <para>获取嵌入 VBMeta 镜像的哈希描述符数量。</para>
    /// </summary>
    public int DescriptorCount { get; init; }

    /// <summary>
    /// Gets the output file size in bytes.
    /// <para>获取输出文件的字节大小。</para>
    /// </summary>
    public long OutputSize { get; init; }
}

/// <summary>
/// Signs Android Verified Boot (AVB) VBMeta images and appends AVB hash footers to partition images.
/// The emitted signature is RSASSA-PKCS1-v1_5 SHA-256 over the serialized 256-byte VBMeta header
/// followed by the auxiliary data block — exactly the scheme verified by
/// <see cref="AvbSignatureVerifier"/> (its <c>RsaDigestSigner</c> implementation, and the scheme
/// asserted by the <c>Verify_PssSignature_IsRejected</c> test). Signed output therefore verifies
/// round-trip with <see cref="AvbVerifier"/>.
/// <para>为 Android Verified Boot（AVB）VBMeta 镜像签名，并为分区镜像追加 AVB 哈希尾部。
/// 生成的签名对序列化的 256 字节 VBMeta 头及其后的辅助数据块使用 RSASSA-PKCS1-v1_5 SHA-256——
/// 与 <see cref="AvbSignatureVerifier"/>（其 <c>RsaDigestSigner</c> 实现，以及
/// <c>Verify_PssSignature_IsRejected</c> 测试所断言的算法）完全一致，因此签名输出可被
/// <see cref="AvbVerifier"/> 往返验证通过。</para>
/// </summary>
/// <remarks>
/// All I/O is streaming and bounded: partition images are copied chunk-by-chunk and never fully
/// loaded into memory, and size arithmetic is performed in <c>checked</c> context.
/// <para>所有 IO 均为流式且有界：分区镜像按块复制、从不整文件载入内存；长度运算均在
/// <c>checked</c> 上下文中执行。</para>
/// </remarks>
public static class AvbSigner
{
    /// <summary>
    /// Size of the AVB VBMeta header in bytes.
    /// <para>AVB VBMeta 头的字节大小。</para>
    /// </summary>
    public const int HeaderSize = AvbVBMetaImageHeader.Size;

    private const int Sha256Size = 32;
    private const int BlockSize = 4096;
    private const int HashDescriptorTag = 0;
    private static ReadOnlySpan<byte> MagicBytes => "AVB0"u8;
    private static readonly byte[] ZeroBlock = new byte[BlockSize];

    /// <summary>
    /// AVB vbmeta flag: disable hashtree verification (dm-verity).
    /// <para>AVB vbmeta 标志：禁用哈希树校验（dm-verity）。</para>
    /// </summary>
    public const uint VbmetaFlagHashtreeDisabled = 0x1;

    /// <summary>
    /// AVB vbmeta flag: disable vbmeta verification entirely (libavb short-circuits on this flag
    /// right after reading the header, so the signature does not need to remain valid).
    /// <para>AVB vbmeta 标志：完全禁用 vbmeta 校验（libavb 读完头部即短路返回，签名无需保持有效）。</para>
    /// </summary>
    public const uint VbmetaFlagVerificationDisabled = 0x2;

    /// <summary>
    /// Disables AVB verification on a VBMeta image: reads the bounded 256-byte header, validates the
    /// "AVB0" magic, ORs the flags field (offset 120, big-endian) with
    /// <see cref="VbmetaFlagHashtreeDisabled"/> | <see cref="VbmetaFlagVerificationDisabled"/>, zeroes
    /// the authentication block, then streams the patched image to the output path (atomic temp-file
    /// commit). libavb honors <c>VERIFICATION_DISABLED</c> before validating the signature, so no
    /// private key is required.
    /// <para>禁用 VBMeta 镜像的 AVB 校验：有界读取 256 字节头、校验 "AVB0" 魔数，将标志字段（偏移 120，
    /// 大端）按位或 <see cref="VbmetaFlagHashtreeDisabled"/> | <see cref="VbmetaFlagVerificationDisabled"/>，
    /// 清零认证块，随后流式写出到输出路径（临时文件原子提交）。libavb 在验证签名之前即识别
    /// <c>VERIFICATION_DISABLED</c> 标志，因此无需私钥。</para>
    /// </summary>
    /// <param name="vbmetaPath">The source VBMeta image (must carry "AVB0" magic).
    /// <para>源 VBMeta 镜像（必须携带 "AVB0" 魔数）。</para></param>
    /// <param name="outputPath">The patched VBMeta image path (must differ from the source).
    /// <para>补丁后的 VBMeta 镜像路径（必须与源不同）。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The patch result (reuses <see cref="AvbSignResult"/>).
    /// <para>补丁结果（复用 <see cref="AvbSignResult"/>）。</para></returns>
    public static async Task<AvbSignResult> DisableVbmetaVerificationAsync(
        string vbmetaPath,
        string outputPath,
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

        if (PathsEqual(vbmetaPath, outputPath))
        {
            return Fail("Output path must differ from the source vbmeta. 输出路径不能与源 vbmeta 相同。");
        }

        try
        {
            if (!File.Exists(vbmetaPath))
            {
                return Fail($"vbmeta source file '{vbmetaPath}' does not exist. vbmeta 源文件 '{vbmetaPath}' 不存在。");
            }

            await using var source = new FileStream(
                vbmetaPath, FileMode.Open, FileAccess.Read, FileShare.Read, BlockSize, FileOptions.SequentialScan);

            // 有界读取 256 字节头并校验魔数与块大小。
            // Bounded 256-byte header read with magic and block-size validation.
            var header = new byte[HeaderSize];
            await source.ReadExactlyAsync(header.AsMemory(0, HeaderSize), cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan(0, 4).SequenceEqual(MagicBytes))
            {
                return Fail($"'{vbmetaPath}' is not a valid AVB0 VBMeta image. '{vbmetaPath}' 不是有效的 AVB0 VBMeta 镜像。");
            }

            var authBlockSize = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12)));
            if (authBlockSize < 0 || authBlockSize > source.Length - HeaderSize)
            {
                return Fail($"'{vbmetaPath}' carries an invalid authentication block size. '{vbmetaPath}' 认证块大小非法。");
            }

            // 置位禁校验标志（保留既有标志位）。
            // Set the disable-verification flags (existing flags are preserved).
            var flags = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(120));
            flags |= VbmetaFlagHashtreeDisabled | VbmetaFlagVerificationDisabled;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(120), flags);

            // 输出经同目录临时文件原子提交：补丁后的头 + 清零的认证块 + 其余字节原样复制。
            // Output is committed atomically via a sibling temp file: patched header + zeroed
            // authentication block + the remaining bytes copied verbatim.
            var tempPath = outputPath + ".tmp";
            try
            {
                await using (var output = new FileStream(
                    tempPath, FileMode.Create, FileAccess.Write, FileShare.None, BlockSize, FileOptions.SequentialScan))
                {
                    await output.WriteAsync(header.AsMemory(0, HeaderSize), cancellationToken).ConfigureAwait(false);

                    var zeroBlock = new byte[BlockSize];
                    var remaining = authBlockSize;
                    while (remaining > 0)
                    {
                        var toWrite = (int)Math.Min(zeroBlock.Length, remaining);
                        await output.WriteAsync(zeroBlock.AsMemory(0, toWrite), cancellationToken).ConfigureAwait(false);
                        remaining -= toWrite;
                    }

                    // 跳过原认证块（已以零覆盖），其余字节原样复制。
                    // Skip the original authentication block (replaced by zeros); copy the rest verbatim.
                    source.Seek(HeaderSize + authBlockSize, SeekOrigin.Begin);
                    await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }

                File.Move(tempPath, outputPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tempPath); } catch { /* 临时文件清理失败不覆盖原始异常 */ }
                throw;
            }

            return new AvbSignResult
            {
                Success = true,
                OutputPath = outputPath,
                AlgorithmName = "flags-patched",
                OutputSize = HeaderSize + authBlockSize + (source.Length - HeaderSize - authBlockSize)
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

    /// <summary>
    /// Rebuilds a VBMeta image: parses the source vbmeta header for metadata (libavb version,
    /// rollback index, flags, release string), writes the given hash descriptors and the AVB-format
    /// public key into the auxiliary data block, signs the 256-byte header + auxiliary block with the
    /// private key, and streams the signed image (header + authentication block + auxiliary block) to
    /// the output path. The output is a self-contained, self-signed VBMeta image that
    /// <see cref="AvbVerifier.VerifyVBMeta"/> accepts.
    /// <para>重建 VBMeta 镜像：解析源 vbmeta 头以获取元数据（libavb 版本、回滚索引、标志、发布
    /// 字符串），将给定哈希描述符与 AVB 格式公钥写入辅助数据块，用私钥对 256 字节头 + 辅助数据块
    /// 签名，并将签名镜像（头 + 认证块 + 辅助块）流式写出到输出路径。输出为可被
    /// <see cref="AvbVerifier.VerifyVBMeta"/> 接受的自包含自签名 VBMeta 镜像。</para>
    /// </summary>
    /// <param name="vbmetaPath">The source VBMeta image (must carry "AVB0" magic); its header metadata is preserved.
    /// <para>源 VBMeta 镜像（必须携带 "AVB0" 魔数）；其头部元数据将被保留。</para></param>
    /// <param name="key">The RSA private key used for signing (2048/4096/8192 bits).
    /// <para>用于签名的 RSA 私钥（2048/4096/8192 位）。</para></param>
    /// <param name="outputPath">The output signed VBMeta image path. <para>输出的已签名 VBMeta 镜像路径。</para></param>
    /// <param name="descriptors">The hash descriptors to embed. <para>要嵌入的哈希描述符。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The signing result. <para>签名结果。</para></returns>
    public static async Task<AvbSignResult> SignVbmetaAsync(
        string vbmetaPath,
        RsaKeyParameters key,
        string outputPath,
        IEnumerable<AvbHashDescriptor> descriptors,
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

            var descriptorList = descriptors.ToList();
            var keyBits = key.Modulus.BitLength;
            var algorithmType = GetAlgorithmType(keyBits);

            // Preserve metadata from the source header (bounded 256-byte streaming read).
            var headerBytes = await ReadHeaderBytesAsync(vbmetaPath, cancellationToken);
            if (headerBytes == null || !headerBytes.AsSpan(0, 4).SequenceEqual(MagicBytes))
            {
                return Fail($"'{vbmetaPath}' is not a valid AVB0 VBMeta image. '{vbmetaPath}' 不是有效的 AVB0 VBMeta 镜像。");
            }

            var requiredMajor = BinaryPrimitives.ReadUInt32BigEndian(headerBytes.AsSpan(4));
            var requiredMinor = BinaryPrimitives.ReadUInt32BigEndian(headerBytes.AsSpan(8));
            var rollbackIndex = BinaryPrimitives.ReadUInt64BigEndian(headerBytes.AsSpan(112));
            var flags = BinaryPrimitives.ReadUInt32BigEndian(headerBytes.AsSpan(120));
            var rollbackIndexLocation = BinaryPrimitives.ReadUInt32BigEndian(headerBytes.AsSpan(124));
            var releaseString = ReadAscii(headerBytes.AsSpan(128, 48));
            if (string.IsNullOrEmpty(releaseString))
            {
                releaseString = "avbtool 1.2.0";
            }

            var vbmeta = BuildSignedVbmeta(
                descriptorList, key, keyBits, algorithmType, rollbackIndex, flags, rollbackIndexLocation, releaseString,
                requiredMajor, requiredMinor);

            await using var output = new FileStream(
                outputPath, FileMode.Create, FileAccess.Write, FileShare.None, BlockSize, FileOptions.SequentialScan);
            await output.WriteAsync(vbmeta, cancellationToken);

            return new AvbSignResult
            {
                Success = true,
                OutputPath = outputPath,
                AlgorithmType = algorithmType,
                AlgorithmName = AvbSignatureVerifier.GetAlgorithmName(algorithmType),
                DescriptorCount = descriptorList.Count,
                OutputSize = vbmeta.Length
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

    /// <summary>
    /// Adds an AVB v2 hash footer to a partition image: computes <c>SHA256(salt || image)</c> in a
    /// single bounded streaming pass while copying the image out, pads the image to a 4096-byte
    /// boundary, appends a freshly signed self-contained VBMeta image carrying one hash descriptor,
    /// and finishes with the standard 64-byte footer ("AVBf"). The result verifies round-trip with
    /// <see cref="AvbVerifier.Verify"/>.
    /// <para>为分区镜像追加 AVB v2 哈希尾部：在单次有界流式复制镜像的过程中计算
    /// <c>SHA256(salt || image)</c>，将镜像补齐到 4096 字节边界，追加一个携带单个哈希描述符的、
    /// 全新签名的自包含 VBMeta 镜像，最后写入标准 64 字节尾部（"AVBf"）。结果可被
    /// <see cref="AvbVerifier.Verify"/> 往返验证通过。</para>
    /// </summary>
    /// <param name="imagePath">The partition image to sign. <para>要签名的分区镜像。</para></param>
    /// <param name="partitionName">The target partition name. <para>目标分区名称。</para></param>
    /// <param name="key">The RSA private key used for signing (2048/4096/8192 bits).
    /// <para>用于签名的 RSA 私钥（2048/4096/8192 位）。</para></param>
    /// <param name="outputPath">The output signed image path. <para>输出的已签名镜像路径。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The signing result. <para>签名结果。</para></returns>
    public static async Task<AvbSignResult> AddHashFooterAsync(
        string imagePath,
        string partitionName,
        RsaKeyParameters key,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return Fail("imagePath must be provided. 必须提供 imagePath。");
        }

        if (string.IsNullOrWhiteSpace(partitionName))
        {
            return Fail("partitionName must be provided. 必须提供 partitionName。");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return Fail("outputPath must be provided. 必须提供 outputPath。");
        }

        ArgumentNullException.ThrowIfNull(key);

        try
        {
            ValidatePartitionName(partitionName);
            if (!File.Exists(imagePath))
            {
                return Fail($"Image file '{imagePath}' does not exist. 镜像文件 '{imagePath}' 不存在。");
            }

            if (PathsEqual(imagePath, outputPath))
            {
                return Fail("Output path must differ from the input image. 输出路径不能与输入镜像相同。");
            }

            var keyBits = key.Modulus.BitLength;
            var algorithmType = GetAlgorithmType(keyBits);

            // Random per-image salt.
            var salt = new byte[Sha256Size];
            RandomNumberGenerator.Fill(salt);

            // One bounded pass: compute SHA256(salt || image) while copying the image out.
            await using var input = new FileStream(
                imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, BlockSize, FileOptions.SequentialScan);
            var originalImageSize = input.Length;
            await using var output = new FileStream(
                outputPath, FileMode.Create, FileAccess.Write, FileShare.None, BlockSize, FileOptions.SequentialScan);

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hasher.AppendData(salt);

            var buffer = new byte[64 * 1024];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
            }

            var digest = hasher.GetHashAndReset();

            // 4K-align the VBMeta blob that is appended at the image tail.
            var padding = (int)((((copied + BlockSize - 1) / BlockSize) * BlockSize) - copied);
            if (padding > 0)
            {
                await output.WriteAsync(ZeroBlock.AsMemory(0, padding), cancellationToken);
            }

            var vbmetaOffset = checked((ulong)copied + (ulong)padding);
            var vbmeta = BuildSignedVbmeta(
                [new AvbHashDescriptor { PartitionName = partitionName, Salt = salt, Digest = digest }],
                key, keyBits, algorithmType,
                rollbackIndex: 0, flags: 0, rollbackIndexLocation: 0, releaseString: "avbtool 1.2.0");
            await output.WriteAsync(vbmeta, cancellationToken);

            var footer = new byte[AvbFooter.Size];
            BuildFooter(footer, (ulong)originalImageSize, vbmetaOffset, (ulong)vbmeta.Length);
            await output.WriteAsync(footer, cancellationToken);

            return new AvbSignResult
            {
                Success = true,
                OutputPath = outputPath,
                AlgorithmType = algorithmType,
                AlgorithmName = AvbSignatureVerifier.GetAlgorithmName(algorithmType),
                DescriptorCount = 1,
                OutputSize = checked(copied + padding + vbmeta.Length + footer.Length)
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

    /// <summary>
    /// Loads an RSA private key (PEM: PKCS#1 "RSA PRIVATE KEY" or PKCS#8 "PRIVATE KEY"; DER:
    /// PKCS#8 PrivateKeyInfo) into an <see cref="RsaKeyParameters"/> instance.
    /// <para>将 RSA 私钥（PEM：PKCS#1 "RSA PRIVATE KEY" 或 PKCS#8 "PRIVATE KEY"；DER：PKCS#8
    /// PrivateKeyInfo）加载为 <see cref="RsaKeyParameters"/> 实例。</para>
    /// </summary>
    /// <param name="path">The private key file path. <para>私钥文件路径。</para></param>
    /// <returns>The imported key, or <c>null</c> when the file cannot be read as an RSA private key.
    /// <para>导入的密钥；无法将文件解析为 RSA 私钥时返回 <c>null</c>。</para></returns>
    public static RsaKeyParameters? LoadRsaPrivateKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        // PEM (PKCS#1 or PKCS#8), streamed so only the small key text is read.
        try
        {
            using var reader = new StreamReader(path);
            var pemObject = new PemReader(reader).ReadObject();
            var pemKey = pemObject switch
            {
                AsymmetricCipherKeyPair keyPair => keyPair.Private as RsaKeyParameters,
                RsaKeyParameters rsaKey => rsaKey,
                _ => null
            };
            if (pemKey != null)
            {
                return pemKey;
            }
        }
        catch (Exception)
        {
            // Not PEM; fall through to DER handling.
        }

        // DER (PKCS#8 PrivateKeyInfo). Private keys are tiny, so a single bounded read is fine.
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length > 4 * 1024 * 1024)
            {
                return null;
            }

            var buffer = new byte[(int)fs.Length];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = fs.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total != buffer.Length)
            {
                return null;
            }

            return PrivateKeyFactory.CreateKey(buffer) as RsaKeyParameters;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[] BuildSignedVbmeta(
        IReadOnlyList<AvbHashDescriptor> descriptors,
        RsaKeyParameters key,
        int keyBits,
        uint algorithmType,
        ulong rollbackIndex,
        uint flags,
        uint rollbackIndexLocation,
        string releaseString,
        uint requiredMajor = 1,
        uint requiredMinor = 0)
    {
        var keyNumBytes = keyBits / 8;
        var publicKeyBlob = BuildAvbRsaPublicKeyBlob(key, keyBits);
        var descriptorBytes = SerializeHashDescriptors(descriptors);
        var auxSize = checked((ulong)publicKeyBlob.Length + (ulong)descriptorBytes.Length);
        var authSize = checked(Sha256Size + (ulong)keyNumBytes);

        var header = BuildHeader(
            authSize, auxSize, algorithmType, publicKeyBlob.Length, descriptorBytes.Length,
            rollbackIndex, flags, rollbackIndexLocation, releaseString, requiredMajor, requiredMinor);

        var aux = new byte[checked((int)auxSize)];
        publicKeyBlob.CopyTo(aux, 0);
        descriptorBytes.CopyTo(aux, publicKeyBlob.Length);

        // The signing input is exactly the serialized header followed by the auxiliary block.
        var signingInput = new byte[checked(HeaderSize + (int)auxSize)];
        header.CopyTo(signingInput, 0);
        aux.CopyTo(signingInput, HeaderSize);
        var storedHash = SHA256.HashData(signingInput);
        var signature = SignData(signingInput, key);

        var vbmeta = new byte[checked(HeaderSize + (int)authSize + (int)auxSize)];
        header.CopyTo(vbmeta, 0);
        storedHash.CopyTo(vbmeta, HeaderSize);
        signature.CopyTo(vbmeta, HeaderSize + Sha256Size);
        aux.CopyTo(vbmeta, HeaderSize + (int)authSize);
        return vbmeta;
    }

    private static byte[] BuildHeader(
        ulong authSize,
        ulong auxSize,
        uint algorithmType,
        int publicKeySize,
        int descriptorSize,
        ulong rollbackIndex,
        uint flags,
        uint rollbackIndexLocation,
        string releaseString,
        uint requiredMajor,
        uint requiredMinor)
    {
        var header = new byte[HeaderSize];
        MagicBytes.CopyTo(header);
        WriteUInt32BE(header, 4, requiredMajor);
        WriteUInt32BE(header, 8, requiredMinor);
        WriteUInt64BE(header, 12, authSize);
        WriteUInt64BE(header, 20, auxSize);
        WriteUInt32BE(header, 28, algorithmType);
        WriteUInt64BE(header, 32, 0);                        // hash_offset
        WriteUInt64BE(header, 40, Sha256Size);               // hash_size
        WriteUInt64BE(header, 48, Sha256Size);               // signature_offset
        WriteUInt64BE(header, 56, authSize - Sha256Size); // signature_size
        WriteUInt64BE(header, 64, 0);                        // public_key_offset
        WriteUInt64BE(header, 72, (ulong)publicKeySize);     // public_key_size
        WriteUInt64BE(header, 80, 0);                        // public_key_metadata_offset
        WriteUInt64BE(header, 88, 0);                        // public_key_metadata_size
        WriteUInt64BE(header, 96, (ulong)publicKeySize);     // descriptors_offset
        WriteUInt64BE(header, 104, (ulong)descriptorSize);   // descriptors_size
        WriteUInt64BE(header, 112, rollbackIndex);
        WriteUInt32BE(header, 120, flags);
        WriteUInt32BE(header, 124, rollbackIndexLocation);
        var release = Encoding.ASCII.GetBytes(releaseString ?? string.Empty);
        var releaseLength = Math.Min(release.Length, 47);
        if (releaseLength > 0)
        {
            release.AsSpan(0, releaseLength).CopyTo(header.AsSpan(128));
        }

        return header;
    }

    private static byte[] BuildAvbRsaPublicKeyBlob(RsaKeyParameters key, int keyBits)
    {
        var keyNumBytes = keyBits / 8;
        var modulus = ToFixedLengthModulus(key, keyNumBytes);
        var n = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
        var n0inv = ComputeN0Inv(modulus);
        var rr = BigInteger.ModPow(BigInteger.One << (keyBits * 2), BigInteger.One, n);
        var rrBytes = ToBigEndianPadded(rr, keyNumBytes);

        var blob = new byte[checked(8 + (keyNumBytes * 2))];
        WriteUInt32BE(blob, 0, (uint)keyBits);
        WriteUInt32BE(blob, 4, n0inv);
        modulus.CopyTo(blob, 8);
        rrBytes.CopyTo(blob, 8 + keyNumBytes);
        return blob;
    }

    private static byte[] ToFixedLengthModulus(RsaKeyParameters key, int keyNumBytes)
    {
        var modulus = key.Modulus.ToByteArrayUnsigned();
        if (modulus.Length == keyNumBytes)
        {
            return modulus;
        }

        if (modulus.Length > keyNumBytes)
        {
            return modulus.AsSpan(modulus.Length - keyNumBytes, keyNumBytes).ToArray();
        }

        var padded = new byte[keyNumBytes];
        modulus.CopyTo(padded, keyNumBytes - modulus.Length);
        return padded;
    }

    private static byte[] SerializeHashDescriptors(IReadOnlyList<AvbHashDescriptor> descriptors)
    {
        var totalSize = 0;
        foreach (var descriptor in descriptors)
        {
            totalSize = checked(totalSize + GetDescriptorTotalSize(descriptor));
        }

        var buffer = new byte[totalSize];
        var offset = 0;
        foreach (var descriptor in descriptors)
        {
            var nameBytes = Encoding.ASCII.GetBytes(ValidatePartitionName(descriptor.PartitionName));
            var salt = descriptor.Salt ?? [];
            var digest = descriptor.Digest ?? [];
            ValidateLength(nameBytes.Length, "PartitionName");
            ValidateLength(salt.Length, "Salt");
            ValidateLength(digest.Length, "Digest");

            // Layout expected by the codebase descriptor parser (AvbSignatureVerifier.ParseDescriptor):
            // [body+20..body+52) hash algorithm ASCII, [body+52] partition name length,
            // [body+53] salt length, [body+54] digest length, then name / salt / digest.
            var numBytesFollowing = checked(56 + nameBytes.Length + salt.Length + digest.Length);
            WriteUInt64BE(buffer, offset, HashDescriptorTag);
            WriteUInt64BE(buffer, offset + 8, (ulong)numBytesFollowing);

            var body = offset + 16;
            Encoding.ASCII.GetBytes("sha256").CopyTo(buffer, body + 20);
            buffer[body + 52] = (byte)nameBytes.Length;
            buffer[body + 53] = (byte)salt.Length;
            buffer[body + 54] = (byte)digest.Length;
            nameBytes.CopyTo(buffer, body + 56);
            salt.CopyTo(buffer, body + 56 + nameBytes.Length);
            digest.CopyTo(buffer, body + 56 + nameBytes.Length + salt.Length);

            offset = checked(offset + 16 + numBytesFollowing);
        }

        return buffer;
    }

    private static int GetDescriptorTotalSize(AvbHashDescriptor descriptor)
    {
        var nameLength = Encoding.ASCII.GetByteCount(descriptor.PartitionName ?? string.Empty);
        var saltLength = descriptor.Salt?.Length ?? 0;
        var digestLength = descriptor.Digest?.Length ?? 0;
        return checked(72 + nameLength + saltLength + digestLength);
    }

    private static byte[] SignData(byte[] data, RsaKeyParameters key)
    {
        var signer = new RsaDigestSigner(new Sha256Digest());
        signer.Init(true, key);
        signer.BlockUpdate(data, 0, data.Length);
        return signer.GenerateSignature();
    }

    private static uint GetAlgorithmType(int keyBits) => keyBits switch
    {
        2048 => 1,
        4096 => 2,
        8192 => 3,
        _ => throw new InvalidOperationException(
            $"Unsupported RSA key size {keyBits} bits (expected 2048/4096/8192). 不支持的 RSA 密钥大小 {keyBits} 位（应为 2048/4096/8192）。")
    };

    private static void BuildFooter(Span<byte> footer, ulong originalImageSize, ulong vbmetaOffset, ulong vbmetaSize)
    {
        "AVBf"u8.CopyTo(footer);
        WriteUInt32BE(footer, 4, AvbFooter.SupportedVersionMajor);
        WriteUInt32BE(footer, 8, AvbFooter.SupportedVersionMinor);
        WriteUInt64BE(footer, 12, originalImageSize);
        WriteUInt64BE(footer, 20, vbmetaOffset);
        WriteUInt64BE(footer, 28, vbmetaSize);
    }

    private static string ValidatePartitionName(string? partitionName)
    {
        if (string.IsNullOrEmpty(partitionName))
        {
            throw new ArgumentException("AVB hash descriptor partition name must not be empty. AVB 哈希描述符分区名不能为空。");
        }

        if (partitionName.Length > byte.MaxValue)
        {
            throw new ArgumentException("AVB hash descriptor partition name exceeds 255 bytes. AVB 哈希描述符分区名超过 255 字节。");
        }

        foreach (var c in partitionName)
        {
            if (c > 0x7F)
            {
                throw new ArgumentException("AVB hash descriptor partition name must be ASCII. AVB 哈希描述符分区名必须为 ASCII。");
            }
        }

        return partitionName;
    }

    private static void ValidateLength(int length, string fieldName)
    {
        if (length > byte.MaxValue)
        {
            throw new ArgumentException($"AVB hash descriptor {fieldName} exceeds 255 bytes. AVB 哈希描述符 {fieldName} 超过 255 字节。");
        }
    }

    private static async Task<byte[]?> ReadHeaderBytesAsync(string path, CancellationToken cancellationToken)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BlockSize, FileOptions.SequentialScan);
        if (fs.Length < HeaderSize)
        {
            return null;
        }

        var buffer = new byte[HeaderSize];
        var total = 0;
        while (total < HeaderSize)
        {
            var read = await fs.ReadAsync(buffer.AsMemory(total, HeaderSize - total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total == HeaderSize ? buffer : null;
    }

    private static string ReadAscii(ReadOnlySpan<byte> bytes)
    {
        var end = 0;
        while (end < bytes.Length && bytes[end] != 0)
        {
            end++;
        }

        return end == 0 ? string.Empty : Encoding.ASCII.GetString(bytes[..end]);
    }

    private static uint ComputeN0Inv(byte[] modulusBigEndian)
    {
        // Low 32 bits of the modulus, read from the least-significant bytes (big-endian stream order).
        var count = Math.Min(4, modulusBigEndian.Length);
        var low = 0U;
        for (var i = 0; i < count; i++)
        {
            low = (low << 8) | modulusBigEndian[modulusBigEndian.Length - count + i];
        }

        // Newton iteration: each round doubles the number of correct low bits (5 rounds -> 2^32).
        var inverse = 1U;
        for (var i = 0; i < 5; i++)
        {
            inverse = unchecked(inverse * (2U - (low * inverse)));
        }

        return unchecked(0U - inverse);
    }

    private static byte[] ToBigEndianPadded(BigInteger value, int length)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = new byte[length];
        if (bytes.Length >= length)
        {
            Array.Copy(bytes, bytes.Length - length, padded, 0, length);
        }
        else
        {
            bytes.CopyTo(padded, length - bytes.Length);
        }

        return padded;
    }

    private static bool PathsEqual(string first, string second)
        => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static void WriteUInt32BE(Span<byte> buffer, int offset, uint value)
        => BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(offset, 4), value);

    private static void WriteUInt64BE(Span<byte> buffer, int offset, ulong value)
        => BinaryPrimitives.WriteUInt64BigEndian(buffer.Slice(offset, 8), value);

    private static AvbSignResult Fail(string message) => new() { Success = false, ErrorMessage = message };
}