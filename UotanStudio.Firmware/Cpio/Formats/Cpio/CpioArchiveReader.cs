using System.Text;
using UotanStudio.Firmware.Cpio.Model;

namespace UotanStudio.Firmware.Cpio.Formats.Cpio;

/// <summary>
/// Reads CPIO-family archive streams into normalized archive entries.
/// <para>将 CPIO 系归档流读取为规范化的归档条目。</para>
/// </summary>
public sealed class CpioArchiveReader
{
    /// <summary>
    /// Reads all archive entries from a stream into memory.
    /// <para>从流中读取全部归档条目到内存。</para>
    /// </summary>
    /// <param name="stream">Source archive stream.<para>源归档流。</para></param>
    /// <param name="formatHint">Optional format hint used to skip format detection.<para>可选的格式提示，用于跳过格式检测。</para></param>
    /// <returns>A list containing all archive entries.<para>包含全部归档条目的列表。</para></returns>
    public IReadOnlyList<ArchiveEntry> ReadAll(Stream stream, ArchiveFormat? formatHint = null) => [.. ReadEntries(stream, formatHint)];

    /// <summary>
    /// Reads archive entries from a stream as a lazy sequence.
    /// <para>以惰性序列从流中读取归档条目。</para>
    /// </summary>
    /// <param name="stream">Source archive stream.<para>源归档流。</para></param>
    /// <param name="formatHint">Optional format hint used to skip format detection.<para>可选的格式提示，用于跳过格式检测。</para></param>
    /// <returns>An enumerable that yields archive entries as they are parsed.<para>解析时逐个产出归档条目的可枚举序列。</para></returns>
    public IEnumerable<ArchiveEntry> ReadEntries(Stream stream, ArchiveFormat? formatHint = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("Input stream must be readable.", nameof(stream));
        }

        var hardlinkState = new Dictionary<(ulong Inode, uint DevMaj, uint DevMin), HardlinkGroupState>();
        while (true)
        {
            var detected = formatHint ?? DetectFormat(stream);
            var entry = detected switch
            {
                ArchiveFormat.NewAscii or ArchiveFormat.CrcAscii => ReadNewAsciiLike(stream, detected),
                ArchiveFormat.OldAscii or ArchiveFormat.HpOldAscii => ReadOldAsciiLike(stream, detected),
                ArchiveFormat.Binary or ArchiveFormat.HpBinary => ReadBinaryLike(stream, detected),
                ArchiveFormat.Unknown => throw new NotImplementedException(),
                ArchiveFormat.Tar => throw new NotImplementedException(),
                ArchiveFormat.Ustar => throw new NotImplementedException(),
                _ => throw new NotSupportedException($"Unsupported CPIO format: {detected}"),
            };

            if (entry is null)
            {
                break;
            }

            if (entry.Name == CpioConstants.TrailerName)
            {
                break;
            }

            foreach (var transformed in TransformHardlinkEntry(entry, hardlinkState))
            {
                yield return transformed;
            }
        }

        foreach (var pending in FlushPendingHardlinks(hardlinkState))
        {
            yield return pending;
        }
    }

    /// <summary>
    /// Detects the CPIO format by probing stream header bytes.
    /// <para>通过探测流头部字节检测 CPIO 格式。</para>
    /// </summary>
    /// <param name="stream">Source archive stream.<para>源归档流。</para></param>
    /// <returns>The detected archive format.<para>检测到的归档格式。</para></returns>
    public ArchiveFormat DetectFormat(Stream stream)
    {
        if (!stream.CanSeek)
        {
            throw new NotSupportedException("Format detection requires a seekable stream.");
        }

        var origin = stream.Position;
        Span<byte> probe = stackalloc byte[6];
        var read = stream.Read(probe);
        stream.Position = origin;
        if (read < 6)
        {
            throw new EndOfStreamException("Unexpected end of stream while detecting archive format.");
        }

        var text = Encoding.ASCII.GetString(probe);
        if (text == CpioConstants.NewAsciiMagic)
        {
            return ArchiveFormat.NewAscii;
        }

        if (text == CpioConstants.CrcAsciiMagic)
        {
            return ArchiveFormat.CrcAscii;
        }

        if (text == CpioConstants.OldAsciiMagic)
        {
            return ArchiveFormat.OldAscii;
        }

        var binaryValue = (ushort)((probe[0] << 8) | probe[1]);
        var swapped = (ushort)((probe[1] << 8) | probe[0]);
        return binaryValue == CpioConstants.BinaryMagic || swapped == CpioConstants.BinaryMagic
            ? ArchiveFormat.Binary
            : throw new InvalidDataException("Unable to detect CPIO archive format.");
    }

    private static ArchiveEntry? ReadNewAsciiLike(Stream stream, ArchiveFormat format)
    {
        Span<byte> header = stackalloc byte[CpioConstants.NewAsciiHeaderSize];
        var first = stream.ReadByte();
        if (first == -1)
        {
            return null;
        }

        header[0] = (byte)first;
        stream.ReadExactly(header[1..]);

        var magic = CpioFieldParser.ReadAsciiString(header[..6]);
        if (magic is not CpioConstants.NewAsciiMagic and not CpioConstants.CrcAsciiMagic)
        {
            throw new InvalidDataException($"Invalid newc/crc magic: {magic}");
        }

        var inode = CpioFieldParser.ParseHex64(header.Slice(6, 8));
        var mode = CpioFieldParser.ParseHex(header.Slice(14, 8));
        var uid = CpioFieldParser.ParseHex(header.Slice(22, 8));
        var gid = CpioFieldParser.ParseHex(header.Slice(30, 8));
        var nlink = CpioFieldParser.ParseHex64(header.Slice(38, 8));
        var mtime = CpioFieldParser.ParseHex64(header.Slice(46, 8));
        var fileSize = CpioFieldParser.ParseHex64(header.Slice(54, 8));
        var devMaj = CpioFieldParser.ParseHex(header.Slice(62, 8));
        var devMin = CpioFieldParser.ParseHex(header.Slice(70, 8));
        var rdevMaj = CpioFieldParser.ParseHex(header.Slice(78, 8));
        var rdevMin = CpioFieldParser.ParseHex(header.Slice(86, 8));
        var nameSize = CpioFieldParser.ParseHex(header.Slice(94, 8));
        var checksum = CpioFieldParser.ParseHex(header.Slice(102, 8));

        var nameRaw = ReadExact(stream, checked((int)nameSize));
        var name = Encoding.UTF8.GetString(nameRaw.AsSpan(0, checked((int)nameSize - 1)));

        SkipPadding(stream, CpioConstants.NewAsciiHeaderSize + (int)nameSize, 4);

        var data = ReadExact(stream, checked((int)fileSize));
        SkipPadding(stream, (int)fileSize, 4);

        var fileType = CpioFieldParser.ToFileType(mode);
        ValidateCrc(format, checksum, data, name, fileType);

        return new ArchiveEntry
        {
            Name = name,
            Data = data,
            LinkName = fileType == CpioFileType.SymbolicLink ? Encoding.UTF8.GetString(data) : null,
            Metadata = new ArchiveEntryMetadata
            {
                Inode = inode,
                Mode = mode,
                UserId = uid,
                GroupId = gid,
                LinkCount = nlink,
                ModificationTimeUnixSeconds = checked((long)mtime),
                DeviceMajor = devMaj,
                DeviceMinor = devMin,
                RDeviceMajor = rdevMaj,
                RDeviceMinor = rdevMin,
                Checksum = checksum,
                FileType = fileType,
                UnixPermissions = CpioFieldParser.ToPermissions(mode),
            },
        };
    }

    private static ArchiveEntry? ReadOldAsciiLike(Stream stream, ArchiveFormat format)
    {
        Span<byte> header = stackalloc byte[CpioConstants.OldAsciiHeaderSize];
        var first = stream.ReadByte();
        if (first == -1)
        {
            return null;
        }

        header[0] = (byte)first;
        stream.ReadExactly(header[1..]);

        var magic = CpioFieldParser.ReadAsciiString(header[..6]);
        if (magic != CpioConstants.OldAsciiMagic)
        {
            throw new InvalidDataException($"Invalid old ASCII magic: {magic}");
        }

        var dev = CpioFieldParser.ParseOctal(header.Slice(6, 6));
        var inode = CpioFieldParser.ParseOctal(header.Slice(12, 6));
        var mode = CpioFieldParser.ParseOctal(header.Slice(18, 6));
        var uid = CpioFieldParser.ParseOctal(header.Slice(24, 6));
        var gid = CpioFieldParser.ParseOctal(header.Slice(30, 6));
        var nlink = CpioFieldParser.ParseOctal(header.Slice(36, 6));
        var rdev = CpioFieldParser.ParseOctal(header.Slice(42, 6));
        var mtime = CpioFieldParser.ParseOctal64(header.Slice(48, 11));
        var nameSize = CpioFieldParser.ParseOctal(header.Slice(59, 6));
        var fileSize = CpioFieldParser.ParseOctal64(header.Slice(65, 11));

        var nameRaw = ReadExact(stream, checked((int)nameSize));
        var name = Encoding.UTF8.GetString(nameRaw.AsSpan(0, checked((int)nameSize - 1)));

        // odc uses no alignment padding; the switch is kept for parity with the reference.
        const int alignment = 1;
        if (alignment > 1)
        {
            SkipPadding(stream, CpioConstants.OldAsciiHeaderSize + (int)nameSize, alignment);
        }

        var data = ReadExact(stream, checked((int)fileSize));
        if (alignment > 1)
        {
            SkipPadding(stream, (int)fileSize, alignment);
        }

        var fileType = CpioFieldParser.ToFileType(mode);
        return new ArchiveEntry
        {
            Name = name,
            Data = data,
            LinkName = fileType == CpioFileType.SymbolicLink ? Encoding.UTF8.GetString(data) : null,
            Metadata = new ArchiveEntryMetadata
            {
                Inode = inode,
                Mode = mode,
                UserId = uid,
                GroupId = gid,
                LinkCount = nlink,
                ModificationTimeUnixSeconds = mtime,
                DeviceMajor = dev,
                DeviceMinor = 0,
                RDeviceMajor = rdev,
                RDeviceMinor = 0,
                FileType = fileType,
                UnixPermissions = CpioFieldParser.ToPermissions(mode),
            },
        };
    }

    private static ArchiveEntry? ReadBinaryLike(Stream stream, ArchiveFormat format)
    {
        Span<byte> header = stackalloc byte[CpioConstants.OldBinaryHeaderSize];
        var first = stream.ReadByte();
        if (first == -1)
        {
            return null;
        }

        header[0] = (byte)first;
        stream.ReadExactly(header[1..]);

        var magicBigEndian = (ushort)((header[0] << 8) | header[1]);
        var byteSwap = magicBigEndian != CpioConstants.BinaryMagic;
        var magic = CpioFieldParser.ReadUInt16(header[..2], byteSwap);
        if (magic != CpioConstants.BinaryMagic)
        {
            throw new InvalidDataException("Invalid binary magic.");
        }

        var dev = CpioFieldParser.ReadUInt16(header.Slice(2, 2), byteSwap);
        var inode = CpioFieldParser.ReadUInt16(header.Slice(4, 2), byteSwap);
        var mode = CpioFieldParser.ReadUInt16(header.Slice(6, 2), byteSwap);
        var uid = CpioFieldParser.ReadUInt16(header.Slice(8, 2), byteSwap);
        var gid = CpioFieldParser.ReadUInt16(header.Slice(10, 2), byteSwap);
        var nlink = CpioFieldParser.ReadUInt16(header.Slice(12, 2), byteSwap);
        var rdev = CpioFieldParser.ReadUInt16(header.Slice(14, 2), byteSwap);
        var mtimeHigh = CpioFieldParser.ReadUInt16(header.Slice(16, 2), byteSwap);
        var mtimeLow = CpioFieldParser.ReadUInt16(header.Slice(18, 2), byteSwap);
        var nameSize = CpioFieldParser.ReadUInt16(header.Slice(20, 2), byteSwap);
        var fileSizeHigh = CpioFieldParser.ReadUInt16(header.Slice(22, 2), byteSwap);
        var fileSizeLow = CpioFieldParser.ReadUInt16(header.Slice(24, 2), byteSwap);

        var nameRaw = ReadExact(stream, nameSize);
        var name = Encoding.UTF8.GetString(nameRaw.AsSpan(0, nameSize - 1));

        SkipPadding(stream, CpioConstants.OldBinaryHeaderSize + nameSize, 2);

        var fileSize = CpioFieldParser.ComposeUInt32(fileSizeHigh, fileSizeLow);
        var data = ReadExact(stream, checked((int)fileSize));
        SkipPadding(stream, checked((int)fileSize), 2);

        var mode32 = (uint)mode;
        var hpDeviceMajor = format == ArchiveFormat.HpBinary ? fileSizeHigh : (uint)rdev;
        var hpDeviceMinor = format == ArchiveFormat.HpBinary ? fileSizeLow : 0U;

        var fileType = CpioFieldParser.ToFileType(mode32);
        return new ArchiveEntry
        {
            Name = name,
            Data = data,
            LinkName = fileType == CpioFileType.SymbolicLink ? Encoding.UTF8.GetString(data) : null,
            Metadata = new ArchiveEntryMetadata
            {
                Inode = inode,
                Mode = mode32,
                UserId = uid,
                GroupId = gid,
                LinkCount = nlink,
                ModificationTimeUnixSeconds = CpioFieldParser.ComposeUInt32(mtimeHigh, mtimeLow),
                DeviceMajor = dev,
                DeviceMinor = 0,
                RDeviceMajor = hpDeviceMajor,
                RDeviceMinor = hpDeviceMinor,
                FileType = fileType,
                UnixPermissions = CpioFieldParser.ToPermissions(mode32),
            },
        };
    }

    private static void ValidateCrc(ArchiveFormat format, uint expected, byte[] data, string name, CpioFileType fileType)
    {
        if (format != ArchiveFormat.CrcAscii)
        {
            return;
        }

        // GNU cpio does not apply crc checks to links.
        if (fileType is CpioFileType.SymbolicLink or CpioFileType.HardLink)
        {
            return;
        }

        uint actual = 0;
        foreach (var b in data)
        {
            actual += b;
        }

        if (actual != expected)
        {
            throw new InvalidDataException($"CRC ASCII checksum mismatch for '{name}': 0x{actual:X8} != 0x{expected:X8}");
        }
    }

    private static byte[] ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static void SkipPadding(Stream stream, int offset, int align)
    {
        var pad = (align - (offset % align)) % align;
        if (pad == 0)
        {
            return;
        }

        Span<byte> scratch = stackalloc byte[8];
        while (pad > 0)
        {
            var step = Math.Min(pad, scratch.Length);
            stream.ReadExactly(scratch[..step]);
            pad -= step;
        }
    }

    private static IEnumerable<ArchiveEntry> TransformHardlinkEntry(
        ArchiveEntry entry,
        Dictionary<(ulong Inode, uint DevMaj, uint DevMin), HardlinkGroupState> state)
    {
        if (entry.Metadata.Inode == 0 || entry.Metadata.LinkCount <= 1 || entry.Metadata.FileType != CpioFileType.Regular)
        {
            yield return entry;
            yield break;
        }

        var groupKey = (entry.Metadata.Inode, entry.Metadata.DeviceMajor, entry.Metadata.DeviceMinor);
        if (!state.TryGetValue(groupKey, out var groupState))
        {
            groupState = new HardlinkGroupState();
            state[groupKey] = groupState;
        }

        if (entry.Data.Length > 0)
        {
            groupState.DataCarrierName = entry.Name;

            if (groupState.PendingEntries.Count > 0)
            {
                foreach (var pending in groupState.PendingEntries)
                {
                    yield return ToHardlinkEntry(pending, entry.Name);
                }

                groupState.PendingEntries.Clear();
            }

            yield return entry;
            yield break;
        }

        if (groupState.DataCarrierName is { Length: > 0 } carrierName)
        {
            yield return ToHardlinkEntry(entry, carrierName);
            yield break;
        }

        groupState.PendingEntries.Add(entry);
    }

    private static IEnumerable<ArchiveEntry> FlushPendingHardlinks(
        Dictionary<(ulong Inode, uint DevMaj, uint DevMin), HardlinkGroupState> state)
    {
        foreach (var (_, groupState) in state)
        {
            foreach (var pending in groupState.PendingEntries)
            {
                yield return pending;
            }
        }
    }

    private static ArchiveEntry ToHardlinkEntry(ArchiveEntry source, string carrierName)
    {
        return new ArchiveEntry
        {
            Name = source.Name,
            LinkName = carrierName,
            Data = [],
            Metadata = new ArchiveEntryMetadata
            {
                Inode = source.Metadata.Inode,
                Mode = source.Metadata.Mode,
                UserId = source.Metadata.UserId,
                GroupId = source.Metadata.GroupId,
                DeviceMajor = source.Metadata.DeviceMajor,
                DeviceMinor = source.Metadata.DeviceMinor,
                RDeviceMajor = source.Metadata.RDeviceMajor,
                RDeviceMinor = source.Metadata.RDeviceMinor,
                LinkCount = source.Metadata.LinkCount,
                ModificationTimeUnixSeconds = source.Metadata.ModificationTimeUnixSeconds,
                Checksum = source.Metadata.Checksum,
                FileType = CpioFileType.HardLink,
                UnixPermissions = source.Metadata.UnixPermissions,
                ExtendedAttributes = source.Metadata.ExtendedAttributes,
            },
        };
    }

    private sealed class HardlinkGroupState
    {
        public string? DataCarrierName { get; set; }

        public List<ArchiveEntry> PendingEntries { get; } = [];
    }
}