using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.BootSecurity.Avb;

/// <summary>
/// Reads how many bytes of an image are covered by AVB verification. A verified partition is the original
/// image followed by hash-tree data, FEC data and the footer, and care maps must stop at that boundary;
/// for a sparse image the footer sits at the end of the expanded bytes rather than the file tail.
/// <para>读取镜像中受 AVB 校验覆盖的字节数。已校验分区由原始镜像、hash tree 数据、FEC 数据与 footer 依次拼接，
/// care map 必须止于该边界；稀疏镜像的 footer 位于展开后的末尾，而不是文件尾部。</para>
/// </summary>
public static class AvbImageFootprint
{
    /// <summary>
    /// Loads the verified byte count from an image's AVB footer, transparently handling sparse containers.
    /// <para>从镜像的 AVB footer 读取受校验字节数，并透明处理稀疏容器。</para>
    /// </summary>
    /// <param name="imagePath">Raw or sparse image file. <para>原始或稀疏镜像文件。</para></param>
    /// <param name="cancellationToken">
    /// Cancels before and during the scan; a cancelled call rethrows. <para>在扫描前后取消；被取消时重新抛出。</para>
    /// </param>
    /// <returns>The footer's original image size in bytes, or <see langword="null"/> when the image carries
    /// no supported footer. <para>footer 记录的原始镜像字节数；镜像无受支持 footer 时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="FileNotFoundException"><paramref name="imagePath"/> does not exist.
    /// <para><paramref name="imagePath"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The sparse image declares more chunks than the accepted bound.
    /// <para>sparse 镜像声明的块记录数超过接受上限。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public static long? TryRead(string imagePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HeaderProbe.ProbeMagic(imagePath, sizeof(uint), 0, SparseMagic))
        {
            var footer = AvbFooter.TryReadFile(imagePath);
            return footer is null ? null : checked((long)footer.Value.OriginalImageSize);
        }

        using var stream = File.OpenRead(imagePath);
        using var sparse = SparseReader.FromStream(stream, validateCrc: false, verbose: false,
            MaximumChunks, cancellationToken, long.MaxValue);
        using var expanded = new SparseReadStream(sparse);
        var length = expanded.Length;
        if (length < AvbFooter.Size)
        {
            return null;
        }

        expanded.Seek(length - AvbFooter.Size, SeekOrigin.Begin);
        Span<byte> tail = stackalloc byte[AvbFooter.Size];
        expanded.ReadExactly(tail);
        var sparseFooter = AvbFooter.TryLoad(tail);
        return sparseFooter is null ? null : checked((long)sparseFooter.Value.OriginalImageSize);
    }

    private const uint SparseMagic = 0xED26FF3A;
    private const int MaximumChunks = 65_536;
}