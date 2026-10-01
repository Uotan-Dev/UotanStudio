using System.Buffers.Binary;
using System.Text;
using DiscUtils;
using DiscUtils.Fat;
using DiscUtils.Streams;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.FileSystem.Fat;

/// <summary>
/// Read-only view over a FAT12/FAT16/FAT32 volume image. The on-disk decoding is delegated to the
/// managed DiscUtils FAT implementation; this type adds the bounded-input guards the firmware pipeline
/// requires: a structural boot-record check before the library is engaged, a ceiling on the in-memory
/// allocation table, entry-count and depth limits for the directory walk, and per-file extent validation
/// against the real image length so a volume whose BIOS parameter block declares more sectors than the
/// file holds still yields every byte-range it genuinely covers.
/// <para>FAT12/FAT16/FAT32 卷镜像的只读视图。磁盘解码交给托管的 DiscUtils FAT 实现，本类型补上固件流水
/// 线要求的不可信输入约束：调用库之前先做结构化引导记录校验、限制内存中的文件分配表大小、为目录遍历设置
/// 条目数与深度上限，并按镜像真实长度逐文件校验区间，使 BIOS 参数块声明扇区数多于实际文件长度的卷仍能解
/// 出它确实覆盖的全部字节。</para>
/// </summary>
public sealed class FatParser : IDisposable
{
    private const int BootRecordBytes = 512;
    private const long MaximumFatBytes = 64L * 1024 * 1024;

    private readonly FileStream? _ownedStream;
    private readonly FatFileSystem _filesystem;
    private readonly long _imageLength;
    private readonly long _totalClusters;
    private readonly long _declaredVolumeBytes;
    private readonly FatVolumeTree.Index _index = new();
    private bool _enumerated;

    /// <summary>
    /// Opens a FAT volume image from a file path.
    /// <para>按文件路径打开 FAT 卷镜像。</para>
    /// </summary>
    /// <param name="imagePath">
    /// Path to the volume image; the file stays read-only and is opened with shared read access.
    /// <para>卷镜像路径；文件保持只读并以共享读方式打开。</para>
    /// </param>
    /// <exception cref="FileNotFoundException"><paramref name="imagePath"/> does not exist.
    /// <para><paramref name="imagePath"/> 不存在。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The boot record is not a FAT volume boot record.
    /// <para>引导记录不是 FAT 卷引导记录。</para></exception>
    public FatParser(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        _ownedStream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            FatVolumeTree.CopyChunkBytes, FileOptions.RandomAccess);
        _imageLength = _ownedStream.Length;
        _filesystem = Open(_ownedStream, out var boot);
        _totalClusters = ClusterCount(boot, _filesystem);
        _declaredVolumeBytes = ReadDeclaredVolumeBytes(boot, _filesystem);
    }

    /// <summary>
    /// Opens a FAT volume image carried by a caller-owned stream.
    /// <para>由调用方持有的流打开 FAT 卷镜像。</para>
    /// </summary>
    /// <param name="stream">
    /// Seekable stream over the volume; ownership stays with the caller.
    /// <para>承载该卷的可定位流；所有权保留在调用方。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException"><paramref name="stream"/> is not seekable.
    /// <para><paramref name="stream"/> 不可定位。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The boot record is not a FAT volume boot record.
    /// <para>引导记录不是 FAT 卷引导记录。</para></exception>
    public FatParser(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new InvalidDataException("FAT parsing requires a seekable stream.");
        }

        _imageLength = stream.Length;
        _filesystem = Open(stream, out var boot);
        _totalClusters = ClusterCount(boot, _filesystem);
        _declaredVolumeBytes = ReadDeclaredVolumeBytes(boot, _filesystem);
    }

    /// <summary>Detected FAT variant of the open volume.
    /// <para>已打开卷检测到的 FAT 变体。</para></summary>
    /// <value>
    /// <c>"FAT12"</c>, <c>"FAT16"</c> or <c>"FAT32"</c> as derived by the volume's cluster count.
    /// <para>按卷簇数推导出的 <c>"FAT12"</c>、<c>"FAT16"</c> 或 <c>"FAT32"</c>。</para>
    /// </value>
    public string Variant => _filesystem.FatVariant switch
    {
        FatType.Fat12 => "FAT12",
        FatType.Fat16 => "FAT16",
        _ => "FAT32",
    };

    /// <summary>Volume label recorded by the boot sector or the root directory entry.
    /// <para>引导扇区或根目录条目记录的卷标。</para></summary>
    /// <value>
    /// The trimmed label, or an empty string when the volume stores none. A Qualcomm firmware volume
    /// commonly stores the placeholder <c>NONAME</c>.
    /// <para>去除空白后的卷标；卷未存储卷标时为空字符串。高通固件卷常见占位卷标 <c>NONAME</c>。</para>
    /// </value>
    public string VolumeLabel => _filesystem.VolumeLabel.Trim();

    /// <summary>Logical bytes per sector declared by the BIOS parameter block.
    /// <para>BIOS 参数块声明的每扇区逻辑字节数。</para></summary>
    /// <value>
    /// A power-of-two sector size; Android firmware volumes use 512, 1024, 2048 or 4096.
    /// <para>2 的幂扇区大小；Android 固件卷使用 512、1024、2048 或 4096。</para>
    /// </value>
    public int BytesPerSector => _filesystem.SectorSize;

    /// <summary>File-system sectors in one cluster.
    /// <para>一个簇占用的文件系统扇区数。</para></summary>
    /// <value>
    /// The sectors-per-cluster value, a power of two from 1 to 128.
    /// <para>每簇扇区数，为 1 到 128 之间的 2 的幂。</para>
    /// </value>
    public int SectorsPerCluster => _filesystem.SectorsPerCluster;

    /// <summary>Number of duplicated file allocation tables.
    /// <para>重复的文件分配表份数。</para></summary>
    /// <value>
    /// The FAT copy count, normally 2 and 1 on volumes that skip mirroring.
    /// <para>FAT 副本数，通常为 2，不镜像时为 1。</para>
    /// </value>
    public int FatCount => _filesystem.FatCount;

    /// <summary>Reserved sector count ahead of the first file allocation table.
    /// <para>首个文件分配表之前的保留扇区数。</para></summary>
    /// <value>
    /// The reserved-sector count as stored in the BIOS parameter block.
    /// <para>BIOS 参数块中记录的保留扇区数。</para>
    /// </value>
    public int ReservedSectorCount => _filesystem.ReservedSectorCount;

    /// <summary>Fixed root directory entry slots on FAT12 and FAT16 volumes.
    /// <para>FAT12 与 FAT16 卷上固定根目录的条目槽位数。</para></summary>
    /// <value>
    /// The root-entry count; FAT32 stores its root directory as a cluster chain and reports 0 here.
    /// <para>根目录条目数；FAT32 以簇链保存根目录，此值为 0。</para>
    /// </value>
    public int MaxRootDirectoryEntries => _filesystem.MaxRootDirectoryEntries;

    /// <summary>Addressable cluster count of the volume.
    /// <para>卷的可寻址簇数。</para></summary>
    /// <value>
    /// The total cluster count derived from the BIOS parameter block.
    /// <para>由 BIOS 参数块推导出的簇总数。</para>
    /// </value>
    public long TotalClusters => _totalClusters;

    /// <summary>Volume size in bytes as declared by the BIOS parameter block.
    /// <para>BIOS 参数块声明的卷大小（字节）。</para></summary>
    /// <value>
    /// The declared volume size, which can exceed <see cref="ImageBytes"/> on vendor images that ship a
    /// truncated volume.
    /// <para>声明的卷大小；厂商提供的截断卷镜像中该值可能大于 <see cref="ImageBytes"/>。</para>
    /// </value>
    public long DeclaredVolumeBytes => _declaredVolumeBytes;

    /// <summary>Bytes actually available in the backing image.
    /// <para>底层镜像中实际可用的字节数。</para></summary>
    /// <value>
    /// The stream length at open time; entries whose extents exceed this bound are not extractable.
    /// <para>打开时的流长度；区间超出该上限的条目不可解出。</para>
    /// </value>
    public long ImageBytes => _imageLength;

    /// <summary>Whether the declared volume capacity matches the image length.
    /// <para>声明容量是否与镜像长度一致。</para></summary>
    /// <value>
    /// <see langword="true"/> when the BIOS parameter block capacity equals the available bytes;
    /// <see langword="false"/> reports a truncated volume whose tail clusters are unreadable.
    /// <para>BIOS 参数块容量等于可用字节数时为 <see langword="true"/>；为 <see langword="false"/> 表示截断卷，
    /// 尾部簇不可读。</para>
    /// </value>
    public bool IsCapacityEquivalent => DeclaredVolumeBytes == _imageLength;

    /// <summary>
    /// Determines whether a file carries a FAT12/FAT16/FAT32 volume boot record with geometry the parser
    /// accepts.
    /// <para>判断文件是否携带本解析器可接受的 FAT12/FAT16/FAT32 卷引导记录。</para>
    /// </summary>
    /// <param name="filePath">
    /// Candidate image path; blank, missing, short or unreadable paths yield <see langword="false"/>.
    /// <para>候选镜像路径；空白、缺失、过短或不可读时返回 <see langword="false"/>。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> only when the 0x55AA boot signature, the file-system type name at offset 54
    /// or 82, and every BIOS parameter block field pass validation; any read failure is reported as a
    /// non-match.
    /// <para>仅当 0x55AA 引导签名、偏移 54 或 82 的文件系统类型名以及全部 BIOS 参数块字段均通过校验时为
    /// <see langword="true"/>；读取失败按不匹配处理。</para>
    /// </returns>
    public static bool IsFatImage(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            return IsFatStream(stream);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Determines whether a stream starts with an acceptable FAT volume boot record.
    /// <para>判断流是否以可接受的 FAT 卷引导记录开始。</para>
    /// </summary>
    /// <param name="stream">
    /// Seekable stream; its position is restored before returning.
    /// <para>可定位的流；返回前恢复其位置。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the boot record and its geometry pass validation; <see langword="false"/>
    /// for unseekable or short streams and any read failure.
    /// <para>引导记录及其几何信息通过校验时为 <see langword="true"/>；不可定位、过短的流与任何读取错误为
    /// <see langword="false"/>。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    public static bool IsFatStream(Stream stream)
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
            return boot is not null && ValidateGeometry(boot, stream.Length);
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

    /// <summary>
    /// Validates a FAT volume boot record already held in a header buffer.
    /// <para>校验已保存在头部缓冲区中的 FAT 卷引导记录。</para>
    /// </summary>
    /// <param name="header">
    /// Leading bytes of the candidate image; shorter than <c>512</c> bytes yields <see langword="false"/>.
    /// <para>候选镜像的起始字节；不足 <c>512</c> 字节时返回 <see langword="false"/>。</para>
    /// </param>
    /// <param name="imageLength">
    /// Total bytes of the image when known, used to require that the cluster data area actually starts
    /// inside the file; <see langword="null"/> skips that bound, which is what a caller probing only a
    /// header prefix of a wrapped image can offer.
    /// <para>已知时传入镜像总字节数，用于要求簇数据区确实落在文件内；传 <see langword="null"/> 表示跳过该上限，
    /// 适用于只探测到包装镜像头部前缀的调用方。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the boot signature, the file-system type name and the BIOS parameter block
    /// geometry pass validation.
    /// <para>引导签名、文件系统类型名与 BIOS 参数块几何信息均通过校验时返回 <see langword="true"/>。</para>
    /// </returns>
    public static bool IsFatBootRecord(ReadOnlySpan<byte> header, long? imageLength = null) =>
        header.Length >= BootRecordBytes && header[510] == 0x55 && header[511] == 0xAA
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

    /// <summary>
    /// Reads one file's bytes into the caller's stream.
    /// <para>把单个文件的字节读入调用方提供的流。</para>
    /// </summary>
    /// <param name="path">
    /// Image-relative path as returned by <see cref="ListFiles"/>; comparison ignores case, matching the
    /// file system's own case-insensitive name lookup.
    /// <para><see cref="ListFiles"/> 返回的镜像相对路径；比较忽略大小写，与文件系统自身的大小写不敏感名一致。</para>
    /// </param>
    /// <param name="output">
    /// Destination stream; it is written from its current position and never closed here.
    /// <para>目标流；从其当前位置写入且此处不关闭。</para>
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.
    /// <para><paramref name="path"/> 为空白。</para></exception>
    /// <exception cref="FileNotFoundException">No file has that path.
    /// <para>该路径不存在文件。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The file's extents lie beyond the image length.
    /// <para>文件区间超出镜像长度。</para></exception>
    public void ReadFile(string path, Stream output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(output);
        ListFiles();
        var (entry, libraryPath) = _index.Find(path);
        if (entry is not { Kind: FatVolumeEntryKind.File } || libraryPath is null)
        {
            throw new FileNotFoundException($"The FAT volume has no file at '{path}'.", path);
        }

        CopyFile(libraryPath, output, entry.Size);
    }

    /// <summary>
    /// Extracts the whole volume tree into a host directory.
    /// <para>把整棵卷目录树解包到主机目录。</para>
    /// </summary>
    /// <param name="outputDirectory">
    /// Destination root; entry names are resolved through <see cref="FileExtraction"/> so no entry can
    /// escape it.
    /// <para>目标根目录；条目名经 <see cref="FileExtraction"/> 解析，任何条目都无法逃逸。</para>
    /// </param>
    /// <param name="overwrite">
    /// Whether an existing destination file may be replaced.
    /// <para>是否允许替换已存在的目标文件。</para>
    /// </param>
    /// <param name="maximumOutputBytes">
    /// Cumulative write budget for the whole extraction.
    /// <para>整个解包过程的累计写入预算。</para>
    /// </param>
    /// <param name="byteProgress">
    /// Receives cumulative processed and total bytes; <see langword="null"/> reports nothing.
    /// <para>接收累计已处理与总字节数；<see langword="null"/> 表示不报告。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked per entry and per copy chunk.
    /// <para>按条目和按复制块检查。</para>
    /// </param>
    /// <returns>
    /// The image paths of the written files, plus the paths that could not be read because their clusters lie
    /// beyond the image end; the second list is empty on a capacity-equivalent volume.
    /// <para>已写出文件的镜像路径，以及因簇落在镜像末尾之外而无法读取的路径；容量等价的卷上第二个列表为空。</para>
    /// </returns>
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
            byteProgress, entry => IsFullyBacked(_index.LibraryPath(entry.Path), entry.Size), cancellationToken);
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

    private static FatFileSystem Open(Stream stream, out byte[] boot)
    {
        var record = ReadBootRecord(stream);
        if (record is null || !ValidateGeometry(record, stream.Length))
        {
            throw new FileSystemCorruptedException("The image does not carry a valid FAT volume boot record.");
        }

        stream.Position = 0;
        boot = record;
        return new FatFileSystem(stream, Ownership.None);
    }

    private static long ReadDeclaredVolumeBytes(byte[] boot, FatFileSystem filesystem)
    {
        var bytesPerSector = (int)BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11, 2));
        var totalSectors16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(19, 2));
        var totalSectors32 = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32, 4));
        var totalSectors = totalSectors16 != 0 ? totalSectors16 : totalSectors32;
        return totalSectors == 0 ? filesystem.Size : checked((long)totalSectors * bytesPerSector);
    }

    private static long ClusterCount(byte[] boot, FatFileSystem filesystem)
    {
        var bytesPerSector = (int)BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11, 2));
        var sectorsPerCluster = (int)boot[13];
        var reservedSectors = (int)BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14, 2));
        var fatCount = (int)boot[16];
        var rootEntries = (int)BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(17, 2));
        var fat16Sectors = (int)BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(22, 2));
        var sectorsPerFat = fat16Sectors != 0 ? fat16Sectors : (int)BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(36, 4));
        var totalSectors16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(19, 2));
        var totalSectors32 = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32, 4));
        var totalSectors = totalSectors16 != 0 ? totalSectors16 : totalSectors32;
        if (totalSectors == 0 || sectorsPerCluster == 0)
        {
            return filesystem.TotalClusters;
        }

        var rootSectors = rootEntries == 0 ? 0 : checked(rootEntries * 32 / bytesPerSector);
        var dataSectors = (long)totalSectors - reservedSectors - (long)fatCount * sectorsPerFat - rootSectors;
        return dataSectors <= 0 ? filesystem.TotalClusters : dataSectors / sectorsPerCluster;
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
        return boot[510] != 0x55 || boot[511] != 0xAA ? null : boot;
    }

    private static bool ValidateGeometry(ReadOnlySpan<byte> boot, long? imageLength)
    {
        var bytesPerSector = (int)BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(11, 2));
        var sectorsPerCluster = (int)boot[13];
        var reservedSectors = (int)BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(14, 2));
        var fatCount = boot[16];
        var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(17, 2));
        var fat16Sectors = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(22, 2));
        var fat32Sectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(36, 4));
        if (!IsPowerOfTwo(bytesPerSector) || bytesPerSector is < 128 or > 32_768
            || !IsPowerOfTwo(sectorsPerCluster) || sectorsPerCluster is < 1 or > 128
            || fatCount is < 1 or > 2
            || reservedSectors is 0 or >= 65_536
            || !HasFatFamilyTypeName(boot, rootEntries, fat16Sectors))
        {
            return false;
        }

        var sectorsPerFat = fat16Sectors != 0 ? (int)fat16Sectors : checked((int)fat32Sectors);
        if (sectorsPerFat <= 0 || (long)sectorsPerFat * bytesPerSector > MaximumFatBytes)
        {
            return false;
        }

        var fatStart = (long)reservedSectors * bytesPerSector;
        var rootOffset = checked(fatStart + (long)fatCount * sectorsPerFat * bytesPerSector);
        var rootBytes = rootEntries == 0 ? 0L : checked((long)rootEntries * 32L);
        var dataStartOffset = checked(rootOffset + rootBytes);
        return imageLength is null || dataStartOffset < imageLength.Value;
    }

    private static bool HasFatFamilyTypeName(ReadOnlySpan<byte> boot, int rootEntries, int fat16Sectors)
    {
        var isFat32 = rootEntries == 0 && fat16Sectors == 0;
        var name = isFat32
            ? Encoding.ASCII.GetString(boot.Slice(82, 8)).Trim()
            : Encoding.ASCII.GetString(boot.Slice(54, 8)).Trim();
        return name is "FAT12" or "FAT16" or "FAT32";
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private bool IsFullyBacked(string libraryPath, long size)
    {
        if (size == 0)
        {
            return true;
        }

        return _filesystem.PathToExtents(libraryPath).All(extent =>
            extent.Start >= 0 && extent.Start <= _imageLength - extent.Length);
    }

    private void CopyFile(string libraryPath, Stream output, long size)
    {
        if (!IsFullyBacked(libraryPath, size))
        {
            throw new FileSystemCorruptedException(
                $"The FAT file '{libraryPath}' stores clusters beyond the end of the image.");
        }

        FatVolumeTree.Copy(_filesystem, libraryPath, output, size);
    }
}