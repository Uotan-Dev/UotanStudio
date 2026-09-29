using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BlockImage.Sparse.Models;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.OtaDelta.CareMap;

/// <summary>
/// Derives care-map records from built partition images. A verified image is the original filesystem
/// image followed by hash-tree data, FEC data and an AVB footer; the verifier reads back only the
/// original blocks, so the record is the image's own populated blocks clipped to the verified length the
/// caller read from the image's AVB footer.
/// <para>从已构建的分区镜像推导 care map 记录。已校验镜像由原始文件系统镜像、hash tree 数据、FEC 数据与
/// AVB footer 依次拼接而成；校验方只读回原始块，因此记录取镜像自身已填充的块，并按调用方从 footer 读到的受校验长度裁剪。</para>
/// </summary>
public static class CareMapBuilder
{
    /// <summary>
    /// Ceiling on sparse chunk records accepted while scanning an image.
    /// <para>扫描镜像时接受的 sparse 块记录数量上限。</para>
    /// </summary>
    /// <value>65,536 chunks. <para>65,536 个块记录。</para></value>
    public const int MaximumChunks = 65_536;

    private static readonly string[] SupportedPartitionNames =
    [
        "system",
        "vendor",
        "product",
        "system_ext",
        "odm",
        "vendor_dlkm",
        "odm_dlkm",
        "system_dlkm"
    ];

    /// <summary>Gets the partition names a care map may carry, in the order upstream emits them.
    /// <para>获取可以承载 care map 的分区名，顺序与上游输出一致。</para></summary>
    /// <value>Eight verity-capable partitions; other partitions never appear in a care map.
    /// <para>8 个可启用 verity 的分区；其余分区永不出现在 care map 中。</para></value>
    public static IReadOnlyList<string> SupportedPartitions => SupportedPartitionNames;

    /// <summary>
    /// Determines whether a partition name is eligible for a care-map record.
    /// <para>判断分区名是否有资格承载 care map 记录。</para>
    /// </summary>
    /// <param name="partitionName">Candidate name compared ordinally. <para>按序数比较的候选名。</para></param>
    /// <returns><see langword="true"/> for the eight supported names. <para>八个受支持名称时为 <see langword="true"/>。</para></returns>
    public static bool IsSupportedPartition(string? partitionName) =>
        partitionName is not null && Array.IndexOf(SupportedPartitionNames, partitionName) >= 0;

    /// <summary>
    /// Builds the care-map record for one partition image.
    /// <para>为单个分区镜像构建 care map 记录。</para>
    /// </summary>
    /// <param name="partitionName">
    /// Partition the image belongs to; only <see cref="SupportedPartitions"/> produce a record.
    /// <para>镜像所属分区；只有 <see cref="SupportedPartitions"/> 中的分区才会产生记录。</para>
    /// </param>
    /// <param name="imagePath">
    /// Raw or sparse image file. <para>原始或 sparse 镜像文件。</para>
    /// </param>
    /// <param name="verifiedImageBytes">
    /// Byte count covered by verification, read from the image's AVB footer by the caller.
    /// <para>受校验覆盖的字节数，由调用方从镜像的 AVB footer 读取后传入。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels before and during the scan; a cancelled call rethrows.
    /// <para>在扫描前后取消；被取消时重新抛出。</para>
    /// </param>
    /// <returns>The record, or <see langword="null"/> when the partition is not eligible.
    /// <para>记录；当分区不受支持时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="FileNotFoundException"><paramref name="imagePath"/> does not exist.
    /// <para><paramref name="imagePath"/> 不存在。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="verifiedImageBytes"/> is not positive.
    /// <para><paramref name="verifiedImageBytes"/> 不是正数。</para></exception>
    /// <exception cref="InvalidDataException">The sparse image uses a block size other than
    /// <see cref="CareMap.BlockSize"/>, the verified length is not smaller than the image on disk, or
    /// clipping leaves no block to verify.
    /// <para>sparse 镜像使用了非 <see cref="CareMap.BlockSize"/> 的块大小；受校验长度不小于磁盘上的镜像长度；
    /// 或裁剪后没有任何待校验块。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public static CareMapPartitionInfo? TryBuildFromImage(string partitionName, string imagePath,
        long verifiedImageBytes, CancellationToken cancellationToken = default)
    {
        if (!IsSupportedPartition(partitionName))
        {
            return null;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(verifiedImageBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var sparse = HeaderProbe.ProbeMagic(imagePath, sizeof(uint), 0, SparseMagic);
        var (present, onDiskLength) = sparse
            ? ReadSparsePresentBlocks(imagePath, cancellationToken)
            : (RawRanges(new FileInfo(imagePath).Length), new FileInfo(imagePath).Length);
        if (verifiedImageBytes >= onDiskLength)
        {
            throw new InvalidDataException(
                $"Partition '{partitionName}' reports {verifiedImageBytes} verified bytes, which is not smaller than the {onDiskLength} byte image.");
        }

        var verifiedBlocks = verifiedImageBytes / CareMap.BlockSize;
        if (verifiedBlocks < 1)
        {
            throw new InvalidDataException($"Partition '{partitionName}' leaves no full {CareMap.BlockSize} byte block to verify.");
        }

        var clipped = CareMapRanges.Intersect(present, [new CareMapRange(0, verifiedBlocks)]);
        if (clipped.Count == 0)
        {
            throw new InvalidDataException($"Partition '{partitionName}' has no populated block inside its verified image size.");
        }

        return new CareMapPartitionInfo { Name = partitionName, Ranges = CareMapRanges.Format(clipped) };
    }

    private const uint SparseMagic = 0xED26FF3A;

    private static (IReadOnlyList<CareMapRange> Present, long Length) ReadSparsePresentBlocks(string imagePath,
        CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(imagePath);
        using var sparse = SparseReader.FromStream(stream, validateCrc: false, verbose: false,
            MaximumChunks, cancellationToken, long.MaxValue);
        if (sparse.Header.BlockSize != CareMap.BlockSize)
        {
            throw new InvalidDataException($"Sparse image uses a {sparse.Header.BlockSize} byte block size; care maps count {CareMap.BlockSize} byte blocks.");
        }

        var ranges = new List<CareMapRange>();
        foreach (var chunk in sparse.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Header.ChunkType is (ushort)ChunkType.DontCare or (ushort)ChunkType.Crc32
                || chunk.Header.ChunkSize == 0)
            {
                continue;
            }

            ranges.Add(new CareMapRange(chunk.StartBlock, checked((long)chunk.StartBlock + chunk.Header.ChunkSize)));
        }

        return (ranges, checked((long)sparse.Header.TotalBlocks * CareMap.BlockSize));
    }

    private static IReadOnlyList<CareMapRange> RawRanges(long length)
    {
        var blocks = length / CareMap.BlockSize;
        return blocks > 0 ? [new CareMapRange(0, blocks)] : [];
    }
}