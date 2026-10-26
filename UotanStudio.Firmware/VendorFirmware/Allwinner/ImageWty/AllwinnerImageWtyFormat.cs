using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;

/// <summary>Layout of the entry table inside an IMAGEWTY container.
/// <para>IMAGEWTY 容器内条目表的布局。</para></summary>
public enum ImageWtyLayout
{
    /// <summary>Official new layout: the item table starts at the <c>itemoffset</c> declared in the header and each item is a 1024-byte <c>ImageItem_t</c> record.
    /// <para>官方新版布局：条目表起始于头部声明的 <c>itemoffset</c>，每条为 1024 字节的 <c>ImageItem_t</c> 记录。</para></summary>
    NewFormat = 0,

    /// <summary>Classic layout: V1/V3 header metadata followed by a variable-length file-header chain starting at 0x400 that is advanced by each record's total header size.
    /// <para>经典布局：V1/V3 头元数据之后是自 0x400 起、按每条记录总头大小推进的变长文件头链。</para></summary>
    Classic = 1,
}

/// <summary>One file record of an IMAGEWTY container: name, absolute stored-data range, declared types, the raw attribute/checksum words and the layout-specific size declarations.
/// <para>IMAGEWTY 容器中的一个文件记录：名称、绝对已存储数据区间、声明的类型、原始属性/校验字，以及随布局变化的长度声明。</para></summary>
/// <param name="Name">Entry name from the name field of its record.<para>来自记录名称字段的条目名。</para></param>
/// <param name="MainType">Declared main type string.<para>声明的主类型字符串。</para></param>
/// <param name="SubType">Declared sub type string.<para>声明的子类型字符串。</para></param>
/// <param name="Offset">Absolute offset of the stored payload inside the container.<para>容器内已存储负载的绝对偏移。</para></param>
/// <param name="Length">Stored payload size in bytes; this is the extent copied by <see cref="AllwinnerImageWtyFormat.ExtractEntry"/>.<para>已存储负载的字节大小；即 <see cref="AllwinnerImageWtyFormat.ExtractEntry"/> 复制的区间。</para></param>
/// <param name="Attributes">Raw attribute word; its bit semantics are not standardized, so the value is reported uninterpreted.<para>原始属性字；其位义未标准化，原样报告不做解释。</para></param>
/// <param name="Checksum">Raw checksum word; its coverage is not enforced on read.<para>原始校验字；读取时不强制校验其覆盖范围。</para></param>
/// <param name="Layout">Container layout this record was parsed from.<para>解析出该记录的容器布局。</para></param>
/// <param name="FileLength">Declared actual file size: the 64-bit file length field of the new layout or the original length of the classic layout; it is only checked to fit a signed 64-bit value and may legitimately exceed the container for compressed or sparse entries.<para>声明的文件实际大小：新版布局为 64 位文件长度字段，经典布局为原始长度；只校验其可容纳于有符号 64 位值，对压缩或稀疏条目允许超过容器大小。</para></param>
/// <param name="DataLength">Declared in-image data size: the new-layout data length field or the classic stored length; always equals <paramref name="Length"/>.<para>镜像内声明的数据大小：新版布局为数据长度字段，经典布局为存储长度；恒等于 <paramref name="Length"/>。</para></param>
public sealed record ImageWtyEntry(string Name, string MainType, string SubType, long Offset, long Length,
    uint Attributes, uint Checksum,
    ImageWtyLayout Layout = ImageWtyLayout.NewFormat,
    long FileLength = 0,
    long DataLength = 0);

/// <summary>Validated header metadata of an IMAGEWTY container.
/// <para>已校验的 IMAGEWTY 容器头元数据。</para></summary>
/// <param name="HeaderVersion">Container header version, <c>0x100</c> or <c>0x300</c>.
/// <para>容器头版本，取 <c>0x100</c> 或 <c>0x300</c>。</para></param>
/// <param name="HeaderSize">Declared header size word.<para>声明的头部大小字。</para></param>
/// <param name="RamBase">Classic-layout RAM base word; the new layout uses this word as the head attribute.
/// <para>经典布局的 RAM 基址字；新版布局将该字用作头属性。</para></param>
/// <param name="Version">Container version word.<para>容器版本字。</para></param>
/// <param name="ImageSize">Low word of the declared total image length.<para>声明的镜像总长度低位字。</para></param>
/// <param name="ImageHeaderSize">Classic-layout combined header size word; the new layout uses this word as the high word of the total image length.
/// <para>经典布局的合并头部大小字；新版布局将该字用作镜像总长度的高位字。</para></param>
/// <param name="Vid">Declared USB vendor id.<para>声明的 USB 厂商 id。</para></param>
/// <param name="Pid">Declared USB peripheral id.<para>声明的 USB 外设 id。</para></param>
/// <param name="HardwareId">Declared hardware id.<para>声明的硬件 id。</para></param>
/// <param name="FirmwareId">Declared firmware id.<para>声明的固件 id。</para></param>
/// <param name="Entries">Validated entry records in table order.<para>按表顺序排列的已校验条目。</para></param>
/// <param name="Layout">Entry-table layout the container was parsed with.<para>解析容器时使用的条目表布局。</para></param>
public sealed record ImageWtyHeader(uint HeaderVersion, uint HeaderSize, uint RamBase, uint Version,
    uint ImageSize, uint ImageHeaderSize, uint Vid, uint Pid, uint HardwareId, uint FirmwareId,
    IReadOnlyList<ImageWtyEntry> Entries, ImageWtyLayout Layout = ImageWtyLayout.NewFormat);

/// <summary>
/// Bounded reader for Allwinner IMAGEWTY boot containers. The 32-byte fixed header carries the
/// <c>IMAGEWTY</c> magic and six little-endian words (header version, header size, head attribute/RAM base,
/// image version, image length low word, image length high word); a magic mismatch marks the container as
/// encrypted and encrypted payloads are not unpacked. After the version body (<c>0x100</c> or <c>0x300</c>)
/// the entry-table layout is probed: when the item count at offset 60 and the item table offset at offset 64
/// are consistent with the container size, the official new layout is used (pid/vid/hardware/firmware ids at
/// offsets 36-48, the item table at <c>itemoffset</c>, each entry a 1024-byte <c>ImageItem_t</c> record with
/// main/sub type strings, a 256-byte name and 64-bit data length/file length/offset fields); otherwise the
/// classic layout is attempted (V1/V3 body metadata and a variable-length file-header chain starting at 0x400
/// that is advanced by each record's total header size). Counts, offsets and lengths are bounded before use
/// and all reads honor the cancellation token.
/// <para>Allwinner IMAGEWTY 启动容器的有界读取器。32 字节固定头携带 <c>IMAGEWTY</c> 魔数与六个小端 u32 字
/// （头版本、头大小、头属性/RAM 基址、镜像版本、镜像长度低位、镜像长度高位）；魔数不匹配表示容器已加密，
/// 加密负载不在本处解包。读取版本主体（<c>0x100</c> 或 <c>0x300</c>）后探测条目表布局：当偏移 60 的条目计数
/// 与偏移 64 的条目表偏移和容器大小一致时采用官方新版布局（pid/vid/硬件 id/固件 id 位于偏移 36-48，条目表
/// 位于 <c>itemoffset</c>，每条为 1024 字节 <c>ImageItem_t</c> 记录，含主/子类型字符串、256 字节名称与 64 位
/// 数据长度/文件长度/偏移字段）；否则尝试经典布局（V1/V3 主体元数据，配合自 0x400 起、按每条记录总头大小
/// 推进的变长文件头链）。所有计数、偏移与长度先做上限校验再使用，全部读取贯穿取消令牌。</para>
/// </summary>
internal static class AllwinnerImageWtyFormat
{
    /// <summary>Byte length of one new-format item table record; classic file headers are variable-length.
    /// <para>新版条目表记录的字节长度；经典文件头为变长。</para></summary>
    public const int EntryRecordSize = 1024;

    /// <summary>Header version constant for the V1 body layout. <para>V1 主体布局的头版本常量。</para></summary>
    public const uint HeaderVersionV1 = 0x100;

    /// <summary>Header version constant for the V3 body layout. <para>V3 主体布局的头版本常量。</para></summary>
    public const uint HeaderVersionV3 = 0x300;

    /// <summary>Hard ceiling on the embedded file count. <para>内嵌文件计数的硬性上限。</para></summary>
    public const int MaximumEntries = 4096;

    private const int FixedHeaderSize = 32;

    private const int NewLayoutPidOffset = 36;
    private const int NewLayoutVidOffset = 40;
    private const int NewLayoutHardwareIdOffset = 44;
    private const int NewLayoutFirmwareIdOffset = 48;
    private const int NewLayoutItemCountOffset = 60;
    private const int NewLayoutItemTableOffset = 64;
    private const uint MinimumNewItemTableOffset = 0x300;

    private const int NewItemMainTypeOffset = 8;
    private const int NewItemMainTypeLength = 8;
    private const int NewItemSubTypeOffset = 16;
    private const int NewItemSubTypeLength = 16;
    private const int NewItemAttributesOffset = 32;
    private const int NewItemNameOffset = 36;
    private const int NewItemNameLength = 256;
    private const int NewItemDataLengthOffset = 292;
    private const int NewItemFileLengthOffset = 300;
    private const int NewItemPayloadOffset = 308;
    private const int NewItemChecksumOffset = 380;

    private const uint ClassicFileHeaderStart = 0x400;
    private const int ClassicFileNameLengthOffset = 0;
    private const int ClassicTotalHeaderSizeOffset = 4;
    private const int ClassicMainTypeOffset = 8;
    private const int ClassicMainTypeLength = 8;
    private const int ClassicSubTypeOffset = 16;
    private const int ClassicSubTypeLength = 16;
    private const int ClassicStoredLengthOffset = 32;
    private const int ClassicOriginalLengthOffset = 36;
    private const int ClassicPayloadOffset = 40;
    private const int ClassicRecordPrefixSize = 44;
    private const uint MinimumClassicRecordSize = 84;
    private const uint MaximumClassicRecordSize = 4096;
    private const uint MaximumClassicFileNameLength = 256;

    /// <summary>
    /// Reports whether the container starts with the plaintext <c>IMAGEWTY</c> magic; the reference
    /// readers treat any mismatch on an otherwise-expected container as an encryption flag.
    /// <para>判断容器是否以明文 <c>IMAGEWTY</c> 魔数开头；参考读取器将在预期容器上出现魔数不符时视为加密标志。</para>
    /// </summary>
    /// <param name="header">Leading bytes of the file, at least 8. <para>文件起始字节，至少 8 个。</para></param>
    /// <returns><see langword="true"/> when the magic matches. <para>魔数匹配时为 <see langword="true"/>。</para></returns>
    public static bool HasPlainMagic(ReadOnlySpan<byte> header) => header.Length >= 8
        && header[..8].SequenceEqual("IMAGEWTY"u8);

    /// <summary>Reads and validates the header, version body and entry table from a seekable stream, dispatching between the new-format and classic layouts.
    /// <para>从可寻址流读取并校验头部、版本主体与条目表，在新版与经典布局间分派。</para></summary>
    /// <param name="input">Readable, seekable container stream; position is restored on return.
    /// <para>可读、可寻址的容器流；返回时恢复位置。</para></param>
    /// <param name="cancellationToken">Cancels the read.<para>取消本次读取。</para></param>
    /// <returns>The validated header with its entry table.<para>已校验的头部及其条目表。</para></returns>
    /// <exception cref="ArgumentException">The stream cannot be read or cannot seek.
    /// <para>流不可读或不可寻址。</para></exception>
    /// <exception cref="InvalidDataException">The magic is missing (encrypted container), the header version is unknown, the header or version body is truncated, neither entry-table layout matches the container, or an entry range is invalid.
    /// <para>魔数缺失（加密容器）、头版本未知、头部或版本主体被截断、两种条目表布局均不匹配，或条目区间无效。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
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
            if (input.Length < FixedHeaderSize)
            {
                throw new InvalidDataException("The IMAGEWTY header is truncated.");
            }

            Span<byte> fixedHeader = stackalloc byte[FixedHeaderSize];
            input.Position = 0;
            input.ReadExactly(fixedHeader);
            if (!HasPlainMagic(fixedHeader))
            {
                throw new InvalidDataException("The IMAGEWTY magic is absent; encrypted containers are not unpacked.");
            }

            var headerVersion = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[8..]);
            var bodyLength = headerVersion switch
            {
                HeaderVersionV1 => 48,
                HeaderVersionV3 => 52,
                _ => throw new InvalidDataException($"Unknown IMAGEWTY header version 0x{headerVersion:X}.")
            };
            if (input.Length < FixedHeaderSize + bodyLength)
            {
                throw new InvalidDataException("The IMAGEWTY version body is truncated.");
            }

            var body = new byte[bodyLength];
            input.ReadExactly(body, 0, bodyLength);
            var itemCount = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(NewLayoutItemCountOffset - FixedHeaderSize));
            var itemTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(NewLayoutItemTableOffset - FixedHeaderSize));
            cancellationToken.ThrowIfCancellationRequested();
            if (IsNewLayoutApplicable(itemCount, itemTableOffset, input.Length))
            {
                return ReadNewLayout(input, fixedHeader, body, headerVersion, itemCount, itemTableOffset, cancellationToken);
            }

            try
            {
                return ReadClassicLayout(input, fixedHeader, body, headerVersion, cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    "The IMAGEWTY container matches neither the new-format nor the classic entry layout; encrypted containers are not unpacked.",
                    exception);
            }
        }
        finally
        {
            input.Position = original;
        }
    }

    /// <summary>
    /// Copies one validated entry's stored payload into the destination stream.
    /// <para>将单个已校验条目的已存储负载复制到目标流。</para>
    /// </summary>
    /// <param name="input">Source container stream. <para>源容器流。</para></param>
    /// <param name="entry">Entry produced by <see cref="Read"/>. <para>由 <see cref="Read"/> 产生的条目。</para></param>
    /// <param name="output">Destination stream receiving exactly <see cref="ImageWtyEntry.Length"/> bytes.
    /// <para>接收恰好 <see cref="ImageWtyEntry.Length"/> 字节的目标流。</para></param>
    /// <param name="cancellationToken">Cancels the copy. <para>取消本次复制。</para></param>
    /// <exception cref="ArgumentNullException">Any argument is null. <para>任一参数为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The entry offset or length is negative.
    /// <para>条目偏移或长度为负。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public static void ExtractEntry(Stream input, ImageWtyEntry entry, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(output);
        if (entry.Offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "The entry offset must not be negative.");
        }

        if (entry.Length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "The entry length must not be negative.");
        }

        using var range = new BoundedReadStream(input, entry.Offset, entry.Length, leaveOpen: true);
        StreamCopy.CopyExactly(range, output, entry.Length, cancellationToken);
    }

    /// <summary>
    /// Builds a collision-safe local file name for an entry, keeping only the base name.
    /// <para>为条目构建防碰撞的本地文件名，仅保留基础名。</para></summary>
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
        var characters = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. baseName.Where(c => !characters.Contains(c) && c > 0x1F)]).TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned is "." or "..")
        {
            cleaned = $"image_{index}.bin";
        }

        return cleaned;
    }

    /// <summary>Checks whether the header item count and item table offset describe a usable new-format entry table.
    /// <para>检查头部条目计数与条目表偏移是否构成可用的新版条目表。</para></summary>
    /// <param name="itemCount">Item count word at header offset 60.<para>头部偏移 60 处的条目计数字。</para></param>
    /// <param name="itemTableOffset">Item table offset word at header offset 64.<para>头部偏移 64 处的条目表偏移字。</para></param>
    /// <param name="containerLength">Total container size in bytes.<para>容器总字节数。</para></param>
    /// <returns><see langword="true"/> when the new layout should be parsed.<para>应按新版布局解析时为 <see langword="true"/>。</para></returns>
    private static bool IsNewLayoutApplicable(uint itemCount, uint itemTableOffset, long containerLength)
    {
        if (itemCount > MaximumEntries
            || itemTableOffset < MinimumNewItemTableOffset
            || itemTableOffset > (ulong)containerLength)
        {
            return false;
        }

        return checked((long)itemTableOffset + (long)itemCount * EntryRecordSize) <= containerLength;
    }

    /// <summary>Parses the new-format layout; the caller must have validated the table bounds via <see cref="IsNewLayoutApplicable"/>.
    /// <para>解析新版布局；调用方必须已通过 <see cref="IsNewLayoutApplicable"/> 校验条目表边界。</para></summary>
    private static ImageWtyHeader ReadNewLayout(Stream input, ReadOnlySpan<byte> fixedHeader, ReadOnlySpan<byte> body,
        uint headerVersion, uint itemCount, uint itemTableOffset, CancellationToken cancellationToken)
    {
        var tableStart = (long)itemTableOffset;
        var tableEnd = checked(tableStart + (long)itemCount * EntryRecordSize);
        var entries = new List<ImageWtyEntry>((int)itemCount);
        var record = new byte[EntryRecordSize];
        for (var index = 0; index < itemCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            input.Position = tableStart + (index * EntryRecordSize);
            input.ReadExactly(record);
            var dataLength = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(NewItemDataLengthOffset));
            var fileLength = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(NewItemFileLengthOffset));
            var payloadOffset = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(NewItemPayloadOffset));
            ValidateNewEntryRange(index, payloadOffset, dataLength, fileLength, tableEnd, input.Length);
            entries.Add(new ImageWtyEntry(
                ReadFixedAscii(record, NewItemNameOffset, NewItemNameLength),
                ReadFixedAscii(record, NewItemMainTypeOffset, NewItemMainTypeLength),
                ReadFixedAscii(record, NewItemSubTypeOffset, NewItemSubTypeLength),
                checked((long)payloadOffset),
                checked((long)dataLength),
                BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(NewItemAttributesOffset)),
                BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(NewItemChecksumOffset)),
                ImageWtyLayout.NewFormat,
                checked((long)fileLength),
                checked((long)dataLength)));
        }

        return new ImageWtyHeader(
            headerVersion,
            BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[28..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[(NewLayoutVidOffset - FixedHeaderSize)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[(NewLayoutPidOffset - FixedHeaderSize)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[(NewLayoutHardwareIdOffset - FixedHeaderSize)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[(NewLayoutFirmwareIdOffset - FixedHeaderSize)..]),
            entries,
            ImageWtyLayout.NewFormat);
    }

    /// <summary>Validates that one new-format entry stays inside the container payload area.
    /// <para>校验单个新版条目位于容器负载区内。</para></summary>
    private static void ValidateNewEntryRange(int index, ulong payloadOffset, ulong dataLength, ulong fileLength,
        long tableEnd, long containerLength)
    {
        var limit = (ulong)containerLength;
        if (payloadOffset > limit)
        {
            throw new InvalidDataException($"IMAGEWTY entry {index} offset escapes the container.");
        }

        if (dataLength > (ulong)long.MaxValue || fileLength > (ulong)long.MaxValue)
        {
            throw new InvalidDataException($"IMAGEWTY entry {index} declares an out-of-range 64-bit size.");
        }

        if (dataLength == 0)
        {
            return;
        }

        if (dataLength > limit || payloadOffset < (ulong)tableEnd || payloadOffset > limit - dataLength)
        {
            throw new InvalidDataException($"IMAGEWTY entry {index} points outside the container payload area.");
        }
    }

    /// <summary>Parses the classic layout: V1/V3 body metadata plus a variable-length file-header chain starting at 0x400.
    /// <para>解析经典布局：V1/V3 主体元数据，配合自 0x400 起的变长文件头链。</para></summary>
    private static ImageWtyHeader ReadClassicLayout(Stream input, ReadOnlySpan<byte> fixedHeader, ReadOnlySpan<byte> body,
        uint headerVersion, CancellationToken cancellationToken)
    {
        var shift = headerVersion == HeaderVersionV3 ? 4 : 0;
        var pid = BinaryPrimitives.ReadUInt32LittleEndian(body[shift..]);
        var vid = BinaryPrimitives.ReadUInt32LittleEndian(body[(shift + 4)..]);
        var hardwareId = BinaryPrimitives.ReadUInt32LittleEndian(body[(shift + 8)..]);
        var firmwareId = BinaryPrimitives.ReadUInt32LittleEndian(body[(shift + 12)..]);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[(shift + 24)..]);
        if (count is < 0 or > MaximumEntries)
        {
            throw new InvalidDataException($"The IMAGEWTY embedded file count {count} is outside the supported range.");
        }

        var entries = new List<ImageWtyEntry>(count);
        Span<byte> record = stackalloc byte[ClassicRecordPrefixSize];
        var position = (long)ClassicFileHeaderStart;
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position + ClassicRecordPrefixSize > input.Length)
            {
                throw new InvalidDataException($"The classic IMAGEWTY entry {index} header escapes the container.");
            }

            input.Position = position;
            input.ReadExactly(record);
            var fileNameLength = BinaryPrimitives.ReadUInt32LittleEndian(record[ClassicFileNameLengthOffset..]);
            var totalHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(record[ClassicTotalHeaderSizeOffset..]);
            var storedLength = BinaryPrimitives.ReadUInt32LittleEndian(record[ClassicStoredLengthOffset..]);
            var originalLength = BinaryPrimitives.ReadUInt32LittleEndian(record[ClassicOriginalLengthOffset..]);
            var payloadOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[ClassicPayloadOffset..]);
            if (fileNameLength > MaximumClassicFileNameLength)
            {
                throw new InvalidDataException($"The classic IMAGEWTY entry {index} file name length {fileNameLength} is outside the supported range.");
            }

            if (totalHeaderSize < MinimumClassicRecordSize || totalHeaderSize > MaximumClassicRecordSize)
            {
                throw new InvalidDataException($"The classic IMAGEWTY entry {index} header size {totalHeaderSize} is outside the supported range.");
            }

            if (fileNameLength > totalHeaderSize - ClassicRecordPrefixSize)
            {
                throw new InvalidDataException($"The classic IMAGEWTY entry {index} file name exceeds the declared header size.");
            }

            if (checked(position + totalHeaderSize) > input.Length)
            {
                throw new InvalidDataException($"The classic IMAGEWTY entry {index} header escapes the container.");
            }

            if (storedLength > 0
                && ((ulong)payloadOffset < ClassicFileHeaderStart || (ulong)payloadOffset + storedLength > (ulong)input.Length))
            {
                throw new InvalidDataException($"IMAGEWTY entry {index} points outside the container payload area.");
            }

            var nameBytes = new byte[(int)fileNameLength];
            input.ReadExactly(nameBytes);
            entries.Add(new ImageWtyEntry(
                Encoding.ASCII.GetString(nameBytes),
                ReadFixedAscii(record, ClassicMainTypeOffset, ClassicMainTypeLength),
                ReadFixedAscii(record, ClassicSubTypeOffset, ClassicSubTypeLength),
                payloadOffset,
                storedLength,
                0,
                0,
                ImageWtyLayout.Classic,
                originalLength,
                storedLength));
            position += totalHeaderSize;
        }

        return new ImageWtyHeader(
            headerVersion,
            BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[28..]),
            vid,
            pid,
            hardwareId,
            firmwareId,
            entries,
            ImageWtyLayout.Classic);
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> data, int offset, int length)
    {
        var slice = data.Slice(offset, length);
        var terminator = slice.IndexOf((byte)0);
        var end = terminator < 0 ? length : terminator;
        return Encoding.ASCII.GetString(slice[..end]);
    }
}