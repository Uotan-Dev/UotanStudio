using System.Buffers.Binary;
using System.Text.Json.Nodes;
using UotanStudio.Compress;
using UotanStudio.Compress.Codecs.Lzma;
using UotanStudio.Compress.Compressors;
using UotanStudio.Compress.Detection;
using UotanStudio.Firmware.Archive.Formats.Cpio;
using UotanStudio.Firmware.Archive.Internal;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.Archive.Writing;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Hash.Digests;

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
        catch (Exception ex) when (ex is InvalidDataException or ArchiveException)
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

    private FileStream OpenArchiveForRead(string path, CancellationToken cancellationToken,
        out CompressionFormat compression)
    {
        compression = CompressionFormat.None;
        var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var compressedPath = IsCompressedCpioPath(path);
        if (source.Length > MaximumArchiveBytes)
        {
            if (!compressedPath)
            {
                return source;
            }

            source.Dispose();
            throw new ArchiveCorruptedException("Compressed CPIO input exceeds the 8 GiB limit.");
        }

        var detected = DetectCpioCompression(source);
        if (detected == CompressionFormat.None)
        {
            if (!compressedPath)
            {
                return source;
            }

            source.Dispose();
            throw new ArchiveCorruptedException("Compressed CPIO framing is not recognized.");
        }

        if (compressedPath)
        {
            try
            {
                var scratch = DecompressToScratch(source, detected, cancellationToken);
                compression = detected;
                return scratch;
            }
            finally
            {
                source.Dispose();
            }
        }

        if (IsArchiveStream(source))
        {
            return source;
        }

        if (TryDecompressToArchiveScratch(source, detected, cancellationToken) is { } decoded)
        {
            compression = detected;
            source.Dispose();
            return decoded;
        }

        source.Position = 0;
        return source;
    }

    private FileStream? TryDecompressToArchiveScratch(Stream source, CompressionFormat compression,
        CancellationToken cancellationToken)
    {
        FileStream? scratch = null;
        try
        {
            scratch = DecompressToScratch(source, compression, cancellationToken);
            if (IsArchiveStream(scratch))
            {
                return scratch;
            }

            scratch.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scratch?.Dispose();
            return null;
        }
    }

    private FileStream DecompressToScratch(Stream source, CompressionFormat compression,
        CancellationToken cancellationToken)
    {
        var scratch = CreateScratch();
        try
        {
            source.Position = 0;
            CompressionService.Decompress(source, scratch, compression, MaximumArchiveBytes, cancellationToken);
            scratch.Position = 0;
            return scratch;
        }
        catch
        {
            scratch.Dispose();
            throw;
        }
    }

    private bool IsArchiveStream(Stream stream)
    {
        try
        {
            _ = _archive.DetectFormat(stream);
            return true;
        }
        catch (Exception ex) when (ex is ArchiveException or EndOfStreamException or NotSupportedException
            or ArgumentException or IOException)
        {
            return false;
        }
    }

    private static FileStream CreateScratch() => new(
        TempFileHelper.CreateScratchPath("uotan-cpio"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
        FileOptions.SequentialScan | FileOptions.DeleteOnClose);

    private List<ArchiveEntry> CollectValidatedEntries(Stream source, ArchiveFormat format,
        CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        long expanded = 0;
        var entries = new List<ArchiveEntry>();
        foreach (var entry in _archive.ReadEntriesStreaming(source, format))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = PathPatternMatcher.NormalizeArchivePath(entry.Name);
            if (names.Count >= MaximumEntries || name.Length > MaximumNameLength || !names.Add(name))
            {
                throw new ArchiveCorruptedException("CPIO archive exceeds the entry limit or contains duplicate paths.");
            }
            expanded = checked(expanded + entry.DataLength);
            if (expanded > MaximumArchiveBytes)
            {
                throw new ArchiveCorruptedException("CPIO entry payloads exceed the 8 GiB limit.");
            }
            entries.Add(entry);
        }
        return entries;
    }

    private void CaptureArchiveConfiguration(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken)
    {
        ArchiveFormat format;
        IReadOnlyList<ArchiveEntrySummary> sourceEntries;
        CompressionFormat compression;
        if (TryGetArchiveSnapshot(sourcePath) is { } snapshot)
        {
            format = snapshot.Format;
            compression = snapshot.Compression;
            sourceEntries = snapshot.Entries;
        }
        else
        {
            using var source = OpenArchiveForRead(sourcePath, cancellationToken, out compression);
            format = _archive.DetectFormat(source);
            if (format is ArchiveFormat.Tar or ArchiveFormat.Ustar)
            {
                metadata.PackingNotes = " TAR entries use the archive writer.";
                return;
            }
            sourceEntries = ReadSummaries(source, format, cancellationToken);
        }

        var entries = new JsonArray();
        Dictionary<string, PreservedInodeEntry> preserved = new(StringComparer.Ordinal);
        foreach (var entry in sourceEntries)
        {
            if (entries.Count >= MaximumEntries || entry.Name.Length > MaximumNameLength)
            {
                throw new ArchiveCorruptedException("CPIO metadata exceeds the entry or name limit.");
            }

            var header = entry.Metadata;
            entries.Add((JsonNode)new JsonObject
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
            preserved[PathPatternMatcher.NormalizeArchivePath(entry.Name)] = new PreservedInodeEntry
            {
                Path = PathPatternMatcher.NormalizeArchivePath(entry.Name),
                Size = entry.DataLength,
                Inode = header.Inode,
                Mode = (ushort)(header.Mode & 0xFFFF),
                Uid = header.UserId,
                Gid = header.GroupId,
            };
        }

        metadata.Configuration["cpio"] = new JsonObject
        {
            ["format"] = format.ToString(),
            ["compression"] = compression.ToString(),
            ["entries"] = entries,
        };
        FileSystemPreservation.CaptureArchivePayload(sourcePath, metadata,
            [.. preserved.Values], cancellationToken);
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
                throw new ArchiveCorruptedException("Invalid bzip2 block level.");
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
                throw new ArchiveUnsupportedFeatureException("Unsupported LZMA-alone coder properties.");
            }
            metadata.Configuration["cpio"]!["lzmaProperty"] = property;
            metadata.Configuration["cpio"]!["dictionarySize"] = dictionary;
            metadata.Configuration["cpio"]!["unknownSize"] = BinaryPrimitives.ReadInt64LittleEndian(header[5..]) == -1;
        }
    }

    /// <summary>
    /// Re-encodes the archive body as a single bzip2 stream after validating the block level recorded in the
    /// repack metadata. The managed encoder always writes the 900 kB block layout, so a source recorded with
    /// a smaller block size is re-encoded at level 9: the result stays a valid bzip2 stream and only the
    /// compression ratio is recomputed.
    /// <para>校验重打包元数据记录的块等级后，把归档主体重新编码为单条 bzip2 流。托管编码器固定写出
    /// 900 kB 块布局，因此记录为更小块等级的源会以等级 9 重新编码：结果仍是合法的 bzip2 流，仅压缩率
    /// 被重新计算。</para>
    /// </summary>
    /// <remarks>
    /// Encoding is delegated to <see cref="Bzip2Compressor.Compress(Stream, Stream)"/>, which appends the encoded stream at the
    /// current position of <paramref name="destination"/>, leaves that stream open for the caller's follow-up writes and never
    /// disposes <paramref name="source"/>. A read, encode or write failure surfaces as <see cref="CompressionException"/> instead
    /// of the compressor's own exception type.
    /// <para>编码委托给 <see cref="Bzip2Compressor.Compress(Stream, Stream)"/>：它在 <paramref name="destination"/> 的当前位置追加写出编码流，
    /// 保持该流打开以供调用方继续写入，并且不会释放 <paramref name="source"/>。读取、编码或写入失败会以 <see cref="CompressionException"/>
    /// 抛出，而不再是压缩库自身的异常类型。</para>
    /// </remarks>
    /// <param name="source">Uncompressed CPIO body positioned at its first byte.
    /// <para>定位在起始处的未压缩 CPIO 主体。</para></param>
    /// <param name="destination">Writable destination for the encoded stream, left open.
    /// <para>编码流的写入目标，保持打开。</para></param>
    /// <param name="blockLevel">Block level recorded from the source, accepted in the range 1-9.
    /// <para>从源记录的块等级，取值 1-9。</para></param>
    /// <exception cref="ArchiveCorruptedException"><paramref name="blockLevel"/> is outside 1-9.
    /// <para><paramref name="blockLevel"/> 超出 1-9。</para></exception>
    private static void WriteBzip2(Stream source, Stream destination, int blockLevel)
    {
        if (blockLevel is < 1 or > 9)
        {
            throw new ArchiveCorruptedException("Invalid stored bzip2 block level.");
        }

        Bzip2Compressor.Compress(source, destination);
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
            throw new ArchiveUnsupportedFeatureException("The stored CPIO dialect is unsupported.");
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
                throw new ArchiveUnsupportedFeatureException("The stored compression format is unsupported.");
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
        var fsConfig = ResolveFsConfig(inputPath, metadata);
        var files = EnumerateDirectoryEntries(inputPath, fsConfig, cancellationToken);
        if (files.Count > MaximumEntries || files.Any(entry => entry.Name.Length > MaximumNameLength))
        {
            throw new ArchiveCorruptedException("CPIO directory exceeds the entry or name limit.");
        }
        if (metadata?.Configuration["cpio"] is not JsonObject config)
        {
            return files;
        }

        var recorded = config["entries"] as JsonArray
            ?? throw new ArchiveCorruptedException("CPIO entry metadata is missing.");
        if (recorded.Count > MaximumEntries || files.Count > MaximumEntries)
        {
            throw new ArchiveCorruptedException("CPIO entry limit exceeded.");
        }

        var available = files.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var result = new List<ArchiveEntry>(files.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in recorded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is not JsonObject header)
            {
                throw new ArchiveCorruptedException("Invalid CPIO entry metadata.");
            }
            var name = (string?)header["name"] ?? throw new ArchiveCorruptedException("CPIO entry name is missing.");
            var projection = (string?)header["projection"] ?? name;
            if (name.Length > MaximumNameLength || projection.Length > MaximumNameLength
                || !seen.Add(projection))
            {
                throw new ArchiveCorruptedException("Invalid or duplicate CPIO entry metadata.");
            }
            ArchiveEntry entry;
            if (projection == ".")
            {
                if ((CpioFileType)header["type"]!.GetValue<int>() != CpioFileType.Directory)
                {
                    throw new ArchiveCorruptedException("CPIO root entry is not a directory.");
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
                throw new ArchiveCorruptedException($"CPIO entry '{name}' changed type.");
            }

            var linkName = entry.LinkName;
            if (linkFallback)
            {
                if (entry.DataLength > MaximumNameLength)
                {
                    throw new ArchiveCorruptedException($"Symbolic link '{name}' target exceeds the limit.");
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
                using var hasher = Sha256.CreateAccumulator();
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    hasher.Append(buffer.AsSpan(0, read));
                }
                var current = Convert.ToHexString(hasher.GetHashAndReset());
                if (digest is not null && digest != current)
                {
                    throw new ArchiveCorruptedException($"Hardlink group '{entry.Name}' contains different payloads.");
                }
                digest = current;
            }
        }
        return result;
    }

    /// <summary>
    /// Resolves the POSIX attribute table a CPIO repack applies to host paths the recorded entry headers do not
    /// cover. The structured preservation payload inside <c>metadata.json</c> is authoritative and is the only
    /// table used for an extraction this handler performed; an external <c>_fs_config</c> is consulted solely when
    /// the metadata document carries no payload, which a CPIO extraction never produces because this handler
    /// writes no flat sidecar at all.
    /// <para>解析 CPIO 重打包在记录的条目头未覆盖的宿主路径上使用的 POSIX 属性表。结构化保留负载位于
    /// <c>metadata.json</c> 内并具有权威性，是本处理器完成解包后唯一使用的表；只有当元数据文档不携带负载时才会
    /// 参考外部 <c>_fs_config</c>，而 CPIO 解包永远不会出现这种情况，因为本处理器不写出任何扁平附属文件。</para>
    /// </summary>
    /// <param name="inputPath">Directory being packed. <para>正在打包的目录。</para></param>
    /// <param name="metadata">Metadata read for that directory, or <see langword="null"/> for a directory build.
    /// <para>为该目录读取到的元数据；目录构建时为 <see langword="null"/>。</para></param>
    /// <returns>Ordinal path-keyed entries taken from the payload, or from an external sidecar when no payload
    /// exists; empty when neither is available.
    /// <para>按序号比较路径的条目表：取自负载；无负载时取自外部附属文件；两者都不存在时为空。</para></returns>
    private static IReadOnlyDictionary<string, FsConfigEntry> ResolveFsConfig(string inputPath, RepackMetadata? metadata)
        => FileSystemMetadataOverrides.TryLoadDocument(metadata) is { } document
            ? document.FsConfig
            : FileSystemMetadataOverrides.LoadFsConfig(
                RepackMetadata.DiscoverMetadataFile(inputPath, "_fs_config", metadata?.SourceFileName));

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
                        ?? throw new ArchiveCorruptedException("Missing XZ check type.");
                    if (dictionary is < 4096 or > 128 * 1024 * 1024 || check is not (1 or 4))
                    {
                        throw new ArchiveCorruptedException("Invalid stored XZ parameters.");
                    }
                    XzCompressor.Compress(raw, bounded, dictionary, check);
                }
                else if (compression == CompressionFormat.Bzip2 && metadata?.Configuration["cpio"] is JsonObject bzip2
                    && bzip2["blockLevel"] is not null)
                {
                    WriteBzip2(raw, bounded, bzip2["blockLevel"]!.GetValue<int>());
                }
                else if (compression == CompressionFormat.Lzma && metadata?.Configuration["cpio"] is JsonObject lzma
                    && lzma["lzmaProperty"] is not null)
                {
                    var property = lzma["lzmaProperty"]!.GetValue<byte>();
                    var dictionary = lzma["dictionarySize"]!.GetValue<uint>();
                    var unknownSize = lzma["unknownSize"]!.GetValue<bool>();
                    if (property >= 225 || dictionary is < 4096 or > 128 * 1024 * 1024)
                    {
                        throw new ArchiveCorruptedException("Invalid stored LZMA-alone properties.");
                    }
                    var encoder = new LzmaEncoder(
                        dictionarySize: (int)dictionary,
                        posStateBits: property / 45,
                        litContextBits: property % 9,
                        litPosBits: property % 45 / 9,
                        endMarker: unknownSize);
                    encoder.WriteCoderProperties(bounded);
                    Span<byte> size = stackalloc byte[8];
                    BinaryPrimitives.WriteInt64LittleEndian(size, unknownSize ? -1 : raw.Length);
                    bounded.Write(size);
                    encoder.Code(raw, bounded, -1, -1);
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
                throw new ArchiveCorruptedException("Invalid stored gzip header.");
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
            throw new ArchiveCorruptedException("Repacked CPIO payload differs from its encoded contents.");
        }
        raw.Position = 0;
        if (_archive.DetectFormat(raw) != format)
        {
            throw new ArchiveCorruptedException("Repacked CPIO dialect differs from the requested format.");
        }
        foreach (var _ in new CpioArchiveReader().ReadEntriesStreaming(raw, format,
            MaximumArchiveBytes, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static byte[] HashStream(Stream stream, CancellationToken cancellationToken)
    {
        using var hasher = Sha256.CreateAccumulator();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hasher.Append(buffer.AsSpan(0, read));
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
            throw new ArchiveCorruptedException("Invalid gzip header.");
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
                    throw new ArchiveCorruptedException("Gzip header exceeds 64 KiB.");
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
            throw new ArchiveUnsupportedFeatureException("Unsupported XZ stream flags.");
        }

        var headerSize = source.ReadByte();
        if (headerSize is < 1 or > 255)
        {
            throw new ArchiveUnsupportedFeatureException("Unsupported XZ block header.");
        }
        var block = new byte[checked((headerSize + 1) * 4)];
        block[0] = (byte)headerSize;
        source.ReadExactly(block.AsSpan(1));
        var flags = block[1];
        if ((flags & 0x3F) != 0)
        {
            throw new ArchiveUnsupportedFeatureException("XZ filter chain cannot be preserved.");
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
            throw new ArchiveUnsupportedFeatureException("Unsupported XZ LZMA2 filter.");
        }
        var property = block[cursor];
        if (property > 40)
        {
            throw new ArchiveCorruptedException("Invalid XZ dictionary property.");
        }
        var dictionary = property == 40
            ? uint.MaxValue
            : checked((uint)((ulong)(2 | (property & 1)) << ((property / 2) + 11)));
        if (dictionary > 128 * 1024 * 1024)
        {
            throw new ArchiveCorruptedException("XZ dictionary exceeds the 128 MiB rebuild limit.");
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
            throw new ArchiveCorruptedException("Invalid XZ block integer.");
        }
    }
}