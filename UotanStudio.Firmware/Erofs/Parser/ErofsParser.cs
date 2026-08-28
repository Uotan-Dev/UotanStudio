using System.Buffers;
using System.Buffers.Binary;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// erofs 文件清单条目（路径 + 大小 + nid）。
/// <para>An erofs file-list entry (path + size + nid).</para>
/// </summary>
public sealed record ErofsFileEntry(string Path, long Size, ulong Nid);

/// <summary>
/// 流式 erofs 镜像解析器：持有可 seek 的数据源（FileStream、外部传入的 Stream，或对内存 byte[] 的轻量包装），
/// 按需读取超级块 / inode / 目录块 / pcluster，不再整镜像载入内存。
/// <para>A streaming erofs image parser: holds a seekable source (a FileStream, an externally supplied
/// Stream, or a lightweight wrapper over in-memory bytes) and reads the superblock, inodes, directory
/// blocks and pclusters on demand instead of loading the whole image into memory.</para>
/// </summary>
public sealed class ErofsParser : IDisposable
{
    private const int CopyBufferSize = 1 << 20;

    /// <summary>单次载入内存的元数据组件（目录数据 / 压缩索引表）大小上限，防止损坏镜像造成过量分配。
    /// Upper bound for one in-memory metadata component (directory data / pcluster index table).</summary>
    private const long MaxMetadataComponentSize = 512L * 1024 * 1024;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly long _baseOffset;
    private readonly long _imageSize;
    private readonly object _streamLock = new();
    private readonly ErofsSuperblock _superblock;
    private readonly int _blockSize;
    private List<ErofsFileEntry>? _fileTable;

    /// <summary>
    /// 从镜像字节创建解析器（字节数组直接包装为内存流，不复制；校验偏移 1024 处的魔数）。
    /// <para>Creates a parser from image bytes (the array is wrapped in a memory stream without
    /// copying; validates the magic at offset 1024).</para>
    /// </summary>
    public ErofsParser(byte[] image)
        : this(CreateMemorySource(image), ownsStream: false)
    {
    }

    /// <summary>
    /// 从流创建解析器。流可 seek 时按需读取；否则回退为读入内存缓冲（保持向后兼容）。
    /// 解析器不取得流的所有权，调用方负责释放。
    /// <para>Creates a parser from a stream. A seekable stream is read on demand; a non-seekable one
    /// falls back to buffering into memory (backward compatible). The parser does not take ownership;
    /// the caller remains responsible for disposing the stream.</para>
    /// </summary>
    public ErofsParser(Stream stream)
        : this(stream, ownsStream: false)
    {
    }

    /// <summary>
    /// 从文件路径创建解析器（内部打开并持有 FileStream；释放解析器即关闭文件）。
    /// <para>Creates a parser from a file path (opens and owns a FileStream; disposing the parser
    /// closes the file).</para>
    /// </summary>
    public ErofsParser(string filePath)
        : this(OpenFileSource(filePath), ownsStream: true)
    {
    }

    private ErofsParser(Stream stream, bool ownsStream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Stream? sourceToDisposeOnFailure = ownsStream ? stream : null;
        try
        {
            if (!stream.CanRead)
            {
                throw new ArgumentException("The source stream must be readable.", nameof(stream));
            }

            if (stream.CanSeek)
            {
                try
                {
                    _baseOffset = stream.Position;
                    var sourceLength = stream.Length;
                    if (_baseOffset < 0 || sourceLength < _baseOffset)
                    {
                        throw new InvalidDataException("The source stream position must be within its length.");
                    }

                    _imageSize = sourceLength - _baseOffset;
                    _stream = stream;
                    _ownsStream = ownsStream;
                }
                catch (NotSupportedException)
                {
                    var staged = StageToTemporaryFile(stream);
                    if (ownsStream)
                    {
                        stream.Dispose();
                    }

                    _stream = staged;
                    sourceToDisposeOnFailure = _stream;
                    _ownsStream = true;
                    _baseOffset = 0;
                    _imageSize = _stream.Length;
                }
            }
            else
            {
                _stream = StageToTemporaryFile(stream);
                sourceToDisposeOnFailure = _stream;
                _ownsStream = true;
                _baseOffset = 0;
                _imageSize = _stream.Length;
            }

            if (_imageSize < ErofsSuperblock.SuperblockOffset + ErofsSuperblock.SuperblockSize)
            {
                throw new FormatException($"Image too small to hold an EROFS superblock: {_imageSize} bytes.");
            }

            _superblock = ErofsSuperblock.FromBytes(
                ReadBytes(ErofsSuperblock.SuperblockOffset, ErofsSuperblock.SuperblockSize, "Superblock"));
            _blockSize = _superblock.BlockSize;
        }
        catch
        {
            sourceToDisposeOnFailure?.Dispose();
            throw;
        }
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
    public long ImageSize => _imageSize;

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
    /// 将镜像中全部文件解包到输出目录。每个文件先写入同目录临时文件，完整后再原子替换为目标文件；
    /// 中途失败不会留下半成品文件。
    /// <para>Extracts all files to the output directory. Each file is first written to a temporary file
    /// in the same directory and atomically moved into place only once complete; a failure never
    /// leaves a partial output file behind.</para>
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

            ExtractFileAtomic(target, entry.Path, overwrite);
        }
    }

    /// <summary>
    /// 释放解析器持有的源（仅当解析器自己打开的文件流）。
    /// <para>Disposes the source held by the parser (only when the parser opened the file itself).</para>
    /// </summary>
    public void Dispose()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static Stream CreateMemorySource(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new MemoryStream(image, writable: false);
    }

    private static FileStream OpenFileSource(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("EROFS image file not found.", filePath);
        }

        return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static FileStream StageToTemporaryFile(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var path = Path.Combine(Path.GetTempPath(), $"uotan-erofs-parser-{Guid.NewGuid():N}.tmp");
        FileStream? staging = null;
        try
        {
            staging = new FileStream(
                path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, CopyBufferSize,
                FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            stream.CopyTo(staging, CopyBufferSize);
            staging.Position = 0;
            return staging;
        }
        catch
        {
            staging?.Dispose();
            try { File.Delete(path); } catch { /* Best-effort cleanup must not hide the primary failure. */ }
            throw;
        }
    }

    /// <summary>
    /// 从数据源读取 <paramref name="length"/> 字节；先做范围校验（offset &lt;= len 且 size &lt;= len - offset），
    /// 再按 ReadExactly 精确读取。
    /// <para>Reads <paramref name="length"/> bytes from the source; validates the range first
    /// (offset &lt;= len and size &lt;= len - offset), then reads exactly via ReadExactly.</para>
    /// </summary>
    private byte[] ReadBytes(long offset, int length, string what)
    {
        if (offset < 0 || length < 0 || offset > _imageSize || (long)length > _imageSize - offset)
        {
            throw new InvalidDataException($"{what} exceeds the image bounds.");
        }

        var buffer = new byte[length];
        lock (_streamLock)
        {
            _stream.Position = checked(_baseOffset + offset);
            _stream.ReadExactly(buffer);
        }

        return buffer;
    }

    /// <summary>
    /// 将数据源中 [offset, offset + length) 的字节直接拷贝到输出流（分块，不整体载入）。
    /// <para>Copies [offset, offset + length) bytes from the source to the output stream in chunks
    /// without loading the whole range into memory.</para>
    /// </summary>
    private void CopyFromSource(Stream output, long offset, long length)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            lock (_streamLock)
            {
                _stream.Position = checked(_baseOffset + offset);
                while (length > 0)
                {
                    var count = (int)Math.Min(CopyBufferSize, length);
                    _stream.ReadExactly(buffer, 0, count);
                    output.Write(buffer, 0, count);
                    length -= count;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ExtractFileAtomic(string target, string entryPath, bool overwrite)
    {
        var tempPath = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                ReadFile(entryPath, output);
                output.Flush(flushToDisk: true);
            }

            File.Move(tempPath, target, overwrite);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // 尽力清理临时文件。Best-effort cleanup of the temporary file.
            }

            throw;
        }
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
        if (offset < 0 || offset > _imageSize - 2)
        {
            throw new InvalidDataException($"Inode nid {nid} is out of range.");
        }

        var advise = BinaryPrimitives.ReadUInt16LittleEndian(
            ReadBytes(offset, 2, $"Inode nid {nid}"));
        var inodeSize = (advise & ErofsInode.FlagExtended) != 0 ? ErofsInode.ExtendedSize : ErofsInode.CompactSize;
        if (offset < 0 || inodeSize < 0 || offset > _imageSize || inodeSize > _imageSize - offset)
        {
            throw new InvalidDataException($"Inode nid {nid} exceeds the image bounds.");
        }

        return ErofsInode.FromBytes(ReadBytes(offset, inodeSize, $"Inode nid {nid}"));
    }

    private List<ErofsDirectoryEntry> ReadDirectory(ErofsInode inode)
    {
        var start = checked((long)inode.RawBlockAddress * _blockSize);
        var length = (long)inode.Size;
        if (start < 0 || length < 0 || start > _imageSize || length > _imageSize - start)
        {
            throw new InvalidDataException("Directory data exceeds the image bounds.");
        }
        if (length > MaxMetadataComponentSize)
        {
            throw new InvalidDataException(
                $"Directory data size {length} exceeds the limit of {MaxMetadataComponentSize} bytes.");
        }

        // 逐目录块按需读取，单块内存占用及目录总长度均有明确上限。
        // Read directory blocks on demand while bounding both per-block memory and total input.
        var result = new List<ErofsDirectoryEntry>();
        for (var done = 0L; done < length; done += _blockSize)
        {
            var blockLength = (int)Math.Min(_blockSize, length - done);
            var block = ReadBytes(start + done, blockLength, "Directory data");
            result.AddRange(ErofsDirectoryEntry.ParseBlock(block));
        }

        return result;
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

        var start = checked((long)inode.RawBlockAddress * _blockSize);
        if (start < 0 || length < 0 || start > _imageSize || length > _imageSize - start)
        {
            throw new InvalidDataException("File data exceeds the image bounds.");
        }

        CopyFromSource(output, start, length);
    }

    private void WriteCompressedData(ulong nid, ErofsInode inode, Stream output)
    {
        var clusterSize = _blockSize;
        var pclusterCount = ErofsCompression.PclusterCount(inode.Size, clusterSize);
        if (pclusterCount == 0)
        {
            return;
        }

        var indexOffset = _superblock.GetInodeOffset(nid) + inode.OnDiskSize;
        var indexLength = checked((long)pclusterCount * ErofsCompression.PclusterIndexSize);
        if (indexOffset < 0 || indexLength < 0 || indexOffset > _imageSize || indexLength > _imageSize - indexOffset)
        {
            throw new InvalidDataException("Pcluster index table exceeds the image bounds.");
        }

        if (indexLength > MaxMetadataComponentSize)
        {
            throw new InvalidDataException(
                $"Pcluster index table size {indexLength} exceeds the limit of {MaxMetadataComponentSize} bytes.");
        }

        const int IndexEntriesPerBatch = 1024;
        var decoded = ArrayPool<byte>.Shared.Rent(clusterSize);
        var compressed = ArrayPool<byte>.Shared.Rent(ushort.MaxValue);
        var indexBatch = ArrayPool<byte>.Shared.Rent(IndexEntriesPerBatch * ErofsCompression.PclusterIndexSize);
        try
        {
            for (var first = 0; first < pclusterCount; first += IndexEntriesPerBatch)
            {
                var batchCount = Math.Min(IndexEntriesPerBatch, pclusterCount - first);
                lock (_streamLock)
                {
                    _stream.Position = checked(_baseOffset + indexOffset + (long)first * ErofsCompression.PclusterIndexSize);
                    _stream.ReadExactly(indexBatch, 0, batchCount * ErofsCompression.PclusterIndexSize);
                }
                for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
                {
                    var i = first + batchIndex;
                    var entry = PclusterIndex.FromBytes(indexBatch.AsSpan(
                        batchIndex * ErofsCompression.PclusterIndexSize,
                        ErofsCompression.PclusterIndexSize));
                    var remaining = (long)inode.Size - (long)i * clusterSize;
                    var outputLength = (int)Math.Min(clusterSize, remaining);
                    if (outputLength <= 0)
                    {
                        break;
                    }

                    var dataStart = checked((long)entry.BlockAddress * _blockSize);
                    if (entry.IsPlain)
                    {
                        if (dataStart < 0 || outputLength < 0 || dataStart > _imageSize || outputLength > _imageSize - dataStart)
                        {
                            throw new InvalidDataException("Plain pcluster data exceeds the image bounds.");
                        }

                        CopyFromSource(output, dataStart, outputLength);
                        continue;
                    }

                    var compressedLength = entry.CompressedLength;
                    if (dataStart < 0 || dataStart > _imageSize || compressedLength > _imageSize - dataStart)
                    {
                        throw new InvalidDataException("Compressed pcluster data exceeds the image bounds.");
                    }

                    lock (_streamLock)
                    {
                        _stream.Position = checked(_baseOffset + dataStart);
                        _stream.ReadExactly(compressed, 0, compressedLength);
                    }

                    var decodedLength = ErofsCompression.Decompress(
                        compressed,
                        0,
                        compressedLength,
                        decoded,
                        0,
                        outputLength);
                    if (decodedLength != outputLength)
                    {
                        throw new InvalidDataException(
                            $"Pcluster {i} decoded to {decodedLength} bytes, expected {outputLength}.");
                    }

                    output.Write(decoded, 0, outputLength);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(indexBatch);
            ArrayPool<byte>.Shared.Return(compressed);
            ArrayPool<byte>.Shared.Return(decoded);
        }
    }
}