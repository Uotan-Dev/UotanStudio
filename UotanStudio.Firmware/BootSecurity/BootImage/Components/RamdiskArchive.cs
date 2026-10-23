using UotanStudio.Compress;
using UotanStudio.Compress.Detection;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;

namespace UotanStudio.Firmware.BootSecurity.BootImage.Components;

/// <summary>
/// Turns a raw boot ramdisk component into the CPIO/TAR archive inside it, trying the header-declared
/// container first and then the remaining supported containers until one decodes to a recognized archive.
/// <para>把原始启动镜像 ramdisk 组件转换为其中包含的 CPIO/TAR 归档：先尝试头部声明的容器，再按受支持容器
/// 逐个尝试，直到某次解码得到可识别的归档为止。</para>
/// </summary>
/// <remarks>
/// A raw ramdisk that already starts with an archive header is returned unchanged, and a component that is
/// neither an archive nor decodable to one is reported as a miss instead of being mislabeled as CPIO.
/// <para>本身以归档头起始的原始 ramdisk 原样返回；既不是归档也无法解码成归档的组件按失败上报，而不会被
/// 误标为 CPIO。</para>
/// </remarks>
public static class RamdiskArchive
{
    private static readonly CompressionFormat[] SupportedContainers =
    [
        CompressionFormat.Gzip,
        CompressionFormat.Lz4Legacy,
        CompressionFormat.Lz4,
        CompressionFormat.Zstd,
        CompressionFormat.Xz,
        CompressionFormat.Bzip2,
        CompressionFormat.Brotli,
        CompressionFormat.Lzma,
        CompressionFormat.Lzop,
    ];

    /// <summary>
    /// Materializes the archive stored in a raw ramdisk component.
    /// <para>物化原始 ramdisk 组件中存储的归档。</para>
    /// </summary>
    /// <param name="ramdisk">Raw ramdisk bytes as read from the boot image; an empty array always reports a miss.
    /// <para>从启动镜像读出的原始 ramdisk 字节；空数组始终上报未命中。</para></param>
    /// <param name="declared">Container recorded by the image header; used as the first decoding attempt and ignored when it cannot decode.
    /// <para>镜像头部记录的容器；作为首次解码尝试，无法解码时忽略。</para></param>
    /// <param name="archive">The archive bytes on success, otherwise an empty array. The input array is returned by reference when it is already an archive.
    /// <para>成功时为归档字节，否则为空数组；输入本身是归档时按引用返回该数组。</para></param>
    /// <returns><see langword="true"/> when the ramdisk already is an archive or one of the decoding attempts produced an archive.
    /// <para>ramdisk 本身是归档，或某次解码尝试产出归档时返回 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="ramdisk"/> is <see langword="null"/>.
    /// <para><paramref name="ramdisk"/> 为 <see langword="null"/>。</para></exception>
    public static bool TryMaterialize(byte[] ramdisk, CompressionType declared, out byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(ramdisk);
        archive = [];
        if (ramdisk.Length == 0)
        {
            return false;
        }

        if (IsArchive(ramdisk))
        {
            archive = ramdisk;
            return true;
        }

        var candidates = new List<CompressionFormat>(SupportedContainers.Length + 3);
        AddCandidate(MapContainer(declared));
        AddCandidate(MapContainer(BootCompressor.Detect(ramdisk)));
        AddCandidate(CompressionDetector.Detect(ramdisk));
        foreach (var container in SupportedContainers)
        {
            AddCandidate(container);
        }

        foreach (var container in candidates)
        {
            if (TryDecompressToArchive(ramdisk, container, out archive))
            {
                return true;
            }
        }

        return false;

        void AddCandidate(CompressionFormat container)
        {
            if (container != CompressionFormat.None && !candidates.Contains(container))
            {
                candidates.Add(container);
            }
        }
    }

    private static CompressionFormat MapContainer(CompressionType type) => type switch
    {
        CompressionType.Gzip or CompressionType.Zopfli => CompressionFormat.Gzip,
        CompressionType.Lz4 => CompressionFormat.Lz4,
        CompressionType.Lz4Legacy or CompressionType.Lz4Lg => CompressionFormat.Lz4Legacy,
        CompressionType.Lzma => CompressionFormat.Lzma,
        CompressionType.Xz => CompressionFormat.Xz,
        CompressionType.Bzip2 => CompressionFormat.Bzip2,
        CompressionType.Lzop => CompressionFormat.Lzop,
        CompressionType.Zstd => CompressionFormat.Zstd,
        CompressionType.Brotli => CompressionFormat.Brotli,
        _ => CompressionFormat.None,
    };

    private static bool TryDecompressToArchive(byte[] ramdisk, CompressionFormat container, out byte[] archive)
    {
        archive = [];
        try
        {
            var decoded = CompressionService.Decompress(ramdisk, container);
            if (decoded.Length == 0 || !IsArchive(decoded))
            {
                return false;
            }

            archive = decoded;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool IsArchive(byte[] data)
    {
        if (data.Length < 6)
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            _ = new CpioArchive().DetectFormat(stream);
            return true;
        }
        catch (Exception ex) when (ex is ArchiveException or EndOfStreamException or IOException
            or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}