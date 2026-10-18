using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;

/// <summary>One file record of an IMAGEWTY container: name, absolute data range, declared types and the raw attribute/checksum words.
/// <para>IMAGEWTY 容器中的一个文件记录：名称、绝对数据区间、声明的类型，以及原始的属性/校验字。</para></summary>
/// <param name="Name">Entry name from the 32-byte field.<para>来自 32 字段的条目名。</para></param>
/// <param name="MainType">Declared main type string, up to 32 bytes.
/// <para>声明的主类型字符串，至多 32 字节。</para></param>
/// <param name="SubType">Declared sub type string, up to 32 bytes.
/// <para>声明的子类型字符串，至多 32 字节。</para></param>
/// <param name="Offset">Absolute payload offset inside the container.
/// <para>容器内绝对负载偏移。</para></param>
/// <param name="Length">Payload size in bytes.<para>负载字节大小。</para></param>
/// <param name="Attributes">Raw attribute word.<para>原始属性字。</para></param>
/// <param name="Checksum">Raw checksum word; its coverage is not enforced on read.
/// <para>原始校验字；读取时不强制校验其覆盖范围。</para></param>
public sealed record ImageWtyEntry(string Name, string MainType, string SubType, long Offset, long Length,
    uint Attributes, uint Checksum);

/// <summary>Validated header metadata of an IMAGEWTY container.
/// <para>已校验的 IMAGEWTY 容器头元数据。</para></summary>
/// <param name="HeaderVersion">Container header version, <c>0x100</c> or <c>0x300</c>.
/// <para>容器头版本，取 <c>0x100</c> 或 <c>0x300</c>。</para></param>
/// <param name="HeaderSize">Declared header size word.<para>声明的头部大小字。</para></param>
/// <param name="RamBase">Declared RAM base address word.<para>声明的 RAM 基地址字。</para></param>
/// <param name="Version">Container version word.<para>容器版本字。</para></param>
/// <param name="ImageSize">Declared image size word.<para>声明的镜像大小字。</para></param>
/// <param name="ImageHeaderSize">Declared combined header size word.
/// <para>声明的合并头部大小字。</para></param>
/// <param name="Vid">Declared USB vendor id from the version body.
/// <para>来自版本主体的 USB 厂商 id。</para></param>
/// <param name="Pid">Declared USB peripheral id from the version body.
/// <para>来自版本主体的 USB 外设 id。</para></param>
/// <param name="HardwareId">Declared hardware id.<para>声明的硬件 id。</para></param>
/// <param name="FirmwareId">Declared firmware id.<para>声明的固件 id。</para></param>
/// <param name="Entries">Validated entry records in table order.<para>按表顺序排列的已校验条目。</para></param>
public sealed record ImageWtyHeader(uint HeaderVersion, uint HeaderSize, uint RamBase, uint Version,
    uint ImageSize, uint ImageHeaderSize, uint Vid, uint Pid, uint HardwareId, uint FirmwareId,
    IReadOnlyList<ImageWtyEntry> Entries);

/// <summary>
/// Bounded reader for Allwinner IMAGEWTY boot containers, following the layout proven by the
/// reference structure definitions: an 8-byte <c>IMAGEWTY</c> magic followed by six big u32 words
/// (header version, header size, RAM base, version, image size, image header size); a version body
/// of 0x100 (twelve u32: pid, vid, hardware id, firmware id, ...) or 0x300 (the same shifted by an
/// extra leading u32) carrying the embedded-file count; then that many 1024-byte file records with
/// a 32-byte name, 64-bit absolute data offset, 64-bit length, 32-byte main/sub type strings and
/// attribute/checksum words. A magic mismatch marks the container as encrypted; encrypted payload
/// decryption is out of scope here.
/// <para>Allwinner IMAGEWTY 启动容器的有界读取器，布局依据参考结构定义证实：8 字节 <c>IMAGEWTY</c> 魔数，
/// 其后六个 u32 字（头版本、头大小、RAM 基址、版本、镜像大小、镜像头大小）；随后为版本主体：
/// 0x100（12 个 u32：pid、vid、硬件 id、固件 id 等）或 0x300（同样内容前移一个 u32），其中携带内嵌文件计数；
/// 之后是每个 1024 字节的文件记录：32 字节名称、64 位绝对数据偏移、64 位长度、32 字节主/子类型字符串与
/// 属性/校验字。魔数不匹配表示容器已加密；加密负载解密不在本处范围内。</para>
/// </summary>
internal static class AllwinnerImageWtyFormat
{
    /// <summary>Byte length of the fixed entry records. <para>固定条目记录的字节长度。</para></summary>
    public const int EntryRecordSize = 1024;

    /// <summary>Header version constant for the V1 body layout. <para>V1 主体布局的头版本常量。</para></summary>
    public const uint HeaderVersionV1 = 0x100;

    /// <summary>Header version constant for the V3 body layout. <para>V3 主体布局的头版本常量。</para></summary>
    public const uint HeaderVersionV3 = 0x300;

    /// <summary>Hard ceiling on the embedded file count. <para>内嵌文件计数的硬性上限。</para></summary>
    public const int MaximumEntries = 4096;

    private const int FixedHeaderSize = 32;

    /// <summary>
    /// Reports whether the container starts with the plaintext <c>IMAGEWTY</c> magic; the reference
    /// readers treat any mismatch on an otherwise-expected container as an encryption flag.
    /// <para>判断容器是否以明文 <c>IMAGEWTY</c> 魔数开头；参考读取器将在预期容器上出现魔数不符时视为加密标志。</para>
    /// </summary>
    /// <param name="header">Leading bytes of the file, at least 8. <para>文件起始字节，至少 8 个。</para></param>
    /// <returns><see langword="true"/> when the magic matches. <para>魔数匹配时为 <see langword="true"/>。</para></returns>
    public static bool HasPlainMagic(ReadOnlySpan<byte> header) => header.Length >= 8
        && header[..8].SequenceEqual("IMAGEWTY"u8);

    /// <summary>Reads and validates the header, version body and entry table from a seekable stream.
    /// <para>从可寻址流读取并校验头部、版本主体与条目表。</para></summary>
    /// <param name="input">Readable, seekable container stream; position is restored on return.
    /// <para>可读、可寻址的容器流；返回时恢复位置。</para></param>
    /// <param name="cancellationToken">Cancels the read.<para>取消本次读取。</para></param>
    /// <returns>The validated header with its entry table.<para>已校验的头部及其条目表。</para></returns>
    /// <exception cref="ArgumentException">The stream cannot be read or cannot seek.
    /// <para>流不可读或不可寻址。</para></exception>
    /// <exception cref="InvalidDataException">The magic is missing (encrypted container), the header version is unknown, the entry table escapes the file, or an entry payload range is invalid.
    /// <para>魔数缺失（加密容器）、头版本未知、条目表超出文件或条目负载区间无效。</para></exception>
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
            var shift = headerVersion == HeaderVersionV3 ? 4 : 0;
            var pid = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(shift));
            var vid = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(shift + 4));
            var hardwareId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(shift + 8));
            var firmwareId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(shift + 12));
            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(shift + 24));
            if (count is < 0 or > MaximumEntries)
            {
                throw new InvalidDataException($"The IMAGEWTY embedded file count {count} is outside the supported range.");
            }

            var tableStart = FixedHeaderSize + bodyLength;
            var tableEnd = checked((long)tableStart + (long)count * EntryRecordSize);
            if (tableEnd > input.Length)
            {
                throw new InvalidDataException("The IMAGEWTY entry table exceeds the container.");
            }

            var entries = new List<ImageWtyEntry>(count);
            var record = new byte[EntryRecordSize];
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                input.Position = tableStart + (long)index * EntryRecordSize;
                input.ReadExactly(record, 0, EntryRecordSize);
                var name = ReadFixedAscii(record, 0, 32);
                var offset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32)));
                var length = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40)));
                if (length > 0 && (offset < tableEnd || offset > input.Length - length))
                {
                    throw new InvalidDataException($"IMAGEWTY entry {index} points outside the container payload area.");
                }

                entries.Add(new ImageWtyEntry(
                    name,
                    ReadFixedAscii(record, 48, 32),
                    ReadFixedAscii(record, 80, 32),
                    offset,
                    length,
                    BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(112)),
                    BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(116))));
            }

            return new ImageWtyHeader(
                headerVersion,
                BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[20..]),
                BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[24..]),
                BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[28..]),
                vid, pid, hardwareId, firmwareId,
                entries);
        }
        finally
        {
            input.Position = original;
        }
    }

    /// <summary>
    /// Copies one validated entry payload into the destination stream.
    /// <para>将单个已校验条目的负载复制到目标流。</para>
    /// </summary>
    /// <param name="input">Source container stream. <para>源容器流。</para></param>
    /// <param name="entry">Entry produced by <see cref="Read"/>. <para>由 <see cref="Read"/> 产生的条目。</para></param>
    /// <param name="output">Destination stream receiving exactly <see cref="ImageWtyEntry.Length"/> bytes.
    /// <para>接收恰好 <see cref="ImageWtyEntry.Length"/> 字节的目标流。</para></param>
    /// <param name="cancellationToken">Cancels the copy. <para>取消本次复制。</para></param>
    /// <exception cref="ArgumentNullException">Any argument is null. <para>任一参数为 null。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public static void ExtractEntry(Stream input, ImageWtyEntry entry, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(output);
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

    private static string ReadFixedAscii(byte[] data, int offset, int length)
    {
        var terminator = Array.IndexOf(data, (byte)0, offset, length);
        var end = terminator < 0 ? offset + length : terminator;
        return Encoding.ASCII.GetString(data, offset, end - offset);
    }
}