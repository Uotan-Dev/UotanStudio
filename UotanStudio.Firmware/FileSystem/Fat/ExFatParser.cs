using System.Buffers.Binary;
using DiscUtils.ExFat;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.FileSystem.Fat;

/// <summary>
/// Read-only view over an exFAT volume image, decoding through the managed DiscUtils exFAT implementation behind bounded-input guards: a structural boot-record check, ceilings on the sector geometry and the allocation table, and the shared entry-count and depth limits of the directory walk.
/// <para>exFAT 卷镜像的只读视图，磁盘解码委托给托管的 DiscUtils exFAT 实现，并加有不可信输入约束：结构化引导记录校验、扇区几何与文件分配表上限，以及目录遍历共用的条目数与深度上限。</para>
/// </summary>
public sealed class ExFatParser : IDisposable
{
    private const int BootRecordBytes = 512;
    private const long MaximumFatBytes = 64L * 1024 * 1024;
    private const int VolumeLengthSectorsOffset = 0x48;
    private const int FatOffsetSectorOffset = 0x50;
    private const int FatLengthSectorsOffset = 0x54;
    private const int ClusterHeapOffsetSectorOffset = 0x58;
    private const int ClusterCountOffset = 0x5C;
    private const int RootDirectoryClusterOffset = 0x60;
    private const int BytesPerSectorShiftOffset = 0x6C;
    private const int SectorsPerClusterShiftOffset = 0x6D;
    private const int NumberOfFatsOffset = 0x6E;

    private readonly FileStream? _ownedStream;
    private readonly ExFatFileSystem _filesystem;
    private readonly long _imageLength;
    private readonly Geometry _geometry;
    private readonly FatVolumeTree.Index _index = new();
    private bool _enumerated;


    /// <summary>Opens an exFAT volume image from a file path.<para>按文件路径打开 exFAT 卷镜像。</para></summary>
    /// <param name="imagePath">Path to the volume image; the file stays read-only and is opened with shared read access.
    /// <para>卷镜像路径；文件保持只读并以共享读方式打开。</para></param>
    /// <exception cref="ArgumentException"><paramref name="imagePath"/> is blank.
    /// <para><paramref name="imagePath"/> 为空白。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="imagePath"/> does not exist.
    /// <para><paramref name="imagePath"/> 不存在。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The boot record is not an exFAT volume boot record.
    /// <para>引导记录不是 exFAT 卷引导记录。</para></exception>
    public ExFatParser(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        _ownedStream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            FatVolumeTree.CopyChunkBytes, FileOptions.RandomAccess);
        _imageLength = _ownedStream.Length;
        _filesystem = Open(_ownedStream, out var boot);
        _geometry = Geometry.Read(boot);
    }

    /// <summary>Opens an exFAT volume image carried by a caller-owned stream.
    /// <para>由调用方持有的流打开 exFAT 卷镜像。</para></summary>
    /// <param name="stream">Seekable stream over the volume; ownership stays with the caller.
    /// <para>承载该卷的可定位流；所有权保留在调用方。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException"><paramref name="stream"/> is not seekable.
    /// <para><paramref name="stream"/> 不可定位。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The boot record is not an exFAT volume boot record.
    /// <para>引导记录不是 exFAT 卷引导记录。</para></exception>
    public ExFatParser(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new InvalidDataException("exFAT parsing requires a seekable stream.");
        }

        _imageLength = stream.Length;
        _filesystem = Open(stream, out var boot);
        _geometry = Geometry.Read(boot);
    }

    /// <summary>Format variant of the open volume.
    /// <para>已打开卷的格式变体。</para></summary>
    /// <value>The constant <c>"exFAT"</c>. <para>常量 <c>"exFAT"</c>。</para></value>
    public string Variant => "exFAT";

    /// <summary>Volume label recorded by the volume descriptor entry.
    /// <para>卷描述符条目记录的卷标。</para></summary>
    /// <value>
    /// The trimmed label, or an empty string when the volume stores none.
    /// <para>去除空白后的卷标；卷未存储卷标时为空字符串。</para>
    /// </value>
    public string VolumeLabel => _filesystem.VolumeLabel?.Trim() ?? string.Empty;

    /// <summary>Logical bytes per sector declared by the boot record.
    /// <para>引导记录声明的每扇区逻辑字节数。</para></summary>
    /// <value>
    /// A power-of-two sector size; exFAT volumes use 512 up to 32768 bytes.
    /// <para>2 的幂扇区大小；exFAT 卷使用 512 至 32768 字节。</para>
    /// </value>
    public int BytesPerSector => _geometry.BytesPerSector;

    /// <summary>File-system sectors in one cluster.
    /// <para>一个簇占用的文件系统扇区数。</para></summary>
    /// <value>
    /// The sectors-per-cluster value, a power of two from 1 to 2048.
    /// <para>每簇扇区数，为 1 到 2048 之间的 2 的幂。</para>
    /// </value>
    public int SectorsPerCluster => _geometry.SectorsPerCluster;

    /// <summary>Number of duplicated file allocation tables.
    /// <para>重复的文件分配表份数。</para></summary>
    /// <value>
    /// The FAT copy count; exFAT volumes normally store one and the boot record allows up to two.
    /// <para>FAT 副本数；exFAT 卷通常为 1，引导记录最多允许 2。</para>
    /// </value>
    public int FatCount => _geometry.FatCount;

    /// <summary>Addressable cluster count of the cluster heap.
    /// <para>簇堆的可寻址簇数。</para></summary>
    /// <value>
    /// The cluster count declared by the boot record.
    /// <para>引导记录声明的簇数。</para>
    /// </value>
    public long ClusterCount => _geometry.ClusterCount;

    /// <summary>Volume size in bytes as declared by the boot record.
    /// <para>引导记录声明的卷大小（字节）。</para></summary>
    /// <value>
    /// The declared volume size, which can exceed <see cref="ImageBytes"/> on vendor images that ship a
    /// truncated volume.
    /// <para>声明的卷大小；厂商提供的截断卷镜像中该值可能大于 <see cref="ImageBytes"/>。</para>
    /// </value>
    public long DeclaredVolumeBytes => _geometry.DeclaredVolumeBytes;

    /// <summary>Bytes actually available in the backing image.
    /// <para>底层镜像中实际可用的字节数。</para></summary>
    /// <value>
    /// The stream length at open time; entries whose data lies beyond this bound are not extractable.
    /// <para>打开时的流长度；数据超出该上限的条目不可解出。</para>
    /// </value>
    public long ImageBytes => _imageLength;

    /// <summary>Whether the declared volume capacity matches the image length.
    /// <para>声明容量是否与镜像长度一致。</para></summary>
    /// <value>
    /// <see langword="true"/> when the declared capacity equals the available bytes;
    /// <see langword="false"/> reports a truncated volume whose tail clusters are unreadable.
    /// <para>声明容量等于可用字节数时为 <see langword="true"/>；为 <see langword="false"/> 表示截断卷，
    /// 尾部簇不可读。</para>
    /// </value>
    public bool IsCapacityEquivalent => _geometry.DeclaredVolumeBytes == _imageLength;

    /// <summary>Determines whether a file carries an exFAT volume boot record with geometry the parser accepts.
    /// <para>判断文件是否携带本解析器可接受的 exFAT 卷引导记录。</para></summary>
    /// <param name="filePath">Candidate image path; blank, missing, short or unreadable paths yield <see langword="false"/>.
    /// <para>候选镜像路径；空白、缺失、过短或不可读时返回 <see langword="false"/>。</para></param>
    /// <returns><see langword="true"/> only when the <c>"EXFAT "</c> file-system name, the 0x55AA boot signature, every declared geometry field and the library's own detection agree; any read failure is reported as a non-match.
    /// <para>仅当 <c>"EXFAT "</c> 文件系统名、0x55AA 引导签名、全部声明几何字段以及库自身的识别一致时为 <see langword="true"/>；读取失败按不匹配处理。</para></returns>
    public static bool IsExFatImage(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            return IsExFatStream(stream);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Determines whether a stream starts with an acceptable exFAT volume boot record.
    /// <para>判断流是否以可接受的 exFAT 卷引导记录开始。</para></summary>
    /// <param name="stream">Seekable stream; its position is restored before returning.
    /// <para>可定位的流；返回前恢复其位置。</para></param>
    /// <returns><see langword="true"/> when the boot record, its geometry and the library detection all pass; <see langword="false"/> for unseekable or short streams and any read failure.
    /// <para>引导记录、几何信息与库识别全部通过时为 <see langword="true"/>；不可定位、过短的流与任何读取错误 为 <see langword="false"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    public static bool IsExFatStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || stream.Length < BootRecordBytes)
        {
            return false;
        }

        var origin = stream.Position;
        try
        {
            var boot = ReadBootRecord(stream);
            return boot is not null && ValidateGeometry(boot, stream.Length) && IsDetected(stream);
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    /// <summary>Validates an exFAT volume boot record already held in a header buffer.
    /// <para>校验已保存在头部缓冲区中的 exFAT 卷引导记录。</para></summary>
    /// <param name="header">Leading bytes of the candidate image; shorter than <c>512</c> bytes yields <see langword="false"/>.
    /// <para>候选镜像的起始字节；不足 <c>512</c> 字节时返回 <see langword="false"/>。</para></param>
    /// <param name="imageLength">Total bytes of the image when known, used to require that the allocation table and the cluster heap actually start inside the file; <see langword="null"/> skips that bound, which is what a caller probing only a header prefix of a wrapped image can offer.
    /// <para>已知时传入镜像总字节数，用于要求文件分配表与簇堆确实落在文件内；传 <see langword="null"/> 表示跳过该 上限，适用于只探测到包装镜像头部前缀的调用方。</para></param>
    /// <returns><see langword="true"/> when the boot signature, the <c>"EXFAT "</c> file-system name and the declared geometry pass validation.
    /// <para>引导签名、<c>"EXFAT "</c> 文件系统名与声明几何信息均通过校验时返回 <see langword="true"/>。</para></returns>
    public static bool IsExFatBootRecord(ReadOnlySpan<byte> header, long? imageLength = null) =>
        header.Length >= BootRecordBytes && header[510] == 0x55 && header[511] == 0xAA
        && header.Slice(3, 8).SequenceEqual("EXFAT   "u8)
        && ValidateGeometry(header, imageLength);

    /// <summary>
    /// Lists every file and directory in the volume with its image-relative path.
    /// <para>列出卷内全部文件与目录及其镜像相对路径。</para>
    /// </summary>
    /// <returns>
    /// Files and directories in depth-first order using forward-slash paths without a leading separator.
    /// <para>深度优先顺序的文件与目录，路径使用正斜杠且不含前导分隔符。</para>
    /// </returns>
    /// <exception cref="FileSystemCorruptedException">The tree exceeds the entry-count or depth limits.
    /// <para>目录树超出条目数或深度上限。</para></exception>
    public IReadOnlyList<FatVolumeEntry> ListFiles()
    {
        if (_enumerated)
        {
            return _index.Entries;
        }

        FatVolumeTree.Walk(_filesystem, string.Empty, string.Empty, 0, _index);
        _enumerated = true;
        return _index.Entries;
    }

    /// <summary>Reads one file's bytes into the caller's stream.
    /// <para>把单个文件的字节读入调用方提供的流。</para></summary>
    /// <param name="path">Image-relative path as returned by <see cref="ListFiles"/>; comparison ignores case.
    /// <para><see cref="ListFiles"/> 返回的镜像相对路径；比较忽略大小写。</para></param>
    /// <param name="output">Destination stream; it is written from its current position and never closed here.
    /// <para>目标流；从其当前位置写入且此处不关闭。</para></param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.
    /// <para><paramref name="path"/> 为空白。</para></exception>
    /// <exception cref="FileNotFoundException">No file has that path.<para>该路径不存在文件。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The file's data lies beyond the image length.
    /// <para>文件数据超出镜像长度。</para></exception>
    public void ReadFile(string path, Stream output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(output);
        ListFiles();
        var (entry, libraryPath) = _index.Find(path);
        if (entry is not { Kind: FatVolumeEntryKind.File } || libraryPath is null)
        {
            throw new FileNotFoundException($"The exFAT volume has no file at '{path}'.", path);
        }

        try
        {
            FatVolumeTree.Copy(_filesystem, libraryPath, output, entry.Size);
        }
        catch (IOException ex)
        {
            throw new FileSystemCorruptedException(
                $"The exFAT file '{path}' stores clusters beyond the end of the image.", ex);
        }
    }

    /// <summary>Extracts the whole volume tree into a host directory.
    /// <para>把整棵卷目录树解包到主机目录。</para></summary>
    /// <param name="outputDirectory">Destination root; entry names are resolved through <see cref="FileExtraction"/> so no entry can escape it.
    /// <para>目标根目录；条目名经 <see cref="FileExtraction"/> 解析，任何条目都无法逃逸。</para></param>
    /// <param name="overwrite">Whether an existing destination file may be replaced.
    /// <para>是否允许替换已存在的目标文件。</para></param>
    /// <param name="maximumOutputBytes">Cumulative write budget for the whole extraction.
    /// <para>整个解包过程的累计写入预算。</para></param>
    /// <param name="byteProgress">Receives cumulative processed and total bytes; <see langword="null"/> reports nothing.
    /// <para>接收累计已处理与总字节数；<see langword="null"/> 表示不报告。</para></param>
    /// <param name="cancellationToken">Checked per entry and per copy chunk.
    /// <para>按条目和按复制块检查。</para></param>
    /// <returns>The image paths of the written files, plus the paths that could not be read because their clusters lie beyond the image end; the second list is empty on a capacity-equivalent volume.
    /// <para>已写出文件的镜像路径，以及因簇落在镜像末尾之外而无法读取的路径；容量等价的卷上第二个列表为空。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="outputDirectory"/> is blank.
    /// <para><paramref name="outputDirectory"/> 为空白。</para></exception>
    /// <exception cref="FileSystemCorruptedException">A file exceeds the output budget.
    /// <para>文件超出输出预算。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public (IReadOnlyList<string> Extracted, IReadOnlyList<string> Unreadable) ExtractToDirectory(
        string outputDirectory,
        bool overwrite,
        long maximumOutputBytes,
        IProgress<(long Processed, long Total)>? byteProgress,
        CancellationToken cancellationToken)
    {
        ListFiles();
        return FatVolumeTree.Extract(_filesystem, _index, outputDirectory, overwrite, maximumOutputBytes,
            byteProgress, isBacked: null, cancellationToken);
    }

    /// <summary>
    /// Releases the volume and the file handle owned by this instance.
    /// <para>释放该卷以及本实例持有的文件句柄。</para>
    /// </summary>
    public void Dispose()
    {
        _filesystem.Dispose();
        _ownedStream?.Dispose();
    }

    private record struct Geometry(int BytesPerSector, int SectorsPerCluster, int FatCount, long ClusterCount, long DeclaredVolumeBytes)
    {
        internal static Geometry Read(byte[] boot) => new(
            1 << boot[BytesPerSectorShiftOffset],
            1 << boot[SectorsPerClusterShiftOffset],
            boot[NumberOfFatsOffset],
            BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(ClusterCountOffset, 4)),
            checked((long)BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(VolumeLengthSectorsOffset, 4))
                * (1 << boot[BytesPerSectorShiftOffset])));
    }

    private static ExFatFileSystem Open(Stream stream, out byte[] boot)
    {
        var record = ReadBootRecord(stream);
        if (record is null || !ValidateGeometry(record, stream.Length))
        {
            throw new FileSystemCorruptedException("The image does not carry a valid exFAT volume boot record.");
        }

        stream.Position = 0;
        if (!ExFatFileSystem.Detect(stream))
        {
            throw new FileSystemCorruptedException("The image does not carry a valid exFAT volume boot record.");
        }

        stream.Position = 0;
        boot = record;
        return new ExFatFileSystem(stream);
    }


    private static byte[]? ReadBootRecord(Stream stream)
    {
        if (stream.Length < BootRecordBytes)
        {
            return null;
        }

        stream.Position = 0;
        var boot = new byte[BootRecordBytes];
        stream.ReadExactly(boot);
        if (boot[510] != 0x55 || boot[511] != 0xAA
            || !boot.AsSpan(3, 8).SequenceEqual("EXFAT   "u8))
        {
            return null;
        }

        return boot;
    }

    private static bool ValidateGeometry(ReadOnlySpan<byte> boot, long? imageLength)
    {
        var bytesPerSectorShift = boot[BytesPerSectorShiftOffset];
        var sectorsPerClusterShift = boot[SectorsPerClusterShiftOffset];
        var fatCount = boot[NumberOfFatsOffset];
        if (bytesPerSectorShift is < 9 or > 15 || sectorsPerClusterShift > 11 || fatCount is < 1 or > 2)
        {
            return false;
        }

        var bytesPerSector = 1 << bytesPerSectorShift;
        var volumeLengthSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(VolumeLengthSectorsOffset, 4));
        var fatOffsetSector = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(FatOffsetSectorOffset, 4));
        var fatLengthSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(FatLengthSectorsOffset, 4));
        var clusterHeapOffsetSector = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(ClusterHeapOffsetSectorOffset, 4));
        var clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(ClusterCountOffset, 4));
        var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(RootDirectoryClusterOffset, 4));
        if (volumeLengthSectors == 0 || fatOffsetSector == 0 || fatLengthSectors == 0
            || clusterHeapOffsetSector == 0 || clusterCount == 0 || rootCluster < 2
            || rootCluster - 2L >= clusterCount)
        {
            return false;
        }

        if ((long)fatLengthSectors * bytesPerSector > MaximumFatBytes)
        {
            return false;
        }

        var fatStart = (long)fatOffsetSector * bytesPerSector;
        var clusterHeapStart = (long)clusterHeapOffsetSector * bytesPerSector;
        return (imageLength is null || fatStart < imageLength.Value)
            && (imageLength is null || clusterHeapStart < imageLength.Value);
    }

    private static bool IsDetected(Stream stream)
    {
        var origin = stream.Position;
        try
        {
            stream.Position = 0;
            return ExFatFileSystem.Detect(stream);
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = origin;
        }
    }

}