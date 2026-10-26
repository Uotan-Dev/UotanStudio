using System.Numerics;
using System.Security.Cryptography;
using UotanStudio.Firmware.Core;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.BootSecurity.Avb.Verification;

/// <summary>
/// Verifies raw partition bytes against hash or dm-verity version-one descriptors using bounded buffers.
/// <para>使用有界缓冲，按哈希或 dm-verity 第一版描述符验证原始分区字节。</para>
/// </summary>
/// <remarks>
/// Descriptors must be authenticated separately. Sparse containers must first be exposed as a logical raw stream.
/// Persistent digests, zeroed trees and FEC parity are not silently accepted as verified. Hashtree content is read
/// through one bounded prefetch window per cursor (data blocks and stored tree blocks keep separate windows), while
/// every data block is still hashed individually; the level-0 blocks are hashed in bounded parallel batches with one
/// digest accumulator per slot and the higher levels scalarly.
/// <para>描述符须单独认证。Sparse 容器必须先暴露为逻辑原始流。持久化摘要、清零树和 FEC 校验码不会被静默视为已验证。
/// 哈希树内容按每个游标一个有界预取窗口读取（数据块与存储树块各自持有窗口），但每个数据块仍单独哈希；
/// level-0 各块以有界并行批次哈希（每槽一个摘要累加器），更高层保持标量处理。</para>
/// </remarks>
internal static class AvbPartitionVerifier
{
    private const int MaximumBlockSize = 1024 * 1024;
    private const int MaximumSaltSize = 16 * 1024 * 1024;
    /// <summary>Byte budget of one level-0 hash batch, matching the builder's read buffer size.
    /// <para>单次 level-0 哈希批次的字节预算，与构建器的读取缓冲大小一致。</para></summary>
    private const int Level0BatchBytes = 4 * 1024 * 1024;
    /// <summary>Upper bound of data blocks hashed in one parallel batch.
    /// <para>单次并行批次哈希的数据块数上限。</para></summary>
    private const int Level0BatchBlocks = 256;
    private sealed record TreeLevel(long Offset, long Size);

    /// <summary>Checks a descriptor against a caller-owned partition stream without writing or loading the whole image.
    /// <para>按描述符检查调用方拥有的分区流，不写入也不整体加载镜像。</para></summary>
    /// <param name="image">Readable, seekable raw partition stream, starting logically at offset zero; its position is restored.
    /// <para>可读、可定位的原始分区流，逻辑起点为偏移零；结束时恢复位置。</para></param>
    /// <param name="descriptor">Hash or hashtree metadata whose signature and trust are checked independently.
    /// <para>哈希或哈希树元数据，其签名和信任单独检查。</para></param>
    /// <param name="cancellationToken">Cancels reads and block processing.
    /// <para>取消读取和块处理。</para></param>
    /// <returns>Content and stored-tree results with separate, unevaluated FEC status.
    /// <para>内容和存储树结果，另附未评估的 FEC 状态。</para></returns>
    /// <exception cref="ArgumentNullException">An input is null.<para>某个输入为 null。</para></exception>
    /// <exception cref="ArgumentException">The stream cannot read or seek.
    /// <para>流不可读或不可定位。</para></exception>
    /// <exception cref="EndOfStreamException">Declared bytes cannot be read completely.
    /// <para>无法完整读取声明的字节。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.
    /// <para>请求了取消。</para></exception>
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
            using var hasher = CreateDescriptorDigest(descriptor.HashAlgorithm);
            if (hasher is null)
            {
                return Result("The descriptor hash algorithm is unsupported.");
            }
            var expectedDigest = descriptor.Tag == 2 ? descriptor.Digest : descriptor.RootDigest;
            if (expectedDigest is not { Length: > 0 })
            {
                return Result("Persistent digests require an external trusted digest and are not verified by this entry point.");
            }
            if (expectedDigest.Length != hasher.HashLengthInBytes)
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
                hasher.Append(salt);
                var buffer = new byte[64 * 1024];
                var remaining = (long)descriptor.ImageSize;
                while (remaining > 0)
                {
                    var count = (int)Math.Min(buffer.Length, remaining);
                    await image.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    hasher.Append(buffer.AsSpan(0, count));
                    remaining -= count;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var digest = new byte[hasher.HashLengthInBytes];
                hasher.GetHashAndReset(digest);
                return Result(CryptographicOperations.FixedTimeEquals(digest, expectedDigest) ? null : "Partition hash mismatch.");
            }
            var geometryError = ValidateTreeGeometry(descriptor, (ulong)image.Length);
            if (geometryError is not null)
            {
                return Result(geometryError);
            }
            var slotSize = (int)BitOperations.RoundUpToPowerOf2((uint)hasher.HashLengthInBytes);
            var levels = CalculateLevels((long)descriptor.ImageSize, (int)descriptor.DataBlockSize, (int)descriptor.HashBlockSize, slotSize);
            var calculatedSize = levels.Sum(level => level.Size);
            if ((ulong)calculatedSize != descriptor.TreeSize)
            {
                return Result("Stored tree size does not match the descriptor geometry.");
            }
            var dataBlockSize = (int)descriptor.DataBlockSize;
            var treeBlockSize = (int)descriptor.HashBlockSize;
            var imageSize = (long)descriptor.ImageSize;
            var treeLimit = checked((long)descriptor.TreeOffset + (long)descriptor.TreeSize);
            var computedBlock = new byte[treeBlockSize];
            var digestBytes = new byte[hasher.HashLengthInBytes];
            var slotsPerHashBlock = Math.Max(1, treeBlockSize / slotSize);
            var dataWindowSize = (int)Math.Min(AvbWindowedReader.MaximumWindowBytes,
                Math.Max(Math.Max(dataBlockSize, treeBlockSize), (long)slotsPerHashBlock * dataBlockSize));
            using var dataReader = new AvbWindowedReader(image, Math.Max(imageSize, treeLimit), dataWindowSize);
            using var treeReader = new AvbWindowedReader(image, treeLimit, treeBlockSize);
            var sourceOffset = 0L;
            var sourceSize = imageSize;
            var sourceBlockSize = dataBlockSize;
            for (var levelIndex = 0; levelIndex < levels.Count; levelIndex++)
            {
                var level = levels[levelIndex];
                var blocks = sourceSize / sourceBlockSize;
                if (levelIndex == 0)
                {
                    var mismatch = await HashLevel0Async(
                        dataReader, treeReader, salt, descriptor.HashAlgorithm, hasher.HashLengthInBytes,
                        (long)descriptor.TreeOffset + level.Offset, blocks, dataBlockSize, treeBlockSize,
                        slotSize, slotsPerHashBlock, computedBlock, cancellationToken).ConfigureAwait(false);
                    if (mismatch is not null)
                    {
                        return Result(mismatch, false);
                    }
                }
                else
                {
                    var blockIndex = 0L;
                    for (var destination = 0L; destination < level.Size; destination += computedBlock.Length)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Array.Clear(computedBlock);
                        for (var slot = 0; slot < computedBlock.Length && blockIndex < blocks; slot += slotSize, blockIndex++)
                        {
                            var source = await dataReader.ReadBlockAsync(
                                checked(sourceOffset + blockIndex * sourceBlockSize), sourceBlockSize, cancellationToken).ConfigureAwait(false);
                            HashBlock(hasher, salt, source.Span, digestBytes);
                            digestBytes.CopyTo(computedBlock.AsSpan(slot));
                        }
                        var stored = await treeReader.ReadBlockAsync(
                            checked((long)descriptor.TreeOffset + level.Offset + destination), computedBlock.Length, cancellationToken).ConfigureAwait(false);
                        if (!CryptographicOperations.FixedTimeEquals(computedBlock, stored.Span))
                        {
                            return Result("Stored hashtree block mismatch.", false);
                        }
                    }
                }
                sourceOffset = checked((long)descriptor.TreeOffset + level.Offset);
                sourceSize = level.Size;
                sourceBlockSize = treeBlockSize;
            }
            var rootBlock = new byte[(int)sourceSize];
            var rootReader = levels.Count == 0 ? dataReader : treeReader;
            await rootReader.ReadExactlyAsync(sourceOffset, rootBlock, cancellationToken).ConfigureAwait(false);
            HashBlock(hasher, salt, rootBlock, digestBytes);
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
    /// <param name="cancellationToken">Cancels content and parity reads.
    /// <para>取消内容与校验码读取。</para></param>
    /// <returns>Independent content, stored-tree and FEC results; invalid parity does not change the content result.
    /// <para>独立的内容、存储树与 FEC 结果；校验码不匹配不改变内容结果。</para></returns>
    /// <exception cref="ArgumentNullException">An input is null.<para>输入为 null。</para></exception>
    /// <exception cref="ArgumentException">The stream cannot read or seek.
    /// <para>流不可读或不可定位。</para></exception>
    /// <exception cref="EndOfStreamException">Declared bytes cannot be read.
    /// <para>无法读取声明的字节。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.
    /// <para>请求了取消。</para></exception>
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

    private static void HashBlock(DigestAccumulator hasher, byte[] salt, ReadOnlySpan<byte> block, Span<byte> digest)
    {
        hasher.Append(salt);
        hasher.Append(block);
        hasher.GetHashAndReset(digest);
    }

    /// <summary>
    /// Hashes every level-0 data block in parallel batches while the stored-tree comparisons stay in stored order,
    /// mirroring the builder's level-0 worker layout: one digest accumulator per batch slot, bounded batch bytes,
    /// and digests copied back in ascending block order.
    /// <para>以并行批次哈希全部 level-0 数据块，而存储树对比保持存储顺序，与构建器的 level-0 工作者布局一致：
    /// 每个批次槽位一个摘要累加器、批字节数有界、摘要按块号升序回填。</para>
    /// </summary>
    /// <param name="dataReader">Windowed reader over the partition content starting at offset zero.
    /// <para>覆盖从偏移零开始的分区内容的窗口读取器。</para></param>
    /// <param name="treeReader">Windowed reader over the stored tree blocks.
    /// <para>覆盖存储树块的窗口读取器。</para></param>
    /// <param name="salt">Descriptor salt prepended to every block hash. <para>前置到每个块哈希的描述符盐。</para></param>
    /// <param name="hashAlgorithm">Descriptor algorithm name used to create the per-slot accumulators.
    /// <para>用于创建每槽摘要累加器的描述符算法名。</para></param>
    /// <param name="hashLength">Digest length in bytes of the algorithm. <para>算法的摘要字节长度。</para></param>
    /// <param name="level0TreeOffset">Absolute offset of the level-0 run within the stored tree.
    /// <para>存储树中 level-0 段的绝对偏移。</para></param>
    /// <param name="blocks">Number of level-0 data blocks. <para>level-0 数据块数。</para></param>
    /// <param name="dataBlockSize">Data block size in bytes. <para>数据块字节大小。</para></param>
    /// <param name="treeBlockSize">Stored tree block size in bytes. <para>存储树块字节大小。</para></param>
    /// <param name="slotSize">Rounded digest slot size in bytes. <para>取整后的摘要槽位字节大小。</para></param>
    /// <param name="slotsPerHashBlock">Digest slots fitting one tree block. <para>单个树块容纳的摘要槽位数。</para></param>
    /// <param name="computedBlock">Scratch tree block rebuilt from the computed digests.
    /// <para>由计算摘要重建的临时树块。</para></param>
    /// <param name="cancellationToken">Cancels reads and hashing. <para>取消读取与哈希。</para></param>
    /// <returns><see langword="null"/> when every stored block matches; otherwise the mismatch error.
    /// <para>全部存储块匹配时为 <see langword="null"/>；否则为不匹配错误。</para></returns>
    /// <exception cref="EndOfStreamException">Declared bytes cannot be read completely.
    /// <para>无法完整读取声明的字节。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.
    /// <para>请求了取消。</para></exception>
    private static async Task<string?> HashLevel0Async(
        AvbWindowedReader dataReader, AvbWindowedReader treeReader, byte[] salt, string? hashAlgorithm,
        int hashLength, long level0TreeOffset, long blocks, int dataBlockSize, int treeBlockSize,
        int slotSize, int slotsPerHashBlock, byte[] computedBlock, CancellationToken cancellationToken)
    {
        var blockCapacity = Math.Max(1, Math.Min(Level0BatchBytes / dataBlockSize, Level0BatchBlocks));
        var batchBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(checked(blockCapacity * dataBlockSize));
        var digests = new byte[checked(blockCapacity * hashLength)];
        var slots = new int[blockCapacity];
        for (var slot = 0; slot < slots.Length; slot++)
        {
            slots[slot] = slot;
        }

        var hashers = new DigestAccumulator[blockCapacity];
        var created = 0;
        try
        {
            for (; created < hashers.Length; created++)
            {
                hashers[created] = CreateDescriptorDigest(hashAlgorithm)!;
            }

            var remaining = blocks;
            var blockIndex = 0L;
            var filledSlots = 0;
            string? mismatch = null;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(blockCapacity, remaining);
                var bytes = checked(count * dataBlockSize);
                await dataReader.ReadExactlyAsync(checked(blockIndex * dataBlockSize), batchBuffer.AsMemory(0, bytes), cancellationToken).ConfigureAwait(false);
                FirmwareParallelism.ForEach(new ArraySegment<int>(slots, 0, count), (slot, _) =>
                {
                    HashBlock(
                        hashers[slot],
                        salt,
                        batchBuffer.AsSpan(slot * dataBlockSize, dataBlockSize),
                        digests.AsSpan(slot * hashLength, hashLength));
                });
                for (var i = 0; i < count; i++, blockIndex++)
                {
                    if (filledSlots == 0)
                    {
                        Array.Clear(computedBlock);
                    }

                    digests.AsSpan(i * hashLength, hashLength).CopyTo(computedBlock.AsSpan(filledSlots * slotSize));
                    filledSlots++;
                    if (filledSlots == slotsPerHashBlock)
                    {
                        mismatch = await CompareLevel0BlockAsync(
                            treeReader, level0TreeOffset, blockIndex, slotsPerHashBlock, computedBlock, cancellationToken).ConfigureAwait(false);
                        if (mismatch is not null)
                        {
                            return mismatch;
                        }

                        filledSlots = 0;
                    }
                }

                remaining -= count;
            }

            if (filledSlots > 0)
            {
                mismatch = await CompareLevel0BlockAsync(
                    treeReader, level0TreeOffset, blockIndex, slotsPerHashBlock, computedBlock, cancellationToken).ConfigureAwait(false);
            }

            return mismatch;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(batchBuffer);
            for (var slot = 0; slot < created; slot++)
            {
                hashers[slot].Dispose();
            }
        }
    }

    /// <summary>
    /// Compares one rebuilt tree block against its stored counterpart at the position implied by the consumed block
    /// count.
    /// <para>按已消费块数隐含的位置，将重建的树块与其存储对应块对比。</para>
    /// </summary>
    /// <param name="treeReader">Windowed reader over the stored tree blocks. <para>覆盖存储树块的窗口读取器。</para></param>
    /// <param name="level0TreeOffset">Absolute offset of the level-0 run. <para>level-0 段的绝对偏移。</para></param>
    /// <param name="consumedBlocks">Data blocks consumed including this tree block.
    /// <para>含本树块在内已消费的数据块数。</para></param>
    /// <param name="slotsPerHashBlock">Digest slots fitting one tree block. <para>单个树块容纳的摘要槽位数。</para></param>
    /// <param name="computedBlock">Rebuilt tree block. <para>重建的树块。</para></param>
    /// <param name="cancellationToken">Cancels the stored-block read. <para>取消存储块读取。</para></param>
    /// <returns><see langword="null"/> on a match; otherwise the mismatch error.
    /// <para>匹配时为 <see langword="null"/>；否则为不匹配错误。</para></returns>
    private static async Task<string?> CompareLevel0BlockAsync(
        AvbWindowedReader treeReader, long level0TreeOffset, long consumedBlocks, int slotsPerHashBlock,
        byte[] computedBlock, CancellationToken cancellationToken)
    {
        var lastFullBlock = consumedBlocks % slotsPerHashBlock == 0;
        var blockNumber = lastFullBlock ? consumedBlocks / slotsPerHashBlock - 1 : consumedBlocks / slotsPerHashBlock;
        var stored = await treeReader.ReadBlockAsync(
            checked(level0TreeOffset + blockNumber * computedBlock.Length), computedBlock.Length, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(computedBlock, stored.Span) ? null : "Stored hashtree block mismatch.";
    }

    /// <summary>
    /// Creates the digest an AVB descriptor names, or <see langword="null"/> when the name is outside
    /// the accepted set (only <c>sha1</c>, <c>sha256</c> and <c>sha512</c> are supported).
    /// <para>创建 AVB 描述符所指定的摘要；名称不在受支持集合（仅 <c>sha1</c>、<c>sha256</c>、<c>sha512</c>）内时返回 <see langword="null"/>。</para>
    /// </summary>
    private static DigestAccumulator? CreateDescriptorDigest(string? algorithm) => algorithm switch
    {
        "sha1" => Sha1.CreateAccumulator(),
        "sha256" => Sha256.CreateAccumulator(),
        "sha512" => Sha512.CreateAccumulator(),
        _ => null
    };

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