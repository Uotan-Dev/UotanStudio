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

public static class AvbSigner
{
    /// <summary>
    /// Size of the AVB VBMeta header in bytes.
    /// <para>AVB VBMeta 头的字节大小。</para>
    /// </summary>
    public const int HeaderSize = AvbVBMetaImageHeader.Size;

    private const int Sha256Size = 32;
    private const int BlockSize = 4096;

    // 上游 libavb avb_descriptor.h 的 tag 常量：0=Property、1=Hashtree、2=Hash、
    // 3=KernelCmdline、4=ChainPartition。
    // Upstream libavb avb_descriptor.h tags: 0=Property, 1=Hashtree, 2=Hash,
    // 3=KernelCmdline, 4=ChainPartition.
    private const int PropertyDescriptorTag = 0;
    private const int HashtreeDescriptorTag = 1;
    private const int HashDescriptorTag = 2;
    private const int KernelCmdlineDescriptorTag = 3;
    private const int ChainPartitionDescriptorTag = 4;
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
    /// Rebuilds a VBMeta image with an arbitrary mix of descriptors (hash, hashtree, chain
    /// partition, kernel cmdline and property descriptors are all supported): parses the source
    /// vbmeta header for metadata, serializes the descriptors in the upstream libavb layouts, signs
    /// the 256-byte header + auxiliary block with the private key, and streams the signed image to
    /// the output path. Serialization is byte-for-byte aligned with vendored avbtool 1.2.0.
    /// <para>以任意混合描述符（哈希、哈希树、链式分区、内核命令行与属性描述符均支持）重建
    /// VBMeta 镜像：解析源 vbmeta 头以获取元数据，按上游 libavb 布局序列化描述符，用私钥对
    /// 256 字节头 + 辅助数据块签名，并将签名镜像流式写出到输出路径。序列化与仓库内置的
    /// avbtool 1.2.0 逐字节对齐。</para>
    /// </summary>
    /// <typeparam name="TDescriptor">The common element type of the descriptor list (typically
    /// <see cref="object"/> for a mixed list). <para>描述符列表的公共元素类型（混合列表通常为
    /// <see cref="object"/>）。</para></typeparam>
    /// <param name="vbmetaPath">The source VBMeta image (must carry "AVB0" magic); its header metadata is preserved.
    /// <para>源 VBMeta 镜像（必须携带 "AVB0" 魔数）；其头部元数据将被保留。</para></param>
    /// <param name="key">The RSA private key used for signing (2048/4096/8192 bits).
    /// <para>用于签名的 RSA 私钥（2048/4096/8192 位）。</para></param>
    /// <param name="outputPath">The output signed VBMeta image path. <para>输出的已签名 VBMeta 镜像路径。</para></param>
    /// <param name="descriptors">The mixed descriptors to embed
    /// (<see cref="AvbHashDescriptor"/>, <see cref="AvbHashtreeDescriptor"/>,
    /// <see cref="AvbChainPartitionDescriptor"/>, <see cref="AvbKernelCmdlineDescriptor"/> or
    /// <see cref="AvbPropertyDescriptor"/>). <para>要嵌入的混合描述符
    /// （<see cref="AvbHashDescriptor"/>、<see cref="AvbHashtreeDescriptor"/>、
    /// <see cref="AvbChainPartitionDescriptor"/>、<see cref="AvbKernelCmdlineDescriptor"/> 或
    /// <see cref="AvbPropertyDescriptor"/>）。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The signing result. <para>签名结果。</para></returns>
    public static async Task<AvbSignResult> SignVbmetaAsync<TDescriptor>(
        string vbmetaPath,
        RsaKeyParameters key,
        string outputPath,
        IReadOnlyList<TDescriptor> descriptors,
        CancellationToken cancellationToken = default)
        where TDescriptor : class
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

            var descriptorList = descriptors as IReadOnlyList<object> ?? descriptors.Cast<object>().ToList();
            var keyBits = key.Modulus.BitLength;
            var algorithmType = GetAlgorithmType(keyBits);

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

            var vbmeta = BuildSignedVbmetaCore(
                SerializeDescriptors(descriptorList), key, keyBits, algorithmType,
                rollbackIndex, flags, rollbackIndexLocation, releaseString, requiredMajor, requiredMinor);

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
    /// Adds an AVB dm-verity hashtree footer to a partition image, byte-for-byte aligned with
    /// <c>avbtool add_hashtree_footer</c> (avbtool 1.2.0): builds the Merkle hash tree over the
    /// image rounded up to 4096 bytes (via <see cref="AvbHashtreeBuilder"/>), streams the image out,
    /// pads it to the 4K boundary, appends the hash tree, appends a freshly signed self-contained
    /// VBMeta image carrying one hashtree descriptor, pads the VBMeta to a 4K boundary and finishes
    /// with the standard 64-byte footer ("AVBf"). The result verifies round-trip with
    /// <see cref="AvbVerifier.Verify"/> and is accepted by <c>avbtool verify_image</c>.
    /// <para>为分区镜像追加 AVB dm-verity 哈希树尾部，与 <c>avbtool add_hashtree_footer</c>
    /// （avbtool 1.2.0）逐字节对齐：对向上取整到 4096 字节的镜像构建 Merkle 哈希树
    /// （经 <see cref="AvbHashtreeBuilder"/>），流式写出镜像、补齐到 4K 边界、追加哈希树、
    /// 追加一个携带单个哈希树描述符的、全新签名的自包含 VBMeta 镜像、把 VBMeta 补齐到 4K 边界，
    /// 最后写入标准 64 字节尾部（"AVBf"）。结果可被 <see cref="AvbVerifier.Verify"/> 往返验证
    /// 通过，且被 <c>avbtool verify_image</c> 接受。</para>
    /// </summary>
    /// <param name="imagePath">The partition image to sign. <para>要签名的分区镜像。</para></param>
    /// <param name="partitionName">The target partition name. <para>目标分区名称。</para></param>
    /// <param name="key">The RSA private key used for signing (2048/4096/8192 bits).
    /// <para>用于签名的 RSA 私钥（2048/4096/8192 位）。</para></param>
    /// <param name="outputPath">The output signed image path. <para>输出的已签名镜像路径。</para></param>
    /// <param name="salt">The hash tree salt; when <c>null</c> a random 32-byte salt is generated.
    /// <para>哈希树盐值；为 <c>null</c> 时生成随机 32 字节盐值。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The signing result. <para>签名结果。</para></returns>
    public static async Task<AvbSignResult> AddHashtreeFooterAsync(
        string imagePath,
        string partitionName,
        RsaKeyParameters key,
        string outputPath,
        byte[]? salt = null,
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

            var effectiveSalt = salt ?? new byte[Sha256Size];
            if (salt == null)
            {
                RandomNumberGenerator.Fill(effectiveSalt);
            }

            // 构建哈希树（流式，64KiB 有界缓冲），并预生成携带哈希树描述符的已签名 vbmeta。
            // Build the hash tree (streamed with a bounded 64 KiB buffer) and pre-generate the
            // signed vbmeta carrying the hashtree descriptor.
            var tree = AvbHashtreeBuilder.Build(imagePath, effectiveSalt, BlockSize, BlockSize);
            var treeOffset = checked((ulong)tree.ImageSize);
            var vbmeta = BuildSignedVbmetaCore(
                SerializeDescriptors(
                    [new AvbHashtreeDescriptor
                    {
                        DmVerityVersion = 1,
                        PartitionName = partitionName,
                        ImageSize = (ulong)tree.ImageSize,
                        TreeOffset = treeOffset,
                        TreeSize = (ulong)tree.TreeSize,
                        DataBlockSize = (uint)BlockSize,
                        HashBlockSize = (uint)BlockSize,
                        FecNumRoots = 0,
                        HashAlgorithm = "sha256",
                        Salt = effectiveSalt,
                        RootDigest = tree.RootDigest,
                        Flags = 0
                    }]),
                key, keyBits, algorithmType,
                rollbackIndex: 0, flags: 0, rollbackIndexLocation: 0, releaseString: "avbtool 1.2.0",
                requiredMajor: 1, requiredMinor: 0);
            var vbmetaPadding = (BlockSize - (vbmeta.Length % BlockSize)) % BlockSize;

            // 流式复制镜像 + 4K 对齐 + 哈希树 + vbmeta（4K 对齐）+ footer 块，经同目录临时文件原子提交。
            // Stream the image, then append 4K alignment, the hash tree, the vbmeta (4K-aligned)
            // and the footer block, committing atomically via a sibling temp file.
            var tempPath = outputPath + ".tmp";
            try
            {
                await using (var input = new FileStream(
                    imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, BlockSize, FileOptions.SequentialScan))
                await using (var output = new FileStream(
                    tempPath, FileMode.Create, FileAccess.Write, FileShare.None, BlockSize, FileOptions.SequentialScan))
                {
                    var originalImageSize = input.Length;
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

                    var padding = checked((int)(tree.ImageSize - originalImageSize));
                    if (padding > 0)
                    {
                        await output.WriteAsync(ZeroBlock.AsMemory(0, padding), cancellationToken).ConfigureAwait(false);
                    }

                    await output.WriteAsync(tree.TreeBytes, cancellationToken).ConfigureAwait(false);

                    var vbmetaOffset = checked(treeOffset + (ulong)tree.TreeSize);
                    await output.WriteAsync(vbmeta, cancellationToken).ConfigureAwait(false);

                    // 把 vbmeta 补齐到 4K 边界，再写一个整 4K 块、64 字节 footer 置于其末尾。
                    // Pad the vbmeta to a 4K boundary, then write a full 4K block with the 64-byte
                    // footer at its end (avbtool layout).
                    if (vbmetaPadding > 0)
                    {
                        await output.WriteAsync(ZeroBlock.AsMemory(0, vbmetaPadding), cancellationToken).ConfigureAwait(false);
                    }

                    await output.WriteAsync(ZeroBlock.AsMemory(0, BlockSize - AvbFooter.Size), cancellationToken).ConfigureAwait(false);
                    var footer = new byte[AvbFooter.Size];
                    BuildFooter(footer, checked((ulong)originalImageSize), vbmetaOffset, (ulong)vbmeta.Length);
                    await output.WriteAsync(footer, cancellationToken).ConfigureAwait(false);
                }

                File.Move(tempPath, outputPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tempPath); } catch { /* 临时文件清理失败不覆盖原始异常 */ }
                throw;
            }

            var outputSize = checked(tree.ImageSize + tree.TreeSize + vbmetaPadding + vbmeta.Length + BlockSize);
            return new AvbSignResult
            {
                Success = true,
                OutputPath = outputPath,
                AlgorithmType = algorithmType,
                AlgorithmName = AvbSignatureVerifier.GetAlgorithmName(algorithmType),
                DescriptorCount = 1,
                OutputSize = outputSize
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
                [new AvbHashDescriptor { PartitionName = partitionName, ImageSize = (ulong)copied, Salt = salt, Digest = digest }],
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
        => BuildSignedVbmetaCore(
            SerializeHashDescriptors(descriptors), key, keyBits, algorithmType,
            rollbackIndex, flags, rollbackIndexLocation, releaseString, requiredMajor, requiredMinor);

    private static byte[] BuildSignedVbmetaCore(
        byte[] descriptorBytes,
        RsaKeyParameters key,
        int keyBits,
        uint algorithmType,
        ulong rollbackIndex,
        uint flags,
        uint rollbackIndexLocation,
        string releaseString,
        uint requiredMajor,
        uint requiredMinor)
    {
        var keyNumBytes = keyBits / 8;
        var publicKeyBlob = BuildAvbRsaPublicKeyBlob(key, keyBits);
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

    /// <summary>
    /// Serializes hash descriptors in the upstream libavb <c>AvbHashDescriptor</c> layout
    /// (tag 2; 116-byte body: image_size u64, 32-byte algorithm name, u32 name/salt/digest
    /// lengths, u32 flags, 60 reserved bytes; then partition name / salt / digest, padded to 8).
    /// <para>按上游 libavb <c>AvbHashDescriptor</c> 布局序列化哈希描述符（tag 2；116 字节正文：
    /// image_size u64、32 字节算法名、u32 名称/盐/摘要长度、u32 flags、60 字节保留；随后为
    /// 分区名 / 盐 / 摘要，8 字节对齐）。</para>
    /// </summary>
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

            const int bodySize = 116;
            var numBytesFollowing = checked((ulong)((bodySize + nameBytes.Length + salt.Length + digest.Length + 7) & ~7));

            WriteUInt64BE(buffer, offset, HashDescriptorTag);
            WriteUInt64BE(buffer, offset + 8, numBytesFollowing);

            var body = offset + 16;
            WriteUInt64BE(buffer, body, descriptor.ImageSize);
            var algorithmBytes = Encoding.ASCII.GetBytes(descriptor.HashAlgorithm);
            if (algorithmBytes.Length > 32)
            {
                throw new ArgumentException("Hash algorithm name must be 32 bytes or fewer.", nameof(descriptors));
            }
            algorithmBytes.CopyTo(buffer, body + 8);
            WriteUInt32BE(buffer, body + 40, (uint)nameBytes.Length);
            WriteUInt32BE(buffer, body + 44, (uint)salt.Length);
            WriteUInt32BE(buffer, body + 48, (uint)digest.Length);
            WriteUInt32BE(buffer, body + 52, descriptor.Flags);
            // body+56..116 为保留区，保持全零。
            // body+56..116 is the reserved area, left zeroed.
            nameBytes.CopyTo(buffer, body + 116);
            salt.CopyTo(buffer, body + 116 + nameBytes.Length);
            digest.CopyTo(buffer, body + 116 + nameBytes.Length + salt.Length);

            offset = checked(offset + 16 + (int)numBytesFollowing);
        }

        return buffer;
    }

    private static int GetDescriptorTotalSize(AvbHashDescriptor descriptor)
    {
        var nameLength = Encoding.ASCII.GetByteCount(descriptor.PartitionName ?? string.Empty);
        var saltLength = descriptor.Salt?.Length ?? 0;
        var digestLength = descriptor.Digest?.Length ?? 0;
        var numBytesFollowing = (116 + nameLength + saltLength + digestLength + 7) & ~7;
        return checked(16 + numBytesFollowing);
    }

    /// <summary>
    /// Serializes a heterogeneous descriptor list in the upstream libavb layouts, byte-for-byte
    /// aligned with vendored avbtool 1.2.0. Each descriptor is followed by its variable-length
    /// payload with <c>num_bytes_following</c> padded to an 8-byte boundary.
    /// <para>按上游 libavb 布局序列化异构描述符列表，与仓库内置 avbtool 1.2.0 逐字节对齐。
    /// 每个描述符之后为变长负载，<c>num_bytes_following</c> 补齐到 8 字节边界。</para>
    /// </summary>
    private static byte[] SerializeDescriptors(IReadOnlyList<object> descriptors)
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
            var serialized = SerializeDescriptor(descriptor);
            serialized.CopyTo(buffer, offset);
            offset = checked(offset + serialized.Length);
        }

        return buffer;
    }

    private static byte[] SerializeDescriptor(object descriptor) => descriptor switch
    {
        AvbHashDescriptor hash => SerializeHashDescriptor(hash),
        AvbHashtreeDescriptor hashtree => SerializeHashtreeDescriptor(hashtree),
        AvbKernelCmdlineDescriptor cmdline => SerializeKernelCmdlineDescriptor(cmdline),
        AvbChainPartitionDescriptor chain => SerializeChainPartitionDescriptor(chain),
        AvbPropertyDescriptor property => SerializePropertyDescriptor(property),
        _ => throw new ArgumentException(
            $"Unsupported AVB descriptor type '{descriptor.GetType().Name}'. 不支持的 AVB 描述符类型 '{descriptor.GetType().Name}'。", nameof(descriptor))
    };

    private static int GetDescriptorTotalSize(object descriptor)
    {
        var totalSize = descriptor switch
        {
            AvbHashDescriptor hash => 16 + ((116 + AsciiByteCount(hash.PartitionName) + (hash.Salt?.Length ?? 0) + (hash.Digest?.Length ?? 0) + 7) & ~7),
            AvbHashtreeDescriptor hashtree => 16 + ((164 + AsciiByteCount(hashtree.PartitionName) + (hashtree.Salt?.Length ?? 0) + (hashtree.RootDigest?.Length ?? 0) + 7) & ~7),
            AvbKernelCmdlineDescriptor cmdline => 16 + ((8 + Utf8ByteCount(cmdline.KernelCmdline) + 7) & ~7),
            AvbChainPartitionDescriptor chain => 16 + ((76 + AsciiByteCount(chain.PartitionName) + (chain.PublicKey?.Length ?? 0) + 7) & ~7),
            AvbPropertyDescriptor property => 16 + ((16 + Utf8ByteCount(property.Key) + 1 + (property.Value?.Length ?? 0) + 1 + 7) & ~7),
            _ => throw new ArgumentException(
                $"Unsupported AVB descriptor type '{descriptor.GetType().Name}'. 不支持的 AVB 描述符类型 '{descriptor.GetType().Name}'。", nameof(descriptor))
        };
        return checked(totalSize);
    }

    private static byte[] SerializeHashDescriptor(AvbHashDescriptor descriptor)
    {
        var nameBytes = Encoding.ASCII.GetBytes(ValidatePartitionName(descriptor.PartitionName));
        var salt = descriptor.Salt ?? [];
        var digest = descriptor.Digest ?? [];
        ValidateLength(nameBytes.Length, "PartitionName");
        ValidateLength(salt.Length, "Salt");
        ValidateLength(digest.Length, "Digest");

        const int bodySize = 116;
        var numBytesFollowing = checked((ulong)((bodySize + nameBytes.Length + salt.Length + digest.Length + 7) & ~7));
        var buffer = new byte[16 + (int)numBytesFollowing];

        WriteUInt64BE(buffer, 0, HashDescriptorTag);
        WriteUInt64BE(buffer, 8, numBytesFollowing);
        var body = 16;
        WriteUInt64BE(buffer, body, descriptor.ImageSize);
        var algorithmBytes = Encoding.ASCII.GetBytes(descriptor.HashAlgorithm);
        if (algorithmBytes.Length > 32)
        {
            throw new ArgumentException("Hash algorithm name must be 32 bytes or fewer. 哈希算法名至多 32 字节。", nameof(descriptor));
        }

        algorithmBytes.CopyTo(buffer, body + 8);
        WriteUInt32BE(buffer, body + 40, (uint)nameBytes.Length);
        WriteUInt32BE(buffer, body + 44, (uint)salt.Length);
        WriteUInt32BE(buffer, body + 48, (uint)digest.Length);
        WriteUInt32BE(buffer, body + 52, descriptor.Flags);
        // body+56..116 为保留区，保持全零。
        // body+56..116 is the reserved area, left zeroed.
        nameBytes.CopyTo(buffer, body + 116);
        salt.CopyTo(buffer, body + 116 + nameBytes.Length);
        digest.CopyTo(buffer, body + 116 + nameBytes.Length + salt.Length);
        return buffer;
    }

    private static byte[] SerializeHashtreeDescriptor(AvbHashtreeDescriptor descriptor)
    {
        var nameBytes = Encoding.ASCII.GetBytes(ValidatePartitionName(descriptor.PartitionName));
        var salt = descriptor.Salt ?? [];
        var rootDigest = descriptor.RootDigest ?? [];
        var algorithmBytes = Encoding.ASCII.GetBytes(descriptor.HashAlgorithm);
        if (algorithmBytes.Length > 32)
        {
            throw new ArgumentException("Hash algorithm name must be 32 bytes or fewer. 哈希算法名至多 32 字节。", nameof(descriptor));
        }

        // 上游 AvbHashtreeDescriptor：164 字节正文 + 名称 + 盐 + 根摘要，8 字节对齐。
        // Upstream AvbHashtreeDescriptor: 164-byte body + name + salt + root digest, 8-aligned.
        const int bodySize = 164;
        var numBytesFollowing = checked((ulong)((bodySize + nameBytes.Length + salt.Length + rootDigest.Length + 7) & ~7));
        var buffer = new byte[16 + (int)numBytesFollowing];

        WriteUInt64BE(buffer, 0, HashtreeDescriptorTag);
        WriteUInt64BE(buffer, 8, numBytesFollowing);
        var body = 16;
        WriteUInt32BE(buffer, body, descriptor.DmVerityVersion);
        WriteUInt64BE(buffer, body + 4, descriptor.ImageSize);
        WriteUInt64BE(buffer, body + 12, descriptor.TreeOffset);
        WriteUInt64BE(buffer, body + 20, descriptor.TreeSize);
        WriteUInt32BE(buffer, body + 28, descriptor.DataBlockSize);
        WriteUInt32BE(buffer, body + 32, descriptor.HashBlockSize);
        WriteUInt32BE(buffer, body + 36, descriptor.FecNumRoots);
        WriteUInt64BE(buffer, body + 40, descriptor.FecOffset);
        WriteUInt64BE(buffer, body + 48, descriptor.FecSize);
        algorithmBytes.CopyTo(buffer, body + 56);
        WriteUInt32BE(buffer, body + 88, (uint)nameBytes.Length);
        WriteUInt32BE(buffer, body + 92, (uint)salt.Length);
        WriteUInt32BE(buffer, body + 96, (uint)rootDigest.Length);
        WriteUInt32BE(buffer, body + 100, descriptor.Flags);
        // body+104..163 为保留区（60 字节），保持全零。
        // body+104..163 is the reserved area (60 bytes), left zeroed.
        nameBytes.CopyTo(buffer, body + 164);
        salt.CopyTo(buffer, body + 164 + nameBytes.Length);
        rootDigest.CopyTo(buffer, body + 164 + nameBytes.Length + salt.Length);
        return buffer;
    }

    private static byte[] SerializeKernelCmdlineDescriptor(AvbKernelCmdlineDescriptor descriptor)
    {
        var cmdlineBytes = Encoding.UTF8.GetBytes(descriptor.KernelCmdline ?? string.Empty);

        // 上游 AvbKernelCmdlineDescriptor：8 字节正文（flags + cmdline_len）+ 命令行，8 字节对齐。
        // Upstream AvbKernelCmdlineDescriptor: 8-byte body (flags + cmdline_len) + cmdline, 8-aligned.
        const int bodySize = 8;
        var numBytesFollowing = checked((ulong)((bodySize + cmdlineBytes.Length + 7) & ~7));
        var buffer = new byte[16 + (int)numBytesFollowing];

        WriteUInt64BE(buffer, 0, KernelCmdlineDescriptorTag);
        WriteUInt64BE(buffer, 8, numBytesFollowing);
        var body = 16;
        WriteUInt32BE(buffer, body, descriptor.Flags);
        WriteUInt32BE(buffer, body + 4, (uint)cmdlineBytes.Length);
        cmdlineBytes.CopyTo(buffer, body + 8);
        return buffer;
    }

    private static byte[] SerializeChainPartitionDescriptor(AvbChainPartitionDescriptor descriptor)
    {
        var nameBytes = Encoding.ASCII.GetBytes(ValidatePartitionName(descriptor.PartitionName));
        var publicKey = descriptor.PublicKey ?? [];

        // 上游 AvbChainPartitionDescriptor：76 字节正文（rollback_index_location + name_len +
        // key_len + 64 字节保留）+ 名称 + 公钥，8 字节对齐。
        // Upstream AvbChainPartitionDescriptor: 76-byte body (rollback_index_location + name_len +
        // key_len + 64 reserved bytes) + name + public key, 8-aligned.
        const int bodySize = 76;
        var numBytesFollowing = checked((ulong)((bodySize + nameBytes.Length + publicKey.Length + 7) & ~7));
        var buffer = new byte[16 + (int)numBytesFollowing];

        WriteUInt64BE(buffer, 0, ChainPartitionDescriptorTag);
        WriteUInt64BE(buffer, 8, numBytesFollowing);
        var body = 16;
        WriteUInt32BE(buffer, body, descriptor.RollbackIndexLocation);
        WriteUInt32BE(buffer, body + 4, (uint)nameBytes.Length);
        WriteUInt32BE(buffer, body + 8, (uint)publicKey.Length);
        // body+12..75 为保留区（64 字节），保持全零。
        // body+12..75 is the reserved area (64 bytes), left zeroed.
        nameBytes.CopyTo(buffer, body + 76);
        publicKey.CopyTo(buffer, body + 76 + nameBytes.Length);
        return buffer;
    }

    private static byte[] SerializePropertyDescriptor(AvbPropertyDescriptor descriptor)
    {
        var keyBytes = Encoding.UTF8.GetBytes(descriptor.Key ?? string.Empty);
        var value = descriptor.Value ?? [];

        // 上游 AvbPropertyDescriptor：16 字节正文（key_size + value_size）+ key + NUL + value +
        // NUL，8 字节对齐。
        // Upstream AvbPropertyDescriptor: 16-byte body (key_size + value_size) + key + NUL +
        // value + NUL, 8-aligned.
        const int bodySize = 16;
        var numBytesFollowing = checked((ulong)((bodySize + keyBytes.Length + 1 + value.Length + 1 + 7) & ~7));
        var buffer = new byte[16 + (int)numBytesFollowing];

        WriteUInt64BE(buffer, 0, PropertyDescriptorTag);
        WriteUInt64BE(buffer, 8, numBytesFollowing);
        var body = 16;
        WriteUInt64BE(buffer, body, (ulong)keyBytes.Length);
        WriteUInt64BE(buffer, body + 8, (ulong)value.Length);
        keyBytes.CopyTo(buffer, body + 16);
        buffer[body + 16 + keyBytes.Length] = 0;
        value.CopyTo(buffer, body + 17 + keyBytes.Length);
        buffer[body + 17 + keyBytes.Length + value.Length] = 0;
        return buffer;
    }

    private static int AsciiByteCount(string? value) => Encoding.ASCII.GetByteCount(value ?? string.Empty);

    private static int Utf8ByteCount(string? value) => Encoding.UTF8.GetByteCount(value ?? string.Empty);

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