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

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb;

public static class AvbSigner
{
    public const int HeaderSize = AvbVBMetaImageHeader.Size;

    private const int Sha256Size = 32;
    private const int BlockSize = 4096;


    private const int PropertyDescriptorTag = 0;
    private const int HashtreeDescriptorTag = 1;
    private const int HashDescriptorTag = 2;
    private const int KernelCmdlineDescriptorTag = 3;
    private const int ChainPartitionDescriptorTag = 4;
    private static ReadOnlySpan<byte> MagicBytes => "AVB0"u8;
    private static readonly byte[] ZeroBlock = new byte[BlockSize];

    public const uint VbmetaFlagHashtreeDisabled = 0x1;

    public const uint VbmetaFlagVerificationDisabled = 0x2;

    /// <summary>
    /// Disables AVB verification on a VBMeta image by setting the verification-disabled flags and zeroing the authentication block.
    /// <para>通过设置禁用校验标志并清零认证块来禁用 VBMeta 镜像的 AVB 校验。</para>
    /// </summary>
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


            var flags = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(120));
            flags |= VbmetaFlagHashtreeDisabled | VbmetaFlagVerificationDisabled;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(120), flags);


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


                    source.Seek(HeaderSize + authBlockSize, SeekOrigin.Begin);
                    await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }

                File.Move(tempPath, outputPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tempPath); } catch {  }
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
    /// Rebuilds and signs a VBMeta image with hash descriptors from a source VBMeta.
    /// <para>从源 VBMeta 重建并签名带有哈希描述符的 VBMeta 镜像。</para>
    /// </summary>
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
    /// Rebuilds and signs a VBMeta image with a mixed set of descriptor types from a source VBMeta.
    /// <para>从源 VBMeta 重建并签名带有混合描述符类型的 VBMeta 镜像。</para>
    /// </summary>
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
    /// Adds an AVB dm-verity hashtree footer to a partition image: builds the Merkle hash tree, appends a signed VBMeta with a hashtree descriptor, and writes the standard footer.
    /// <para>为分区镜像追加 AVB dm-verity 哈希树尾部：构建 Merkle 哈希树，追加带有哈希树描述符的 已签名 VBMeta，并写入标准尾部。</para>
    /// </summary>
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
                try { File.Delete(tempPath); } catch {  }
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
    /// Adds an AVB hash footer to a partition image: computes the salted SHA-256 digest while streaming, appends a signed VBMeta with a hash descriptor, and writes the standard footer.
    /// <para>为分区镜像追加 AVB 哈希尾部：流式计算加盐 SHA-256 摘要，追加带有哈希描述符的 已签名 VBMeta，并写入标准尾部。</para>
    /// </summary>
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


            var salt = new byte[Sha256Size];
            RandomNumberGenerator.Fill(salt);


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
    /// Loads an RSA private key from a PEM or DER file.
    /// <para>从 PEM 或 DER 文件加载 RSA 私钥。</para>
    /// </summary>
    public static RsaKeyParameters? LoadRsaPrivateKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }


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

        }


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
        WriteUInt64BE(header, 32, 0);
        WriteUInt64BE(header, 40, Sha256Size);
        WriteUInt64BE(header, 48, Sha256Size);
        WriteUInt64BE(header, 56, authSize - Sha256Size);
        WriteUInt64BE(header, 64, 0);
        WriteUInt64BE(header, 72, (ulong)publicKeySize);
        WriteUInt64BE(header, 80, 0);
        WriteUInt64BE(header, 88, 0);
        WriteUInt64BE(header, 96, (ulong)publicKeySize);
        WriteUInt64BE(header, 104, (ulong)descriptorSize);
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
    /// Serializes hash descriptors in the upstream libavb hash descriptor layout.
    /// <para>按上游 libavb 哈希描述符布局序列化哈希描述符。</para>
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
    /// Serializes a heterogeneous descriptor list in the upstream libavb layouts.
    /// <para>按上游 libavb 布局序列化异构描述符列表。</para>
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


        nameBytes.CopyTo(buffer, body + 164);
        salt.CopyTo(buffer, body + 164 + nameBytes.Length);
        rootDigest.CopyTo(buffer, body + 164 + nameBytes.Length + salt.Length);
        return buffer;
    }

    private static byte[] SerializeKernelCmdlineDescriptor(AvbKernelCmdlineDescriptor descriptor)
    {
        var cmdlineBytes = Encoding.UTF8.GetBytes(descriptor.KernelCmdline ?? string.Empty);


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


        const int bodySize = 76;
        var numBytesFollowing = checked((ulong)((bodySize + nameBytes.Length + publicKey.Length + 7) & ~7));
        var buffer = new byte[16 + (int)numBytesFollowing];

        WriteUInt64BE(buffer, 0, ChainPartitionDescriptorTag);
        WriteUInt64BE(buffer, 8, numBytesFollowing);
        var body = 16;
        WriteUInt32BE(buffer, body, descriptor.RollbackIndexLocation);
        WriteUInt32BE(buffer, body + 4, (uint)nameBytes.Length);
        WriteUInt32BE(buffer, body + 8, (uint)publicKey.Length);


        nameBytes.CopyTo(buffer, body + 76);
        publicKey.CopyTo(buffer, body + 76 + nameBytes.Length);
        return buffer;
    }

    private static byte[] SerializePropertyDescriptor(AvbPropertyDescriptor descriptor)
    {
        var keyBytes = Encoding.UTF8.GetBytes(descriptor.Key ?? string.Empty);
        var value = descriptor.Value ?? [];


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

        var count = Math.Min(4, modulusBigEndian.Length);
        var low = 0U;
        for (var i = 0; i < count; i++)
        {
            low = (low << 8) | modulusBigEndian[modulusBigEndian.Length - count + i];
        }


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
