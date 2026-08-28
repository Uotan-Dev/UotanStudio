using System.Buffers.Binary;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// erofs 文件清单条目（路径 + 大小 + nid）。
/// <para>An erofs file-list entry (path + size + nid).</para>
/// </summary>
public sealed record ErofsFileEntry(string Path, long Size, ulong Nid);

/// <summary>
/// 全托管 erofs 镜像解析器：超级块 → 根 inode（s_root_nid）→ 目录树遍历 → 文件清单；
/// 支持整镜像解包与单文件读取（uncompressed 直读，compressed 逐 pcluster 经 LZ4 解压）。
/// <para>A fully managed erofs image parser: superblock → root inode (s_root_nid) → directory tree
/// traversal → file listing; supports whole-image extraction and single-file reads (plain data is
/// read directly, compressed data is decompressed pcluster by pcluster via LZ4).</para>
/// </summary>
public sealed class ErofsParser
{
    private const int CopyBufferSize = 1 << 20;

    private readonly byte[] _image;
    private readonly ErofsSuperblock _superblock;
    private readonly int _blockSize;
    private List<ErofsFileEntry>? _fileTable;

    /// <summary>
    /// 从镜像字节创建解析器（校验偏移 1024 处的魔数）。
    /// <para>Creates a parser from image bytes (validates the magic at offset 1024).</para>
    /// </summary>
    public ErofsParser(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _image = image;
        _superblock = ErofsSuperblock.FromImage(image);
        _blockSize = _superblock.BlockSize;
    }

    /// <summary>
    /// 从流创建解析器（读取全部字节）。
    /// <para>Creates a parser from a stream (reads all bytes).</para>
    /// </summary>
    public ErofsParser(Stream stream)
        : this(ReadAllBytes(stream))
    {
    }

    /// <summary>
    /// 判断镜像字节是否包含 erofs 魔数。
    /// <para>Checks whether the image bytes hold the erofs magic.</para>
    /// </summary>
    public static bool CanHandle(byte[] image) => image is not null && ErofsSuperblock.HasMagic(image);

    /// <summary>
    /// 块大小（字节）。
    /// <para>Block size in bytes.</para>
    /// </summary>
    public int BlockSize => _blockSize;

    /// <summary>
    /// 文件系统总块数（s_blocks）。
    /// <para>Total filesystem block count (s_blocks).</para>
    /// </summary>
    public long BlockCount => _superblock.Blocks;

    /// <summary>
    /// 镜像总字节数（分区大小）。
    /// <para>Total image size in bytes (partition size).</para>
    /// </summary>
    public long ImageSize => _image.LongLength;

    /// <summary>
    /// inode 总数（s_inos）。
    /// <para>Total inode count (s_inos).</para>
    /// </summary>
    public long InodeCount => (long)_superblock.Inos;

    /// <summary>
    /// 根目录 nid（s_root_nid）。
    /// <para>Root directory nid (s_root_nid).</para>
    /// </summary>
    public ulong RootNid => _superblock.RootNid;

    /// <summary>
    /// 卷名（s_volume_name）。
    /// <para>Volume name (s_volume_name).</para>
    /// </summary>
    public string VolumeName => _superblock.VolumeName;

    /// <summary>
    /// 遍历目录树并返回文件清单（路径使用 '/' 分隔）。
    /// <para>Walks the directory tree and returns the file listing (paths use '/').</para>
    /// </summary>
    public IReadOnlyList<ErofsFileEntry> EnumerateFiles()
    {
        _fileTable ??= EnumerateFilesCore();
        return _fileTable;
    }

    /// <summary>
    /// 读取单个文件内容并返回字节数组。
    /// <para>Reads a single file and returns its bytes.</para>
    /// </summary>
    public byte[] ReadFileBytes(string path)
    {
        using var buffer = new MemoryStream();
        ReadFile(path, buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// 读取单个文件内容到输出流（uncompressed 直读，compressed 逐 pcluster 解压）。
    /// <para>Reads a single file into the output stream (plain data read directly, compressed data
    /// decompressed pcluster by pcluster).</para>
    /// </summary>
    public void ReadFile(string path, Stream outputStream)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(outputStream);

        var nid = FindFile(path);
        var inode = ReadInode(nid);
        if (!inode.IsRegularFile)
        {
            throw new InvalidDataException($"'{path}' is not a regular file.");
        }

        WriteFileData(nid, inode, outputStream);
    }

    /// <summary>
    /// 将镜像中全部文件解包到输出目录。
    /// <para>Extracts all files to the output directory.</para>
    /// </summary>
    public void ExtractToDirectory(string outputDirectory, bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        foreach (var entry in EnumerateFiles())
        {
            var target = Path.Combine(outputDirectory, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (!overwrite && File.Exists(target))
            {
                continue;
            }

            using var output = File.Create(target);
            ReadFile(entry.Path, output);
        }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private List<ErofsFileEntry> EnumerateFilesCore()
    {
        var result = new List<ErofsFileEntry>();
        var visitedDirectories = new HashSet<ulong>();
        WalkDirectory(_superblock.RootNid, string.Empty, visitedDirectories, result);
        return result;
    }

    private void WalkDirectory(ulong nid, string prefix, HashSet<ulong> visitedDirectories, List<ErofsFileEntry> result)
    {
        if (!visitedDirectories.Add(nid))
        {
            throw new InvalidDataException($"Directory cycle detected at nid {nid}.");
        }

        var inode = ReadInode(nid);
        if (!inode.IsDirectory)
        {
            return;
        }

        foreach (var entry in ReadDirectory(inode))
        {
            if (entry.FileType == ErofsInode.FileTypeDirectory)
            {
                var directoryPath = JoinPath(prefix, entry.Name);
                WalkDirectory(entry.Nid, directoryPath, visitedDirectories, result);
                continue;
            }

            if (entry.FileType == ErofsInode.FileTypeRegularFile)
            {
                var fileInode = ReadInode(entry.Nid);
                var path = JoinPath(prefix, entry.Name);
                result.Add(new ErofsFileEntry(path, (long)fileInode.Size, entry.Nid));
            }
        }
    }

    private static string JoinPath(string prefix, string name)
        => prefix.Length == 0 ? name : prefix + "/" + name;

    private ulong FindFile(string path)
    {
        foreach (var entry in EnumerateFiles())
        {
            if (string.Equals(entry.Path, path, StringComparison.Ordinal))
            {
                return entry.Nid;
            }
        }

        throw new FileNotFoundException($"File not found in erofs image: '{path}'.", path);
    }

    private ErofsInode ReadInode(ulong nid)
    {
        var offset = _superblock.GetInodeOffset(nid);
        if (offset < 0 || offset > int.MaxValue - ErofsInode.ExtendedSize)
        {
            throw new InvalidDataException($"Inode nid {nid} is out of range.");
        }

        var intOffset = (int)offset;
        if (intOffset + 2 > _image.Length)
        {
            throw new InvalidDataException($"Inode nid {nid} exceeds the image bounds.");
        }

        var advise = BinaryPrimitives.ReadUInt16LittleEndian(_image.AsSpan(intOffset, 2));
        var isExtended = (advise & ErofsInode.FlagExtended) != 0;
        var inodeSize = isExtended ? ErofsInode.ExtendedSize : ErofsInode.CompactSize;
        if (intOffset + inodeSize > _image.Length)
        {
            throw new InvalidDataException($"Inode nid {nid} exceeds the image bounds.");
        }

        return ErofsInode.FromBytes(_image.AsSpan(intOffset, inodeSize));
    }

    private List<ErofsDirectoryEntry> ReadDirectory(ErofsInode inode)
    {
        if (inode.Size > int.MaxValue)
        {
            throw new InvalidDataException($"Directory size {inode.Size} is too large.");
        }

        var start = (long)inode.RawBlockAddress * _blockSize;
        var length = (int)inode.Size;
        if (length < 0 || start < 0 || start > _image.LongLength - length)
        {
            throw new InvalidDataException("Directory data exceeds the image bounds.");
        }

        return ErofsDirectoryEntry.ParseDirectory(_image.AsSpan((int)start, length), _blockSize);
    }

    private void WriteFileData(ulong nid, ErofsInode inode, Stream output)
    {
        if (inode.IsCompressed)
        {
            WriteCompressedData(nid, inode, output);
            return;
        }

        WriteFlatData(inode, output);
    }

    private void WriteFlatData(ErofsInode inode, Stream output)
    {
        var length = (long)inode.Size;
        if (length == 0)
        {
            return;
        }

        var start = (long)inode.RawBlockAddress * _blockSize;
        if (start < 0 || start > _image.LongLength - length)
        {
            throw new InvalidDataException("File data exceeds the image bounds.");
        }

        WriteBytes(output, _image, start, length);
    }

    private void WriteCompressedData(ulong nid, ErofsInode inode, Stream output)
    {
        var clusterSize = _blockSize;
        var pclusterCount = ErofsCompression.PclusterCount(inode.Size, clusterSize);
        if (pclusterCount == 0)
        {
            return;
        }

        var indexOffset = (int)(_superblock.GetInodeOffset(nid) + inode.OnDiskSize);
        var indexEnd = indexOffset + (long)pclusterCount * ErofsCompression.PclusterIndexSize;
        if (indexEnd > _image.Length)
        {
            throw new InvalidDataException("Pcluster index table exceeds the image bounds.");
        }

        var decoded = new byte[clusterSize];
        for (var i = 0; i < pclusterCount; i++)
        {
            var entry = PclusterIndex.FromBytes(
                _image.AsSpan(indexOffset + i * ErofsCompression.PclusterIndexSize, ErofsCompression.PclusterIndexSize));
            var remaining = (long)inode.Size - (long)i * clusterSize;
            var outputLength = (int)Math.Min(clusterSize, remaining);
            if (outputLength <= 0)
            {
                break;
            }

            var dataStart = (long)entry.BlockAddress * _blockSize;
            if (entry.IsPlain)
            {
                if (dataStart < 0 || dataStart > _image.LongLength - outputLength)
                {
                    throw new InvalidDataException("Plain pcluster data exceeds the image bounds.");
                }

                output.Write(_image, (int)dataStart, outputLength);
                continue;
            }

            if (dataStart < 0 || dataStart > _image.LongLength - entry.CompressedLength)
            {
                throw new InvalidDataException("Compressed pcluster data exceeds the image bounds.");
            }

            var decodedLength = ErofsCompression.Decompress(
                _image, (int)dataStart, entry.CompressedLength, decoded, 0, outputLength);
            if (decodedLength != outputLength)
            {
                throw new InvalidDataException(
                    $"Pcluster {i} decoded to {decodedLength} bytes, expected {outputLength}.");
            }

            output.Write(decoded, 0, outputLength);
        }
    }

    private static void WriteBytes(Stream output, byte[] source, long offset, long length)
    {
        while (length > 0)
        {
            var count = (int)Math.Min(CopyBufferSize, length);
            output.Write(source, (int)offset, count);
            offset += count;
            length -= count;
        }
    }
}