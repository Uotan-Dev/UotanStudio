using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.GZip;
using SevenZip;
using SevenZip.Compression.LZMA;
using UotanStudio.Firmware.Archive.Formats.Cpio;
using UotanStudio.Firmware.Archive.Internal;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.Archive.Writing;
using UotanStudio.Firmware.Compression;
using UotanStudio.Firmware.Compression.Compressors;
using UotanStudio.Firmware.Compression.Detection;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.Archive;

public sealed partial class CpioFormatHandler
{
    private const long MaximumArchiveBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 65536;
    private const int MaximumNameLength = 4096;

    private static bool IsCompressedCpioPath(string path)
    {
        var name = Path.GetFileName(path);
        return new[] { ".gz", ".xz", ".zst", ".lz4", ".bz2", ".lzma", ".lzo" }
            .Any(suffix => name.EndsWith(".cpio" + suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static CompressionFormat DetectCpioCompression(Stream source)
    {
        var detected = CompressionDetector.Detect(source);
        return detected is CompressionFormat.Gzip or CompressionFormat.Xz or CompressionFormat.Zstd
            or CompressionFormat.Lz4 or CompressionFormat.Lz4Legacy or CompressionFormat.Bzip2
            or CompressionFormat.Lzma or CompressionFormat.Lzop ? detected : CompressionFormat.None;
    }

    private static bool ProbeCompressedCpio(Stream source)
    {
        var compression = DetectCpioCompression(source);
        if (compression == CompressionFormat.None)
        {
            return false;
        }
        using var probe = new CpioPrefixProbe();
        try
        {
            CompressionService.Decompress(source, probe, compression, MaximumArchiveBytes);
        }
        catch (Exception) when (probe.IsComplete)
        {
        }
        if (probe.Length < 6)
        {
            return false;
        }
        using var head = new MemoryStream(probe.ToArray());
        try
        {
            var format = new CpioArchiveReader().DetectFormat(head);
            return format is ArchiveFormat.NewAscii or ArchiveFormat.CrcAscii
                or ArchiveFormat.OldAscii or ArchiveFormat.Binary;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private sealed class CpioPrefixProbe : Stream
    {
        private readonly MemoryStream _prefix = new(6);
        public bool IsComplete => _prefix.Length == 6;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _prefix.Length;
        public override long Position { get => _prefix.Position; set => throw new NotSupportedException(); }
        public byte[] ToArray() => _prefix.ToArray();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var length = (int)Math.Min(6 - _prefix.Length, buffer.Length);
            _prefix.Write(buffer[..length]);
            if (IsComplete)
            {
                throw new InvalidDataException("CPIO prefix probe is complete.");
            }
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static FileStream OpenArchiveForRead(string path, CancellationToken cancellationToken)
    {
        var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!IsCompressedCpioPath(path))
        {
            return source;
        }

        try
        {
            if (source.Length > MaximumArchiveBytes)
            {
                throw new InvalidDataException("Compressed CPIO input exceeds the 8 GiB limit.");
            }
            var compression = DetectCpioCompression(source);
            if (compression == CompressionFormat.None)
            {
                throw new InvalidDataException("Compressed CPIO framing is not recognized.");
            }

            var scratch = CreateScratch();
            try
            {
                if (compression == CompressionFormat.Gzip)
                {
                    using var gzip = new GZipInputStream(source) { IsStreamOwner = false };
                    using var bounded = new CountingWriteStream(scratch, MaximumArchiveBytes, leaveOpen: true,
                        cancellationToken: cancellationToken);
                    gzip.CopyTo(bounded);
                }
                else
                {
                    CompressionService.Decompress(source, scratch, compression, MaximumArchiveBytes, cancellationToken);
                }
                scratch.Position = 0;
                return scratch;
            }
            catch
            {
                scratch.Dispose();
                throw;
            }
        }
        finally
        {
            source.Dispose();
        }
    }

    private static FileStream CreateScratch() => new(
        TempFileHelper.CreateScratchPath("uotan-cpio"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
        FileOptions.SequentialScan | FileOptions.DeleteOnClose);

    private void ValidateBeforeExtraction(Stream source, CancellationToken cancellationToken)
    {
        var format = _archive.DetectFormat(source);
        if (format is ArchiveFormat.Tar or ArchiveFormat.Ustar)
        {
            return;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        long expanded = 0;
        foreach (var entry in new CpioArchiveReader().ReadEntriesStreaming(source, format,
            MaximumArchiveBytes, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = PathPatternMatcher.NormalizeArchivePath(entry.Name);
            if (names.Count >= MaximumEntries || name.Length > MaximumNameLength || !names.Add(name))
            {
                throw new InvalidDataException("CPIO archive exceeds the entry limit or contains duplicate paths.");
            }
            expanded = checked(expanded + entry.DataLength);
            if (expanded > MaximumArchiveBytes)
            {
                throw new InvalidDataException("CPIO entry payloads exceed the 8 GiB limit.");
            }
        }
        source.Position = 0;
    }

    private void CaptureArchiveConfiguration(RepackMetadata metadata, string sourcePath, CancellationToken cancellationToken)
    {
        using var source = OpenArchiveForRead(sourcePath, cancellationToken);
        var format = _archive.DetectFormat(source);
        if (format is ArchiveFormat.Tar or ArchiveFormat.Ustar)
        {
            metadata.PackingNotes = " TAR entries use the archive writer.";
            return;
        }

        var compression = CompressionFormat.None;
        if (IsCompressedCpioPath(sourcePath))
        {
            using var compressed = File.OpenRead(sourcePath);
            compression = DetectCpioCompression(compressed);
        }

        var entries = new JsonArray();
        foreach (var entry in new CpioArchiveReader().ReadEntriesStreaming(source, format, MaximumArchiveBytes, cancellationToken))
        {
            if (entries.Count >= MaximumEntries || entry.Name.Length > MaximumNameLength)
            {
                throw new InvalidDataException("CPIO metadata exceeds the entry or name limit.");
            }

            var header = entry.Metadata;
            entries.Add(new JsonObject
            {
                ["name"] = entry.Name,
                ["projection"] = PathPatternMatcher.NormalizeArchivePath(entry.Name),
                ["inode"] = header.Inode,
                ["mode"] = header.Mode,
                ["uid"] = header.UserId,
                ["gid"] = header.GroupId,
                ["nlink"] = header.LinkCount,
                ["mtime"] = header.ModificationTimeUnixSeconds,
                ["devMajor"] = header.DeviceMajor,
                ["devMinor"] = header.DeviceMinor,
                ["rdevMajor"] = header.RDeviceMajor,
                ["rdevMinor"] = header.RDeviceMinor,
                ["type"] = (int)header.FileType,
            });
        }

        metadata.Configuration["cpio"] = new JsonObject
        {
            ["format"] = format.ToString(),
            ["compression"] = compression.ToString(),
            ["entries"] = entries,
        };
        if (compression == CompressionFormat.Gzip)
        {
            using var compressed = File.OpenRead(sourcePath);
            var header = ReadGzipHeader(compressed);
            metadata.Configuration["cpio"]!["gzipHeader"] = Convert.ToHexString(header);
            metadata.Configuration["cpio"]!["level"] = header[8] switch { 2 => 9, 4 => 1, _ => 6 };
        }
        if (compression == CompressionFormat.Xz)
        {
            using var compressed = File.OpenRead(sourcePath);
            var (dictionarySize, checkType) = ReadXzParameters(compressed);
            metadata.Configuration["cpio"]!["dictionarySize"] = dictionarySize;
            metadata.Configuration["cpio"]!["checkType"] = checkType;
        }
        if (compression == CompressionFormat.Bzip2)
        {
            using var compressed = File.OpenRead(sourcePath);
            Span<byte> header = stackalloc byte[4];
            compressed.ReadExactly(header);
            if (!header[..3].SequenceEqual("BZh"u8) || header[3] is < (byte)'1' or > (byte)'9')
            {
                throw new InvalidDataException("Invalid bzip2 block level.");
            }
            metadata.Configuration["cpio"]!["blockLevel"] = header[3] - '0';
        }
        if (compression == CompressionFormat.Lzma)
        {
            using var compressed = File.OpenRead(sourcePath);
            Span<byte> header = stackalloc byte[13];
            compressed.ReadExactly(header);
            var property = header[0];
            var dictionary = BinaryPrimitives.ReadUInt32LittleEndian(header[1..5]);
            if (property >= 225 || dictionary is < 4096 or > 128 * 1024 * 1024)
            {
                throw new InvalidDataException("Unsupported LZMA-alone coder properties.");
            }
            metadata.Configuration["cpio"]!["lzmaProperty"] = property;
            metadata.Configuration["cpio"]!["dictionarySize"] = dictionary;
            metadata.Configuration["cpio"]!["unknownSize"] = BinaryPrimitives.ReadInt64LittleEndian(header[5..]) == -1;
        }
    }

    private static ArchiveFormat ResolveArchiveFormat(RepackMetadata? metadata)
    {
        if (metadata?.Configuration["cpio"] is not JsonObject config)
        {
            return ArchiveFormat.NewAscii;
        }

        if (!Enum.TryParse<ArchiveFormat>((string?)config["format"], out var format)
            || format is not (ArchiveFormat.NewAscii or ArchiveFormat.CrcAscii or ArchiveFormat.OldAscii or ArchiveFormat.Binary))
        {
            throw new InvalidDataException("The stored CPIO dialect is unsupported.");
        }
        return format;
    }

    private static CompressionFormat ResolveCompression(RepackMetadata? metadata, string outputPath)
    {
        if (metadata?.Configuration["cpio"] is JsonObject config)
        {
            if (!Enum.TryParse<CompressionFormat>((string?)config["compression"], out var format)
                || format is not (CompressionFormat.None or CompressionFormat.Gzip or CompressionFormat.Xz
                    or CompressionFormat.Zstd or CompressionFormat.Lz4 or CompressionFormat.Lz4Legacy
                    or CompressionFormat.Bzip2 or CompressionFormat.Lzma or CompressionFormat.Lzop))
            {
                throw new InvalidDataException("The stored compression format is unsupported.");
            }
            return format;
        }

        if (!IsCompressedCpioPath(outputPath))
        {
            return CompressionFormat.None;
        }
        return Path.GetExtension(outputPath).ToLowerInvariant() switch
        {
            ".gz" => CompressionFormat.Gzip,
            ".xz" => CompressionFormat.Xz,
            ".zst" => CompressionFormat.Zstd,
            ".lz4" => CompressionFormat.Lz4,
            ".bz2" => CompressionFormat.Bzip2,
            ".lzma" => CompressionFormat.Lzma,
            ".lzo" => CompressionFormat.Lzop,
            _ => CompressionFormat.None,
        };
    }

    private static List<ArchiveEntry> BuildRepackEntries(string inputPath, RepackMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var fsConfig = FileSystemMetadataOverrides.LoadFsConfig(
            RepackMetadata.DiscoverMetadataFile(inputPath, "_fs_config"));
        var files = EnumerateDirectoryEntries(inputPath, fsConfig, cancellationToken);
        if (files.Count > MaximumEntries || files.Any(entry => entry.Name.Length > MaximumNameLength))
        {
            throw new InvalidDataException("CPIO directory exceeds the entry or name limit.");
        }
        if (metadata?.Configuration["cpio"] is not JsonObject config)
        {
            return files;
        }

        var recorded = config["entries"] as JsonArray
            ?? throw new InvalidDataException("CPIO entry metadata is missing.");
        if (recorded.Count > MaximumEntries || files.Count > MaximumEntries)
        {
            throw new InvalidDataException("CPIO entry limit exceeded.");
        }

        var available = files.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var result = new List<ArchiveEntry>(files.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in recorded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is not JsonObject header)
            {
                throw new InvalidDataException("Invalid CPIO entry metadata.");
            }
            var name = (string?)header["name"] ?? throw new InvalidDataException("CPIO entry name is missing.");
            var projection = (string?)header["projection"] ?? name;
            if (name.Length > MaximumNameLength || projection.Length > MaximumNameLength
                || !seen.Add(projection))
            {
                throw new InvalidDataException("Invalid or duplicate CPIO entry metadata.");
            }
            ArchiveEntry entry;
            if (projection == ".")
            {
                if ((CpioFileType)header["type"]!.GetValue<int>() != CpioFileType.Directory)
                {
                    throw new InvalidDataException("CPIO root entry is not a directory.");
                }
                var root = Path.GetFullPath(inputPath);
                entry = BuildDirectoryEntry(root, root, fsConfig);
            }
            else if (!available.Remove(projection, out entry!))
            {
                continue;
            }

            var originalType = (CpioFileType)header["type"]!.GetValue<int>();
            var linkFallback = originalType == CpioFileType.SymbolicLink
                && entry.Metadata.FileType == CpioFileType.Regular;
            if (originalType != entry.Metadata.FileType
                && !(originalType == CpioFileType.HardLink && entry.Metadata.FileType == CpioFileType.Regular)
                && !linkFallback
                && !(originalType is CpioFileType.CharacterDevice or CpioFileType.BlockDevice or CpioFileType.Fifo
                    && entry.Metadata.FileType == CpioFileType.Regular && entry.DataLength == 0))
            {
                throw new InvalidDataException($"CPIO entry '{name}' changed type.");
            }

            var linkName = entry.LinkName;
            if (linkFallback)
            {
                if (entry.DataLength > MaximumNameLength)
                {
                    throw new InvalidDataException($"Symbolic link '{name}' target exceeds the limit.");
                }
                using var fallback = entry.DataStreamFactory!();
                var bytes = new byte[checked((int)entry.DataLength)];
                fallback.ReadExactly(bytes);
                linkName = System.Text.Encoding.UTF8.GetString(bytes);
            }
            result.Add(new ArchiveEntry
            {
                Name = name,
                LinkName = linkName,
                Data = linkFallback ? [] : entry.Data,
                DataStreamFactory = linkFallback ? null : entry.DataStreamFactory,
                DeclaredDataLength = linkFallback ? null : entry.DeclaredDataLength,
                Metadata = new ArchiveEntryMetadata
                {
                    Inode = header["inode"]!.GetValue<ulong>(),
                    Mode = header["mode"]!.GetValue<uint>(),
                    UserId = header["uid"]!.GetValue<uint>(),
                    GroupId = header["gid"]!.GetValue<uint>(),
                    LinkCount = header["nlink"]!.GetValue<ulong>(),
                    ModificationTimeUnixSeconds = header["mtime"]!.GetValue<long>(),
                    DeviceMajor = header["devMajor"]!.GetValue<uint>(),
                    DeviceMinor = header["devMinor"]!.GetValue<uint>(),
                    RDeviceMajor = header["rdevMajor"]!.GetValue<uint>(),
                    RDeviceMinor = header["rdevMinor"]!.GetValue<uint>(),
                    FileType = originalType == CpioFileType.HardLink ? CpioFileType.Regular : originalType,
                    UnixPermissions = (int)(header["mode"]!.GetValue<uint>() & 0xFFF),
                },
            });
        }
        result.AddRange(available.Values);
        var hardlinks = result.Where(entry => entry.Metadata is { FileType: CpioFileType.Regular, LinkCount: > 1, Inode: > 0 })
            .GroupBy(entry => (entry.Metadata.Inode, entry.Metadata.DeviceMajor, entry.Metadata.DeviceMinor));
        foreach (var group in hardlinks)
        {
            string? digest = null;
            foreach (var entry in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = entry.DataStreamFactory?.Invoke();
                if (stream is null)
                {
                    continue;
                }
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    hasher.AppendData(buffer.AsSpan(0, read));
                }
                var current = Convert.ToHexString(hasher.GetHashAndReset());
                if (digest is not null && digest != current)
                {
                    throw new InvalidDataException($"Hardlink group '{entry.Name}' contains different payloads.");
                }
                digest = current;
            }
        }
        return result;
    }

    private byte[] WriteArchive(Stream destination, List<ArchiveEntry> entries, ArchiveFormat format,
        CompressionFormat compression, RepackMetadata? metadata, CancellationToken cancellationToken)
    {
        using var raw = CreateScratch();
        using (var bounded = new CountingWriteStream(raw, MaximumArchiveBytes, leaveOpen: true,
            cancellationToken: cancellationToken))
        {
            _archive.SaveAll(bounded, entries, format, new WriteOptions());
        }
        cancellationToken.ThrowIfCancellationRequested();
        raw.Position = 0;
        var expectedHash = HashStream(raw, cancellationToken);
        raw.Position = 0;
        using var encoded = CreateScratch();
        using (var bounded = new CountingWriteStream(encoded, MaximumArchiveBytes, leaveOpen: true,
            cancellationToken: cancellationToken))
        {
            var level = metadata?.Configuration["cpio"]?["level"]?.GetValue<int>();
            try
            {
                if (compression == CompressionFormat.Xz && metadata?.Configuration["cpio"] is JsonObject xz
                    && xz["dictionarySize"] is not null)
                {
                    var dictionary = xz["dictionarySize"]!.GetValue<uint>();
                    var check = xz["checkType"]?.GetValue<byte>()
                        ?? throw new InvalidDataException("Missing XZ check type.");
                    if (dictionary is < 4096 or > 128 * 1024 * 1024 || check is not (1 or 4))
                    {
                        throw new InvalidDataException("Invalid stored XZ parameters.");
                    }
                    XzCompressor.Compress(raw, bounded, dictionary, check);
                }
                else if (compression == CompressionFormat.Bzip2 && metadata?.Configuration["cpio"] is JsonObject bzip2
                    && bzip2["blockLevel"] is not null)
                {
                    var blockLevel = bzip2["blockLevel"]!.GetValue<int>();
                    if (blockLevel is < 1 or > 9)
                    {
                        throw new InvalidDataException("Invalid stored bzip2 block level.");
                    }
                    using var encoder = new BZip2OutputStream(bounded, blockLevel) { IsStreamOwner = false };
                    raw.CopyTo(encoder);
                }
                else if (compression == CompressionFormat.Lzma && metadata?.Configuration["cpio"] is JsonObject lzma
                    && lzma["lzmaProperty"] is not null)
                {
                    var property = lzma["lzmaProperty"]!.GetValue<byte>();
                    var dictionary = lzma["dictionarySize"]!.GetValue<uint>();
                    var unknownSize = lzma["unknownSize"]!.GetValue<bool>();
                    if (property >= 225 || dictionary is < 4096 or > 128 * 1024 * 1024)
                    {
                        throw new InvalidDataException("Invalid stored LZMA-alone properties.");
                    }
                    var encoder = new Encoder();
                    encoder.SetCoderProperties(
                        [CoderPropID.DictionarySize, CoderPropID.PosStateBits, CoderPropID.LitPosBits,
                            CoderPropID.LitContextBits, CoderPropID.EndMarker],
                        [(int)dictionary, property / 45, property % 45 / 9, property % 9, unknownSize]);
                    encoder.WriteCoderProperties(bounded);
                    Span<byte> size = stackalloc byte[8];
                    BinaryPrimitives.WriteInt64LittleEndian(size, unknownSize ? -1 : raw.Length);
                    bounded.Write(size);
                    encoder.Code(raw, bounded, -1, -1, null);
                }
                else
                {
                    CompressionService.Compress(raw, bounded, compression,
                        level is null ? null : new CompressionOptions { Level = level });
                }
            }
            catch (CompressionException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }
        encoded.Position = 0;
        if (compression == CompressionFormat.Gzip && metadata?.Configuration["cpio"]?["gzipHeader"] is JsonNode savedHeader)
        {
            var header = Convert.FromHexString(savedHeader.GetValue<string>());
            if (header.Length < 10 || header.Length > 65536
                || header[0] != 0x1F || header[1] != 0x8B || header[2] != 8)
            {
                throw new InvalidDataException("Invalid stored gzip header.");
            }
            encoded.Position = 10;
            using var bounded = new CountingWriteStream(destination, MaximumArchiveBytes,
                leaveOpen: true, cancellationToken: cancellationToken);
            bounded.Write(header);
            encoded.CopyTo(bounded);
            return expectedHash;
        }
        using var output = new CountingWriteStream(destination, MaximumArchiveBytes,
            leaveOpen: true, cancellationToken: cancellationToken);
        encoded.CopyTo(output);
        return expectedHash;
    }

    private void VerifyArchive(string path, ArchiveFormat format, CompressionFormat compression,
        byte[] expectedHash, CancellationToken cancellationToken)
    {
        using var source = File.OpenRead(path);
        using var raw = CreateScratch();
        CompressionService.Decompress(source, raw, compression, MaximumArchiveBytes, cancellationToken);
        raw.Position = 0;
        if (!HashStream(raw, cancellationToken).AsSpan().SequenceEqual(expectedHash))
        {
            throw new InvalidDataException("Repacked CPIO payload differs from its encoded contents.");
        }
        raw.Position = 0;
        if (_archive.DetectFormat(raw) != format)
        {
            throw new InvalidDataException("Repacked CPIO dialect differs from the requested format.");
        }
        foreach (var _ in new CpioArchiveReader().ReadEntriesStreaming(raw, format,
            MaximumArchiveBytes, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static byte[] HashStream(Stream stream, CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hasher.AppendData(buffer.AsSpan(0, read));
        }
        return hasher.GetHashAndReset();
    }

    private static byte[] ReadGzipHeader(Stream source)
    {
        using var captured = new MemoryStream();
        var header = new byte[10];
        source.ReadExactly(header);
        if (header[0] != 0x1F || header[1] != 0x8B || header[2] != 8 || (header[3] & 0xE0) != 0)
        {
            throw new InvalidDataException("Invalid gzip header.");
        }
        captured.Write(header);
        var flags = header[3];
        if ((flags & 4) != 0)
        {
            var length = new byte[2];
            source.ReadExactly(length);
            captured.Write(length);
            var extra = new byte[length[0] | length[1] << 8];
            source.ReadExactly(extra);
            captured.Write(extra);
        }
        if ((flags & 8) != 0)
        {
            ReadTerminated();
        }
        if ((flags & 16) != 0)
        {
            ReadTerminated();
        }
        if ((flags & 2) != 0)
        {
            Span<byte> crc = stackalloc byte[2];
            source.ReadExactly(crc);
            captured.Write(crc);
        }
        return captured.ToArray();

        void ReadTerminated()
        {
            do
            {
                if (captured.Length >= 65536)
                {
                    throw new InvalidDataException("Gzip header exceeds 64 KiB.");
                }
                var value = source.ReadByte();
                if (value < 0)
                {
                    throw new EndOfStreamException("Truncated gzip header.");
                }
                captured.WriteByte((byte)value);
                if (value == 0)
                {
                    return;
                }
            } while (true);
        }
    }

    private static (uint DictionarySize, byte CheckType) ReadXzParameters(Stream source)
    {
        Span<byte> streamHeader = stackalloc byte[12];
        source.ReadExactly(streamHeader);
        if (!streamHeader[..6].SequenceEqual((ReadOnlySpan<byte>)[0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00])
            || streamHeader[6] != 0 || streamHeader[7] is not (1 or 4))
        {
            throw new InvalidDataException("Unsupported XZ stream flags.");
        }

        var headerSize = source.ReadByte();
        if (headerSize is < 1 or > 255)
        {
            throw new InvalidDataException("Unsupported XZ block header.");
        }
        var block = new byte[checked((headerSize + 1) * 4)];
        block[0] = (byte)headerSize;
        source.ReadExactly(block.AsSpan(1));
        var flags = block[1];
        if ((flags & 0x3F) != 0)
        {
            throw new InvalidDataException("XZ filter chain cannot be preserved.");
        }
        var cursor = 2;
        if ((flags & 0x40) != 0)
        {
            ReadVli();
        }
        if ((flags & 0x80) != 0)
        {
            ReadVli();
        }
        if (ReadVli() != 0x21 || ReadVli() != 1 || cursor >= block.Length - 4)
        {
            throw new InvalidDataException("Unsupported XZ LZMA2 filter.");
        }
        var property = block[cursor];
        if (property > 40)
        {
            throw new InvalidDataException("Invalid XZ dictionary property.");
        }
        var dictionary = property == 40
            ? uint.MaxValue
            : checked((uint)((ulong)(2 | (property & 1)) << ((property / 2) + 11)));
        if (dictionary > 128 * 1024 * 1024)
        {
            throw new InvalidDataException("XZ dictionary exceeds the 128 MiB rebuild limit.");
        }
        return (dictionary, streamHeader[7]);

        ulong ReadVli()
        {
            ulong value = 0;
            for (var i = 0; i < 9; i++)
            {
                if (cursor >= block.Length - 4)
                {
                    break;
                }
                var current = block[cursor++];
                value |= (ulong)(current & 0x7F) << (i * 7);
                if ((current & 0x80) == 0)
                {
                    return value;
                }
            }
            throw new InvalidDataException("Invalid XZ block integer.");
        }
    }
}