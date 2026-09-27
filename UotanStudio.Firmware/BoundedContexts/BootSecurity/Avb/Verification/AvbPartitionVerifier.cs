using System.Numerics;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Build;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Verification;

/// <summary>
/// Verifies raw partition bytes against hash or dm-verity version-one descriptors using bounded buffers.
/// <para>使用有界缓冲，按哈希或 dm-verity 第一版描述符验证原始分区字节。</para>
/// </summary>
/// <remarks>
/// Descriptors must be authenticated separately. Sparse containers must first be exposed as a logical raw stream.
/// Persistent digests, zeroed trees and FEC parity are not silently accepted as verified.
/// <para>描述符须单独认证。Sparse 容器必须先暴露为逻辑原始流。持久化摘要、清零树和 FEC 校验码不会被静默视为已验证。</para>
/// </remarks>
public static class AvbPartitionVerifier
{
    private const int MaximumBlockSize = 1024 * 1024;
    private const int MaximumSaltSize = 16 * 1024 * 1024;
    private sealed record TreeLevel(long Offset, long Size);

    /// <summary>
    /// Checks a descriptor against a caller-owned partition stream without writing or loading the whole image.
    /// <para>按描述符检查调用方拥有的分区流，不写入也不整体加载镜像。</para>
    /// </summary>
    /// <param name="image">Readable, seekable raw partition stream, starting logically at offset zero; its position is restored.
    /// <para>可读、可定位的原始分区流，逻辑起点为偏移零；结束时恢复位置。</para></param>
    /// <param name="descriptor">Hash or hashtree metadata whose signature and trust are checked independently.
    /// <para>哈希或哈希树元数据，其签名和信任单独检查。</para></param>
    /// <param name="cancellationToken">Cancels reads and block processing. <para>取消读取和块处理。</para></param>
    /// <returns>Content and stored-tree results with separate, unevaluated FEC status.
    /// <para>内容和存储树结果，另附未评估的 FEC 状态。</para></returns>
    /// <exception cref="ArgumentNullException">An input is null. <para>某个输入为 null。</para></exception>
    /// <exception cref="ArgumentException">The stream cannot read or seek. <para>流不可读或不可定位。</para></exception>
    /// <exception cref="EndOfStreamException">Declared bytes cannot be read completely. <para>无法完整读取声明的字节。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
    public static async Task<AvbPartitionVerifyResult> VerifyAsync(
        Stream image, AvbDescriptorInfo descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!image.CanRead || !image.CanSeek)
        {
            throw new ArgumentException("The partition stream must be readable and seekable.", nameof(image));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var position = image.Position;
        var fec = descriptor.FecNumRoots != 0 || descriptor.FecOffset != 0 || descriptor.FecSize != 0;
        AvbPartitionVerifyResult Result(string? error, bool? tree = null) => new()
        {
            PartitionName = descriptor.PartitionName,
            ContentIsValid = error is null,
            StoredTreeIsValid = tree,
            FecPresent = fec,
            ErrorMessage = error
        };
        try
        {
            if (descriptor.Tag is not (1 or 2))
            {
                return Result("Only hash and hashtree descriptors protect partition content.");
            }
            if (descriptor.Salt is not { } salt || salt.Length > MaximumSaltSize)
            {
                return Result("The descriptor salt is missing or exceeds 16 MiB.");
            }
            if (descriptor.ImageSize > (ulong)image.Length)
            {
                return Result("The protected image size exceeds the partition stream.");
            }
            var hasher = CreateHasher(descriptor.HashAlgorithm);
            if (hasher is null)
            {
                return Result("The descriptor hash algorithm is unsupported.");
            }
            var expectedDigest = descriptor.Tag == 2 ? descriptor.Digest : descriptor.RootDigest;
            if (expectedDigest is not { Length: > 0 })
            {
                return Result("Persistent digests require an external trusted digest and are not verified by this entry point.");
            }
            if (expectedDigest.Length != hasher.GetDigestSize())
            {
                return Result("The descriptor digest length does not match its algorithm.");
            }
            if (descriptor.Tag == 2)
            {
                if ((descriptor.Flags & ~1U) != 0)
                {
                    return Result("The hash descriptor contains unsupported flags.");
                }
                image.Position = 0;
                hasher.BlockUpdate(salt);
                var buffer = new byte[64 * 1024];
                var remaining = (long)descriptor.ImageSize;
                while (remaining > 0)
                {
                    var count = (int)Math.Min(buffer.Length, remaining);
                    await image.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    hasher.BlockUpdate(buffer, 0, count);
                    remaining -= count;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var digest = new byte[hasher.GetDigestSize()];
                hasher.DoFinal(digest);
                return Result(CryptographicOperations.FixedTimeEquals(digest, expectedDigest) ? null : "Partition hash mismatch.");
            }
            var geometryError = ValidateTreeGeometry(descriptor, (ulong)image.Length);
            if (geometryError is not null)
            {
                return Result(geometryError);
            }
            var slotSize = (int)BitOperations.RoundUpToPowerOf2((uint)hasher.GetDigestSize());
            var levels = CalculateLevels((long)descriptor.ImageSize, (int)descriptor.DataBlockSize, (int)descriptor.HashBlockSize, slotSize);
            var calculatedSize = levels.Sum(level => level.Size);
            if ((ulong)calculatedSize != descriptor.TreeSize)
            {
                return Result("Stored tree size does not match the descriptor geometry.");
            }
            var sourceBlock = new byte[Math.Max(descriptor.DataBlockSize, descriptor.HashBlockSize)];
            var computedBlock = new byte[descriptor.HashBlockSize];
            var digestBytes = new byte[hasher.GetDigestSize()];
            var sourceOffset = 0L;
            var sourceSize = (long)descriptor.ImageSize;
            var sourceBlockSize = (int)descriptor.DataBlockSize;
            foreach (var level in levels)
            {
                var blocks = sourceSize / sourceBlockSize;
                var blockIndex = 0L;
                for (var destination = 0L; destination < level.Size; destination += computedBlock.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Array.Clear(computedBlock);
                    for (var slot = 0; slot < computedBlock.Length && blockIndex < blocks; slot += slotSize, blockIndex++)
                    {
                        image.Position = sourceOffset + blockIndex * sourceBlockSize;
                        await image.ReadExactlyAsync(sourceBlock.AsMemory(0, sourceBlockSize), cancellationToken).ConfigureAwait(false);
                        HashBlock(hasher, salt, sourceBlock.AsSpan(0, sourceBlockSize), digestBytes);
                        digestBytes.CopyTo(computedBlock.AsSpan(slot));
                    }
                    image.Position = checked((long)descriptor.TreeOffset + level.Offset + destination);
                    await image.ReadExactlyAsync(sourceBlock.AsMemory(0, computedBlock.Length), cancellationToken).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(computedBlock, sourceBlock.AsSpan(0, computedBlock.Length)))
                    {
                        return Result("Stored hashtree block mismatch.", false);
                    }
                }
                sourceOffset = checked((long)descriptor.TreeOffset + level.Offset);
                sourceSize = level.Size;
                sourceBlockSize = (int)descriptor.HashBlockSize;
            }
            image.Position = sourceOffset;
            await image.ReadExactlyAsync(sourceBlock.AsMemory(0, (int)sourceSize), cancellationToken).ConfigureAwait(false);
            HashBlock(hasher, salt, sourceBlock.AsSpan(0, (int)sourceSize), digestBytes);
            cancellationToken.ThrowIfCancellationRequested();
            return Result(CryptographicOperations.FixedTimeEquals(digestBytes, expectedDigest) ? null : "Hashtree root digest mismatch.", true);
        }
        catch (OverflowException)
        {
            return Result("The descriptor geometry exceeds the supported address range.");
        }
        finally
        {
            image.Position = position;
        }
    }

    /// <summary>Checks partition content and, when declared, recomputes every FEC parity byte.
    /// <para>验证分区内容，并在描述符声明 FEC 时重新计算每个校验码字节。</para></summary>
    /// <param name="image">Caller-owned readable, seekable raw image; its position is restored.
    /// <para>调用方持有的可读、可定位原始镜像；结束后恢复位置。</para></param>
    /// <param name="descriptor">Separately authenticated hash or hashtree descriptor.
    /// <para>需要单独认证的哈希或哈希树描述符。</para></param>
    /// <param name="cancellationToken">Cancels content and parity reads. <para>取消内容与校验码读取。</para></param>
    /// <returns>Independent content, stored-tree and FEC results; invalid parity does not change the content result.
    /// <para>独立的内容、存储树与 FEC 结果；校验码不匹配不改变内容结果。</para></returns>
    /// <exception cref="ArgumentNullException">An input is null. <para>输入为 null。</para></exception>
    /// <exception cref="ArgumentException">The stream cannot read or seek. <para>流不可读或不可定位。</para></exception>
    /// <exception cref="EndOfStreamException">Declared bytes cannot be read. <para>无法读取声明的字节。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
    public static async Task<AvbPartitionVerifyResult> VerifyWithFecAsync(
        Stream image, AvbDescriptorInfo descriptor, CancellationToken cancellationToken = default)
    {
        var content = await VerifyAsync(image, descriptor, cancellationToken).ConfigureAwait(false);
        if (!content.ContentIsValid || !content.FecPresent)
        {
            return content;
        }
        bool valid;
        try
        {
            valid = await AvbFecCodec.VerifyAsync(image, checked((long)descriptor.FecOffset),
                checked((long)descriptor.FecSize), descriptor.FecNumRoots, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            valid = false;
        }
        return new AvbPartitionVerifyResult
        {
            PartitionName = content.PartitionName,
            ContentIsValid = content.ContentIsValid,
            StoredTreeIsValid = content.StoredTreeIsValid,
            FecPresent = true,
            FecIsValid = valid,
            ErrorMessage = valid ? content.ErrorMessage : "FEC parity geometry or bytes do not match."
        };
    }

    private static IDigest? CreateHasher(string? algorithm) => algorithm switch
    {
        "sha1" => new Sha1Digest(),
        "sha256" => new Sha256Digest(),
        "sha512" => new Sha512Digest(),
        "blake2b-256" => new Blake2bDigest(256),
        _ => null
    };

    private static void HashBlock(IDigest hasher, byte[] salt, ReadOnlySpan<byte> block, byte[] digest)
    {
        hasher.BlockUpdate(salt);
        hasher.BlockUpdate(block);
        hasher.DoFinal(digest);
    }

    private static string? ValidateTreeGeometry(AvbDescriptorInfo descriptor, ulong length)
    {
        if (descriptor.DmVerityVersion != 1 || (descriptor.Flags & ~3U) != 0)
        {
            return "The hashtree version or flags are unsupported.";
        }
        if (!ValidBlockSize(descriptor.DataBlockSize) || !ValidBlockSize(descriptor.HashBlockSize))
        {
            return "Data and hash block sizes must be powers of two between 512 bytes and 1 MiB.";
        }
        if (descriptor.ImageSize == 0 || descriptor.ImageSize % descriptor.DataBlockSize != 0)
        {
            return "Hashtree content must contain whole nonempty data blocks.";
        }
        if (descriptor.TreeOffset > length || descriptor.TreeSize > length - descriptor.TreeOffset
            || descriptor.TreeSize != 0 && descriptor.TreeOffset < descriptor.ImageSize
            || descriptor.TreeOffset % descriptor.HashBlockSize != 0)
        {
            return "The stored hashtree is unaligned, overlaps content or exceeds the partition.";
        }
        if (descriptor.FecNumRoots == 0)
        {
            return descriptor.FecOffset == 0 && descriptor.FecSize == 0 ? null : "FEC offsets require nonzero FEC roots.";
        }
        if (descriptor.FecNumRoots >= 255 || descriptor.FecSize == 0 || descriptor.FecOffset < descriptor.ImageSize
            || descriptor.FecOffset < descriptor.TreeOffset + descriptor.TreeSize
            || descriptor.FecOffset > length || descriptor.FecSize > length - descriptor.FecOffset)
        {
            return "The FEC region overlaps protected data or exceeds the partition.";
        }
        return null;
    }

    private static bool ValidBlockSize(uint size) => size is >= 512 and <= MaximumBlockSize && BitOperations.IsPow2(size);

    private static List<TreeLevel> CalculateLevels(long size, int dataBlockSize, int hashBlockSize, int slotSize)
    {
        var sizes = new List<long>();
        var blockSize = dataBlockSize;
        while (size > blockSize)
        {
            size = checked(((size / blockSize * slotSize + hashBlockSize - 1) / hashBlockSize) * hashBlockSize);
            sizes.Add(size);
            blockSize = hashBlockSize;
        }
        var levels = new TreeLevel[sizes.Count];
        var offset = 0L;
        for (var index = sizes.Count - 1; index >= 0; index--)
        {
            levels[index] = new TreeLevel(offset, sizes[index]);
            offset = checked(offset + sizes[index]);
        }
        return [.. levels];
    }
}