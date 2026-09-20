using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Checksums;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Models;
using System.Buffers;
using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;

public sealed record ErofsFileEntry(
    string Path,
    long Size,
    ulong Nid)
{
    public uint DeviceId { get; init; }

    public ushort Mode { get; init; }

    public uint Uid { get; init; }

    public uint Gid { get; init; }

    public ulong MTime { get; init; }

    /// <summary>Nanosecond component of the inode modification time.</summary>
    public uint MTimeNanoseconds { get; init; }

    public bool IsSymlink => (Mode & ErofsInode.ModeTypeMask) == ErofsInode.ModeSymlink;

    public bool IsDirectory => (Mode & ErofsInode.ModeTypeMask) == ErofsInode.ModeDirectory;

    public bool IsCharacterDevice => (Mode & ErofsInode.ModeTypeMask) == ErofsInode.ModeCharacterDevice;

    public bool IsBlockDevice => (Mode & ErofsInode.ModeTypeMask) == ErofsInode.ModeBlockDevice;

    public bool IsFifo => (Mode & ErofsInode.ModeTypeMask) == ErofsInode.ModeFifo;

    public bool IsSocket => (Mode & ErofsInode.ModeTypeMask) == ErofsInode.ModeSocket;

    public bool IsSpecialFile => IsCharacterDevice || IsBlockDevice || IsFifo || IsSocket;

    public string? SymlinkTarget { get; init; }

    public IReadOnlyList<ErofsXattrEntry>? Xattrs { get; init; }
}

public sealed partial class ErofsParser : IDisposable
{
    private const int CopyBufferSize = 1 << 20;

    private const long MaxMetadataComponentSize = 512L * 1024 * 1024;

    private const ulong NidMetaboxFlag = 1UL << 63;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly long _baseOffset;
    private readonly long _imageSize;
    private readonly object _streamLock = new();
    private readonly ErofsSuperblock _superblock;
    private readonly int _blockSize;
    private readonly bool _hasMetabox;
    private readonly byte[]? _metaboxData;
    private readonly Stream?[]? _deviceStreams;
    private readonly ErofsDeviceSlot[]? _deviceSlots;
    private bool _flatDevice;
    private List<ErofsFileEntry>? _fileTable;
    private List<string> _directoryPaths = [];
    private readonly List<ErofsFileEntry> _directoryEntries = [];
    private ErofsInode? _packedInode;

    /// <summary>
    /// Creates a parser from image bytes (the array is wrapped in a memory stream without copying; validates the magic at offset 1024).
    /// <para>从镜像字节创建解析器（字节数组直接包装为内存流，不复制；校验偏移 1024 处的魔数）。</para>
    /// </summary>
    public ErofsParser(byte[] image)
        : this(CreateMemorySource(image), ownsStream: false)
    {
    }

    /// <summary>
    /// Creates a parser from a stream.
    /// <para>从流创建解析器。</para>
    /// </summary>
    public ErofsParser(Stream stream)
        : this(stream, ownsStream: false)
    {
    }

    /// <summary>
    /// Creates a parser from a file path (opens and owns a FileStream; disposing the parser closes the file).
    /// <para>从文件路径创建解析器（内部打开并持有 FileStream；释放解析器即关闭文件）。</para>
    /// </summary>
    public ErofsParser(string filePath)
        : this(OpenFileSource(filePath), ownsStream: true)
    {
    }

    /// <summary>Creates a parser for an image with extra devices mapped into one stream. <para>为额外设备按设备表映射合并到单一流的镜像创建解析器。</para></summary>
    /// <param name="stream">The readable, seekable unified device stream. <para>可读可寻址的统一设备流。</para></param>
    /// <returns>A parser that leaves the supplied stream open. <para>不会关闭传入流的解析器。</para></returns>
    public static ErofsParser OpenFlatDevice(Stream stream) => new(stream) { _flatDevice = true };

    public ErofsParser(IReadOnlyList<Stream> deviceStreams)
        : this(ValidateDeviceStreams(deviceStreams)[0], ownsStream: false)
    {
        if (deviceStreams.Count < 2) return;
        var streams = ValidateDeviceStreams(deviceStreams);
        if (_deviceStreams is not null)
        {
            for (var i = 1; i < streams.Length && i < _deviceStreams.Length; i++)
                _deviceStreams[i] = streams[i];
        }
        else
        {
            _deviceStreams = new Stream[streams.Length];
            for (var i = 0; i < streams.Length; i++)
                _deviceStreams[i] = streams[i];
        }
    }

    private static Stream[] ValidateDeviceStreams(IReadOnlyList<Stream> deviceStreams)
    {
        ArgumentNullException.ThrowIfNull(deviceStreams);
        if (deviceStreams.Count == 0)
            throw new ArgumentException("At least one device stream is required.", nameof(deviceStreams));
        var streams = new Stream[deviceStreams.Count];
        for (var i = 0; i < deviceStreams.Count; i++)
            streams[i] = deviceStreams[i] ?? throw new ArgumentNullException(nameof(deviceStreams), $"Device stream at index {i} is null.");
        return streams;
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


            var sbCore = ReadBytes(ErofsSuperblock.SuperblockOffset, ErofsSuperblock.SuperblockSize, "Superblock");
            var preliminarySuperblock = ErofsSuperblock.FromBytes(sbCore);
            var sbSlotSize = ErofsSuperblock.SuperblockSize
                             + preliminarySuperblock.SbExtSlots * ErofsSuperblock.ExtSlotSize;
            _superblock = sbSlotSize > ErofsSuperblock.SuperblockSize
                ? ErofsSuperblock.FromBytes(ReadBytes(
                    ErofsSuperblock.SuperblockOffset,
                    checked((int)Math.Min(sbSlotSize, _imageSize - ErofsSuperblock.SuperblockOffset)),
                    "Superblock"))
                : preliminarySuperblock;


            if (_superblock.BlockSizeBits is < 9 or > 16)
            {
                throw new FormatException(
                    $"Unsupported EROFS block size shift {_superblock.BlockSizeBits} (expected 9..16).");
            }

            if (_superblock.HasCompatChecksum)
            {
                // fs/erofs/super.c erofs_superblock_csum_verify: crc32c(0x5045B54A) over [1024+8, blockSize),
                // skipping the magic and checksum fields. Mirror the builder's capture window.
                var windowLength = (int)Math.Min(
                    _superblock.BlockSize > ErofsSuperblock.SuperblockOffset
                        ? _superblock.BlockSize - ErofsSuperblock.SuperblockOffset
                        : _superblock.BlockSize,
                    _imageSize - ErofsSuperblock.SuperblockOffset);
                var crcWindow = ReadBytes(ErofsSuperblock.SuperblockOffset, windowLength, "Superblock checksum window");
                var computed = ErofsCrc32c.VerifySuperblock(crcWindow);
                if (computed != _superblock.Checksum)
                {
                    throw new FormatException(
                        $"EROFS superblock checksum mismatch: stored 0x{_superblock.Checksum:X8}, computed 0x{computed:X8}.");
                }
            }


            var unknownIncompat = _superblock.FeatureIncompat & ~ErofsSuperblock.AllIncompat;
            if (unknownIncompat != 0)
            {
                throw new FormatException(
                    $"EROFS image uses unknown incompatible feature bits 0x{unknownIncompat:X8}.");
            }

            _hasMetabox = _superblock.HasMetabox;
            _blockSize = _superblock.BlockSize;


            if (_hasMetabox && _superblock.MetaBoxNid != 0)
            {
                _metaboxData = ReadMetaboxData(_superblock.MetaBoxNid);
            }


            if (_superblock.HasDeviceTable && _superblock.ExtraDevices > 0)
            {
                _deviceStreams = new Stream[_superblock.ExtraDevices + 1];
                _deviceStreams[0] = _stream;
                _deviceSlots = new ErofsDeviceSlot[_superblock.ExtraDevices + 1];


                _deviceSlots[0] = new ErofsDeviceSlot { Blocks = _superblock.BlockCount64, UniAddr = 0 };
                var devTableOffset = (long)_superblock.DevtSlotoff * ErofsDeviceSlot.Size;
                for (var i = 0; i < _superblock.ExtraDevices; i++)
                {
                    var slotOffset = devTableOffset + (long)i * ErofsDeviceSlot.Size;
                    var slotData = ReadBytes(slotOffset, ErofsDeviceSlot.Size, $"Device slot {i + 1}");
                    _deviceSlots[i + 1] = ErofsDeviceSlot.FromBytes(slotData);
                }
            }
        }
        catch
        {
            sourceToDisposeOnFailure?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Checks whether the image bytes hold the erofs magic.
    /// <para>判断镜像字节是否包含 erofs 魔数。</para>
    /// </summary>
    public static bool CanHandle(byte[] image) => image is not null && ErofsSuperblock.HasMagic(image);

    internal byte[] SourceUuid => _superblock.Uuid;
    internal string SourceVolumeName => _superblock.VolumeName;

    public int BlockSize => _blockSize;

    public long BlockCount => (long)_superblock.BlockCount64;

    public long ImageSize => _imageSize;

    public long InodeCount => (long)_superblock.Inos;

    public ulong RootNid => _superblock.RootNid64;

    public string VolumeName => _superblock.VolumeName;

    internal ErofsSuperblock Superblock => _superblock;

    /// <summary>
    /// Walks the directory tree and returns the file listing (paths use '/').
    /// <para>遍历目录树并返回文件清单（路径使用 '/' 分隔）。</para>
    /// </summary>
    /// <summary>Returns all inode entries, including the root and empty directories.</summary>
    public IReadOnlyList<ErofsFileEntry> EnumerateEntries()
    {
        var files = EnumerateFiles();
        return [.. _directoryEntries, .. files];
    }

    public IReadOnlyList<ErofsFileEntry> EnumerateFiles()
    {
        _fileTable ??= EnumerateFilesCore();
        return _fileTable;
    }

    /// <summary>
    /// Reads a single file and returns its bytes.
    /// <para>读取单个文件内容并返回字节数组。</para>
    /// </summary>
    public byte[] ReadFileBytes(string path)
    {
        using var buffer = new MemoryStream();
        ReadFile(path, buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Reads a single file into the output stream (plain data read directly, inline data read directly, compressed data decompressed pcluster by pcluster, chunk-based data assembled chunk by chunk).
    /// <para>读取单个文件内容到输出流（plain 直读，inline 直读，compressed 逐 pcluster 解压，chunk 逐块拼接）。</para>
    /// </summary>
    public void ReadFile(string path, Stream outputStream)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(outputStream);

        var nid = FindFile(path);
        var inode = ReadInode(nid);
        WriteFileData(nid, inode, outputStream);
    }

    /// <summary>
    /// Whether the inode is a special file with no data content (char/block device, FIFO, socket).
    /// <para>判定 inode 是否为无数据内容的特殊文件（字符/块设备、FIFO、套接字）。</para>
    /// </summary>
    private static bool IsSpecialFile(ErofsInode inode)
        => inode.IsCharacterDevice || inode.IsBlockDevice || inode.IsFifo || inode.IsSocket;

    /// <summary>
    /// Extracts all files to the output directory.
    /// <para>将镜像中全部文件解包到输出目录。</para>
    /// </summary>
    public void ExtractToDirectory(string outputDirectory, bool overwrite = true, IProgress<(int processed, int total)>? progress = null)
        => ExtractToDirectory(outputDirectory, null, CancellationToken.None, overwrite, progress);

    /// <summary>Extracts with measured output bytes and cancellation, including writes within a large file.</summary>
    public void ExtractToDirectory(string outputDirectory, IProgress<(long Processed, long Total)>? byteProgress,
        CancellationToken cancellationToken, bool overwrite = true, IProgress<(int processed, int total)>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var entries = EnumerateFiles();
        // Preserve ReadFile's first-match rule even for duplicate paths in malformed images.
        // 即使畸形镜像含重复路径，也保持 ReadFile 的首次命中语义。
        var firstNids = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var entry in entries) firstNids.TryAdd(entry.Path, entry.Nid);
        var hostPaths = new SharedKernel.PortableExtractionPaths(outputDirectory, entries.Select(entry => entry.Path).Concat(_directoryPaths));
        foreach (var directory in _directoryPaths)
            Directory.CreateDirectory(hostPaths.Resolve(directory));
        var firstSizes = entries.GroupBy(e => e.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Size, StringComparer.Ordinal);
        var totalBytes = entries.Where(e => overwrite || !File.Exists(hostPaths.Resolve(e.Path)))
            .Sum(e => e.IsSymlink ? System.Text.Encoding.UTF8.GetByteCount(e.SymlinkTarget ?? string.Empty)
                : e.IsSpecialFile ? 0L : firstSizes[e.Path]);
        var bytes = new SharedKernel.ExtractionByteCounter(totalBytes, byteProgress, cancellationToken);
        var totalCount = entries.Count;
        var processedCount = 0;
        var progressGate = new object();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        // Alias paths stay ordered within one worker, preserving first-nid and overwrite behavior.
        var groups = entries.GroupBy(entry => hostPaths.Resolve(entry.Path), comparer).ToArray();
        SharedKernel.FirmwareParallelism.ForEach(groups, group =>
        {
            foreach (var entry in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = hostPaths.Resolve(entry.Path);
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                if (overwrite || !File.Exists(target))
                {
                    if (entry.IsSymlink)
                    {
                        File.WriteAllText(target, entry.SymlinkTarget ?? string.Empty);
                        bytes.Add(System.Text.Encoding.UTF8.GetByteCount(entry.SymlinkTarget ?? string.Empty));
                    }
                    else if (entry.IsSpecialFile) SharedKernel.FileExtraction.WriteAtomic(target, _ => { }, overwrite);
                    else ExtractFileAtomic(target, firstNids[entry.Path], overwrite, bytes);
                }
                lock (progressGate)
                {
                    processedCount++;
                    progress?.Report((processedCount, totalCount));
                }
            }
        });
        bytes.Complete();
    }
    /// <summary>
    /// Disposes the source held by the parser (only when the parser opened the file itself).
    /// <para>释放解析器持有的源（仅当解析器自己打开的文件流）。</para>
    /// </summary>
    public void Dispose()
    {
        _packedData?.Dispose();
        if (_deviceStreams is not null)
        {
            for (var i = 1; i < _deviceStreams.Length; i++)
            {
                _deviceStreams[i]?.Dispose();
            }
        }

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
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Reads length bytes from the source; validates the range first (offset &lt;= len and size &lt;= len - offset), then reads exactly via ReadExactly.
    /// <para>从数据源读取 length 字节；先做范围校验（offset &lt;= len 且 size &lt;= len - offset）， 再按 ReadExactly 精确读取。</para>
    /// </summary>
    private byte[] ReadMetaboxData(ulong metaboxNid)
    {
        var metaboxOffset = _superblock.GetInodeOffset(metaboxNid);
        if (metaboxOffset < 0 || metaboxOffset > _imageSize - 2)
            throw new InvalidDataException($"METABOX inode nid {metaboxNid} is out of range.");

        var advise = BinaryPrimitives.ReadUInt16LittleEndian(
            ReadBytes(metaboxOffset, 2, "METABOX inode advise"));
        var inodeSize = (advise & ErofsInode.FlagExtended) != 0 ? ErofsInode.ExtendedSize : ErofsInode.CompactSize;
        if (metaboxOffset > _imageSize - inodeSize)
            throw new InvalidDataException("METABOX inode exceeds the image bounds.");

        var inode = ErofsInode.FromBytes(ReadBytes(metaboxOffset, inodeSize, "METABOX inode"));
        var dataSize = (long)inode.Size;
        if (dataSize <= 0) return [];
        if (dataSize > MaxMetadataComponentSize)
            throw new InvalidDataException($"METABOX inode data size {dataSize} exceeds the limit of {MaxMetadataComponentSize}.");

        using var buffer = new MemoryStream();
        switch (inode.DataLayout)
        {
            case ErofsInode.DataLayoutInline:
                {
                    var inlineStart = metaboxOffset + inode.InlineDataOffset;
                    CheckRange(inlineStart, dataSize, "METABOX inline data");
                    CopyFromSource(buffer, inlineStart, dataSize);
                    break;
                }
            case ErofsInode.DataLayoutFlat:
                {
                    var start = checked(inode.RawBlockAddress * _blockSize);
                    CheckRange(start, dataSize, "METABOX flat data");
                    CopyFromSource(buffer, start, dataSize);
                    break;
                }
            default:
                throw new InvalidDataException($"Unsupported METABOX inode data layout {inode.DataLayout}.");
        }

        return buffer.ToArray();
    }

    private void CopyFromDevice(Stream output, int deviceId, long offset, long length, string what)
    {
        if (offset < 0 || length < 0)
            throw new InvalidDataException($"{what} has invalid offset/length.");
        if (deviceId <= 0 || _deviceSlots is null || deviceId >= _deviceSlots.Length)
            throw new InvalidDataException($"{what} references an invalid device id {deviceId}.");
        var slot = _deviceSlots[deviceId];
        var deviceLength = checked((long)slot.Blocks * _blockSize);
        if (offset > deviceLength || length > deviceLength - offset)
            throw new InvalidDataException($"{what} exceeds device {deviceId} bounds.");
        if (_deviceStreams?[deviceId] is not { } device)
        {
            if (!_flatDevice || slot.UniAddr == 0)
                throw new InvalidDataException($"{what} requires missing EROFS device {deviceId}.");
            var mappedOffset = checked((long)slot.UniAddr * _blockSize + offset);
            CheckRange(mappedOffset, length, what);
            CopyFromSource(output, mappedOffset, length);
            return;
        }
        if (offset > device.Length || length > device.Length - offset)
            throw new InvalidDataException($"{what} exceeds device {deviceId} stream bounds.");

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (length > 0)
            {
                var count = (int)Math.Min(CopyBufferSize, length);
                lock (_streamLock)
                {
                    device.Position = offset;
                    device.ReadExactly(buffer, 0, count);
                }
                output.Write(buffer, 0, count);
                offset += count;
                length -= count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private byte[] ReadBytes(long offset, int length, string what)
    {
        if (offset < 0 || length < 0 || offset > _imageSize || length > _imageSize - offset)
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
    /// Copies [offset, offset + length) bytes from the source to the output stream in chunks without loading the whole range into memory.
    /// <para>将数据源中 [offset, offset + length) 的字节直接拷贝到输出流（分块，不整体载入）。</para>
    /// </summary>
    private void CopyFromSource(Stream output, long offset, long length)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (length > 0)
            {
                var count = (int)Math.Min(CopyBufferSize, length);
                lock (_streamLock)
                {
                    _stream.Position = checked(_baseOffset + offset);
                    _stream.ReadExactly(buffer, 0, count);
                }
                output.Write(buffer, 0, count);
                offset += count;
                length -= count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ExtractFileAtomic(string target, ulong nid, bool overwrite, SharedKernel.ExtractionByteCounter bytes)
    {
        var tempPath = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var counted = bytes.Wrap(output);
                WriteFileData(nid, ReadInode(nid), counted);
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

            }

            throw;
        }
    }

    private List<ErofsFileEntry> EnumerateFilesCore()
    {
        _directoryEntries.Clear();
        var result = new List<ErofsFileEntry>();
        var visitedDirectories = new HashSet<ulong>();
        var directories = new List<string>();
        WalkDirectory(_superblock.RootNid64, string.Empty, visitedDirectories, result, directories);
        _directoryPaths = directories;
        return result;
    }

    private void WalkDirectory(ulong nid, string prefix, HashSet<ulong> visitedDirectories, List<ErofsFileEntry> result, List<string> directories)
    {
        if (!visitedDirectories.Add(nid))
        {


            return;
        }

        var inode = ReadInode(nid);
        if (!inode.IsDirectory)
        {
            return;
        }

        _directoryEntries.Add(new(prefix.Length == 0 ? "." : prefix, (long)inode.Size, nid)
        {
            Mode = inode.Mode,
            Uid = inode.Uid,
            Gid = inode.Gid,
            MTime = inode.CTime,
            MTimeNanoseconds = inode.CTimeNsec,
            Xattrs = ReadXattrs(nid, inode)
        });

        foreach (var entry in ReadDirectory(nid, inode))
        {
            if (entry.Name is "." or "..") continue;
            if (entry.FileType == ErofsInode.FileTypeDirectory)
            {


                if (entry.Nid == nid)
                {
                    continue;
                }

                var directoryPath = JoinPath(prefix, entry.Name);
                directories.Add(directoryPath);
                WalkDirectory(entry.Nid, directoryPath, visitedDirectories, result, directories);
                continue;
            }

            if (entry.FileType == ErofsInode.FileTypeRegularFile ||
                entry.FileType == ErofsInode.FileTypeSymlink ||
                entry.FileType is ErofsInode.FileTypeCharacterDevice or
                    ErofsInode.FileTypeBlockDevice or ErofsInode.FileTypeFifo or
                    ErofsInode.FileTypeSocket)
            {
                var fileInode = ReadInode(entry.Nid);
                var path = JoinPath(prefix, entry.Name);
                var fileEntry = new ErofsFileEntry(path, (long)fileInode.Size, entry.Nid)
                {
                    Mode = fileInode.Mode,
                    DeviceId = fileInode.U,
                    Uid = fileInode.Uid,
                    Gid = fileInode.Gid,
                    MTime = fileInode.CTime,
                    MTimeNanoseconds = fileInode.CTimeNsec,
                };
                if (fileInode.IsSymlink)
                {


                    try
                    {
                        using var buffer = new MemoryStream();
                        WriteFileData(entry.Nid, fileInode, buffer);
                        fileEntry = fileEntry with { SymlinkTarget = ReadSymlinkTarget(buffer) };
                    }
                    catch (Exception ex) when (ex is InvalidDataException or FormatException)
                    {
                        fileEntry = fileEntry with { SymlinkTarget = null };
                    }
                }
                else if (fileInode.IsCharacterDevice || fileInode.IsBlockDevice ||
                         fileInode.IsFifo || fileInode.IsSocket)
                {


                    result.Add(fileEntry);
                    continue;
                }

                var xattrs = ReadXattrs(entry.Nid, fileInode);
                if (xattrs is { Count: > 0 })
                {
                    fileEntry = fileEntry with { Xattrs = xattrs };
                }

                result.Add(fileEntry);
            }
        }
    }

    private static string? ReadSymlinkTarget(Stream content)
    {
        content.Position = 0;
        using var reader = new StreamReader(content, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var target = reader.ReadToEnd();
        return string.IsNullOrEmpty(target) ? null : target;
    }

    private static string JoinPath(string prefix, string name)
        => prefix.Length == 0 ? name : prefix + "/" + name;

    /// <summary>
    /// Reads the inode's xattr list: parses the inline xattr ibody (including shared xattr id resolution).
    /// <para>读取 inode 的 xattr 列表：解析 inline xattr ibody（含共享 xattr id 引用解析）。</para>
    /// </summary>
    private List<ErofsXattrEntry> ReadXattrs(ulong nid, ErofsInode inode)
    {
        var result = new List<ErofsXattrEntry>();
        if (inode.XattrICount == 0)
        {
            return result;
        }

        try
        {


            var ibodyOffset = _superblock.GetInodeOffset(nid) + inode.OnDiskSize;
            if (ibodyOffset < 0 || ibodyOffset > _imageSize - ErofsXattr.HeaderSize)
            {
                return result;
            }

            var ibody = ReadBytes(ibodyOffset, inode.XattrTotalSize, "xattr ibody");
            var prefixTable = LoadXattrPrefixTable();
            var entries = ErofsXattr.ParseInline(ibody, inode.XattrICount, prefixTable);
            result.AddRange(entries);


            var sharedIds = ErofsXattr.ParseSharedXattrIds(ibody, inode.XattrICount);
            if (sharedIds.Length > 0)
            {


                var sharedStart = _superblock.XattrBlkAddr * _blockSize;
                var sharedLength = Math.Min(_imageSize - sharedStart, MaxMetadataComponentSize);
                if (sharedLength > 0)
                {
                    foreach (var id in sharedIds)
                    {
                        // Shared IDs are offsets in four-byte units, not block numbers.
                        // 共享 ID 是以四字节为单位的偏移，不是块号。
                        var relativeOffset = ErofsXattr.GetSharedXattrByteOffset(id);
                        if (relativeOffset > sharedLength - ErofsXattr.EntryHeaderSize)
                            continue;
                        var entryOffset = checked(sharedStart + relativeOffset);
                        var header = ReadBytes(entryOffset, ErofsXattr.EntryHeaderSize, "shared xattr header");
                        var size = ErofsXattr.EntryHeaderSize + header[0] +
                            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
                        if (size > sharedLength - relativeOffset)
                            continue;
                        var entryBytes = ReadBytes(entryOffset, size, "shared xattr entry");
                        if (ErofsXattr.TryReadEntry(entryBytes, 0, prefixTable, out var sharedEntry, out _))
                        {
                            result.Add(sharedEntry);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException)
        {


        }

        return result;
    }

    /// <summary>
    /// Loads the long xattr name prefix table (feature XATTR_PREFIXES); null without the feature.
    /// The table is image-level static data and cached after the first load, avoiding one full
    /// re-read per file during directory walks.
    /// <para>加载长 xattr 名称前缀表（feature XATTR_PREFIXES）；无特性时返回 null。
    /// 表内容为镜像级静态数据，首次加载后缓存，避免目录遍历时每文件重读。</para>
    /// </summary>
    private IReadOnlyList<(byte BaseIndex, string Infix)>? _xattrPrefixTable;
    private bool _xattrPrefixTableLoaded;

    private IReadOnlyList<(byte BaseIndex, string Infix)>? LoadXattrPrefixTable()
    {
        if (_xattrPrefixTableLoaded)
        {
            return _xattrPrefixTable;
        }

        _xattrPrefixTable = LoadXattrPrefixTableCore();
        _xattrPrefixTableLoaded = true;
        return _xattrPrefixTable;
    }

    private IReadOnlyList<(byte BaseIndex, string Infix)>? LoadXattrPrefixTableCore()
    {
        if (!_superblock.HasXattrPrefixes || _superblock.XattrPrefixCount == 0)
        {
            return null;
        }

        var tableOffset = ErofsXattr.GetPrefixTableByteOffset(_superblock.XattrPrefixStart);
        if (tableOffset < 0 || tableOffset > _imageSize)
        {
            return null;
        }

        var tableLength = Math.Min(_imageSize - tableOffset, MaxMetadataComponentSize);
        var table = ReadBytes(tableOffset, checked((int)tableLength), "xattr prefix table");
        return ErofsXattr.ParsePrefixTable(table, _superblock.XattrPrefixCount);
    }

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


        if (_hasMetabox && (nid & NidMetaboxFlag) != 0)
        {
            if (_metaboxData is null || _metaboxData.Length == 0)
                throw new InvalidDataException(
                    $"Inode nid 0x{nid:X16} references the METABOX data stream, but no METABOX data was cached.");

            var slotIndex = nid & ~NidMetaboxFlag;
            var slotOffset = checked((long)slotIndex * ErofsSuperblock.CompactInodeSize);
            if (slotOffset < 0 || slotOffset + ErofsSuperblock.CompactInodeSize > _metaboxData.Length)
                throw new InvalidDataException(
                    $"METABOX slot index {slotIndex} (offset {slotOffset}) is out of range (metabox data size {_metaboxData.Length}).");

            return ResolveTimestamp(ErofsInode.FromBytes(
                _metaboxData.AsSpan((int)slotOffset, ErofsSuperblock.CompactInodeSize)));
        }

        var offset = _superblock.GetInodeOffset(nid);
        if (offset < 0 || offset > _imageSize - 2)
        {
            throw new InvalidDataException($"Inode nid {nid} is out of range.");
        }

        var available = (int)Math.Min(ErofsInode.ExtendedSize, _imageSize - offset);
        var data = ReadBytes(offset, available, $"Inode nid {nid}");
        var advise = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var inodeSize = (advise & ErofsInode.FlagExtended) != 0 ? ErofsInode.ExtendedSize : ErofsInode.CompactSize;
        if (inodeSize > data.Length)
        {
            throw new InvalidDataException($"Inode nid {nid} exceeds the image bounds.");
        }

        return ResolveTimestamp(ErofsInode.FromBytes(data.AsSpan(0, inodeSize)));
    }

    private int DirectoryBlockSize => _superblock.DirBlkBits == 0 ? _blockSize : 1 << _superblock.DirBlkBits;

    private List<ErofsDirectoryEntry> ReadDirectory(ulong nid, ErofsInode inode)
    {
        var dirBlockSize = DirectoryBlockSize;
        if (inode.DataLayout is not (ErofsInode.DataLayoutFlat or ErofsInode.DataLayoutInline))
        {


            throw new InvalidDataException($"Unsupported directory data layout {inode.DataLayout} for nid {nid}.");
        }


        var flatStart = checked(inode.RawBlockAddress * _blockSize);
        var length = checked((long)inode.Size);
        var flatLength = inode.DataLayout == ErofsInode.DataLayoutInline
            ? length / _blockSize * _blockSize : length;
        if (flatLength > 0) CheckRange(flatStart, flatLength, "Directory data");
        if (length > MaxMetadataComponentSize)
        {
            throw new InvalidDataException(
                $"Directory data size {length} exceeds the limit of {MaxMetadataComponentSize} bytes.");
        }


        var result = new List<ErofsDirectoryEntry>();
        // Linux erofs_map_blocks: only the final partial block resides beside the inode.
        for (var done = 0L; done < length; done += dirBlockSize)
        {
            var blockLength = (int)Math.Min(dirBlockSize, length - done);
            var offset = done < flatLength ? checked(flatStart + done)
                : checked(_superblock.GetInodeOffset(nid) + inode.InlineDataOffset + done - flatLength);
            var block = ReadBytes(offset, blockLength, "Directory data");
            result.AddRange(ParseDirectoryData(block, dirBlockSize, blockLength));
        }

        return result;
    }

    /// <summary>
    /// Parses one directory block: compact dirents per the kernel erofs_fill_dentries semantics first; falls back to dense parsing (long-name head slots) when the first entry's nameoff carries the dense high-bit flag (0x8000).
    /// <para>解析单块目录数据：优先按内核 erofs_fill_dentries 语义解析 compact dirent； 若首条目 nameoff 带 dense 高位标记（0x8000）则回退到 dense 解析（长名头槽位）。</para>
    /// </summary>
    private static List<ErofsDirectoryEntry> ParseDirectoryData(ReadOnlySpan<byte> block, int blockSize, long totalLength)
    {
        if (block.Length >= ErofsDirectoryEntry.DirentSize)
        {
            var firstNameOff = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(8, 2));
            if ((firstNameOff & ErofsDirectoryEntry.NameOffHeadFlag) != 0)
            {
                return ErofsDirectoryEntry.ParseDenseBlock(block, blockSize);
            }
        }

        return ErofsDirectoryEntry.ParseBlock(block);
    }

}