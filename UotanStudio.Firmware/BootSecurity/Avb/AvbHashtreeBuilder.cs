using System.Security.Cryptography;
using UotanStudio.Firmware.Core.IO;

namespace UotanStudio.Firmware.BootSecurity.Avb;

/// <summary>
/// Result of building a dm-verity hash tree: exposes the root digest, tree size and image size, and lets
/// the tree spill to a temporary backing store so large images can be streamed out rather than held fully in memory.
/// <para>dm-verity 哈希树构建结果：暴露根摘要、树大小与镜像大小，并允许在大镜像时把树写入临时后备存储后流式拷出。</para>
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

    /// <summary>The Merkle root digest. <para>Merkle 根摘要。</para></summary>
    /// <value>
    /// 32 SHA-256 bytes of the salt-prefixed top-level hash block; for an empty image it is the digest of the salt
    /// with no data. The array is shared with the builder and must not be modified.
    /// <para>顶层哈希块经盐值前缀后的 32 字节 SHA-256 摘要；空镜像时为仅含盐值、不含数据的摘要。
    /// 该数组由构建过程共享，不得修改。</para>
    /// </value>
    public byte[] RootDigest { get; }

    /// <summary>Total tree byte size. <para>树字节总大小。</para></summary>
    /// <value>
    /// Bytes of hash-tree data that follow the image, or 0 when the image needs no tree because it is empty or fits
    /// in a single data block.
    /// <para>镜像之后哈希树数据的字节数；当镜像为空或仅占一个数据块而不需要树时为 0。</para>
    /// </value>
    public long TreeSize { get; }

    /// <summary>The block-aligned image size. <para>对齐后的镜像大小。</para></summary>
    /// <value>
    /// Image length rounded up to a whole <c>dataBlockSize</c>; this is where the tree starts in the partition.
    /// <para>镜像长度向上对齐到完整 <c>dataBlockSize</c> 后的值；哈希树在分区中即从此偏移开始。</para>
    /// </value>
    public long ImageSize { get; }

    /// <summary>
    /// <para>Full tree bytes. Small trees are held inline; large trees are read back from the temp backing store, intended for tests or small inputs — image writers should use <see cref="WriteTreeToAsync"/> instead.</para>
    /// <para>完整树字节。小树内联保存；大树从临时后备存储读回，仅供测试或小规模使用，生产写镜像应改用 <see cref="WriteTreeToAsync"/>。</para>
    /// </summary>
    /// <value>
    /// The whole tree, or an empty array when <see cref="TreeSize"/> is 0. For an inline tree the builder's own
    /// array is returned without copying, so the caller must not modify it; a spilled tree is read back into a
    /// freshly allocated array of the full tree size.
    /// <para>完整树字节；当 <see cref="TreeSize"/> 为 0 时返回空数组。内联树直接返回构建过程持有的数组而不复制，
    /// 调用方不得修改；落盘的树会被读回到一个按树总大小新分配的数组中。</para>
    /// </value>
    public byte[] TreeBytes => _store?.ToArray() ?? [];

    /// <summary>
    /// <para>Streams the tree bytes to the target without materializing a large tree in memory.</para>
    /// <para>把树字节流式写入目标流；大树不整体进入内存。</para>
    /// </summary>
    /// <param name="output">
    /// Destination the tree bytes are appended to; the stream is neither flushed nor closed here.
    /// <para>接收树字节并追加写入的目标流；此处不会刷新或关闭该流。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the copy, which can leave <paramref name="output"/> partially written.
    /// <para>取消拷贝操作，取消后 <paramref name="output"/> 可能已被部分写入。</para>
    /// </param>
    /// <returns>
    /// A task that completes once every tree byte has been written; it completes immediately when the image needs
    /// no tree.
    /// <para>在每个树字节写入后完成的 Task；镜像无需哈希树时立即完成。</para>
    /// </returns>
    /// <exception cref="IOException">The temporary tree file cannot be read. <para>无法读取临时树文件。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 已取消。</para></exception>
    public Task WriteTreeToAsync(Stream output, CancellationToken cancellationToken = default)
        => _store is null ? Task.CompletedTask : _store.CopyToAsync(output, cancellationToken);

    /// <summary>Releases the inline or temporary backing store. <para>释放内联或临时后备存储。</para></summary>
    /// <remarks>
    /// A spilled tree lives in a delete-on-close file, so the file is removed here; calling this more than once has
    /// no additional effect.
    /// <para>落盘的树位于删除即关闭的临时文件中，因此文件在此处被删除；重复调用不会产生额外影响。</para>
    /// </remarks>
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

            var path = TempFileHelper.CreateScratchPath("uotan-avb-tree", ".bin");
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

/// <summary>
/// Builds the dm-verity SHA-256 hash tree and root digest that an AVB hashtree descriptor points at.
/// <para>构建 AVB hashtree 描述符所指向的 dm-verity SHA-256 哈希树与根摘要。</para>
/// </summary>
/// <remarks>
/// Each block is hashed as <c>SHA256(salt || block)</c>, the trailing partial data block is zero-padded, and the
/// image length used for sizing is rounded up to a whole data block.
/// <para>每个块按 <c>SHA256(salt || block)</c> 计算，末尾不完整的最后一个数据块以零补齐，用于计算大小的镜像长度会向上对齐到完整的数据块。</para>
/// </remarks>
internal static class AvbHashtreeBuilder
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
    /// <param name="imagePath">
    /// Path opened read-share for sequential reading; the file handle is closed before this call returns.
    /// <para>以可读共享方式打开并顺序读取的路径；本调用返回前会关闭该文件句柄。</para>
    /// </param>
    /// <param name="salt">Salt prepended to every hashed block; null is treated as an empty salt.
    /// <para>每个被哈希块前拼接的盐值；null 视为空盐。</para></param>
    /// <param name="dataBlockSize">Bytes per data block; a power of two from 512 to 1048576.
    /// <para>数据块字节数；必须是 512 至 1048576 的二次幂。</para></param>
    /// <param name="hashBlockSize">Bytes per tree block; a power of two from 512 to 1048576.
    /// <para>树块字节数；必须是 512 至 1048576 的二次幂。</para></param>
    /// <returns>The root digest plus tree and block-aligned image sizes, with trees above 16 MiB held in a temporary file.
    /// <para>根摘要以及树大小与对齐后的镜像大小；超过 16 MiB 的树保存在临时文件中。</para></returns>
    /// <example>
    /// <code>
    /// using AvbHashtreeResult tree = AvbHashtreeBuilder.Build(@"images\system.img", salt);
    /// Console.WriteLine(Convert.ToHexString(tree.RootDigest));
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="imagePath"/> is null. <para><paramref name="imagePath"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">A block size is outside the supported power-of-two range.
    /// <para>某个块大小不在支持的二次幂范围内。</para></exception>
    /// <exception cref="FileNotFoundException">The image file does not exist. <para>镜像文件不存在。</para></exception>
    /// <exception cref="IOException">The image file cannot be opened or read. <para>无法打开或读取镜像文件。</para></exception>
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
    /// <param name="imageData">Image bytes to hash; the array is wrapped read-only and is never modified.
    /// <para>待哈希的镜像字节；该数组以只读方式包装，不会被修改。</para></param>
    /// <param name="salt">Salt prepended to every hashed block; null is treated as an empty salt.
    /// <para>每个被哈希块前拼接的盐值；null 视为空盐。</para></param>
    /// <param name="dataBlockSize">Bytes per data block; a power of two from 512 to 1048576.
    /// <para>数据块字节数；必须是 512 至 1048576 的二次幂。</para></param>
    /// <param name="hashBlockSize">Bytes per tree block; a power of two from 512 to 1048576.
    /// <para>树块字节数；必须是 512 至 1048576 的二次幂。</para></param>
    /// <returns>The root digest plus tree and block-aligned image sizes; the caller owns the returned result and disposes it.
    /// <para>根摘要以及树大小与对齐后的镜像大小；调用方拥有返回结果并负责释放。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="imageData"/> is null. <para><paramref name="imageData"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">A block size is outside the supported power-of-two range.
    /// <para>某个块大小不在支持的二次幂范围内。</para></exception>
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
    /// <param name="stream">
    /// Readable, seekable stream whose content starting at position 0 is hashed; the stream is rewound for the
    /// build and its original position is restored afterwards, and it stays open.
    /// <para>可读且可定位的流；从位置 0 开始的内容将被哈希，构建过程会将其倒回，结束后恢复其原始位置，并保持流打开。</para>
    /// </param>
    /// <param name="salt">Salt prepended to every hashed block; null is treated as an empty salt.
    /// <para>每个被哈希块前拼接的盐值；null 视为空盐。</para></param>
    /// <param name="dataBlockSize">Bytes per data block; a power of two from 512 to 1048576.
    /// <para>数据块字节数；必须是 512 至 1048576 的二次幂。</para></param>
    /// <param name="hashBlockSize">Bytes per tree block; a power of two from 512 to 1048576.
    /// <para>树块字节数；必须是 512 至 1048576 的二次幂。</para></param>
    /// <returns>The root digest plus tree and block-aligned image sizes. An empty stream yields a zero tree size and
    /// a zero image size; a stream that fits one data block yields a zero tree size with the block's digest.
    /// <para>根摘要以及树大小与对齐后的镜像大小。空流的树大小与镜像大小均为 0；仅占一个数据块的流树大小为 0，但会给出该块的摘要。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null. <para><paramref name="stream"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read or cannot seek.
    /// <para><paramref name="stream"/> 不可读或不可定位。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">A block size is outside the supported power-of-two range.
    /// <para>某个块大小不在支持的二次幂范围内。</para></exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The SHA-256 digest could not be written.
    /// <para>无法写入 SHA-256 摘要。</para></exception>
    /// <exception cref="System.OverflowException">The image or tree size exceeds the addressable range.
    /// <para>镜像或哈希树大小超出可表示范围。</para></exception>
    public static AvbHashtreeResult Build(Stream stream, byte[]? salt, int dataBlockSize = 4096, int hashBlockSize = 4096)
        => BuildCore(stream, salt, dataBlockSize, hashBlockSize, SpillThresholdBytes);

    /// <summary>Builds a bounded hash tree with cancellation between data and tree blocks.
    /// <para>构建有界哈希树，并在数据块和树块之间响应取消。</para></summary>
    /// <param name="stream">Caller-owned readable seekable stream; its position is restored.
    /// <para>调用方持有的可读可定位流；结束后恢复位置。</para></param>
    /// <param name="salt">Salt prepended to each block. <para>每个块前附加的盐。</para></param>
    /// <param name="dataBlockSize">Data block size in bytes. <para>数据块字节大小。</para></param>
    /// <param name="hashBlockSize">Hash block size in bytes. <para>哈希块字节大小。</para></param>
    /// <param name="cancellationToken">Cancels block processing. <para>取消块处理。</para></param>
    /// <returns>The caller-owned tree and root digest. <para>调用方持有的树和根摘要。</para></returns>
    public static AvbHashtreeResult Build(Stream stream, byte[]? salt, int dataBlockSize, int hashBlockSize,
        CancellationToken cancellationToken)
        => BuildCore(stream, salt, dataBlockSize, hashBlockSize, SpillThresholdBytes, cancellationToken);

    internal static AvbHashtreeResult BuildCore(Stream stream, byte[]? salt, int dataBlockSize, int hashBlockSize, long spillThresholdBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable. 流必须可读且可定位。", nameof(stream));
        }

        ValidateBlockSize(dataBlockSize, nameof(dataBlockSize));
        ValidateBlockSize(hashBlockSize, nameof(hashBlockSize));
        cancellationToken.ThrowIfCancellationRequested();

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
                cancellationToken.ThrowIfCancellationRequested();
                return new AvbHashtreeResult(HashBlock(effectiveSalt, block), null, 0, imageSize);
            }

            store = AvbHashtreeResult.TreeStore.Create(treeSize, spillThresholdBytes, Math.Max(ReadBufferSize, hashBlockSize));
            var digestIndex = 0;
            stream.Position = 0;
            StreamLevel0IntoStore(stream, effectiveSalt, dataBlockSize, store, levelOffsets[0], ref digestIndex, cancellationToken);

            var srcOffset = (long)levelOffsets[0];
            var srcSize = RoundUp((long)digestIndex * Sha256Size, hashBlockSize);
            var blockBuffer = new byte[hashBlockSize];
            var digestBuffer = new byte[Sha256Size];
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var level = 1; level < levelOffsets.Length; level++)
            {
                var numDigests = checked((int)(srcSize / hashBlockSize));
                var dstOffset = (long)levelOffsets[level];
                for (var i = 0; i < numDigests; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    store.Read(srcOffset + (long)i * hashBlockSize, blockBuffer);
                    HashBlockTo(hasher, effectiveSalt, blockBuffer, digestBuffer);
                    store.Write(dstOffset + (long)i * Sha256Size, digestBuffer);
                }

                srcOffset = dstOffset;
                srcSize = RoundUp((long)numDigests * Sha256Size, hashBlockSize);
            }

            var rootBlock = new byte[srcSize];
            store.Read(srcOffset, rootBlock);
            cancellationToken.ThrowIfCancellationRequested();
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
        var inputBlockSize = dataBlockSize;
        while (size > inputBlockSize)
        {
            var numBlocks = checked(((size + inputBlockSize - 1) / inputBlockSize));
            var levelSize = checked((int)RoundUp(numBlocks * Sha256Size, hashBlockSize));
            levelSizes.Add(levelSize);
            treeSize = checked(treeSize + levelSize);
            size = levelSize;
            inputBlockSize = hashBlockSize;
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
    private static void StreamLevel0IntoStore(Stream stream, byte[] salt, int dataBlockSize, AvbHashtreeResult.TreeStore store, int level0Offset,
        ref int digestIndex, CancellationToken cancellationToken)
    {
        var buffer = new byte[ReadBufferSize];
        var block = new byte[dataBlockSize];
        var digest = new byte[Sha256Size];
        var blockFill = 0;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = 0;
            while (offset < read)
            {
                var toCopy = Math.Min(read - offset, dataBlockSize - blockFill);
                Array.Copy(buffer, offset, block, blockFill, toCopy);
                blockFill += toCopy;
                offset += toCopy;
                if (blockFill == dataBlockSize)
                {
                    HashBlockTo(hasher, salt, block, digest);
                    store.Write(level0Offset + (long)digestIndex * Sha256Size, digest);
                    digestIndex = checked(digestIndex + 1);
                    blockFill = 0;
                }
            }
        }

        if (blockFill > 0)
        {
            Array.Clear(block, blockFill, dataBlockSize - blockFill);
            HashBlockTo(hasher, salt, block, digest);
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
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var digest = new byte[Sha256Size];
        HashBlockTo(hasher, salt, data, digest);
        return digest;
    }

    private static void HashBlockTo(IncrementalHash hasher, byte[] salt, ReadOnlySpan<byte> data, Span<byte> destination)
    {
        hasher.AppendData(salt);
        hasher.AppendData(data);
        if (hasher.TryGetHashAndReset(destination, out _)) return;
        throw new CryptographicException("Failed to write the SHA256 digest. 写入 SHA256 摘要失败。");
    }

    private static long RoundUp(long value, int multiple) => checked(((value + multiple - 1) / multiple) * multiple);

    private static void ValidateBlockSize(int blockSize, string paramName)
    {
        if (blockSize < 512 || blockSize > 1024 * 1024 || (blockSize & (blockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(paramName, blockSize, "Block size must be a power of two from 512 bytes to 1 MiB. 块大小必须是 512 字节至 1 MiB 的二次幂。");
        }
    }
}