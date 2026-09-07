using System.Text;
using UotanStudio.Firmware.Cpio.Model;
using UotanStudio.Firmware.Common;
using UotanStudio.Firmware.Common.Streams;

namespace UotanStudio.Firmware.Cpio.Formats.Cpio;

public sealed class CpioArchiveReader
{
    /// <summary>
    /// Reads all archive entries from a stream into memory.
    /// <para>从流中读取全部归档条目到内存。</para>
    /// </summary>
    public IReadOnlyList<ArchiveEntry> ReadAll(Stream stream, ArchiveFormat? formatHint = null) => [.. ReadEntries(stream, formatHint)];

    /// <summary>
    /// Reads archive entries from a stream as a lazy sequence.
    /// <para>以惰性序列从流中读取归档条目。</para>
    /// </summary>
    public IEnumerable<ArchiveEntry> ReadEntries(Stream stream, ArchiveFormat? formatHint = null)
    {
        foreach (var entry in ReadEntriesStreaming(stream, formatHint))
        {
            if (entry.DataStreamFactory is null) { yield return entry; continue; }
            if (entry.DataLength > 256 * 1024 * 1024)
                throw new InvalidDataException("CPIO entry exceeds the 256 MiB materialization limit; use streaming access.");
            using var data = entry.DataStreamFactory();
            yield return new ArchiveEntry
            {
                Name = entry.Name,
                Metadata = entry.Metadata,
                LinkName = entry.LinkName,
                Data = ReadExact(data, checked((int)entry.DataLength))
            };
        }
    }

    /// <summary>
    /// Reads bounded payload views whose source remains valid during enumeration.
    /// <para>读取有界负载视图，源流须在枚举期间保持有效。</para>
    /// Non-seekable input is spooled to disk with a 64 GiB limit.
    /// <para>不可定位输入暂存到磁盘，上限为 64 GiB。</para>
    /// </summary>
    public IEnumerable<ArchiveEntry> ReadEntriesStreaming(Stream stream, ArchiveFormat? formatHint = null)
        => ReadEntriesStreaming(stream, formatHint, 64L * 1024 * 1024 * 1024);

    /// <summary>
    /// Reads bounded payload views with a disk-spooling limit and cancellation.
    /// <para>读取有界负载视图，限制磁盘暂存量并支持取消。</para>
    /// </summary>
    /// <param name="stream">The archive source. <para>归档输入流。</para></param>
    /// <param name="formatHint">The optional format. <para>可选的格式提示。</para></param>
    /// <param name="maximumSpoolBytes">The non-seekable input limit in bytes. <para>不可定位输入的字节上限。</para></param>
    /// <param name="cancellationToken">The cancellation token. <para>取消令牌。</para></param>
    /// <returns>Entries valid during enumeration. <para>枚举期间有效的条目。</para></returns>
    public IEnumerable<ArchiveEntry> ReadEntriesStreaming(Stream stream, ArchiveFormat? formatHint,
        long maximumSpoolBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumSpoolBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (!stream.CanRead)
        {
            throw new ArgumentException("Input stream must be readable.", nameof(stream));
        }
        if (!stream.CanSeek)
        {
            var temporaryPath = Path.Combine(Path.GetTempPath(), "uotan-cpio-" + Guid.NewGuid().ToString("N"));
            using var temporary = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 65536, FileOptions.DeleteOnClose);
            using (var bounded = new CountingWriteStream(temporary, maximumSpoolBytes,
                leaveOpen: true, cancellationToken: cancellationToken))
            {
                var buffer = new byte[65536];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = stream.Read(buffer);
                    if (count == 0) break;
                    bounded.Write(buffer, 0, count);
                }
            }
            temporary.Position = 0;
            foreach (var entry in ReadEntriesStreaming(temporary, formatHint, maximumSpoolBytes, cancellationToken)) yield return entry;
            yield break;
        }

        var hardlinkState = new Dictionary<(ulong Inode, uint DevMaj, uint DevMin), HardlinkGroupState>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detected = formatHint ?? DetectFormat(stream);
            var entry = detected switch
            {
                ArchiveFormat.NewAscii or ArchiveFormat.CrcAscii => ReadNewAsciiLike(stream, detected),
                ArchiveFormat.OldAscii or ArchiveFormat.HpOldAscii => ReadOldAsciiLike(stream, detected),
                ArchiveFormat.Binary or ArchiveFormat.HpBinary => ReadBinaryLike(stream, detected),


                ArchiveFormat.Unknown => throw new NotSupportedException("Unknown CPIO archive format."),
                ArchiveFormat.Tar or ArchiveFormat.Ustar => throw new NotSupportedException("TAR/Ustar is not a CPIO format; use TarArchiveReader instead."),
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

            var nextPosition = stream.Position;
            foreach (var transformed in TransformHardlinkEntry(entry, hardlinkState))
            {
                yield return transformed;
            }
            stream.Position = nextPosition;
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
    public ArchiveFormat DetectFormat(Stream stream)
    {
        if (!stream.CanSeek)
        {
            throw new NotSupportedException("Format detection requires a seekable stream.");
        }

        var origin = stream.Position;
        Span<byte> probe = stackalloc byte[6];
        var read = stream.ReadAtLeast(probe, probe.Length, throwOnEndOfStream: false);
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
        var fileSize = checked((long)CpioFieldParser.ParseHex64(header.Slice(54, 8)));
        var devMaj = CpioFieldParser.ParseHex(header.Slice(62, 8));
        var devMin = CpioFieldParser.ParseHex(header.Slice(70, 8));
        var rdevMaj = CpioFieldParser.ParseHex(header.Slice(78, 8));
        var rdevMin = CpioFieldParser.ParseHex(header.Slice(86, 8));
        var nameSize = CpioFieldParser.ParseHex(header.Slice(94, 8));
        var checksum = CpioFieldParser.ParseHex(header.Slice(102, 8));

        var name = ReadName(stream, nameSize);

        SkipPadding(stream, CpioConstants.NewAsciiHeaderSize + (int)nameSize, 4);

        var fileType = CpioFieldParser.ToFileType(mode);
        var (data, factory) = ReadPayload(stream, fileSize, fileType, format, checksum, name);
        SkipPadding(stream, fileSize, 4);

        return new ArchiveEntry
        {
            Name = name,
            Data = data,
            DataStreamFactory = factory,
            DeclaredDataLength = fileSize,
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

        var name = ReadName(stream, nameSize);

        var fileType = CpioFieldParser.ToFileType(mode);
        var hpSpecial = format == ArchiveFormat.HpOldAscii
            && fileType is CpioFileType.CharacterDevice or CpioFileType.BlockDevice;
        var payloadSize = hpSpecial ? 0L : fileSize;

        var (data, factory) = ReadPayload(stream, payloadSize, fileType, format, 0, name);
        var hpDeviceMajor = hpSpecial ? (uint)(fileSize >> 16) : rdev;
        var hpDeviceMinor = hpSpecial ? (uint)(fileSize & 0xFFFF) : 0U;

        return new ArchiveEntry
        {
            Name = name,
            Data = data,
            DataStreamFactory = factory,
            DeclaredDataLength = payloadSize,
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
                RDeviceMajor = hpDeviceMajor,
                RDeviceMinor = hpDeviceMinor,
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

        var name = ReadName(stream, nameSize);

        SkipPadding(stream, CpioConstants.OldBinaryHeaderSize + nameSize, 2);

        var fileSize = CpioFieldParser.ComposeUInt32(fileSizeHigh, fileSizeLow);
        var mode32 = (uint)mode;
        var fileType = CpioFieldParser.ToFileType(mode32);
        var hpSpecial = format == ArchiveFormat.HpBinary
            && fileType is CpioFileType.CharacterDevice or CpioFileType.BlockDevice;
        var payloadSize = hpSpecial ? 0U : fileSize;
        var (data, factory) = ReadPayload(stream, payloadSize, fileType, format, 0, name);
        SkipPadding(stream, payloadSize, 2);

        var hpDeviceMajor = hpSpecial ? fileSizeHigh : (uint)rdev;
        var hpDeviceMinor = hpSpecial ? fileSizeLow : 0U;
        return new ArchiveEntry
        {
            Name = name,
            Data = data,
            DataStreamFactory = factory,
            DeclaredDataLength = payloadSize,
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

    private static (byte[] Data, Func<Stream>? Factory) ReadPayload(Stream stream, long length,
        CpioFileType fileType, ArchiveFormat format, uint expected, string name)
    {
        var start = stream.Position;
        FileExtraction.ValidateRange(start, length, stream.Length, "CPIO payload");
        if (fileType == CpioFileType.SymbolicLink)
        {
            if (length > 65536) throw new InvalidDataException("CPIO symlink target is too large.");
            return (ReadExact(stream, checked((int)length)), null);
        }
        if (format == ArchiveFormat.CrcAscii)
        {
            uint actual = 0;
            Span<byte> buffer = stackalloc byte[65536];
            var remaining = length;
            while (remaining > 0)
            {
                var size = (int)Math.Min(remaining, buffer.Length);
                stream.ReadExactly(buffer[..size]);
                foreach (var b in buffer[..size]) actual = unchecked(actual + b);
                remaining -= size;
            }
            if (actual != expected)
                throw new InvalidDataException($"CRC ASCII checksum mismatch for '{name}': 0x{actual:X8} != 0x{expected:X8}");
        }
        stream.Position = checked(start + length);
        return ([], () => new BoundedReadStream(stream, start, length, leaveOpen: true));
    }

    private static byte[] ReadExact(Stream stream, int length)
    {
        if (length < 0 || length > 256 * 1024 * 1024)
            throw new InvalidDataException("CPIO metadata or materialized payload exceeds its limit.");
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static string ReadName(Stream stream, uint length)
    {
        if (length is 0 or > 65536)
            throw new InvalidDataException("CPIO entry name exceeds the 64 KiB limit or has no terminator.");
        var data = ReadExact(stream, (int)length);
        if (data[^1] != 0 || data.AsSpan(0, data.Length - 1).Contains((byte)0))
            throw new InvalidDataException("CPIO entry name has invalid NUL termination.");
        return Encoding.UTF8.GetString(data, 0, data.Length - 1);
    }

    private static void SkipPadding(Stream stream, long offset, int align)
    {
        var pad = (align - (offset % align)) % align;
        if (pad == 0)
        {
            return;
        }

        Span<byte> scratch = stackalloc byte[8];
        while (pad > 0)
        {
            var step = (int)Math.Min(pad, scratch.Length);
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

        if (entry.DataLength > 0)
        {
            groupState.DataCarrierName = entry.Name;
            groupState.DataCarrier = entry;
            yield return entry;

            if (groupState.PendingEntries.Count > 0)
            {
                foreach (var pending in groupState.PendingEntries)
                {
                    yield return ToHardlinkEntry(pending, entry.Name, entry);
                }

                groupState.PendingEntries.Clear();
            }

            yield break;
        }

        if (groupState.DataCarrierName is { Length: > 0 } carrierName)
        {
            yield return ToHardlinkEntry(entry, carrierName, groupState.DataCarrier!);
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

    private static ArchiveEntry ToHardlinkEntry(ArchiveEntry source, string carrierName, ArchiveEntry carrier)
    {
        return new ArchiveEntry
        {
            Name = source.Name,
            LinkName = carrierName,
            Data = carrier.Data,
            DataStreamFactory = carrier.DataStreamFactory,
            DataStream = carrier.DataStream,
            DeclaredDataLength = carrier.DataLength,
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

        public ArchiveEntry? DataCarrier { get; set; }

        public List<ArchiveEntry> PendingEntries { get; } = [];
    }
}