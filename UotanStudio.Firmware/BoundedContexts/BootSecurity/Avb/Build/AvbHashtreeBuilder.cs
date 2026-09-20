using System.Security.Cryptography;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Build;

/// <summary>
/// dm-verity 哈希树构建结果：暴露根摘要、树大小与镜像大小，并允许在大镜像时把树写入临时后备存储后流式拷出。
/// <para>Result of building a dm-verity hash tree: exposes the root digest, tree size and image size, and lets
/// the tree spill to a temporary backing store so large images can be streamed out rather than held fully in memory.</para>
/// </summary>
public sealed class AvbHashtreeResult : IDisposable
{
    private TreeStore? _store;

    internal AvbHashtreeResult(byte[] rootDigest, TreeStore? store, long treeSize, long imageSize)
    {
        RootDigest = rootDigest;
        _store = store;
        TreeSize = treeSize;
        ImageSize = imageSize;
    }

    /// <summary>根摘要。<para>The Merkle root digest.</para></summary>
    public byte[] RootDigest { get; }

    /// <summary>树字节总大小。<para>Total tree byte size.</para></summary>
    public long TreeSize { get; }

    /// <summary>对齐后的镜像大小。<para>The block-aligned image size.</para></summary>
    public long ImageSize { get; }

    /// <summary>
    /// <para>完整树字节。小树内联保存；大树从临时后备存储读回，仅供测试或小规模使用，生产写镜像应改用 <see cref="WriteTreeToAsync"/>。</para>
    /// <para>Full tree bytes. Small trees are held inline; large trees are read back from the temp backing store, intended for tests or small inputs — image writers should use <see cref="WriteTreeToAsync"/> instead.</para>
    /// </summary>
    public byte[] TreeBytes => _store?.ToArray() ?? [];

    /// <summary>
    /// <para>把树字节流式写入目标流；大树不整体进入内存。</para>
    /// <para>Streams the tree bytes to the target without materializing a large tree in memory.</para>
    /// </summary>
    public Task WriteTreeToAsync(Stream output, CancellationToken cancellationToken = default)
        => _store is null ? Task.CompletedTask : _store.CopyToAsync(output, cancellationToken);

    /// <summary>释放内联或临时后备存储。<para>Releases the inline or temporary backing store.</para></summary>
    public void Dispose() => _store?.Dispose();

    /// <summary>
    /// <para>树字节后备存储：小尺寸内联为数组，大尺寸落到删除即清理的临时文件，使峰值内存受读缓冲约束。</para>
    /// <para>Tree byte backing store: inline array for small trees, or a delete-on-close temp file for large ones so peak memory is bounded by the read buffers.</para>
    /// </summary>
    internal sealed class TreeStore : IDisposable
    {
        private readonly byte[]? _inline;
        private readonly FileStream? _file;
        private readonly int _copyBufferSize;

        private TreeStore(byte[]? inline, FileStream? file, long length, int copyBufferSize)
        {
            _inline = inline;
            _file = file;
            Length = length;
            _copyBufferSize = copyBufferSize;
        }

        public long Length { get; }

        public static TreeStore Create(long length, long thresholdBytes, int copyBufferSize)
        {
            if (length <= thresholdBytes) return new TreeStore(new byte[(int)length], null, length, copyBufferSize);

            var path = Path.Combine(Path.GetTempPath(), $"uotan-avb-tree-{Guid.NewGuid():N}.bin");
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                copyBufferSize, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
            try
            {
                file.SetLength(length);
                return new TreeStore(null, file, length, copyBufferSize);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            if (_inline is not null)
            {
                data.CopyTo(_inline.AsSpan((int)offset));
                return;
            }

            _file!.Seek(offset, SeekOrigin.Begin);
            _file.Write(data);
        }

        public void Read(long offset, Span<byte> buffer)
        {
            if (_inline is not null)
            {
                _inline.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
                return;
            }

            _file!.Seek(offset, SeekOrigin.Begin);
            _file.ReadExactly(buffer);
        }

        public byte[] ToArray()
        {
            if (_inline is not null) return _inline;
            var array = new byte[(int)Length];
            Read(0, array);
            return array;
        }

        public async Task CopyToAsync(Stream output, CancellationToken cancellationToken)
        {
            if (_inline is not null)
            {
                await output.WriteAsync(_inline, cancellationToken).ConfigureAwait(false);
                return;
            }

            _file!.Seek(0, SeekOrigin.Begin);
            await _file.CopyToAsync(output, _copyBufferSize, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose() => _file?.Dispose();
    }
}

public static class AvbHashtreeBuilder
{
    private const int Sha256Size = 32;
    private const int ReadBufferSize = 64 * 1024;

    /// <summary>
    /// <para>超过此大小的树改用临时文件后备存储，使构建与输出的峰值 RSS 受读缓冲约束而非整棵树。</para>
    /// <para>Trees larger than this spill to a temporary file backing store so peak RSS is bounded by the read buffers rather than the whole tree.</para>
    /// </summary>
    private const long SpillThresholdBytes = 16L * 1024 * 1024;

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
        => BuildCore(stream, salt, dataBlockSize, hashBlockSize, SpillThresholdBytes);

    internal static AvbHashtreeResult BuildCore(Stream stream, byte[]? salt, int dataBlockSize, int hashBlockSize, long spillThresholdBytes)
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
        AvbHashtreeResult.TreeStore? store = null;
        try
        {
            var imageSize = checked(((stream.Length + dataBlockSize - 1) / dataBlockSize) * (long)dataBlockSize);
            if (imageSize == 0)
            {
                return new AvbHashtreeResult(HashBlock(effectiveSalt, []), null, 0, 0);
            }

            var (levelOffsets, treeSize) = CalculateLevels(imageSize, dataBlockSize, hashBlockSize);
            if (treeSize == 0)
            {
                stream.Position = 0;
                var block = ReadAndPadBlock(stream, dataBlockSize);
                return new AvbHashtreeResult(HashBlock(effectiveSalt, block), null, 0, imageSize);
            }

            store = AvbHashtreeResult.TreeStore.Create(treeSize, spillThresholdBytes, Math.Max(ReadBufferSize, hashBlockSize));
            var digestIndex = 0;
            stream.Position = 0;
            StreamLevel0IntoStore(stream, effectiveSalt, dataBlockSize, store, levelOffsets[0], ref digestIndex);

            var srcOffset = (long)levelOffsets[0];
            var srcSize = RoundUp((long)digestIndex * Sha256Size, hashBlockSize);
            var blockBuffer = new byte[hashBlockSize];
            var digestBuffer = new byte[Sha256Size];
            for (var level = 1; level < levelOffsets.Length; level++)
            {
                var numDigests = checked((int)(srcSize / hashBlockSize));
                var dstOffset = (long)levelOffsets[level];
                for (var i = 0; i < numDigests; i++)
                {
                    store.Read(srcOffset + (long)i * hashBlockSize, blockBuffer);
                    HashBlockTo(effectiveSalt, blockBuffer, digestBuffer);
                    store.Write(dstOffset + (long)i * Sha256Size, digestBuffer);
                }

                srcOffset = dstOffset;
                srcSize = RoundUp((long)numDigests * Sha256Size, hashBlockSize);
            }

            var rootBlock = new byte[srcSize];
            store.Read(srcOffset, rootBlock);
            var root = HashBlock(effectiveSalt, rootBlock);
            var result = new AvbHashtreeResult(root, store, treeSize, imageSize);
            store = null;
            return result;
        }
        finally
        {
            store?.Dispose();
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
    /// Streams the image in a single pass, computing each data block's digest into the level-0 region of the store.
    /// <para>单遍流式读取镜像，逐数据块计算摘要并写入后备存储的第 0 层区域。</para>
    /// </summary>
    private static void StreamLevel0IntoStore(Stream stream, byte[] salt, int dataBlockSize, AvbHashtreeResult.TreeStore store, int level0Offset, ref int digestIndex)
    {
        var buffer = new byte[ReadBufferSize];
        var block = new byte[dataBlockSize];
        var digest = new byte[Sha256Size];
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
                    HashBlockTo(salt, block, digest);
                    store.Write(level0Offset + (long)digestIndex * Sha256Size, digest);
                    digestIndex = checked(digestIndex + 1);
                    blockFill = 0;
                }
            }
        }

        if (blockFill > 0)
        {
            Array.Clear(block, blockFill, dataBlockSize - blockFill);
            HashBlockTo(salt, block, digest);
            store.Write(level0Offset + (long)digestIndex * Sha256Size, digest);
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
        var digest = new byte[Sha256Size];
        HashBlockTo(salt, data, digest);
        return digest;
    }

    private static void HashBlockTo(byte[] salt, ReadOnlySpan<byte> data, Span<byte> destination)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(salt);
        hasher.AppendData(data);
        if (hasher.TryGetHashAndReset(destination, out _)) return;
        throw new CryptographicException("Failed to write the SHA256 digest. 写入 SHA256 摘要失败。");
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