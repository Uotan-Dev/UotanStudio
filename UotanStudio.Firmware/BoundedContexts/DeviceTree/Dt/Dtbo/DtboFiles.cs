using System.IO.Compression;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;

public static partial class DtboFormat
{
    /// <summary>Names the repack configuration file.<para>重打包配置文件名。</para></summary>
    public const string ManifestFileName = RepackMetadata.FileName;

    private const string OriginalFileName = "original.dtbo.img";
    private const int MaximumEntries = 65536;

    internal static System.Text.Json.Nodes.JsonObject CreateConfiguration(Stream input)
    {
        var (header, entries) = ReadTable(input);
        var manifest = new DtboManifest { Header = header, ImageSize = input.Length, HasOriginalImage = true };
        for (var i = 0; i < entries.Count; i++)
            manifest.Entries.Add(new() { File = $"overlay_{i:D2}.dtb", Metadata = entries[i] });
        return (System.Text.Json.Nodes.JsonObject)JsonSerializer.SerializeToNode(manifest, DtboJsonContext.Default.DtboManifest)!;
    }

    /// <summary>
    /// Reads and validates the table without loading blobs; offsets are absolute from stream start.
    /// <para>仅读取并校验条目表，不加载数据块；偏移以流起点为基准。</para>
    /// </summary>
    public static (DtboHeader Header, List<DtboEntry> Entries) ReadTable(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
            throw new ArgumentException("A readable, seekable stream is required.", nameof(input));
        if (input.Length < FdtConstants.DtboHeaderSize)
            throw new InvalidDataException("Truncated DTBO header.");
        input.Position = 0;
        Span<byte> bytes = stackalloc byte[32];
        input.ReadExactly(bytes);
        var header = DtboHeader.ReadFrom(bytes);
        ValidateHeader(header);
        if (header.TotalSize > input.Length || header.HeaderSize > header.TotalSize)
            throw new InvalidDataException("DTBO total size is outside the input.");
        if (header.EntryCount > MaximumEntries)
            throw new InvalidDataException("DTBO entry count exceeds 65536.");
        var tableEnd = checked((long)header.EntriesOffset + (long)header.EntryCount * header.EntrySize);
        if (tableEnd > header.TotalSize)
            throw new InvalidDataException("DTBO entry table is outside total_size.");
        input.Position = header.EntriesOffset;
        var entries = new List<DtboEntry>((int)header.EntryCount);
        for (var i = 0; i < header.EntryCount; i++)
        {
            input.ReadExactly(bytes);
            var entry = DtboEntry.ReadFrom(bytes);
            if (entry.DtOffset < tableEnd || entry.DtOffset > header.TotalSize
                || entry.DtSize > header.TotalSize - entry.DtOffset)
                throw new InvalidDataException($"DTBO entry {i} is outside the blob area.");
            _ = CompressionOf(header.Version, entry);
            entries.Add(entry);
        }
        return (header, entries);
    }

    /// <summary>
    /// Extracts overlays, their configuration and an original image for lossless unchanged repacking.
    /// <para>提取 overlay、配置和原始镜像，支持未修改时无损重打包。</para>
    /// </summary>
    public static IReadOnlyList<string> Unpack(string inputPath, string outputDirectory,
        UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new UnpackOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumOutputBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(destination);
        using var input = File.OpenRead(inputPath);
        var (header, entries) = ReadTable(input);
        var manifest = new DtboManifest { Header = header, ImageSize = input.Length, HasOriginalImage = true };
        for (var i = 0; i < entries.Count; i++)
            manifest.Entries.Add(new() { File = $"overlay_{i:D2}.dtb", Metadata = entries[i] });

        var names = manifest.Entries.Select(e => e.File).Append(OriginalFileName).Append(ManifestFileName).ToArray();
        foreach (var name in names)
        {
            var path = LocalFile(destination, name);
            if (SamePath(path, inputPath))
                throw new IOException("Extraction would overwrite its input image.");
            if (Directory.Exists(path) || (!options.Overwrite && File.Exists(path)))
                throw new IOException($"Output already exists: {path}");
            if (name == ManifestFileName && File.Exists(path) && !RepackMetadata.IsMetadataFile(path))
                throw new IOException("The output contains a file reserved for repack metadata.");
        }

        var staging = Path.Combine(destination, ".dtbo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                using var output = File.Create(Path.Combine(staging, manifest.Entries[i].File));
                CopyBlob(input, header.Version, entries[i], output, options.MaximumOutputBytes, cancellationToken);
                options.Progress?.Report(90.0 * (i + 1) / entries.Count, manifest.Entries[i].File);
            }
            using (var original = File.Create(Path.Combine(staging, OriginalFileName)))
            {
                input.Position = 0;
                StreamCopy.CopyExactly(input, original, input.Length, cancellationToken);
            }
            RepackMetadata.WriteAsync(new DtFormatHandler(), inputPath, staging, cancellationToken).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            // Publish the manifest last, after every blob has passed decompression and length checks.
            foreach (var name in names)
                File.Move(Path.Combine(staging, name), LocalFile(destination, name), options.Overwrite);
            options.Progress?.Report(100, "DTBO overlays and manifest extracted.");
            return names.Select(name => Path.Combine(destination, name)).ToArray();
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>
    /// Rebuilds a manifest directory, preserving metadata and compression; changed authenticated tails are rejected.
    /// <para>从配置目录重建并保留元数据和压缩；修改后拒绝复用认证尾部。</para>
    /// </summary>
    public static void PackDirectory(string inputDirectory, string outputPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetFullPath(inputDirectory);
        var metadata = RepackMetadata.Read(directory);
        if (metadata?.FormatName != new DtFormatHandler().FormatName
            || metadata.Configuration["entries"] is not System.Text.Json.Nodes.JsonArray)
            throw new InvalidDataException("Missing DTBO repack metadata.");
        var manifest = metadata.Configuration.Deserialize(DtboJsonContext.Default.DtboManifest)
            ?? throw new InvalidDataException("Missing DTBO manifest.");
        if (manifest.FormatVersion != 1 || manifest.Header is null || manifest.Entries is null
            || manifest.Entries.Count > MaximumEntries || manifest.ImageSize < 0)
            throw new InvalidDataException("Invalid or unsupported DTBO manifest.");
        ValidateHeader(manifest.Header);
        var inputs = manifest.Entries.Select(entry => entry is not null && entry.Metadata is not null
            ? LocalFile(directory, entry.File) : throw new InvalidDataException("Invalid DTBO manifest entry.")).ToArray();
        if (inputs.Any(path => SamePath(path, outputPath))
            || SamePath(outputPath, LocalFile(directory, ManifestFileName))
            || SamePath(outputPath, LocalFile(directory, OriginalFileName)))
            throw new IOException("DTBO output must not replace a manifest input.");

        using var original = manifest.HasOriginalImage ? File.OpenRead(LocalFile(directory, OriginalFileName)) : null;
        DtboHeader? originalHeader = null;
        List<DtboEntry>? originalEntries = null;
        if (original is not null)
            (originalHeader, originalEntries) = ReadTable(original);
        else if (manifest.Header.HeaderSize != 32 || manifest.Header.EntriesOffset != 32)
            throw new InvalidDataException("Extended DTBO headers require the original image.");

        AtomicWrite(outputPath, output =>
        {
            var scratchPath = output.Name + ".blobs";
            try
            {
                using var scratch = new FileStream(scratchPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                var unchanged = original is not null && original.Length == manifest.ImageSize
                    && originalHeader!.ToBytes().AsSpan().SequenceEqual(manifest.Header.ToBytes())
                    && originalEntries!.Count == manifest.Entries.Count;
                var sizes = new List<uint>(inputs.Length);
                for (var i = 0; i < inputs.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = manifest.Entries[i].Metadata;
                    var compression = CompressionOf(manifest.Header.Version, entry);
                    using var source = File.OpenRead(inputs[i]);
                    ValidateDtbFile(source);
                    var start = scratch.Position;
                    byte[] hash;
                    using (var encoder = CreateEncoder(scratch, compression))
                        hash = CopyAndHash(source, encoder ?? scratch, source.Length, cancellationToken, source.Length);
                    sizes.Add(checked((uint)(scratch.Position - start)));
                    if (unchanged)
                    {
                        unchanged = originalEntries![i].ToBytes().AsSpan().SequenceEqual(entry.ToBytes());
                        if (unchanged)
                        {
                            var originalHash = CopyBlob(original!, originalHeader!.Version, originalEntries[i],
                                Stream.Null, uint.MaxValue, cancellationToken);
                            unchanged = hash.AsSpan().SequenceEqual(originalHash);
                        }
                    }
                }
                if (unchanged)
                {
                    original!.Position = 0;
                    StreamCopy.CopyExactly(original, output, original.Length, cancellationToken);
                    return;
                }

                if (original is not null)
                {
                    if (manifest.Header.HeaderSize != originalHeader!.HeaderSize
                        || manifest.Header.EntriesOffset != originalHeader.EntriesOffset)
                        throw new InvalidDataException("Changing extended DTBO header layout is unsupported.");
                    EnsureZeroTail(original, originalHeader.TotalSize, cancellationToken);
                    original.Position = 0;
                    StreamCopy.CopyExactly(original, output, originalHeader.EntriesOffset, cancellationToken);
                }

                var header = DtboHeader.ReadFrom(manifest.Header.ToBytes());
                header.EntryCount = checked((uint)inputs.Length);
                var offset = checked((long)header.EntriesOffset + (long)header.EntryCount * 32);
                header.TotalSize = checked((uint)(offset + scratch.Length));
                var imageSize = manifest.ImageSize == 0 ? header.TotalSize : manifest.ImageSize;
                if (imageSize < header.TotalSize)
                    throw new InvalidDataException("Rebuilt DTBO exceeds imageSize; increase the unsigned image capacity explicitly.");
                output.SetLength(imageSize);
                output.Position = 0;
                output.Write(header.ToBytes());
                output.Position = header.EntriesOffset;
                for (var i = 0; i < inputs.Length; i++)
                {
                    var entry = DtboEntry.ReadFrom(manifest.Entries[i].Metadata.ToBytes());
                    entry.DtSize = sizes[i];
                    entry.DtOffset = checked((uint)offset);
                    output.Write(entry.ToBytes());
                    offset = checked(offset + sizes[i]);
                }
                scratch.Position = 0;
                StreamCopy.CopyExactly(scratch, output, scratch.Length, cancellationToken);
            }
            finally
            {
                File.Delete(scratchPath);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Validates all overlays and atomically copies an unchanged image, including its padding and authentication data.
    /// <para>校验全部 overlay 后原子重打包未修改镜像，保留填充及认证数据。</para>
    /// </summary>
    public static void Repack(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        // Close the source before the atomic rename so in-place repacking also works on Windows.
        AtomicWrite(outputPath, output =>
        {
            using var input = File.OpenRead(inputPath);
            var (header, entries) = ReadTable(input);
            foreach (var entry in entries)
                CopyBlob(input, header.Version, entry, Stream.Null, DefaultMaximumDecompressedBytes, cancellationToken);
            input.Position = 0;
            StreamCopy.CopyExactly(input, output, input.Length, cancellationToken);
        }, cancellationToken);
    }

    private static void ValidateHeader(DtboHeader header)
    {
        if (!header.IsValidMagic || header.Version > 1 || header.HeaderSize < 32
            || header.EntrySize != 32 || header.EntriesOffset < header.HeaderSize)
            throw new InvalidDataException("Invalid or unsupported DTBO header.");
    }

    private static uint CompressionOf(uint version, DtboEntry entry)
    {
        var compression = version == 0 ? CompressionNone : entry.Custom0 & CompressionFlagMask;
        if (compression > CompressionGzip)
            throw new InvalidDataException($"Unknown DTBO compression flags: 0x{compression:X}.");
        return compression;
    }

    private static Stream? CreateEncoder(Stream output, uint compression) => compression switch
    {
        CompressionZlib => new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true),
        CompressionGzip => new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true),
        _ => null
    };

    private static byte[] CopyBlob(Stream input, uint version, DtboEntry entry, Stream output,
        long limit, CancellationToken cancellationToken)
    {
        using var range = new BoundedReadStream(input, entry.DtOffset, entry.DtSize, leaveOpen: true);
        var compression = CompressionOf(version, entry);
        using Stream? decoder = compression switch
        {
            CompressionZlib => new ZLibStream(range, CompressionMode.Decompress, leaveOpen: true),
            CompressionGzip => new GZipStream(range, CompressionMode.Decompress, leaveOpen: true),
            _ => null
        };
        var crc = compression == CompressionGzip ? new System.IO.Hashing.Crc32() : null;
        var adler = compression == CompressionZlib ? new ICSharpCode.SharpZipLib.Checksum.Adler32() : null;
        long length = 0;
        var hash = CopyAndHash(decoder ?? range, output, limit, cancellationToken,
            compression == CompressionNone ? entry.DtSize : null, (buffer, count) =>
            {
                crc?.Append(buffer.AsSpan(0, count));
                adler?.Update(new ArraySegment<byte>(buffer, 0, count));
                length += count;
            });
        // .NET decompressors can accept EOF before the trailer; validate the trailer explicitly.
        if (compression != CompressionNone)
        {
            var trailerSize = compression == CompressionGzip ? 8 : 4;
            if (entry.DtSize < (compression == CompressionGzip ? 18 : 6))
                throw new InvalidDataException("Truncated DTBO compression wrapper.");
            Span<byte> trailer = stackalloc byte[8];
            input.Position = (long)entry.DtOffset + entry.DtSize - trailerSize;
            input.ReadExactly(trailer[..trailerSize]);
            if (compression == CompressionGzip
                ? BinaryPrimitives.ReadUInt32LittleEndian(trailer) != crc!.GetCurrentHashAsUInt32()
                    || BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) != unchecked((uint)length)
                : BinaryPrimitives.ReadUInt32BigEndian(trailer) != (uint)adler!.Value)
                throw new InvalidDataException("DTBO compression trailer checksum or size mismatch.");
        }
        return hash;
    }

    private static byte[] CopyAndHash(Stream input, Stream output, long limit, CancellationToken cancellationToken,
        long? exactLength = null, Action<byte[], int>? observe = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer);
            if (read == 0) break;
            if (read > limit - total)
                throw new InvalidDataException("DTBO overlay exceeds the output limit.");
            total += read;
            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            observe?.Invoke(buffer, read);
        }
        if (exactLength is not null && total != exactLength)
            throw new InvalidDataException("Truncated DTBO overlay.");
        return hash.GetHashAndReset();
    }

    private static void EnsureZeroTail(Stream input, long offset, CancellationToken cancellationToken)
    {
        input.Position = offset;
        var buffer = new byte[81920];
        while (input.Position < input.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, input.Length - input.Position);
            input.ReadExactly(buffer.AsSpan(0, count));
            if (buffer.AsSpan(0, count).ContainsAnyExcept((byte)0))
                throw new InvalidDataException("Modified DTBO has AVB or other nonzero trailing data. Remove authentication using an AVB-aware workflow before unpacking and editing.");
        }
    }

    private static void ValidateDtbFile(Stream input)
    {
        Span<byte> bytes = stackalloc byte[40];
        input.ReadExactly(bytes);
        var header = FdtHeader.ReadFrom(bytes);
        if (!header.IsValidMagic || header.TotalSize < 40 || header.TotalSize > input.Length
            || input.Length > uint.MaxValue || header.OffsetDtStruct > header.TotalSize
            || header.SizeDtStruct > header.TotalSize - header.OffsetDtStruct
            || header.OffsetDtStrings > header.TotalSize || header.SizeDtStrings > header.TotalSize - header.OffsetDtStrings
            || header.OffsetMemRsvmap > header.TotalSize || 16 > header.TotalSize - header.OffsetMemRsvmap)
            throw new InvalidDataException("Invalid DTB header or component bounds in manifest input.");
        input.Position = 0;
    }

    internal static void AtomicWrite(string outputPath, Action<FileStream> write, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                write(output);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string LocalFile(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ') || name.IndexOfAny(['/', '\\', ':']) >= 0
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.IsPathRooted(name))
            throw new InvalidDataException("DTBO manifest paths must be local filenames.");
        var path = Path.Combine(directory, name);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("DTBO manifest files must not be symbolic links.");
        return path;
    }

    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
