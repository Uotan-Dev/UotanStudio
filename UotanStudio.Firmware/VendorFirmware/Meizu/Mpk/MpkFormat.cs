using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.VendorFirmware.Meizu.Mpk;

/// <summary>
/// One file record inside an MPK archive: path, absolute data range and compression marker.
/// <para>MPK 归档中的一个文件记录：路径、绝对数据区间与压缩标记。</para>
/// </summary>
/// <param name="Path">The stored path, exactly as recorded in the entry table.
/// <para>存储路径，与条目表中记录的内容完全一致。</para></param>
/// <param name="Offset">Absolute byte offset of the payload inside the archive.
/// <para>负载在归档内的绝对字节偏移。</para></param>
/// <param name="Size">Payload length in bytes. <para>负载字节长度。</para></param>
/// <param name="Compression">The recorded compression marker; this library does not decode it.
/// <para>记录的压缩标记；本库不解释其含义。</para></param>
public sealed record MpkEntry(string Path, long Offset, long Size, uint Compression);

/// <summary>
/// Validated header metadata of an MPK archive.
/// <para>已校验的 MPK 归档头元数据。</para>
/// </summary>
/// <param name="Version">The declared version word. <para>声明的版本字。</para></param>
/// <param name="Crc32">Header CRC32 word; its exact coverage is not established by the reference
/// readers, so it is reported, never enforced.
/// <para>头部 CRC32 字；参考读取器未确立其覆盖范围，因此仅上报、不强制校验。</para></param>
/// <param name="Identifier">Module identifier string from the fixed 160-byte field.
/// <para>来自固定 160 字节字段的模块标识。</para></param>
/// <param name="Name">Display name string from the fixed 256-byte field.
/// <para>来自固定 256 字段的显示名称。</para></param>
/// <param name="SupportSystem">Support-system marker, up to 8 bytes. <para>支持系统标记，至多 8 字节。</para></param>
/// <param name="SupportMachine">Support-machine marker, up to 8 bytes. <para>支持机型标记，至多 8 字节。</para></param>
/// <param name="FileCount">Declared entry count, equal to <see cref="MpkArchive.Entries"/> length.
/// <para>声明的条目数量，等于 <see cref="MpkArchive.Entries"/> 的长度。</para></param>
public sealed record MpkHeaderInfo(uint Version, uint Crc32, string Identifier, string Name,
    string SupportSystem, string SupportMachine, int FileCount);

/// <summary>
/// A parsed MPK archive: header metadata plus its validated entry table, bound to the source stream.
/// <para>已解析的 MPK 归档：头元数据与已校验条目表，并绑定到源流。</para>
/// </summary>
/// <param name="Header">The parsed header metadata. <para>已解析的头元数据。</para></param>
/// <param name="Entries">Entry records in table order. <para>按表顺序排列的条目。</para></param>
public sealed record MpkArchive(MpkHeaderInfo Header, IReadOnlyList<MpkEntry> Entries);

/// <summary>
/// Bounded reader for the Meizu MPK package layout proven by the reference structure definitions:
/// a 2048-byte little-endian header (magic <c>!MPK</c>, version word, CRC32 word, fixed ASCII
/// identity fields, opaque icon/description words and a file count), followed by 512-byte entry
/// records (2-byte magic <c>!F</c>, 490-byte path, compression word, 64-bit absolute data offset
/// and 64-bit size). Payload blobs live at their recorded offsets after the table.
/// <para>Meizu MPK 包布局的有界读取器，布局依据参考结构定义证实：2048 字节小端头部
/// （魔数 <c>!MPK</c>、版本字、CRC32 字、固定 ASCII 标识字段、不透明的图标/描述字与文件计数），
/// 其后是 512 字节条目记录（2 字节魔数 <c>!F</c>、490 字节路径、压缩字、64 位绝对数据偏移与 64 位大小）。
/// 负载数据块按记录的偏移位于表之后。</para>
/// </summary>
internal static class MpkFormat
{
    /// <summary>Byte size of the fixed MPK header. <para>固定 MPK 头的字节大小。</para></summary>
    public const int HeaderSize = 2048;

    /// <summary>Byte size of one entry record. <para>单个条目记录的字节大小。</para></summary>
    public const int EntryRecordSize = 512;

    /// <summary>Hard ceiling on the entry count. <para>条目数量的硬性上限。</para></summary>
    public const int MaximumEntries = 65536;

    private static readonly byte[] ArchiveMagic = "!MPK"u8.ToArray();
    private static readonly byte[] EntryMagic = "!F"u8.ToArray();

    /// <summary>
    /// Reads and validates the header and entry table from a seekable stream.
    /// <para>从可寻址流读取并校验头部与条目表。</para>
    /// </summary>
    /// <param name="input">Readable, seekable archive stream; its position is restored on return.
    /// <para>可读、可寻址的归档流；返回时恢复其位置。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>The validated archive metadata with its entry table.
    /// <para>已校验的归档元数据及其条目表。</para></returns>
    /// <exception cref="ArgumentException">The stream cannot be read or cannot seek.
    /// <para>流不可读或不可寻址。</para></exception>
    /// <exception cref="InvalidDataException">The archive is truncated, the magic mismatches, an entry
    /// record is malformed, a payload range escapes the file, or the table exceeds the entry ceiling.
    /// <para>归档被截断、魔数不符、条目记录格式错误、负载区间超出文件或表超出条目上限。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public static MpkArchive Read(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException("MPK reading requires a readable, seekable stream.", nameof(input));
        }

        var original = input.Position;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input.Length < HeaderSize)
            {
                throw new InvalidDataException("The MPK header is truncated.");
            }

            var header = new byte[HeaderSize];
            input.Position = 0;
            input.ReadExactly(header, 0, HeaderSize);
            if (!header.AsSpan(0, 4).SequenceEqual(ArchiveMagic))
            {
                throw new InvalidDataException("The MPK archive magic does not match \"!MPK\".");
            }

            var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            var identifier = ReadFixedAscii(header, 12, 160);
            var name = ReadFixedAscii(header, 172, 256);
            var supportSystem = ReadFixedAscii(header, 428, 8);
            var supportMachine = ReadFixedAscii(header, 436, 8);
            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2044));
            if (count is < 0 or > MaximumEntries)
            {
                throw new InvalidDataException($"The MPK file count {count} is outside the supported range.");
            }

            var tableEnd = checked((long)HeaderSize + (long)count * EntryRecordSize);
            if (tableEnd > input.Length)
            {
                throw new InvalidDataException("The MPK entry table exceeds the file.");
            }

            var entries = new List<MpkEntry>(count);
            var record = new byte[EntryRecordSize];
            input.Position = HeaderSize;
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                input.ReadExactly(record, 0, EntryRecordSize);
                if (!record.AsSpan(0, 2).SequenceEqual(EntryMagic))
                {
                    throw new InvalidDataException($"MPK entry {index} is missing the \"!F\" record magic.");
                }

                var path = ReadFixedAscii(record, 2, 490);
                if (path.Length == 0)
                {
                    throw new InvalidDataException($"MPK entry {index} has an empty path.");
                }

                var compression = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(492));
                var offset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(496)));
                var size = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(504)));
                if (offset < tableEnd || size < 0 || offset > input.Length - size)
                {
                    throw new InvalidDataException($"MPK entry {index} points outside the archive payload area.");
                }

                entries.Add(new MpkEntry(path, offset, size, compression));
            }

            var info = new MpkHeaderInfo(version, crc, identifier, name, supportSystem, supportMachine, count);
            return new MpkArchive(info, entries);
        }
        finally
        {
            input.Position = original;
        }
    }

    /// <summary>
    /// Extracts one validated entry payload into the destination stream.
    /// <para>将单个已校验条目的负载提取到目标流。</para>
    /// </summary>
    /// <param name="input">Source archive stream. <para>源归档流。</para></param>
    /// <param name="entry">Entry record produced by <see cref="Read"/>. <para>由 <see cref="Read"/> 产生的条目。</para></param>
    /// <param name="output">Destination stream receiving exactly <see cref="MpkEntry.Size"/> bytes.
    /// <para>接收恰好 <see cref="MpkEntry.Size"/> 字节的目标流。</para></param>
    /// <param name="cancellationToken">Cancels the copy. <para>取消本次复制。</para></param>
    /// <exception cref="ArgumentNullException">Any stream or the entry is null.
    /// <para>任一参数流或条目为 null。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public static void ExtractEntry(Stream input, MpkEntry entry, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(output);
        using var range = new BoundedReadStream(input, entry.Offset, entry.Size, leaveOpen: true);
        StreamCopy.CopyExactly(range, output, entry.Size, cancellationToken);
    }

    /// <summary>
    /// Builds a collision-safe local file name for an entry path, keeping only the base name.
    /// <para>为条目路径构建防碰撞的本地文件名，仅保留基础名。</para></summary>
    /// <param name="entry">The archive entry. <para>归档条目。</para></param>
    /// <param name="index">Zero-based table position, appended when the base name is empty or reserved.
    /// <para>零基表序号，当基础名为空或为保留名时追加。</para></param>
    /// <returns>A file name with no directory separators or traversal.
    /// <para>不含目录分隔符与路径上溯的文件名。</para></returns>
    public static string SafeEntryFileName(MpkEntry entry, int index)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var normalized = entry.Path.Replace('\\', '/');
        var baseName = normalized[(normalized.LastIndexOf('/') + 1)..];
        var characters = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. baseName.Where(c => !characters.Contains(c) && c > 0x1F)]).TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned is "." or "..")
        {
            cleaned = $"entry_{index}";
        }

        return cleaned;
    }

    private static string ReadFixedAscii(byte[] data, int offset, int length)
    {
        var terminator = Array.IndexOf(data, (byte)0, offset, length);
        var end = terminator < 0 ? offset + length : terminator;
        return Encoding.UTF8.GetString(data, offset, end - offset);
    }
}