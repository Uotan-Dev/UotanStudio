using System.Security.Cryptography;

namespace UotanStudio.Firmware.Avb;

/// <summary>
/// Result of building an AVB dm-verity hash tree: the root digest, the serialized tree bytes and
/// the rounded-up image size the tree was computed over.
/// <para>构建 AVB dm-verity 哈希树的结果：根摘要、序列化后的树字节与树所覆盖的对齐后镜像大小。</para>
/// </summary>
public sealed class AvbHashtreeResult
{
    /// <summary>
    /// Gets the root digest (<c>SHA256(salt || last_hash_block)</c>) to embed in an
    /// <see cref="AvbHashtreeDescriptor"/>.
    /// <para>获取根摘要（<c>SHA256(salt || 末级哈希块)</c>），用于嵌入
    /// <see cref="AvbHashtreeDescriptor"/>。</para>
    /// </summary>
    public byte[] RootDigest { get; init; } = [];

    /// <summary>
    /// Gets the serialized hash tree bytes; all levels are laid out consecutively in hash blocks,
    /// higher levels at lower offsets, each trailing partial hash block zero-padded.
    /// <para>获取序列化后的哈希树字节；所有层级按哈希块连续排布，高层位于低偏移处，
    /// 每个末尾的部分哈希块以零填充。</para>
    /// </summary>
    public byte[] TreeBytes { get; init; } = [];

    /// <summary>
    /// Gets the size of the hash tree in bytes (a multiple of the hash block size).
    /// <para>获取哈希树大小（字节，为哈希块大小的整数倍）。</para>
    /// </summary>
    public long TreeSize { get; init; }

    /// <summary>
    /// Gets the image size the tree covers: the source length rounded up to a multiple of the
    /// data block size (trailing partial blocks are zero-padded before hashing).
    /// <para>获取树所覆盖的镜像大小：源长度向上取整到数据块大小的倍数（末尾部分块
    /// 哈希前以零填充）。</para>
    /// </summary>
    public long ImageSize { get; init; }
}

/// <summary>
/// Builds an AVB dm-verity Merkle hash tree over a partition image, byte-for-byte compatible with
/// AOSP dm-verity and <c>avbtool add_hashtree_footer</c> (<c>calc_hash_level_offsets</c> +
/// <c>generate_hash_tree</c>): each data block is hashed as <c>SHA256(salt || block)</c>, the
/// 32-byte digests are packed into hash blocks of <c>hashBlockSize / 32</c> digests each, the levels
/// are repeated until a single digest remains (the root digest), and every level is padded to whole
/// hash blocks. Images are streamed with a bounded 64&nbsp;KiB buffer, never loaded in full.
/// <para>为分区镜像构建 AVB dm-verity Merkle 哈希树，与 AOSP dm-verity 及
/// <c>avbtool add_hashtree_footer</c>（<c>calc_hash_level_offsets</c> + <c>generate_hash_tree</c>）
/// 逐字节兼容：每个数据块按 <c>SHA256(salt || block)</c> 哈希，32 字节摘要按每
/// <c>hashBlockSize / 32</c> 个组成一个哈希块，逐层计算直至剩单个摘要（根摘要），每层补齐为
/// 整哈希块。镜像以 64&nbsp;KiB 有界缓冲流式读取，绝不整文件载入内存。</para>
/// </summary>
public static class AvbHashtreeBuilder
{
    private const int Sha256Size = 32;
    private const int ReadBufferSize = 64 * 1024;

    /// <summary>
    /// Builds the dm-verity hash tree for the image at <paramref name="imagePath"/>.
    /// <para>为 <paramref name="imagePath"/> 指定的镜像构建 dm-verity 哈希树。</para>
    /// </summary>
    /// <param name="imagePath">The partition image path. <para>分区镜像路径。</para></param>
    /// <param name="salt">The salt prefixed to every block before hashing (may be empty).
    /// <para>哈希前拼接到每个块前的盐值（可为空）。</para></param>
    /// <param name="dataBlockSize">The data block size in bytes (default 4096).
    /// <para>数据块大小（字节，默认 4096）。</para></param>
    /// <param name="hashBlockSize">The hash block size in bytes (default 4096).
    /// <para>哈希块大小（字节，默认 4096）。</para></param>
    /// <returns>The root digest and the serialized tree. <para>根摘要与序列化后的树。</para></returns>
    public static AvbHashtreeResult Build(string imagePath, byte[]? salt, int dataBlockSize = 4096, int hashBlockSize = 4096)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        ValidateBlockSize(dataBlockSize, nameof(dataBlockSize));
        ValidateBlockSize(hashBlockSize, nameof(hashBlockSize));

        using var stream = new FileStream(
            imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, ReadBufferSize, FileOptions.SequentialScan);
        return Build(stream, salt, dataBlockSize, hashBlockSize);
    }

    /// <summary>
    /// Builds the dm-verity hash tree for the given in-memory image bytes.
    /// <para>为给定的内存镜像字节构建 dm-verity 哈希树。</para>
    /// </summary>
    /// <param name="imageData">The partition image bytes. <para>分区镜像字节。</para></param>
    /// <param name="salt">The salt prefixed to every block before hashing (may be empty).
    /// <para>哈希前拼接到每个块前的盐值（可为空）。</para></param>
    /// <param name="dataBlockSize">The data block size in bytes (default 4096).
    /// <para>数据块大小（字节，默认 4096）。</para></param>
    /// <param name="hashBlockSize">The hash block size in bytes (default 4096).
    /// <para>哈希块大小（字节，默认 4096）。</para></param>
    /// <returns>The root digest and the serialized tree. <para>根摘要与序列化后的树。</para></returns>
    public static AvbHashtreeResult Build(byte[] imageData, byte[]? salt, int dataBlockSize = 4096, int hashBlockSize = 4096)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        using var stream = new MemoryStream(imageData, writable: false);
        return Build(stream, salt, dataBlockSize, hashBlockSize);
    }

    /// <summary>
    /// Builds the dm-verity hash tree by streaming a seekable stream; the stream position must be
    /// at the start of the image and is restored afterwards.
    /// <para>通过流式读取一个可定位流构建 dm-verity 哈希树；流位置须位于镜像开头，读取后恢复。</para>
    /// </summary>
    /// <param name="stream">The seekable image stream. <para>可定位的镜像流。</para></param>
    /// <param name="salt">The salt prefixed to every block before hashing (may be empty).
    /// <para>哈希前拼接到每个块前的盐值（可为空）。</para></param>
    /// <param name="dataBlockSize">The data block size in bytes (default 4096).
    /// <para>数据块大小（字节，默认 4096）。</para></param>
    /// <param name="hashBlockSize">The hash block size in bytes (default 4096).
    /// <para>哈希块大小（字节，默认 4096）。</para></param>
    /// <returns>The root digest and the serialized tree. <para>根摘要与序列化后的树。</para></returns>
    public static AvbHashtreeResult Build(Stream stream, byte[]? salt, int dataBlockSize = 4096, int hashBlockSize = 4096)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable. 流必须可读且可定位。", nameof(stream));
        }

        ValidateBlockSize(dataBlockSize, nameof(dataBlockSize));
        ValidateBlockSize(hashBlockSize, nameof(hashBlockSize));

        var effectiveSalt = salt ?? [];
        var originalPosition = stream.Position;
        try
        {
            var imageSize = checked(((stream.Length + dataBlockSize - 1) / dataBlockSize) * (long)dataBlockSize);
            if (imageSize == 0)
            {
                return new AvbHashtreeResult { RootDigest = HashBlock(effectiveSalt, []), ImageSize = 0 };
            }

            var (levelOffsets, treeSize) = CalculateLevels(imageSize, dataBlockSize, hashBlockSize);
            if (treeSize == 0)
            {
                // 单数据块：树为空，根摘要即该块的摘要。
                // Single data block: empty tree, the root digest is the block digest itself.
                stream.Position = 0;
                var block = ReadAndPadBlock(stream, dataBlockSize);
                return new AvbHashtreeResult
                {
                    RootDigest = HashBlock(effectiveSalt, block),
                    TreeBytes = [],
                    ImageSize = imageSize
                };
            }

            var tree = new byte[treeSize];
            var level0Offset = levelOffsets[0];
            var digestIndex = 0;
            stream.Position = 0;
            StreamLevel0IntoTree(stream, effectiveSalt, dataBlockSize, tree, level0Offset, ref digestIndex);

            var srcOffset = level0Offset;
            var srcSize = checked((long)((ulong)digestIndex * Sha256Size));
            srcSize = RoundUp(srcSize, hashBlockSize);
            for (var level = 1; level < levelOffsets.Length; level++)
            {
                var numDigests = checked((int)(srcSize / hashBlockSize));
                var dstOffset = levelOffsets[level];
                for (var i = 0; i < numDigests; i++)
                {
                    var digest = HashBlock(effectiveSalt, tree.AsSpan(srcOffset + i * hashBlockSize, hashBlockSize));
                    digest.CopyTo(tree, dstOffset + i * Sha256Size);
                }

                srcOffset = dstOffset;
                srcSize = RoundUp((long)numDigests * Sha256Size, hashBlockSize);
            }

            var root = HashBlock(effectiveSalt, tree.AsSpan(srcOffset, (int)srcSize));
            return new AvbHashtreeResult
            {
                RootDigest = root,
                TreeBytes = tree,
                TreeSize = treeSize,
                ImageSize = imageSize
            };
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    /// <summary>
    /// 计算各层级在树缓冲中的偏移与树总大小（高层在低偏移处，逐层对齐到哈希块）。
    /// <para>Computes each level's offset within the tree buffer and the total tree size (higher
    /// levels at lower offsets, every level aligned to whole hash blocks).</para>
    /// </summary>
    private static (int[] LevelOffsets, int TreeSize) CalculateLevels(long imageSize, int dataBlockSize, int hashBlockSize)
    {
        var levelSizes = new List<int>();
        var treeSize = 0;
        var size = imageSize;
        while (size > dataBlockSize)
        {
            var numBlocks = checked(((size + dataBlockSize - 1) / dataBlockSize));
            var levelSize = checked((int)RoundUp(numBlocks * Sha256Size, hashBlockSize));
            levelSizes.Add(levelSize);
            treeSize = checked(treeSize + levelSize);
            size = levelSize;
        }

        var offsets = new int[levelSizes.Count];
        for (var n = 0; n < levelSizes.Count; n++)
        {
            var offset = 0;
            for (var m = n + 1; m < levelSizes.Count; m++)
            {
                offset = checked(offset + levelSizes[m]);
            }

            offsets[n] = offset;
        }

        return (offsets, treeSize);
    }

    /// <summary>
    /// 单遍流式读取镜像，逐数据块计算摘要并写入树的第 0 层区域（末尾部分块补零）。
    /// <para>Streams the image in a single pass, computing each data block's digest into the
    /// level-0 region of the tree (the trailing partial block is zero-padded).</para>
    /// </summary>
    private static void StreamLevel0IntoTree(Stream stream, byte[] salt, int dataBlockSize, byte[] tree, int level0Offset, ref int digestIndex)
    {
        var buffer = new byte[ReadBufferSize];
        var block = new byte[dataBlockSize];
        var blockFill = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var offset = 0;
            while (offset < read)
            {
                var toCopy = Math.Min(read - offset, dataBlockSize - blockFill);
                Array.Copy(buffer, offset, block, blockFill, toCopy);
                blockFill += toCopy;
                offset += toCopy;
                if (blockFill == dataBlockSize)
                {
                    var digest = HashBlock(salt, block);
                    digest.CopyTo(tree, level0Offset + digestIndex * Sha256Size);
                    digestIndex = checked(digestIndex + 1);
                    blockFill = 0;
                }
            }
        }

        if (blockFill > 0)
        {
            Array.Clear(block, blockFill, dataBlockSize - blockFill);
            var digest = HashBlock(salt, block);
            digest.CopyTo(tree, level0Offset + digestIndex * Sha256Size);
            digestIndex = checked(digestIndex + 1);
        }
    }

    /// <summary>
    /// 读取单个数据块，EOF 前不足的部分以零补满。
    /// <para>Reads a single data block, zero-padding the part before EOF.</para>
    /// </summary>
    private static byte[] ReadAndPadBlock(Stream stream, int dataBlockSize)
    {
        var block = new byte[dataBlockSize];
        var total = 0;
        while (total < dataBlockSize)
        {
            var read = stream.Read(block, total, dataBlockSize - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return block;
    }

    /// <summary>
    /// 计算 SHA256(salt || data)。
    /// <para>Computes SHA256(salt || data).</para>
    /// </summary>
    private static byte[] HashBlock(byte[] salt, ReadOnlySpan<byte> data)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(salt);
        hasher.AppendData(data);
        return hasher.GetHashAndReset();
    }

    private static long RoundUp(long value, int multiple) => checked(((value + multiple - 1) / multiple) * multiple);

    private static void ValidateBlockSize(int blockSize, string paramName)
    {
        if (blockSize <= 0 || blockSize % Sha256Size != 0)
        {
            throw new ArgumentOutOfRangeException(paramName, blockSize, "Block size must be a positive multiple of 32. 块大小必须是 32 的正整数倍。");
        }
    }
}
