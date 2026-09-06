using System.Security.Cryptography;

namespace UotanStudio.Firmware.Avb;

public sealed class AvbHashtreeResult
{
    public byte[] RootDigest { get; init; } = [];

    public byte[] TreeBytes { get; init; } = [];

    public long TreeSize { get; init; }

    public long ImageSize { get; init; }
}

public static class AvbHashtreeBuilder
{
    private const int Sha256Size = 32;
    private const int ReadBufferSize = 64 * 1024;

    /// <summary>
    /// Builds the dm-verity hash tree for the image at imagePath.
    /// <para>为 imagePath 指定的镜像构建 dm-verity 哈希树。</para>
    /// </summary>
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
    public static AvbHashtreeResult Build(byte[] imageData, byte[]? salt, int dataBlockSize = 4096, int hashBlockSize = 4096)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        using var stream = new MemoryStream(imageData, writable: false);
        return Build(stream, salt, dataBlockSize, hashBlockSize);
    }

    /// <summary>
    /// Builds the dm-verity hash tree by streaming a seekable stream.
    /// <para>通过流式读取可定位流构建 dm-verity 哈希树。</para>
    /// </summary>
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
    /// Computes each level's offset within the tree buffer and the total tree size.
    /// <para>计算各层级在树缓冲中的偏移与树总大小。</para>
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
    /// Streams the image in a single pass, computing each data block's digest into the level-0 region.
    /// <para>单遍流式读取镜像，逐数据块计算摘要并写入树的第 0 层区域。</para>
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
    /// Reads a single data block, zero-padding the remainder before EOF.
    /// <para>读取单个数据块，EOF 前不足部分以零补满。</para>
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
    /// Computes SHA256(salt || data).
    /// <para>计算 SHA256(salt || data)。</para>
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
