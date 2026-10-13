using System.Buffers;
using System.Buffers.Binary;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Hash.Crc32c;
using UnifiedDataLayout = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsDataLayout;
using UnifiedInode = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsInode;

namespace UotanStudio.Firmware.FileSystem.Erofs.Parser;

/// <summary>
/// One inode listing produced by <see cref="ErofsParser"/>: path, size, inode number and decoded metadata.
/// <para><see cref="ErofsParser"/> 输出的一条 inode 清单：路径、大小、inode 编号与已解析的元数据。</para>
/// </summary>
/// <param name="Path">
/// Slash-separated path of the entry relative to the image root.
/// <para>相对于镜像根目录、以 '/' 分隔的条目路径。</para>
/// </param>
/// <param name="Size">
/// Content size in bytes recorded by the inode, before decompression.
/// <para>inode 记录的字节大小（解压前的内容尺寸）。</para>
/// </param>
/// <param name="Nid">
/// On-disk inode number; the METABOX flag bit is kept when the inode lives in the metabox.
/// <para>磁盘 inode 编号；inode 位于 metabox 时保留 METABOX 标志位。</para>
/// </param>
public sealed record ErofsFileEntry(
    string Path,
    long Size,
    ulong Nid)
{
    /// <summary>EROFS device id (<c>i_u</c>) recorded by the source inode.
    /// <para>源 inode 记录的 EROFS 设备 id（<c>i_u</c>）。</para></summary>
    /// <value><c>0</c> for the primary device; other values index the device table of multi-device images.
    /// <para>主设备为 <c>0</c>；其他值索引多设备镜像的设备表。</para></value>
    public uint DeviceId { get; init; }

    /// <summary>Raw 16-bit POSIX mode (<c>i_mode</c>) from the inode, including the file-type bits.
    /// <para>inode 中原始的 16 位 POSIX 模式（<c>i_mode</c>），含文件类型位。</para></summary>
    /// <value>Stored verbatim; the <c>Is*</c> properties decode its type field against the mode mask.
    /// <para>按磁盘原值存储；各 <c>Is*</c> 属性用类型掩码解码其类型字段。</para></value>
    public ushort Mode { get; init; }

    /// <summary>Owning user id of the file. <para>文件的属主用户 id。</para></summary>
    /// <value>Raw <c>i_uid</c> value; no mapping to host accounts is applied.
    /// <para>原始 <c>i_uid</c> 值；不映射到宿主机账户。</para></value>
    public uint Uid { get; init; }

    /// <summary>Owning group id of the file. <para>文件的属主用户组 id。</para></summary>
    /// <value>Raw <c>i_gid</c> value; no mapping to host groups is applied.
    /// <para>原始 <c>i_gid</c> 值；不映射到宿主机用户组。</para></value>
    public uint Gid { get; init; }

    /// <summary>Hard-link count (<c>i_nlink</c>) stored by the inode.
    /// <para>inode 存储的硬链接数（<c>i_nlink</c>）。</para></summary>
    /// <value>Read verbatim from the inode's nlink field.
    /// <para>逐字取自 inode 的 nlink 字段。</para></value>
    public ulong LinkCount { get; init; }

    /// <summary>Modification time in seconds since the Unix epoch, taken from the inode timestamp field.
    /// <para>自 Unix 纪元起的秒级修改时间，取自 inode 时间戳字段。</para></summary>
    /// <value>Paired with <see cref="MTimeNanoseconds"/> for the sub-second part.
    /// <para>与 <see cref="MTimeNanoseconds"/> 组合表示秒以下部分。</para></value>
    public ulong MTime { get; init; }

    /// <summary>Nanosecond component of the inode modification time.
    /// <para>inode 修改时间的纳秒分量。</para></summary>
    /// <value>Read verbatim from the inode; combine with <see cref="MTime"/>.
    /// <para>逐字取自 inode；与 <see cref="MTime"/> 组合使用。</para></value>
    public uint MTimeNanoseconds { get; init; }

    /// <summary>Mode type bits select symbolic link. <para>模式类型位选中符号链接。</para></summary>
    /// <value><see langword="true"/> when this entry is a symlink.
    /// <para>该条目为符号链接时为 <see langword="true"/>。</para></value>
    public bool IsSymlink => (Mode & UnifiedInode.ModeTypeMask) == UnifiedInode.ModeSymlink;

    /// <summary>Mode type bits select a directory. <para>模式类型位选中目录。</para></summary>
    /// <value><see langword="true"/> when this entry is a directory.
    /// <para>该条目为目录时为 <see langword="true"/>。</para></value>
    public bool IsDirectory => (Mode & UnifiedInode.ModeTypeMask) == UnifiedInode.ModeDirectory;

    /// <summary>Mode type bits select a character device. <para>模式类型位选中字符设备。</para></summary>
    /// <value><see langword="true"/> for character device entries.
    /// <para>条目为字符设备时为 <see langword="true"/>。</para></value>
    public bool IsCharacterDevice => (Mode & UnifiedInode.ModeTypeMask) == UnifiedInode.ModeCharacterDevice;

    /// <summary>Mode type bits select a block device. <para>模式类型位选中块设备。</para></summary>
    /// <value><see langword="true"/> for block device entries. <para>条目为块设备时为 <see langword="true"/>。</para></value>
    public bool IsBlockDevice => (Mode & UnifiedInode.ModeTypeMask) == UnifiedInode.ModeBlockDevice;

    /// <summary>Mode type bits select a FIFO. <para>模式类型位选中 FIFO。</para></summary>
    /// <value><see langword="true"/> for FIFO entries. <para>条目为 FIFO 时为 <see langword="true"/>。</para></value>
    public bool IsFifo => (Mode & UnifiedInode.ModeTypeMask) == UnifiedInode.ModeFifo;

    /// <summary>Mode type bits select a socket. <para>模式类型位选中套接字。</para></summary>
    /// <value><see langword="true"/> for socket entries. <para>条目为套接字时为 <see langword="true"/>。</para></value>
    public bool IsSocket => (Mode & UnifiedInode.ModeTypeMask) == UnifiedInode.ModeSocket;

    /// <summary>True for the four device/FIFO/socket types that carry no data content.
    /// <para>对无数据内容的设备、FIFO、套接字四类为真。</para></summary>
    /// <value><see langword="true"/> when the entry is a character device, block device, FIFO or socket.
    /// <para>条目为字符设备、块设备、FIFO 或套接字时为 <see langword="true"/>。</para></value>
    public bool IsSpecialFile => IsCharacterDevice || IsBlockDevice || IsFifo || IsSocket;

    /// <summary>Decoded UTF-8 target content of the symlink.
    /// <para>符号链接目标内容的 UTF-8 解码结果。</para></summary>
    /// <value><see langword="null"/> for non-symlink entries, when the stored target is empty, or when
    /// reading it failed while complete-metadata enforcement is off.
    /// <para>非符号链接条目、目标内容为空，或未启用完整元数据校验时读取失败，均为 <see langword="null"/>。</para></value>
    public string? SymlinkTarget { get; init; }

    /// <summary>Resolved inline and shared extended attributes of the inode.
    /// <para>inode 已解析的内联与共享扩展属性。</para></summary>
    /// <value><see langword="null"/> for regular files without attributes; directory entries carry the
    /// parsed list, which may be empty.
    /// <para>无属性的普通文件为 <see langword="null"/>；目录条目携带解析结果列表，可能为空。</para></value>
    public IReadOnlyList<ErofsXattrEntry>? Xattrs { get; init; }
}

public sealed partial class ErofsParser : IDisposable
{
    private const int CopyBufferSize = 1 << 20;

    /// <summary>
    /// Superblock CRC32c seed used by the EROFS checksum, mirroring the builder.
    /// <para>超级块 CRC32c 种子，与构建器侧使用相同值。</para>
    /// </summary>
    private const uint SuperblockChecksumSeed = 0x5045B54Au;

    private const long MaxMetadataComponentSize = 512L * 1024 * 1024;

    private const ulong NidMetaboxFlag = 1UL << 63;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly long _baseOffset;
    private readonly long _imageSize;
    private readonly Lock _streamLock = new();
    private readonly ErofsSuperblock _superblock;
    private readonly int _blockSize;
    private readonly bool _hasMetabox;
    private readonly byte[]? _metaboxData;
    private readonly Stream?[]? _deviceStreams;
    private readonly ErofsDeviceSlot[]? _deviceSlots;
    private bool _flatDevice;
    private List<ErofsFileEntry>? _fileTable;
    private readonly int _maximumEntries;
    private readonly long _maximumPathCharacters;
    private readonly CancellationToken _readCancellation;
    private int _visitedEntries;
    private long _pathCharacters;
    private List<string> _directoryPaths = [];
    private readonly List<ErofsFileEntry> _directoryEntries = [];
    private UnifiedInode? _packedInode;
    private UnifiedInode? _rootInode;

    /// <summary>
    /// Creates a parser from image bytes (the array is wrapped in a memory stream without copying; validates the magic at offset 1024).
    /// <para>从镜像字节创建解析器（字节数组直接包装为内存流，不复制；校验偏移 1024 处的魔数）。</para>
    /// </summary>
    /// <param name="image">
    /// Complete EROFS image bytes; the parser reads through them for its whole lifetime.
    /// <para>完整的 EROFS 镜像字节；解析器整个生命周期都经由它们读取。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is null.
    /// <para><paramref name="image"/> 为 null。</para></exception>
    /// <exception cref="FormatException">The superblock magic, geometry, features or checksum is invalid.
    /// <para>超级块魔数、几何信息、特性或校验和非法。</para></exception>
    /// <exception cref="FileSystemCorruptedException">A metadata read exceeds the image bounds or the device table mismatches.
    /// <para>元数据读取超出镜像边界，或设备表不一致。</para></exception>
    public ErofsParser(byte[] image)
        : this(CreateMemorySource(image), ownsStream: false)
    {
    }

    /// <summary>
    /// Creates a parser from a stream. The current position is treated as the image base; the stream
    /// is never closed, and a non-seekable stream is staged into a temporary file owned by the parser.
    /// <para>从流创建解析器。流的当前 position 被视为镜像基址；解析器不关闭该流，不可寻址的流会
    /// 转存到解析器持有的临时文件。</para>
    /// </summary>
    /// <param name="stream">
    /// Readable image stream covering the full EROFS image from its current position.
    /// <para>从当前 position 起覆盖完整 EROFS 镜像的可读流。</para>
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not readable.
    /// <para><paramref name="stream"/> 不可读。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The stream position exceeds its length.
    /// <para>流的 position 超出其长度。</para></exception>
    /// <exception cref="FormatException">The superblock magic, geometry, features or checksum is invalid.
    /// <para>超级块魔数、几何信息、特性或校验和非法。</para></exception>
    public ErofsParser(Stream stream)
        : this(stream, ownsStream: false)
    {
    }

    /// <summary>Reads a seekable image with explicit directory-entry/path budgets and cancellable reads; leaves the stream open.
    /// <para>读取可寻址镜像，显式限定目录条目数与路径字符预算，读取可取消；不关闭传入的流。</para></summary>
    /// <param name="stream">Readable, seekable stream positioned at the image base.
    /// <para>定位在镜像基址处的可读写入流。</para></param>
    /// <param name="maximumEntries">Positive budget for entries visited during directory enumeration.
    /// <para>目录枚举中访问条目数的正整数预算。</para></param>
    /// <param name="maximumPathCharacters">Positive budget for the summed length of enumerated paths.
    /// <para>枚举路径总长度的正整数预算。</para></param>
    /// <param name="cancellationToken">Token observed by metadata reads and directory walks.
    /// <para>元数据读取与目录遍历所观察的取消令牌。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.
    /// <para><paramref name="stream"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read or seeked.
    /// <para><paramref name="stream"/> 不可读或不可寻址。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumEntries"/> or
    /// <paramref name="maximumPathCharacters"/> is not positive.
    /// <para>预算参数不是正数。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was already signalled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    /// <exception cref="FormatException">The superblock magic, geometry, features or checksum is invalid.
    /// <para>超级块魔数、几何信息、特性或校验和非法。</para></exception>
    /// <exception cref="FileSystemCorruptedException">A metadata read exceeds the image bounds or the device table mismatches.
    /// <para>元数据读取超出镜像边界，或设备表不一致。</para></exception>
    public ErofsParser(Stream stream, int maximumEntries, long maximumPathCharacters, CancellationToken cancellationToken)
        : this(RequireSeekable(stream), false, maximumEntries, maximumPathCharacters, cancellationToken)
    {
    }

    private static Stream RequireSeekable(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek) { throw new ArgumentException("A readable seekable image stream is required.", nameof(stream)); }
        return stream;
    }

    /// <summary>
    /// Creates a parser from a file path (opens and owns a FileStream; disposing the parser closes the file).
    /// <para>从文件路径创建解析器（内部打开并持有 FileStream；释放解析器即关闭文件）。</para>
    /// </summary>
    /// <param name="filePath">Existing path to an EROFS image file, opened for shared reading.
    /// <para>已存在的 EROFS 镜像文件路径，以共享读方式打开。</para></param>
    /// <exception cref="ArgumentException">The path is null, empty or whitespace.
    /// <para>路径为 null、空字符串或仅空白。</para></exception>
    /// <exception cref="FileNotFoundException">No file exists at the path.
    /// <para>该路径上不存在文件。</para></exception>
    /// <exception cref="FormatException">The superblock magic, geometry, features or checksum is invalid.
    /// <para>超级块魔数、几何信息、特性或校验和非法。</para></exception>
    /// <exception cref="FileSystemCorruptedException">A metadata read exceeds the image bounds.
    /// <para>元数据读取超出镜像边界。</para></exception>
    public ErofsParser(string filePath)
        : this(OpenFileSource(filePath), ownsStream: true)
    {
    }

    /// <summary>Creates a parser for an image with extra devices mapped into one stream. <para>为额外设备按设备表映射合并到单一流的镜像创建解析器。</para></summary>
    /// <param name="stream">The readable, seekable unified device stream. <para>可读可寻址的统一设备流。</para></param>
    /// <returns>A parser that leaves the supplied stream open. <para>不会关闭传入流的解析器。</para></returns>
    public static ErofsParser OpenFlatDevice(Stream stream) => new(stream) { _flatDevice = true };

    /// <summary>
    /// Creates a parser for a multi-device image from one stream per device id, with unlimited budgets.
    /// <para>为多设备镜像创建解析器，每个设备 id 对应一条流，预算不受限。</para>
    /// </summary>
    /// <param name="deviceStreams">
    /// Device streams indexed by device id; the count must equal the extra-device count in the
    /// superblock plus one. A stream is addressed by its own position as device base.
    /// <para>按设备 id 索引的流；数量必须等于超级块声明的额外设备数加一。每条流以其自身位置作为设备基址。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceStreams"/> or one of its elements is null.
    /// <para><paramref name="deviceStreams"/> 或其某个元素为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="deviceStreams"/> is empty.
    /// <para><paramref name="deviceStreams"/> 为空。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The stream count differs from the on-disk device table.
    /// <para>流数量与磁盘设备表不一致。</para></exception>
    public ErofsParser(IReadOnlyList<Stream> deviceStreams)
        : this(deviceStreams, int.MaxValue, long.MaxValue, default)
    {
    }

    /// <summary>
    /// Creates a budgeted, cancellable parser for a multi-device image from one stream per device id.
    /// <para>为多设备镜像创建带预算、可取消的解析器，每个设备 id 对应一条流。</para>
    /// </summary>
    /// <param name="deviceStreams">
    /// Device streams indexed by device id; the count must equal the extra-device count in the
    /// superblock plus one.
    /// <para>按设备 id 索引的流；数量必须等于超级块声明的额外设备数加一。</para>
    /// </param>
    /// <param name="maximumEntries">Positive budget for entries visited during directory enumeration.
    /// <para>目录枚举中访问条目数的正整数预算。</para></param>
    /// <param name="maximumPathCharacters">Positive budget for the summed length of enumerated paths.
    /// <para>枚举路径总长度的正整数预算。</para></param>
    /// <param name="cancellationToken">Token observed by metadata reads and directory walks.
    /// <para>元数据读取与目录遍历所观察的取消令牌。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceStreams"/> or one of its elements is null.
    /// <para><paramref name="deviceStreams"/> 或其某个元素为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="deviceStreams"/> is empty or the primary
    /// stream is not readable.
    /// <para><paramref name="deviceStreams"/> 为空，或主流不可读。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumEntries"/> or
    /// <paramref name="maximumPathCharacters"/> is not positive.
    /// <para>预算参数不是正数。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was already signalled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The stream count differs from the on-disk device table.
    /// <para>流数量与磁盘设备表不一致。</para></exception>
    public ErofsParser(IReadOnlyList<Stream> deviceStreams, int maximumEntries,
        long maximumPathCharacters, CancellationToken cancellationToken)
        : this(ValidateDeviceStreams(deviceStreams)[0], ownsStream: false,
            maximumEntries, maximumPathCharacters, cancellationToken, deviceStreams)
    {
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

    private ErofsParser(Stream stream, bool ownsStream, int maximumEntries = int.MaxValue,
        long maximumPathCharacters = long.MaxValue, CancellationToken cancellationToken = default,
        IReadOnlyList<Stream>? deviceStreams = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPathCharacters);
        cancellationToken.ThrowIfCancellationRequested();
        _maximumEntries = maximumEntries;
        _maximumPathCharacters = maximumPathCharacters;
        _readCancellation = cancellationToken;
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
                        throw new FileSystemCorruptedException("The source stream position must be within its length.");
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

            _superblock.Validate();

            if (_superblock.HasCompatChecksum)
            {
                var windowLength = (int)Math.Min(
                    _superblock.BlockSize > ErofsSuperblock.SuperblockOffset
                        ? _superblock.BlockSize - ErofsSuperblock.SuperblockOffset
                        : _superblock.BlockSize,
                    _imageSize - ErofsSuperblock.SuperblockOffset);
                var crcWindow = ReadBytes(ErofsSuperblock.SuperblockOffset, windowLength, "Superblock checksum window");
                var computed = Crc32c.Compute(crcWindow[8..], SuperblockChecksumSeed);
                if (computed != _superblock.Checksum)
                {
                    throw new FormatException(
                        $"EROFS superblock checksum mismatch: stored 0x{_superblock.Checksum:X8}, computed 0x{computed:X8}.");
                }
            }

            _hasMetabox = _superblock.HasMetabox;
            _blockSize = _superblock.BlockSize;

            if (deviceStreams is not null && deviceStreams.Count != _superblock.ExtraDevices + 1)
                throw new FileSystemCorruptedException("EROFS device stream count differs from the device table.");

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
                    if (deviceStreams is not null)
                        _deviceStreams[i + 1] = deviceStreams[i + 1];
                }
            }

            if (_hasMetabox && _superblock.MetaBoxNid != 0)
            {
                _metaboxData = ReadMetaboxData(_superblock.MetaBoxNid);
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
    /// <param name="image">Candidate image bytes; null reports false.
    /// <para>候选镜像字节；null 返回 false。</para></param>
    /// <returns><see langword="true"/> when the erofs magic is present; otherwise <see langword="false"/>.
    /// <para>存在 erofs 魔数时返回 <see langword="true"/>，否则返回 <see langword="false"/>。</para></returns>
    public static bool CanHandle(byte[] image) => image is not null && ErofsSuperblock.HasMagic(image);

    internal byte[] SourceUuid => _superblock.Uuid;
    internal string SourceVolumeName => _superblock.VolumeName;

    /// <summary>Logical block size decoded from the superblock shift.
    /// <para>由超级块位移解码出的逻辑块大小。</para></summary>
    /// <value>A power of two between 512 and 65536 bytes; the unit of every block address in the image.
    /// <para>512 到 65536 字节之间的 2 的幂；镜像内所有块地址以此为基本单位。</para></value>
    public int BlockSize => _blockSize;

    /// <summary>Total block count recorded by the superblock.
    /// <para>超级块记录的总块数。</para></summary>
    /// <value>The 64-bit on-disk count carried as <see cref="long"/>; multiply by
    /// <see cref="BlockSize"/> for bytes.
    /// <para>磁盘 64 位计数按 <see cref="long"/> 承载；乘以 <see cref="BlockSize"/> 得到字节数。</para></value>
    public long BlockCount => (long)_superblock.BlockCount64;

    /// <summary>Image bytes visible to the parser.
    /// <para>解析器可见的镜像字节数。</para></summary>
    /// <value>From the base position given at construction (or the start of the staged temporary
    /// copy) to the end of the source.
    /// <para>从构造时给定的基址（或转存临时副本的起点）到源末尾。</para></value>
    public long ImageSize => _imageSize;

    /// <summary>Number of allocated inodes reported by the superblock.
    /// <para>超级块报告的已分配 inode 数量。</para></summary>
    /// <value>The on-disk count carried as <see cref="long"/>.
    /// <para>磁盘计数按 <see cref="long"/> 承载。</para></value>
    public long InodeCount => (long)_superblock.Inos;

    /// <summary>Inode number of the root directory.
    /// <para>根目录的 inode 编号。</para></summary>
    /// <value>Starting nid of directory walks, taken from the 64-bit superblock field.
    /// <para>目录遍历的起始 nid，取自超级块的 64 位字段。</para></value>
    public ulong RootNid => _superblock.RootNid64;

    /// <summary>Volume name stored in the superblock.
    /// <para>超级块中存储的卷名。</para></summary>
    /// <value>The decoded label string as carried by the superblock.
    /// <para>按超级块内容解码出的标签字符串。</para></value>
    public string VolumeName => _superblock.VolumeName;

    internal ErofsSuperblock Superblock => _superblock;

    /// <summary>
    /// Walks the directory tree and returns every inode entry — the root, directories (including
    /// empty ones) and files — with paths separated by '/'.
    /// <para>遍历目录树并返回全部 inode 条目——根目录、目录（含空目录）与文件，路径以 '/' 分隔。</para>
    /// </summary>
    /// <returns>Directory entries in traversal order followed by the file listing.
    /// <para>按遍历顺序的目录条目在前，文件清单在后。</para></returns>
    /// <exception cref="OperationCanceledException">The parser's cancellation token was signalled.
    /// <para>解析器的取消令牌已被触发。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The entry or path budget is exhausted or metadata is malformed.
    /// <para>条目/路径预算耗尽或元数据非法。</para></exception>
    public IReadOnlyList<ErofsFileEntry> EnumerateEntries()
    {
        var files = EnumerateFiles();
        return [.. _directoryEntries, .. files];
    }

    /// <summary>
    /// Returns the flat listing of files, symlinks and special files (directories and the root excluded).
    /// <para>返回文件、符号链接与特殊文件的扁平清单（不含目录与根条目）。</para>
    /// </summary>
    /// <returns>The cached file table; the first call walks the tree, later calls reuse the result.
    /// <para>已缓存的文件表；首次调用遍历目录树，其后调用复用结果。</para></returns>
    /// <exception cref="OperationCanceledException">The parser's cancellation token was signalled.
    /// <para>解析器的取消令牌已被触发。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The entry or path budget is exhausted or metadata is malformed.
    /// <para>条目/路径预算耗尽或元数据非法。</para></exception>
    public IReadOnlyList<ErofsFileEntry> EnumerateFiles()
    {
        _readCancellation.ThrowIfCancellationRequested();
        _fileTable ??= EnumerateFilesCore();
        return _fileTable;
    }

    /// <summary>
    /// Root of the unified inode tree the directory walk builds, which carries parent/child links and
    /// the build-time fields the public listing does not expose.
    /// <para>目录遍历构建的统一 inode 树根节点，携带父子链接与公开清单不暴露的构建期字段。</para>
    /// </summary>
    /// <value><see langword="null"/> when the root inode is not a directory, or when the walk found the
    /// root already visited; otherwise the root node after <see cref="EnumerateFiles"/> or
    /// <see cref="EnumerateEntries"/> has run, which this accessor triggers on demand.
    /// <para>根 inode 不是目录，或遍历发现根已被访问时为 <see langword="null"/>；否则在
    /// <see cref="EnumerateFiles"/> 或 <see cref="EnumerateEntries"/> 运行后为根节点，本访问器会按需触发遍历。</para></value>
    internal UnifiedInode? RootInode
    {
        get
        {
            _readCancellation.ThrowIfCancellationRequested();
            _fileTable ??= EnumerateFilesCore();
            return _rootInode;
        }
    }

    /// <summary>
    /// Reads a single file and returns its bytes.
    /// <para>读取单个文件内容并返回字节数组。</para>
    /// </summary>
    /// <param name="path">Exact '/'-separated image path as reported by <see cref="EnumerateFiles"/>.
    /// <para>与 <see cref="EnumerateFiles"/> 输出逐字一致的 '/' 分隔镜像路径。</para></param>
    /// <returns>The complete decoded contents of the file.
    /// <para>文件完整解码后的内容字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.
    /// <para><paramref name="path"/> 为 null。</para></exception>
    /// <exception cref="FileNotFoundException">No file has that exact path.
    /// <para>不存在与该路径逐字匹配的文件。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The file's metadata or data blocks are malformed.
    /// <para>文件的元数据或数据块非法。</para></exception>
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
    /// <param name="path">Exact '/'-separated image path of the file.
    /// <para>文件的 '/' 分隔镜像路径（逐字匹配）。</para></param>
    /// <param name="outputStream">Stream receiving the decoded bytes; writing starts at its current
    /// position and the stream is not closed.
    /// <para>接收解码字节的流；从其当前位置开始写入且不被关闭。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="outputStream"/> is null.
    /// <para><paramref name="path"/> 或 <paramref name="outputStream"/> 为 null。</para></exception>
    /// <exception cref="FileNotFoundException">No file has that exact path.
    /// <para>不存在与该路径逐字匹配的文件。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The file's metadata or data blocks are malformed.
    /// <para>文件的元数据或数据块非法。</para></exception>
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
    private static bool IsSpecialFile(UnifiedInode inode)
        => inode.IsCharacterDevice || inode.IsBlockDevice || inode.IsFifo || inode.IsSocket;

    /// <summary>
    /// Extracts all files to the output directory.
    /// <para>将镜像中全部文件解包到输出目录。</para>
    /// </summary>
    /// <param name="outputDirectory">Host directory receiving the tree; created when missing.
    /// <para>接收目录树的宿主目录；缺失时自动创建。</para></param>
    /// <param name="overwrite">When <see langword="false"/>, files already present on the host are skipped.
    /// <para>为 <see langword="false"/> 时跳过宿主上已存在的文件。</para></param>
    /// <param name="progress">Receives processed/total entry counts, coalesced to one notification per 100 millisecond
    /// window and ended by a notification carrying the total; may be null.
    /// <para>接收已处理/总条目数，按 100 毫秒窗口合并，并以一条携带总量的通知结束；可为 null。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="outputDirectory"/> is null.
    /// <para><paramref name="outputDirectory"/> 为 null。</para></exception>
    public void ExtractToDirectory(string outputDirectory, bool overwrite = true, IProgress<(int processed, int total)>? progress = null)
        => ExtractToDirectory(outputDirectory, null, CancellationToken.None, overwrite, progress);

    /// <summary>Extracts with measured output bytes and cancellation, including writes within a large file.
    /// <para>按字节计量并支持取消地解包，取消点覆盖大文件内部的写入。</para></summary>
    /// <param name="outputDirectory">Host directory receiving the tree; created when missing.
    /// <para>接收目录树的宿主目录；缺失时自动创建。</para></param>
    /// <param name="byteProgress">Receives processed/total output bytes; symlink targets count their
    /// UTF-8 length and special files contribute nothing. May be null.
    /// <para>接收已处理/总输出字节；符号链接目标按 UTF-8 长度计，特殊文件不计入。可为 null。</para></param>
    /// <param name="cancellationToken">Stops remaining entries and interrupts in-flight counted writes.
    /// <para>中止剩余条目，并打断进行中的计量写入。</para></param>
    /// <param name="overwrite">When <see langword="false"/>, files already present on the host are skipped.
    /// <para>为 <see langword="false"/> 时跳过宿主上已存在的文件。</para></param>
    /// <param name="progress">Receives processed/total entry counts, coalesced to one notification per 100 millisecond
    /// window and ended by a notification carrying the total; may be null.
    /// <para>接收已处理/总条目数，按 100 毫秒窗口合并，并以一条携带总量的通知结束；可为 null。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="outputDirectory"/> is null.
    /// <para><paramref name="outputDirectory"/> 为 null。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public void ExtractToDirectory(string outputDirectory, IProgress<(long Processed, long Total)>? byteProgress,
        CancellationToken cancellationToken, bool overwrite = true, IProgress<(int processed, int total)>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var entries = EnumerateFiles();
        var firstNids = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var entry in entries) firstNids.TryAdd(entry.Path, entry.Nid);
        var hostPaths = new UotanStudio.Firmware.FileSystem.Extraction.PortableExtractionPaths(outputDirectory, entries.Select(entry => entry.Path).Concat(_directoryPaths));
        foreach (var directory in _directoryPaths)
            Directory.CreateDirectory(hostPaths.Resolve(directory));
        var firstSizes = entries.GroupBy(e => e.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Size, StringComparer.Ordinal);
        var totalBytes = entries.Where(e => overwrite || !File.Exists(hostPaths.Resolve(e.Path)))
            .Sum(e => e.IsSymlink ? System.Text.Encoding.UTF8.GetByteCount(e.SymlinkTarget ?? string.Empty)
                : e.IsSpecialFile ? 0L : firstSizes[e.Path]);
        var bytes = new Core.ExtractionByteCounter(totalBytes, byteProgress, cancellationToken);
        var totalCount = entries.Count;
        var processedCount = 0;
        var progressGate = new object();
        IProgress<(long Processed, long Total)>? entrySink = progress is null ? null : new Core.EntryCountProgress(progress);
        var entryProgress = new Core.ProgressThrottle(totalCount, entrySink, cancellationToken);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var groups = entries.GroupBy(entry => hostPaths.Resolve(entry.Path), comparer).ToArray();
        Core.FirmwareParallelism.ForEach(groups, group =>
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
                    else if (entry.IsSpecialFile) Core.FileExtraction.WriteAtomic(target, _ => { }, overwrite);
                    else ExtractFileAtomic(target, firstNids[entry.Path], overwrite, bytes);
                }
                int completed;
                lock (progressGate)
                {
                    processedCount++;
                    completed = processedCount;
                }

                entryProgress.Report((completed, totalCount));
            }
        });
        bytes.Complete();
        entryProgress.Complete();
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
            throw new FileSystemCorruptedException($"METABOX inode nid {metaboxNid} is out of range.");

        var advise = BinaryPrimitives.ReadUInt16LittleEndian(
            ReadBytes(metaboxOffset, 2, "METABOX inode advise"));
        var inodeSize = (advise & UnifiedInode.FlagExtended) != 0 ? UnifiedInode.ExtendedSize : UnifiedInode.CompactSize;
        if (metaboxOffset > _imageSize - inodeSize)
            throw new FileSystemCorruptedException("METABOX inode exceeds the image bounds.");

        var inode = UnifiedInode.FromBytes(
            ReadBytes(metaboxOffset, inodeSize, "METABOX inode"), metaboxNid, _superblock.BlockSizeBits, inMetabox: true);
        var dataSize = (long)inode.Size;
        if (dataSize <= 0) return [];
        if (dataSize > MaxMetadataComponentSize)
            throw new FileSystemCorruptedException($"METABOX inode data size {dataSize} exceeds the limit of {MaxMetadataComponentSize}.");

        using var buffer = new MemoryStream();
        switch (inode.DataLayout)
        {
            case UnifiedDataLayout.Inline:
                {
                    var inlineStart = metaboxOffset + inode.InlineDataOffset;
                    CheckRange(inlineStart, dataSize, "METABOX inline data");
                    CopyFromSource(buffer, inlineStart, dataSize);
                    break;
                }
            case UnifiedDataLayout.Flat:
                {
                    var start = checked(inode.AddressField() * _blockSize);
                    CheckRange(start, dataSize, "METABOX flat data");
                    CopyFromSource(buffer, start, dataSize);
                    break;
                }
            case UnifiedDataLayout.Compressed:
            case UnifiedDataLayout.CompactIndexes:
                {
                    using var bounded = new CountingWriteStream(buffer, dataSize, leaveOpen: true, _readCancellation);
                    WriteCompressedData(metaboxNid, inode, bounded, cancellationToken: _readCancellation);
                    if (bounded.BytesWritten != dataSize)
                        throw new FileSystemCorruptedException("METABOX decompressed length differs from its inode size.");
                    break;
                }
            default:
                throw new FileSystemUnsupportedFeatureException($"Unsupported METABOX inode data layout {inode.DataLayout}.");
        }

        return buffer.ToArray();
    }

    private void CopyFromDevice(Stream output, int deviceId, long offset, long length, string what)
    {
        if (offset < 0 || length < 0)
            throw new FileSystemCorruptedException($"{what} has invalid offset/length.");
        if (deviceId <= 0 || _deviceSlots is null || deviceId >= _deviceSlots.Length)
            throw new FileSystemCorruptedException($"{what} references an invalid device id {deviceId}.");
        var slot = _deviceSlots[deviceId];
        var deviceLength = checked((long)slot.Blocks * _blockSize);
        if (offset > deviceLength || length > deviceLength - offset)
            throw new FileSystemCorruptedException($"{what} exceeds device {deviceId} bounds.");
        if (_deviceStreams?[deviceId] is not { } device)
        {
            if (!_flatDevice || slot.UniAddr == 0)
                throw new FileSystemCorruptedException($"{what} requires missing EROFS device {deviceId}.");
            var mappedOffset = checked((long)slot.UniAddr * _blockSize + offset);
            CheckRange(mappedOffset, length, what);
            CopyFromSource(output, mappedOffset, length);
            return;
        }
        if (offset > device.Length || length > device.Length - offset)
            throw new FileSystemCorruptedException($"{what} exceeds device {deviceId} stream bounds.");

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (length > 0)
            {
                var count = (int)Math.Min(CopyBufferSize, length);
                ReadPositioned(device, offset, buffer, count);
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
        _readCancellation.ThrowIfCancellationRequested();
        if (offset < 0 || length < 0 || offset > _imageSize || length > _imageSize - offset)
        {
            throw new FileSystemCorruptedException($"{what} exceeds the image bounds.");
        }

        var buffer = new byte[length];
        ReadPositioned(_stream, checked(_baseOffset + offset), buffer, length);
        return buffer;
    }

    /// <summary>
    /// Reads an absolute source range without moving a shared cursor: a real <see cref="FileStream"/> is read
    /// positionally so concurrent extraction workers overlap, while every other source keeps the parser-wide
    /// lock because it shares one mutable position.
    /// <para>按绝对区间读取数据源而不移动共享游标：真正的 <see cref="FileStream"/> 走定位读取，使并行解包工作者可以重叠；
    /// 其他数据源因共用一个可变位置，仍使用解析器范围的锁。</para>
    /// </summary>
    /// <param name="source">Stream backing the image or one device region. <para>承载镜像或单个设备区域的数据流。</para></param>
    /// <param name="position">Absolute offset to read from. <para>读取的绝对偏移。</para></param>
    /// <param name="buffer">Destination, filled completely or the read fails. <para>目标缓冲，要么填满要么读取失败。</para></param>
    /// <param name="count">Bytes to read from the start of <paramref name="buffer"/>. <para>从 <paramref name="buffer"/> 起始读取的字节数。</para></param>
    /// <exception cref="EndOfStreamException">The source ended before <paramref name="count"/> bytes were read.
    /// <para>数据源在读满 <paramref name="count"/> 字节前结束。</para></exception>
    private void ReadPositioned(Stream source, long position, byte[] buffer, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        if (source.GetType() == typeof(FileStream))
        {
            var handle = ((FileStream)source).SafeFileHandle;
            var read = 0;
            while (read < count)
            {
                _readCancellation.ThrowIfCancellationRequested();
                var step = RandomAccess.Read(handle, buffer.AsSpan(read, count - read), position + read);
                if (step == 0) throw new EndOfStreamException();
                read += step;
            }

            return;
        }

        lock (_streamLock)
        {
            source.Position = position;
            source.ReadExactly(buffer, 0, count);
        }
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
                ReadPositioned(_stream, checked(_baseOffset + offset), buffer, count);
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

    private void ExtractFileAtomic(string target, ulong nid, bool overwrite, Core.ExtractionByteCounter bytes)
    {
        var tempPath = TempFileHelper.CreateAdjacentPath(target);
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
        _rootInode = WalkDirectory(_superblock.RootNid64, string.Empty, visitedDirectories, result, directories);
        _directoryPaths = directories;
        return result;
    }

    private UnifiedInode? WalkDirectory(ulong nid, string prefix, HashSet<ulong> visitedDirectories,
        List<ErofsFileEntry> result, List<string> directories)
    {
        _readCancellation.ThrowIfCancellationRequested();
        if (!visitedDirectories.Add(nid))
        {
            if (RequireCompleteMetadata)
            {
                throw new FileSystemCorruptedException("EROFS directory aliases or cycles cannot be captured losslessly.");
            }
            return null;
        }

        var inode = ReadInode(nid);
        if (!inode.IsDirectory)
        {
            return null;
        }

        inode.Name = LastPathComponent(prefix);
        _directoryEntries.Add(ProjectEntry(prefix.Length == 0 ? "." : prefix, inode, deviceId: 0) with
        {
            Xattrs = ReadXattrs(nid, inode)
        });

        foreach (var entry in ReadDirectory(nid, inode))
        {
            _readCancellation.ThrowIfCancellationRequested();
            if (entry.Name is "." or "..") continue;
            var candidatePath = JoinPath(prefix, entry.Name);
            if (_visitedEntries >= _maximumEntries || candidatePath.Length > _maximumPathCharacters - _pathCharacters)
            {
                throw new FileSystemCorruptedException("EROFS directory enumeration exceeds its entry or path budget.");
            }
            _visitedEntries++;
            _pathCharacters += candidatePath.Length;
            if (entry.FileType == UnifiedInode.FileTypeDirectory)
            {

                if (entry.Nid == nid)
                {
                    continue;
                }

                var directoryPath = candidatePath;
                directories.Add(directoryPath);
                var child = WalkDirectory(entry.Nid, directoryPath, visitedDirectories, result, directories);
                if (child is not null)
                {
                    child.Parent = inode;
                    inode.Children.Add(child);
                }
                continue;
            }

            if (entry.FileType == UnifiedInode.FileTypeRegularFile ||
                entry.FileType == UnifiedInode.FileTypeSymlink ||
                entry.FileType is UnifiedInode.FileTypeCharacterDevice or
                    UnifiedInode.FileTypeBlockDevice or UnifiedInode.FileTypeFifo or
                    UnifiedInode.FileTypeSocket)
            {
                var fileInode = ReadInode(entry.Nid);
                fileInode.Name = entry.Name;
                fileInode.Parent = inode;
                inode.Children.Add(fileInode);
                var fileEntry = ProjectEntry(candidatePath, fileInode, fileInode.AddressField());
                if (fileInode.IsSymlink)
                {

                    try
                    {
                        using var buffer = new MemoryStream();
                        WriteFileData(entry.Nid, fileInode, buffer);
                        fileEntry = fileEntry with { SymlinkTarget = ReadSymlinkTarget(buffer) };
                    }
                    catch (Exception ex) when (!RequireCompleteMetadata && ex is InvalidDataException or FileSystemException or FormatException)
                    {
                        fileEntry = fileEntry with { SymlinkTarget = null };
                    }
                }
                var xattrs = ReadXattrs(entry.Nid, fileInode);
                if (xattrs is { Count: > 0 })
                {
                    fileEntry = fileEntry with { Xattrs = xattrs };
                }

                result.Add(fileEntry);
            }
        }

        return inode;
    }

    /// <summary>
    /// Projects one tree node into the caller-visible listing record, so the walk's single decoded
    /// inode describes both the tree and the entry.
    /// <para>把一个树节点投影为调用方可见的清单记录，使遍历解码出的同一个 inode 同时描述树与条目。</para>
    /// </summary>
    /// <param name="path">Slash-separated image path of the entry. <para>条目的 '/' 分隔镜像路径。</para></param>
    /// <param name="inode">Tree node being projected. <para>被投影的树节点。</para></param>
    /// <param name="deviceId">Value reported as the entry's <c>i_u</c> field; zero for directory entries,
    /// which the flat listing never carried. <para>作为条目 <c>i_u</c> 字段报告的值；目录条目为零，扁平清单
    /// 从不携带该字段。</para></param>
    /// <returns>The listing record built from <paramref name="inode"/>.
    /// <para>由 <paramref name="inode"/> 构建的清单记录。</para></returns>
    private static ErofsFileEntry ProjectEntry(string path, UnifiedInode inode, uint deviceId)
        => new(path, (long)inode.Size, inode.Nid)
        {
            Mode = inode.Mode,
            DeviceId = deviceId,
            Uid = inode.Uid,
            Gid = inode.Gid,
            LinkCount = inode.Nlink,
            MTime = inode.CTime,
            MTimeNanoseconds = inode.CTimeNsec,
        };

    private static string LastPathComponent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
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
    internal bool RequireCompleteMetadata { get; init; }

    private List<ErofsXattrEntry> ReadXattrs(ulong nid, UnifiedInode inode)
    {
        var result = new List<ErofsXattrEntry>();
        if (inode.XattrICount == 0)
        {
            return result;
        }

        try
        {

            var metaboxResident = _hasMetabox && (nid & NidMetaboxFlag) != 0;
            var ibodyOffset = checked((metaboxResident ? MetaboxOffset(nid) : _superblock.GetInodeOffset(nid)) + inode.OnDiskSize);
            var ibody = metaboxResident
                ? ReadMetaboxBytes(ibodyOffset, inode.XattrISize, "xattr ibody")
                : ReadBytes(ibodyOffset, inode.XattrISize, "xattr ibody");
            var prefixTable = LoadXattrPrefixTable();
            var entries = ErofsXattr.ParseInline(ibody, inode.XattrICount, prefixTable);
            result.AddRange(entries);

            var sharedIds = ErofsXattr.ParseSharedXattrIds(ibody, inode.XattrICount);
            if (sharedIds.Length > 0)
            {

                var sharedStart = _superblock.XattrBlkAddr * _blockSize;
                var sharedInMetabox = (_superblock.FeatureCompat & ErofsSuperblock.CompatSharedEaInMetabox) != 0;
                var sharedLength = Math.Min((sharedInMetabox ? _metaboxData?.Length ?? 0 : _imageSize) - sharedStart,
                    MaxMetadataComponentSize);
                if (sharedLength <= 0)
                {
                    throw new FileSystemCorruptedException("EROFS shared xattr area exceeds the image.");
                }
                if (sharedLength > 0)
                {
                    foreach (var id in sharedIds)
                    {
                        var relativeOffset = ErofsXattr.GetSharedXattrByteOffset(id);
                        if (relativeOffset > sharedLength - ErofsXattr.EntryHeaderSize)
                        {
                            throw new FileSystemCorruptedException("EROFS shared xattr offset exceeds the image.");
                        }
                        var entryOffset = checked(sharedStart + relativeOffset);
                        var header = sharedInMetabox
                            ? ReadMetaboxBytes(entryOffset, ErofsXattr.EntryHeaderSize, "shared xattr header")
                            : ReadBytes(entryOffset, ErofsXattr.EntryHeaderSize, "shared xattr header");
                        var size = ErofsXattr.EntryHeaderSize + header[0] +
                            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
                        if (size > sharedLength - relativeOffset)
                        {
                            throw new FileSystemCorruptedException("EROFS shared xattr value exceeds the image.");
                        }
                        var entryBytes = sharedInMetabox
                            ? ReadMetaboxBytes(entryOffset, size, "shared xattr entry")
                            : ReadBytes(entryOffset, size, "shared xattr entry");
                        if (ErofsXattr.TryReadEntry(entryBytes, 0, prefixTable, out var sharedEntry, out _))
                        {
                            result.Add(sharedEntry);
                        }
                        else
                        {
                            throw new FileSystemCorruptedException("Invalid EROFS shared xattr.");
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (!RequireCompleteMetadata && ex is InvalidDataException or FileSystemException or FormatException)
        {

        }

        if (RequireCompleteMetadata && result.GroupBy(entry => entry.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new FileSystemCorruptedException("Duplicate EROFS extended attribute.");
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
        var maximumLength = checked(_superblock.XattrPrefixCount * 260);
        byte[] table;
        var plain = (_superblock.FeatureCompat & 0x10) != 0;
        if (!plain && _superblock.HasMetabox)
        {
            if (_metaboxData is null || tableOffset > _metaboxData.Length)
            {
                throw new FileSystemCorruptedException("EROFS xattr prefixes exceed metabox metadata.");
            }
            table = _metaboxData.AsSpan((int)tableOffset, (int)Math.Min(maximumLength, _metaboxData.Length - tableOffset)).ToArray();
        }
        else if (!plain && _superblock.PackedNid != 0)
        {
            var packed = ReadInode(_superblock.PackedNid);
            if ((ulong)tableOffset > packed.Size)
            {
                throw new FileSystemCorruptedException("EROFS xattr prefixes exceed the packed inode.");
            }
            var length = (int)Math.Min((ulong)maximumLength, packed.Size - (ulong)tableOffset);
            using var buffer = new MemoryStream(length);
            WritePackedData((ulong)tableOffset, length, buffer);
            table = buffer.ToArray();
        }
        else
        {
            if (tableOffset < 0 || tableOffset > _imageSize)
            {
                throw new FileSystemCorruptedException("EROFS xattr prefix table exceeds the image.");
            }
            table = ReadBytes(tableOffset, (int)Math.Min(_imageSize - tableOffset, maximumLength), "xattr prefix table");
        }
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

    private UnifiedInode ReadInode(ulong nid)
    {

        if (_hasMetabox && (nid & NidMetaboxFlag) != 0)
        {
            if (_metaboxData is null || _metaboxData.Length == 0)
                throw new FileSystemCorruptedException(
                    $"Inode nid 0x{nid:X16} references the METABOX data stream, but no METABOX data was cached.");

            var slotIndex = nid & ~NidMetaboxFlag;
            var slotOffset = MetaboxOffset(nid);
            if (slotOffset < 0 || slotOffset > _metaboxData.Length - 2)
                throw new FileSystemCorruptedException(
                    $"METABOX slot index {slotIndex} (offset {slotOffset}) is out of range (metabox data size {_metaboxData.Length}).");

            var metaboxAdvise = BinaryPrimitives.ReadUInt16LittleEndian(_metaboxData.AsSpan((int)slotOffset, 2));
            var metaboxInodeSize = (metaboxAdvise & UnifiedInode.FlagExtended) != 0
                ? UnifiedInode.ExtendedSize : UnifiedInode.CompactSize;
            if (slotOffset > _metaboxData.Length - metaboxInodeSize)
                throw new FileSystemCorruptedException($"METABOX inode nid 0x{nid:X16} exceeds its data stream.");
            return ResolveTimestamp(UnifiedInode.FromBytes(
                _metaboxData.AsSpan((int)slotOffset, metaboxInodeSize), nid, _superblock.BlockSizeBits, inMetabox: true));
        }

        var offset = _superblock.GetInodeOffset(nid);
        if (offset < 0 || offset > _imageSize - 2)
        {
            throw new FileSystemCorruptedException($"Inode nid {nid} is out of range.");
        }

        var available = (int)Math.Min(UnifiedInode.ExtendedSize, _imageSize - offset);
        var data = ReadBytes(offset, available, $"Inode nid {nid}");
        var advise = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var inodeSize = (advise & UnifiedInode.FlagExtended) != 0 ? UnifiedInode.ExtendedSize : UnifiedInode.CompactSize;
        if (inodeSize > data.Length)
        {
            throw new FileSystemCorruptedException($"Inode nid {nid} exceeds the image bounds.");
        }

        return ResolveTimestamp(UnifiedInode.FromBytes(data.AsSpan(0, inodeSize), nid, _superblock.BlockSizeBits));
    }

    private long MetaboxOffset(ulong nid)
        => checked((long)(nid & ~NidMetaboxFlag) * ErofsSuperblock.CompactInodeSize);

    private bool IsMetaboxInode(ulong nid) => _hasMetabox && (nid & NidMetaboxFlag) != 0;

    private long InodeOffset(ulong nid)
        => IsMetaboxInode(nid) ? MetaboxOffset(nid) : _superblock.GetInodeOffset(nid);

    private long InodeMetadataLength(ulong nid)
        => IsMetaboxInode(nid) ? _metaboxData?.LongLength ?? 0 : _imageSize;

    private byte[] ReadInodeMetadata(ulong nid, long offset, int length, string what)
        => IsMetaboxInode(nid) ? ReadMetaboxBytes(offset, length, what) : ReadBytes(offset, length, what);

    private byte[] ReadMetaboxBytes(long offset, int length, string what)
        => ReadMetaboxSpan(offset, length, what).ToArray();

    /// <summary>
    /// Returns the requested METABOX slice without copying it. The span is valid only while the
    /// parser is alive and must not be retained past the current operation; callers that need an
    /// owned buffer use <see cref="ReadMetaboxBytes"/> or copy into their own destination.
    /// <para>返回请求的 METABOX 切片而不复制。该 span 仅在解析器存活期间有效，不得跨当前操作保留；
    /// 需要独占缓冲的调用方使用 <see cref="ReadMetaboxBytes"/> 或复制到自己的目标缓冲。</para>
    /// </summary>
    private ReadOnlySpan<byte> ReadMetaboxSpan(long offset, int length, string what)
    {
        _readCancellation.ThrowIfCancellationRequested();
        if (_metaboxData is null || offset < 0 || length < 0
            || offset > _metaboxData.Length || length > _metaboxData.Length - offset)
        {
            throw new FileSystemCorruptedException($"{what} exceeds the METABOX data stream.");
        }
        return _metaboxData.AsSpan((int)offset, length);
    }

    private int DirectoryBlockSize => _superblock.DirBlkBits == 0 ? _blockSize : 1 << _superblock.DirBlkBits;

    private List<ErofsDirectoryEntry> ReadDirectory(ulong nid, UnifiedInode inode)
    {
        var dirBlockSize = DirectoryBlockSize;
        if (inode.DataLayout is not (UnifiedDataLayout.Flat or UnifiedDataLayout.Inline))
        {

            throw new FileSystemUnsupportedFeatureException($"Unsupported directory data layout {inode.DataLayout} for nid {nid}.");
        }

        var flatStart = checked(inode.AddressField() * _blockSize);
        var length = checked((long)inode.Size);
        var flatLength = inode.DataLayout == UnifiedDataLayout.Inline
            ? length / _blockSize * _blockSize : length;
        if (flatLength > 0) CheckRange(flatStart, flatLength, "Directory data");
        if (length > MaxMetadataComponentSize)
        {
            throw new FileSystemCorruptedException(
                $"Directory data size {length} exceeds the limit of {MaxMetadataComponentSize} bytes.");
        }

        var result = new List<ErofsDirectoryEntry>();
        for (var done = 0L; done < length; done += dirBlockSize)
        {
            var blockLength = (int)Math.Min(dirBlockSize, length - done);
            var inline = done >= flatLength;
            var offset = inline
                ? checked(((_hasMetabox && (nid & NidMetaboxFlag) != 0)
                    ? MetaboxOffset(nid) : _superblock.GetInodeOffset(nid)) + inode.InlineDataOffset + done - flatLength)
                : checked(flatStart + done);
            var block = inline && _hasMetabox && (nid & NidMetaboxFlag) != 0
                ? ReadMetaboxBytes(offset, blockLength, "Directory data")
                : ReadBytes(offset, blockLength, "Directory data");
            foreach (var entry in ParseDirectoryData(block, dirBlockSize, blockLength))
            {
                _readCancellation.ThrowIfCancellationRequested();
                if ((long)result.Count >= (long)_maximumEntries + 2)
                {
                    throw new FileSystemCorruptedException("EROFS directory block entries exceed the configured budget.");
                }
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses one directory block with the compact dirent semantics: a directory block is exactly a dirent
    /// array followed by the contiguous name
    /// area, and a malformed nameoff is corruption rather than an alternate layout.
    /// <para>按 compact dirent 语义解析单个目录块：目录块就是 dirent 数组后接连续
    /// 名称区，非法的 nameoff 属损坏而不是另一种布局。</para>
    /// </summary>
    /// <param name="block">Raw directory block bytes, at least one dirent header long.
    /// <para>目录块的原始字节，长度至少为一条 dirent 头部。</para></param>
    /// <param name="blockSize">Directory block size in bytes, unused by the compact layout because the
    /// payload length is derived from the dirent count.
    /// <para>目录块字节大小；紧凑布局不使用该值，因为负载长度由 dirent 数量推导。</para></param>
    /// <param name="totalLength">Length of the payload this block belongs to, unused for the same reason.
    /// <para>该块所属负载的长度；同样不使用。</para></param>
    /// <returns>The entries decoded from <paramref name="block"/>, in on-disk order.
    /// <para>从 <paramref name="block"/> 解码出的条目，按磁盘顺序。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The block is truncated, a nameoff is not contiguous
    /// with the previous entry, a nameoff carries an undefined bit, or a name length exceeds the block.
    /// <para>块被截断、nameoff 与前一条目不连续、nameoff 携带未定义的位，或名称长度超出该块。</para></exception>
    private static List<ErofsDirectoryEntry> ParseDirectoryData(ReadOnlySpan<byte> block, int blockSize, long totalLength)
    {
        _ = blockSize;
        _ = totalLength;
        return ErofsDirectoryEntry.ParseBlock(block);
    }

}