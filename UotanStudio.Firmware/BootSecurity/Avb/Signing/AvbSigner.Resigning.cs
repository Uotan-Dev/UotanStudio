using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.BootSecurity.Avb.Signing;

public static partial class AvbSigner
{
    internal static async Task<AvbSignResult> ResignFooterAsync(
        string imagePath, string outputPath, RsaKeyParameters key, AvbResignOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            ValidatePartitionName(options.PartitionName);
            if (options.PartitionName.Contains('\0') || !key.IsPrivate ||
                options.Mode is not (AvbResignMode.Hash or AvbResignMode.Hashtree))
            {
                return Fail("A private RSA key, a valid partition name and a supported resign mode are required.");
            }
            if (PathsEqual(imagePath, outputPath))
            {
                return Fail("Output path must differ from the input image.");
            }
            if (options.RollbackIndexLocation >= 32 || (options.Flags & ~3U) != 0)
            {
                return Fail("Rollback index location must be below 32 and VBMeta flags must use only bits 0 and 1.");
            }
            if (options.PartitionSize is { } capacity && (capacity <= 0 || capacity % BlockSize != 0))
            {
                return Fail("Partition size must be a positive multiple of 4096 bytes.");
            }
            if (options.Salt is { Length: > 255 })
            {
                return Fail("Salt must not exceed 255 bytes.");
            }
            if (options.FecNumRoots != 0 &&
                (options.Mode != AvbResignMode.Hashtree || options.FecNumRoots > 254))
            {
                return Fail("FEC requires hashtree mode and 1 to 254 roots.");
            }

            var keyBits = key.Modulus.BitLength;
            var algorithm = GetAlgorithmType(keyBits);
            var salt = options.Salt?.ToArray() ?? RandomNumberGenerator.GetBytes(Sha256Size);
            await using var image = await AvbImageSource.OpenAsync(imagePath, cancellationToken).ConfigureAwait(false);
            var source = image.Stream;
            var originalSize = await GetResignContentSizeAsync(source, cancellationToken).ConfigureAwait(false);
            if (options.PartitionSize is { } limit && originalSize > limit - 2L * BlockSize)
            {
                return Fail("The original content and AVB metadata cannot fit in the requested partition.");
            }
            using var content = new BoundedReadStream(source, 0, originalSize, leaveOpen: true);
            using var tree = options.Mode == AvbResignMode.Hashtree
                ? AvbHashtreeBuilder.Build(content, salt, options.DataBlockSize, options.HashBlockSize, cancellationToken)
                : null;
            var treeAlignment = options.FecNumRoots == 0 ? options.HashBlockSize :
                Math.Max(BlockSize, options.HashBlockSize);
            var treeOffset = tree is null ? 0 : checked((tree.ImageSize + treeAlignment - 1) /
                treeAlignment * treeAlignment);
            var treeEnd = tree is null ? 0 : checked(treeOffset + tree.TreeSize);
            var fecOffset = options.FecNumRoots == 0 ? 0 : AlignPartitionBlock(treeEnd);
            var fecSize = options.FecNumRoots == 0 ? 0 :
                AvbFecCodec.CalculateParitySize(fecOffset, options.FecNumRoots);
            var vbmetaOffset = AlignPartitionBlock(tree is null ? originalSize :
                fecSize == 0 ? treeEnd : checked(fecOffset + fecSize));
            object descriptor;
            if (tree is not null)
            {
                descriptor = new AvbHashtreeDescriptor
                {
                    DmVerityVersion = 1,
                    PartitionName = options.PartitionName,
                    ImageSize = checked((ulong)tree.ImageSize),
                    TreeOffset = checked((ulong)treeOffset),
                    TreeSize = checked((ulong)tree.TreeSize),
                    DataBlockSize = checked((uint)options.DataBlockSize),
                    HashBlockSize = checked((uint)options.HashBlockSize),
                    FecNumRoots = options.FecNumRoots,
                    FecOffset = checked((ulong)fecOffset),
                    FecSize = checked((ulong)fecSize),
                    HashAlgorithm = "sha256",
                    Salt = salt,
                    RootDigest = tree.RootDigest,
                    Flags = options.DoNotUseAb ? 1U : 0U
                };
            }
            else
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hasher.AppendData(salt);
                var buffer = new byte[65536];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    hasher.AppendData(buffer.AsSpan(0, read));
                }
                descriptor = new AvbHashDescriptor
                {
                    PartitionName = options.PartitionName,
                    ImageSize = checked((ulong)originalSize),
                    Salt = salt,
                    Digest = hasher.GetHashAndReset(),
                    Flags = options.DoNotUseAb ? 1U : 0U
                };
            }

            var minor = options.RollbackIndexLocation > 0 ? 2U : options.DoNotUseAb ? 1U : 0U;
            var vbmeta = BuildSignedVbmetaCore(SerializeDescriptors([descriptor]), key, keyBits, algorithm,
                options.RollbackIndex, options.Flags, options.RollbackIndexLocation, ReleaseString, 1, minor);
            var minimumSize = checked(vbmetaOffset + AlignPartitionBlock(vbmeta.Length) + BlockSize);
            var outputSize = options.PartitionSize ?? minimumSize;
            if (outputSize < minimumSize)
            {
                return Fail($"Signed image requires {minimumSize} bytes but the partition holds {outputSize} bytes.");
            }
            if (tree is not null && (treeOffset < originalSize || treeEnd > (fecSize == 0 ? vbmetaOffset : fecOffset)) ||
                fecSize != 0 && (fecOffset < treeEnd || checked(fecOffset + fecSize) > vbmetaOffset) ||
                checked(vbmetaOffset + vbmeta.Length) > outputSize - BlockSize)
            {
                return Fail("The content, tree, FEC, VBMeta and footer regions overlap.");
            }
            var parsedDescriptors = AvbSignatureVerifier.ReadDescriptorsStrict(vbmeta);
            await FileExtraction.WriteAtomicAsync(outputPath, async output =>
            {
                content.Position = 0;
                await StreamCopy.CopyExactlyAsync(content, output, originalSize,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (tree is not null)
                {
                    output.Position = treeOffset;
                    await tree.WriteTreeToAsync(output, cancellationToken).ConfigureAwait(false);
                }
                if (fecSize != 0)
                {
                    output.SetLength(fecOffset);
                    var parityPath = TempFileHelper.CreateScratchPath("uotan-avb-fec", ".bin");
                    await using var parity = new FileStream(parityPath, FileMode.CreateNew, FileAccess.ReadWrite,
                        FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                    await AvbFecCodec.WriteParityAsync(output, parity, fecOffset, options.FecNumRoots,
                        cancellationToken).ConfigureAwait(false);
                    if (parity.Length != fecSize)
                    {
                        throw new InvalidDataException("Generated FEC size differs from the signed descriptor.");
                    }
                    parity.Position = 0;
                    output.Position = fecOffset;
                    await StreamCopy.CopyExactlyAsync(parity, output, fecSize,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                output.Position = vbmetaOffset;
                await output.WriteAsync(vbmeta, cancellationToken).ConfigureAwait(false);
                output.SetLength(outputSize);
                output.Position = outputSize - AvbFooter.Size;
                var footer = new byte[AvbFooter.Size];
                BuildFooter(footer, checked((ulong)originalSize), checked((ulong)vbmetaOffset), (ulong)vbmeta.Length);
                await output.WriteAsync(footer, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                var signature = AvbVerifier.VerifyFromStream(output, output.Length);
                if (!signature.IsValid)
                {
                    throw new InvalidDataException(signature.ErrorMessage ?? "Generated VBMeta signature is invalid.");
                }
                var verification = fecSize == 0
                    ? await AvbPartitionVerifier.VerifyAsync(output, parsedDescriptors[0], cancellationToken)
                        .ConfigureAwait(false)
                    : await AvbPartitionVerifier.VerifyWithFecAsync(output, parsedDescriptors[0], cancellationToken)
                        .ConfigureAwait(false);
                if (!verification.ContentIsValid || fecSize != 0 && verification.FecIsValid != true)
                {
                    throw new InvalidDataException(verification.ErrorMessage ?? "Generated partition content or FEC is invalid.");
                }
            }, cancellationToken: cancellationToken).ConfigureAwait(false);

            return new AvbSignResult
            {
                Success = true,
                OutputPath = outputPath,
                AlgorithmType = algorithm,
                AlgorithmName = AvbSignatureVerifier.GetAlgorithmName(algorithm),
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

    private static long AlignPartitionBlock(long size)
        => checked((size + BlockSize - 1) / BlockSize * BlockSize);

    private static async Task<long> GetResignContentSizeAsync(Stream source, CancellationToken cancellationToken)
    {
        if (source.Length < AvbFooter.Size)
        {
            return source.Length;
        }
        source.Position = source.Length - AvbFooter.Size;
        var bytes = new byte[AvbFooter.Size];
        await source.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (!bytes.AsSpan(0, 4).SequenceEqual("AVBf"u8))
        {
            return source.Length;
        }
        var footer = AvbFooter.TryLoad(bytes) ?? throw new InvalidDataException("Unsupported AVB footer version.");
        var end = checked((ulong)(source.Length - AvbFooter.Size));
        if (footer.OriginalImageSize > footer.VbmetaOffset || footer.VbmetaOffset > end ||
            footer.VbmetaSize < HeaderSize || footer.VbmetaSize > end - footer.VbmetaOffset)
        {
            throw new InvalidDataException("The original AVB footer has invalid image or metadata ranges.");
        }
        source.Position = checked((long)footer.VbmetaOffset);
        var headerBytes = new byte[HeaderSize];
        await source.ReadExactlyAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        var header = AvbVBMetaImageHeader.FromBytes(headerBytes);
        if (!headerBytes.AsSpan(0, 4).SequenceEqual(MagicBytes) ||
            header.AuthenticationDataBlockSize > 16UL * 1024 * 1024 ||
            header.AuxiliaryDataBlockSize > 16UL * 1024 * 1024 ||
            HeaderSize + header.AuthenticationDataBlockSize + header.AuxiliaryDataBlockSize > footer.VbmetaSize)
        {
            throw new InvalidDataException("The original AVB footer does not reference bounded VBMeta metadata.");
        }
        return checked((long)footer.OriginalImageSize);
    }
}