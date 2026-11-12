using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;

/// <summary>
/// Container version of an IMAGEWTY image, taken from the header version word and selecting both the
/// header field layout and the per-file record layout.
/// <para>IMAGEWTY 镜像的容器版本，取自头部版本字，同时决定头部字段布局与每个文件记录的布局。</para>
/// </summary>
public enum ImageWtyVersion : uint
{
    /// <summary>Version 1: 32-bit lengths and payload offsets. <para>版本 1：32 位长度与负载偏移。</para></summary>
    V1 = 0x100,

    /// <summary>Version 3: 64-bit image size, file length and payload offset. <para>版本 3：64 位镜像大小、文件长度与负载偏移。</para></summary>
    V3 = 0x300
}

/// <summary>
/// One file record of an IMAGEWTY container: its name, declared types, absolute stored-data range,
/// length pair, raw attribute and checksum words, and the vendor role derived from its types.
/// <para>IMAGEWTY 容器中的一个文件记录：名称、声明的类型、绝对已存储数据区间、长度对、原始属性与校验字，以及由其类型推导的厂商角色。</para>
/// </summary>
/// <param name="Name">Entry name from the 256-byte name field. <para>来自 256 字节名称字段的条目名。</para></param>
/// <param name="MainType">Declared main type, right-padded with NUL or spaces in the record. <para>声明的主类型，在记录中以 NUL 或空格右填充。</para></param>
/// <param name="SubType">Declared sub type, right-padded with NUL or spaces in the record. <para>声明的子类型，在记录中以 NUL 或空格右填充。</para></param>
/// <param name="Offset">Absolute offset of the stored payload inside the container. <para>容器内已存储负载的绝对偏移。</para></param>
/// <param name="StoredLength">Stored payload size in bytes, including any padding. <para>已存储负载的字节大小，含填充。</para></param>
/// <param name="OriginalLength">Declared unpacked size; zero means the record declares none. <para>声明的解包后大小；为 0 时表示记录未声明。</para></param>
/// <param name="Attributes">Raw attribute word, reported uninterpreted. <para>原始属性字，原样报告不做解释。</para></param>
/// <param name="Checksum">Raw checksum word; the format does not define its coverage, so it is reported uninterpreted. <para>原始校验字；格式未定义其覆盖范围，因此原样报告。</para></param>
/// <param name="Item">Vendor role resolved from the declared types. <para>由声明类型解析出的厂商角色。</para></param>
public sealed record ImageWtyEntry(string Name, string MainType, string SubType,
    long Offset, long StoredLength, long OriginalLength,
    uint Attributes, uint Checksum, AllwinnerImageItem Item);

/// <summary>
/// Validated metadata of an IMAGEWTY container, including whether its records were RC6-encrypted.
/// <para>已校验的 IMAGEWTY 容器元数据，并标明其记录是否经 RC6 加密。</para>
/// </summary>
/// <param name="Version">Container version driving every field layout. <para>决定全部字段布局的容器版本。</para></param>
/// <param name="HeaderSize">Declared header record size. <para>声明的头部记录大小。</para></param>
/// <param name="Attribute">Header attribute word. <para>头部属性字。</para></param>
/// <param name="ContainerVersion">Container format version word, normally <c>0x100234</c>. <para>容器格式版本字，通常为 <c>0x100234</c>。</para></param>
/// <param name="ImageSize">Declared total image length; version 3 combines the low and high words. <para>声明的镜像总长度；版本 3 由低字与高字合并。</para></param>
/// <param name="Alignment">Version 1 alignment word or the version 3 alignment word. <para>版本 1 的对齐字或版本 3 的对齐字。</para></param>
/// <param name="Vid">Declared USB vendor id. <para>声明的 USB 厂商 id。</para></param>
/// <param name="Pid">Declared USB peripheral id. <para>声明的 USB 外设 id。</para></param>
/// <param name="HardwareId">Declared hardware id. <para>声明的硬件 id。</para></param>
/// <param name="FirmwareId">Declared firmware id. <para>声明的固件 id。</para></param>
/// <param name="FileCount">Declared record count. <para>声明的记录数量。</para></param>
/// <param name="TableOffset">Absolute offset of the file record table. <para>文件记录表的绝对偏移。</para></param>
/// <param name="IsEncrypted">Whether the header, file records and payloads are RC6-encrypted. <para>头部、文件记录与负载是否经 RC6 加密。</para></param>
/// <param name="Entries">Validated records in table order. <para>按表顺序排列的已校验记录。</para></param>
public sealed record ImageWtyHeader(ImageWtyVersion Version, uint HeaderSize, uint Attribute,
    uint ContainerVersion, long ImageSize, uint Alignment, uint Vid, uint Pid,
    uint HardwareId, uint FirmwareId, uint FileCount, long TableOffset, bool IsEncrypted,
    IReadOnlyList<ImageWtyEntry> Entries);

/// <summary>
/// Bounded reader for Allwinner IMAGEWTY boot containers. The header is one 1024-byte record whose
/// 32-byte prefix carries the <c>IMAGEWTY</c> magic plus the header version, header size, attribute,
/// container version and image length; the version word then selects the remaining field offsets and
/// the 1024-byte file record layout, with version 3 widening the length and offset fields to 64 bits.
/// A missing magic is retried once through RC6 decryption, and every count, offset and length is
/// bounded before use, with all reads honoring the cancellation token.
/// <para>Allwinner IMAGEWTY 启动容器的有界读取器。头部是一条 1024 字节记录，其 32 字节前缀携带
/// <c>IMAGEWTY</c> 魔数以及头版本、头大小、属性、容器版本与镜像长度；版本字随后决定其余字段偏移与
/// 1024 字节文件记录布局，版本 3 把长度与偏移字段放宽到 64 位。魔数缺失时会经 RC6 解密重试一次；
/// 所有计数、偏移与长度在使用前都做上限校验，全部读取贯穿取消令牌。</para>
/// </summary>
internal static class AllwinnerImageWtyFormat
{
    /// <summary>Byte length of the container header record. <para>容器头记录的字节长度。</para></summary>
    public const int HeaderRecordSize = 1024;

    /// <summary>Byte length of one file record; both versions use fixed 1024-byte records.
    /// <para>一条文件记录的字节长度；两个版本都使用固定的 1024 字节记录。</para></summary>
    public const int FileHeaderSize = 1024;

    /// <summary>Hard ceiling on the declared file count. <para>声明文件数量的硬性上限。</para></summary>
    public const int MaximumEntries = 4096;

    private const int MinimumHeaderBytes = 96;
    private const int HeaderVersionOffset = 8;
    private const int ImageSizeLowOffset = 24;
    private const int ImageSizeHighOffset = 28;
    private const int MainTypeOffset = 8;
    private const int MainTypeLength = 8;
    private const int SubTypeOffset = 16;
    private const int SubTypeLength = 16;
    private const int NameLength = 256;
    private const int AttributesOffset = 32;

    private static readonly RecordLayout V1Records = new(AttributesOffset, 52, 36, 40, 44, 48, Wide: false);
    private static readonly RecordLayout V3Records = new(AttributesOffset, 36, 292, 300, 308, 380, Wide: true);
    private static readonly SearchValues<char> InvalidFileNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>
    /// Reports whether the container starts with the plaintext <c>IMAGEWTY</c> magic.
    /// <para>判断容器是否以明文 <c>IMAGEWTY</c> 魔数开头。</para>
    /// </summary>
    /// <param name="header">Leading bytes of the file, at least 8. <para>文件起始字节，至少 8 个。</para></param>
    /// <returns><see langword="true"/> when the magic matches. <para>魔数匹配时为 <see langword="true"/>。</para></returns>
    public static bool HasPlainMagic(ReadOnlySpan<byte> header) => header.Length >= 8
        && header[..8].SequenceEqual("IMAGEWTY"u8);

    /// <summary>
    /// Reads and validates the header record and the file table of a container.
    /// <para>读取并校验容器的头部记录与文件表。</para>
    /// </summary>
    /// <param name="input">Readable, seekable container stream; position is restored on return.
    /// <para>可读、可寻址的容器流；返回时恢复位置。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>The validated header with its records. <para>已校验的头部及其记录。</para></returns>
    /// <exception cref="ArgumentException">The stream cannot be read or cannot seek.
    /// <para>流不可读或不可寻址。</para></exception>
    /// <exception cref="InvalidDataException">The header is truncated or carries an unknown version, the magic is absent both before and after RC6 decryption, the record count or table range is invalid, or a record declares an out-of-range payload.
    /// <para>头部被截断或版本未知、魔数在 RC6 解密前后都不匹配、记录数量或表区间无效，或记录声明了越界负载。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public static ImageWtyHeader Read(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException("IMAGEWTY reading requires a readable, seekable stream.", nameof(input));
        }

        var original = input.Position;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = input.Length;
            if (length < MinimumHeaderBytes)
            {
                throw new InvalidDataException("The IMAGEWTY header is truncated.");
            }

            var headerLength = (int)Math.Min(length, HeaderRecordSize);
            var header = new byte[headerLength];
            input.Position = 0;
            input.ReadExactly(header);

            var encrypted = false;
            if (!HasPlainMagic(header))
            {
                AllwinnerImageWtyCrypto.DecryptBlocks(ImageWtyCipherScope.Header, header);
                if (!HasPlainMagic(header))
                {
                    throw new InvalidDataException(
                        "The IMAGEWTY magic is absent both before and after RC6 decryption.");
                }

                encrypted = true;
            }

            var version = ParseVersion(ReadUInt32(header, HeaderVersionOffset));
            var metadataShift = version == ImageWtyVersion.V3 ? 4 : 0;
            var pid = ReadUInt32(header, 32 + metadataShift);
            var vid = ReadUInt32(header, 36 + metadataShift);
            var hardwareId = ReadUInt32(header, 40 + metadataShift);
            var firmwareId = ReadUInt32(header, 44 + metadataShift);
            var fileCount = ReadUInt32(header, 56 + metadataShift);
            var tableOffset = ReadUInt32(header, 60 + metadataShift);
            var alignment = ReadUInt32(header, ImageSizeHighOffset + metadataShift);
            var imageSize = version == ImageWtyVersion.V3
                ? (long)(((ulong)ReadUInt32(header, ImageSizeHighOffset) << 32) | ReadUInt32(header, ImageSizeLowOffset))
                : ReadUInt32(header, ImageSizeLowOffset);

            if (fileCount > MaximumEntries)
            {
                throw new InvalidDataException(
                    $"The IMAGEWTY file count {fileCount} is outside the supported range.");
            }

            var tableEnd = (long)tableOffset + ((long)fileCount * FileHeaderSize);
            if (tableOffset < MinimumHeaderBytes || tableEnd > length)
            {
                throw new InvalidDataException("The IMAGEWTY file table escapes the container.");
            }

            var layout = version == ImageWtyVersion.V3 ? V3Records : V1Records;
            var entries = new List<ImageWtyEntry>((int)fileCount);
            var record = new byte[FileHeaderSize];
            for (var index = 0; index < fileCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                input.Position = tableOffset + ((long)index * FileHeaderSize);
                input.ReadExactly(record);
                if (encrypted)
                {
                    AllwinnerImageWtyCrypto.DecryptBlocks(ImageWtyCipherScope.FileHeader, record);
                }

                var storedLength = ReadSize(record, layout.StoredLength, layout.Wide, index, "stored length");
                var originalLength = ReadSize(record, layout.OriginalLength, layout.Wide, index, "original length");
                var payloadOffset = ReadSize(record, layout.Payload, layout.Wide, index, "payload offset");
                ValidateEntryRange(index, payloadOffset, storedLength, length);
                var mainType = ReadFixedAscii(record, MainTypeOffset, MainTypeLength);
                var subType = ReadFixedAscii(record, SubTypeOffset, SubTypeLength);
                entries.Add(new ImageWtyEntry(
                    ReadFixedAscii(record, layout.Name, NameLength),
                    mainType,
                    subType,
                    payloadOffset,
                    storedLength,
                    originalLength,
                    ReadUInt32(record, layout.Attributes),
                    ReadUInt32(record, layout.Checksum),
                    AllwinnerImageItemTable.Resolve(mainType, subType)));
            }

            return new ImageWtyHeader(
                version,
                ReadUInt32(header, 12),
                ReadUInt32(header, 16),
                ReadUInt32(header, 20),
                imageSize,
                alignment,
                vid,
                pid,
                hardwareId,
                firmwareId,
                fileCount,
                tableOffset,
                encrypted,
                entries);
        }
        finally
        {
            input.Position = original;
        }
    }

    /// <summary>
    /// Copies one validated entry's payload into the destination stream, decrypting it first when the
    /// container is encrypted and truncating to the declared original length when that is smaller.
    /// <para>将单个已校验条目的负载复制到目标流；容器加密时先解密，并在声明的原始长度更小时截断到该长度。</para>
    /// </summary>
    /// <param name="input">Source container stream. <para>源容器流。</para></param>
    /// <param name="entry">Entry produced by <see cref="Read"/>. <para>由 <see cref="Read"/> 产生的条目。</para></param>
    /// <param name="output">Destination stream receiving the payload. <para>接收负载的目标流。</para></param>
    /// <param name="decrypt">Whether the payload is RC6-encrypted. <para>负载是否经 RC6 加密。</para></param>
    /// <param name="cancellationToken">Cancels the copy. <para>取消本次复制。</para></param>
    /// <returns>The number of bytes written. <para>写入的字节数。</para></returns>
    /// <exception cref="ArgumentNullException">Any argument is null. <para>任一参数为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The entry offset or length is negative.
    /// <para>条目偏移或长度为负。</para></exception>
    /// <exception cref="EndOfStreamException">The container ends before the declared payload is complete.
    /// <para>容器在声明的负载完成前结束。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public static long ExtractEntry(Stream input, ImageWtyEntry entry, Stream output, bool decrypt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(output);
        if (entry.Offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "The entry offset must not be negative.");
        }

        if (entry.StoredLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "The entry length must not be negative.");
        }

        var copyLength = EffectiveLength(entry);
        using var range = new BoundedReadStream(input, entry.Offset, entry.StoredLength, leaveOpen: true);
        if (decrypt)
        {
            AllwinnerImageWtyCrypto.DecryptCopy(range, output, entry.StoredLength, copyLength, cancellationToken);
        }
        else
        {
            StreamCopy.CopyExactly(range, output, copyLength, cancellationToken);
        }

        return copyLength;
    }

    /// <summary>
    /// Returns the number of payload bytes an entry contributes to an unpacked file: its stored
    /// length, truncated to the original length only when that length is declared and smaller.
    /// <para>返回条目对解包文件贡献的负载字节数：即其已存储长度，仅当原始长度已声明且更小时截断到该长度。</para>
    /// </summary>
    /// <param name="entry">Entry to measure. <para>待度量的条目。</para></param>
    /// <returns>Byte count written for the entry. <para>为该条目写出的字节数。</para></returns>
    public static long EffectiveLength(ImageWtyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.OriginalLength > 0 && entry.OriginalLength < entry.StoredLength
            ? entry.OriginalLength
            : entry.StoredLength;
    }

    /// <summary>
    /// Copies one 16-byte-aligned segment of an entry payload into the output stream, decrypting complete
    /// blocks in place when the container is encrypted. The content cipher is block-independent, so a segment
    /// taken at a block-aligned offset decrypts exactly like the same bytes inside a full extraction; the
    /// caller keeps segment offsets at multiples of the 16-byte block.
    /// <para>把一个条目负载的 16 字节对齐段复制到输出流；容器加密时对完整分组就地解密。内容密钥逐分组独立，
    /// 从分组对齐偏移取得的段与完整提取中的相同字节解密结果一致；调用方应让段偏移保持 16 字节分组对齐。</para>
    /// </summary>
    /// <param name="input">Container stream the entry was read from. <para>读取条目所用容器流。</para></param>
    /// <param name="entry">Entry to extract from. <para>提取来源条目。</para></param>
    /// <param name="offset">Segment offset from the entry payload start; a multiple of the block size.
    /// <para>段相对条目负载起点的偏移；须为分组大小倍数。</para></param>
    /// <param name="count">Bytes to copy; the segment must stay inside the effective payload.
    /// <para>复制的字节数；段必须留在有效负载之内。</para></param>
    /// <param name="output">Destination stream. <para>目标流。</para></param>
    /// <param name="decrypt">Whether the container payloads are RC6-encrypted.
    /// <para>容器负载是否经 RC6 加密。</para></param>
    /// <param name="cancellationToken">Cancels the copy. <para>取消本次复制。</para></param>
    /// <returns>A task that produces the number of bytes written, which is <paramref name="count"/>.
    /// <para>产生写出的字节数（即 <paramref name="count"/>）的任务。</para></returns>
    /// <exception cref="ArgumentNullException">Any argument is null. <para>任一参数为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The offset or count is negative, or the segment leaves the
    /// effective payload. <para>偏移或长度为负，或段离开有效负载范围。</para></exception>
    /// <exception cref="EndOfStreamException">The container ends before the segment is complete.
    /// <para>容器在段完成前结束。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public static async Task<long> ExtractEntrySegmentAsync(Stream input, ImageWtyEntry entry, long offset,
        long count, Stream output, bool decrypt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(output);
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The segment offset must not be negative.");
        }

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "The segment length must not be negative.");
        }

        if (offset + count > EffectiveLength(entry))
        {
            throw new ArgumentOutOfRangeException(nameof(count), "The segment leaves the entry payload.");
        }

        if (count == 0)
        {
            return 0;
        }

        using var range = new BoundedReadStream(input, entry.Offset + offset, entry.StoredLength - offset,
            leaveOpen: true);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var remaining = count;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wanted = (int)Math.Min(buffer.Length, remaining);
                await range.ReadExactlyAsync(buffer, 0, wanted, cancellationToken).ConfigureAwait(false);
                if (decrypt)
                {
                    AllwinnerImageWtyCrypto.DecryptBlocks(ImageWtyCipherScope.Content, buffer.AsSpan(0, wanted));
                }

                await output.WriteAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                remaining -= wanted;
            }

            return count;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Builds a collision-safe local file name for an entry, keeping only the base name.
    /// <para>为条目构建防碰撞的本地文件名，仅保留基础名。</para>
    /// </summary>
    /// <param name="entry">The container entry. <para>容器条目。</para></param>
    /// <param name="index">Zero-based table position used when the name is empty or reserved.
    /// <para>名称为空或为保留名时使用的零基表序号。</para></param>
    /// <returns>A file name without directory separators or traversal.
    /// <para>不含目录分隔符与路径上溯的文件名。</para></returns>
    public static string SafeEntryFileName(ImageWtyEntry entry, int index)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var normalized = entry.Name.Replace('\\', '/');
        var baseName = normalized[(normalized.LastIndexOf('/') + 1)..];
        var cleaned = new string([.. baseName.Where(c => !InvalidFileNameChars.Contains(c) && c > 0x1F)]).TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned is "." or "..")
        {
            cleaned = $"image_{index}.bin";
        }

        return cleaned;
    }

    private static ImageWtyVersion ParseVersion(uint headerVersion) => headerVersion switch
    {
        0x100 => ImageWtyVersion.V1,
        0x300 => ImageWtyVersion.V3,
        _ => throw new InvalidDataException($"Unknown IMAGEWTY header version 0x{headerVersion:X}.")
    };

    private static long ReadSize(byte[] record, int offset, bool wide, int index, string field)
    {
        if (!wide)
        {
            return ReadUInt32(record, offset);
        }

        var value = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(offset));
        return value > long.MaxValue
            ? throw new InvalidDataException($"IMAGEWTY entry {index} declares an out-of-range {field}.")
            : (long)value;
    }

    private static void ValidateEntryRange(int index, long payloadOffset, long storedLength, long containerLength)
    {
        if (storedLength == 0)
        {
            if (payloadOffset > containerLength)
            {
                throw new InvalidDataException($"IMAGEWTY entry {index} offset escapes the container.");
            }

            return;
        }

        if (storedLength > containerLength || payloadOffset > containerLength - storedLength)
        {
            throw new InvalidDataException($"IMAGEWTY entry {index} points outside the container.");
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static string ReadFixedAscii(ReadOnlySpan<byte> data, int offset, int length)
    {
        var slice = data.Slice(offset, length);
        var terminator = slice.IndexOf((byte)0);
        var end = terminator < 0 ? length : terminator;
        return Encoding.ASCII.GetString(slice[..end]).TrimEnd(' ');
    }

    private readonly record struct RecordLayout(int Attributes, int Name, int StoredLength, int OriginalLength,
        int Payload, int Checksum, bool Wide);
}