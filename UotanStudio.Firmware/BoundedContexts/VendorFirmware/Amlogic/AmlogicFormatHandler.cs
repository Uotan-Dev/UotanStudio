using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Amlogic;

public enum AmlogicImageVersion : uint
{
    V1 = 1,
    V2 = 2
}

public sealed class AmlogicPackOptions
{
    public AmlogicImageVersion Version { get; init; } = AmlogicImageVersion.V2;
    public uint Alignment { get; init; } = 4;
    public IReadOnlyCollection<string>? VerifiedEntries { get; init; }
    public bool VerifyOutput { get; init; } = true;
}

/// <summary>Reads, validates, converts and creates AMLogic USB burning images.</summary>
public sealed class AmlogicFormatHandler : IFormatHandler
{
    private const uint Magic = 0x27B51956;
    private const long MaximumItemBytes = 16L * 1024 * 1024 * 1024;
    private const int MaximumItems = 65_536;
    private const int VerifyDataSize = 48;

    public string FormatName => "AMLogic ampack";
    public IReadOnlyList<string> FileExtensions => ["img", "aml", "ampack"];

    public bool CanHandle(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            if (stream.Length < 12)
            {
                return false;
            }

            Span<byte> bytes = stackalloc byte[4];
            stream.Position = 8;
            stream.ReadExactly(bytes);
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic;
        }
        catch
        {
            return false;
        }
    }

    public async Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var image = await ParseAsync(filePath, cancellationToken).ConfigureAwait(false);
            return FirmwareOpResult.Ok(
                $"AMLogic ampack v{(uint)image.Version} metadata confirmed ({image.Items.Count} items).",
                entries: [.. image.Items.Where(item => !item.IsVerify).Select(GetEntryName)],
                metadata: new Dictionary<string, string>
                {
                    ["MagicOffset"] = "8",
                    ["Magic"] = $"0x{Magic:X8}",
                    ["Version"] = ((uint)image.Version).ToString(),
                    ["Alignment"] = image.Alignment.ToString(),
                    ["ItemCount"] = image.Items.Count.ToString(),
                    ["Length"] = image.Length.ToString(),
                    ["Crc32"] = $"0x{image.Crc32:X8}"
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    public async Task<FirmwareOpResult> UnpackAsync(
        string filePath,
        string outputDirectory,
        UnpackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new UnpackOptions();
        try
        {
            var image = await ParseAsync(filePath, cancellationToken).ConfigureAwait(false);
            await VerifyImageAsync(filePath, image, verifyPartitionHashes: true, cancellationToken).ConfigureAwait(false);

            var selected = options.CreateSelectedEntrySet();
            var payloadItems = image.Items.Where(item => !item.IsVerify &&
                (selected is null || selected.Contains(GetEntryName(item)))).ToArray();
            if (selected is not null)
            {
                var missing = selected.Except(payloadItems.Select(GetEntryName), StringComparer.OrdinalIgnoreCase).ToArray();
                if (missing.Length > 0)
                {
                    throw new InvalidDataException($"AMLogic entries were not found: {string.Join(", ", missing)}.");
                }
            }

            var outputs = payloadItems.Select(item =>
            {
                if (item.Size > options.MaximumOutputBytes)
                {
                    throw new InvalidDataException($"AMLogic item '{GetEntryName(item)}' exceeds the output budget.");
                }

                return (Item: item, Path: FileExtraction.ResolvePath(outputDirectory, GetEntryName(item)));
            }).ToArray();

            if (!options.Overwrite && outputs.Any(output => File.Exists(output.Path)))
            {
                throw new IOException("One or more AMLogic output files already exist and overwrite is disabled.");
            }

            await using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            foreach (var output in outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await FileExtraction.WriteAtomicAsync(output.Path, async target =>
                {
                    input.Position = output.Item.Offset;
                    await CopyExactlyAsync(input, target, output.Item.Size, hash: null, cancellationToken).ConfigureAwait(false);
                }, options.Overwrite, cancellationToken).ConfigureAwait(false);
            }

            return FirmwareOpResult.Ok(
                $"Extracted {outputs.Length} AMLogic items after CRC and VERIFY validation.",
                entries: [.. outputs.Select(output => GetEntryName(output.Item))]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => PackAsync(inputPath, outputPath, new AmlogicPackOptions(), cancellationToken);

    public async Task<FirmwareOpResult> PackAsync(
        string inputPath,
        string outputPath,
        AmlogicPackOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Directory.Exists(inputPath))
        {
            return FirmwareOpResult.Fail("AMLogic pack input must be a directory.");
        }

        try
        {
            ValidatePackOptions(options);
            HashSet<string> verified = options.VerifiedEntries is null
                ? []
                : new(options.VerifiedEntries, StringComparer.OrdinalIgnoreCase);
            var sources = Directory.EnumerateFiles(inputPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).Equals("image.cfg", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .Select(path => CreateFileSource(path, verified.Contains(Path.GetFileName(path))))
                .ToArray();
            if (sources.Length == 0)
            {
                throw new InvalidDataException("AMLogic pack input contains no items.");
            }

            var unknownVerified = verified.Except(sources.Select(GetEntryName), StringComparer.OrdinalIgnoreCase).ToArray();
            if (unknownVerified.Length > 0)
            {
                throw new InvalidDataException($"AMLogic verified entries were not found: {string.Join(", ", unknownVerified)}.");
            }

            await WriteImageAsync(sources, outputPath, options, cancellationToken).ConfigureAwait(false);
            if (options.VerifyOutput)
            {
                var verification = await VerifyAsync(outputPath, verifyPartitionHashes: true, cancellationToken).ConfigureAwait(false);
                if (!verification.Success)
                {
                    throw new InvalidDataException(verification.Summary);
                }
            }

            return FirmwareOpResult.Ok(
                $"Packed {sources.Length} AMLogic items as v{(uint)options.Version} with {options.Alignment}-byte alignment.",
                entries: [.. sources.Select(GetEntryName)]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    public async Task<FirmwareOpResult> ConvertAsync(
        string inputPath,
        string outputPath,
        AmlogicPackOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            ValidatePackOptions(options);
            var image = await ParseAsync(inputPath, cancellationToken).ConfigureAwait(false);
            await VerifyImageAsync(inputPath, image, verifyPartitionHashes: true, cancellationToken).ConfigureAwait(false);
            var verifiedSubTypes = image.Items.Where(item => item.IsVerify).Select(item => item.SubType)
                .ToHashSet(StringComparer.Ordinal);
            var sources = image.Items.Where(item => !item.IsVerify)
                .Select(item => new AmlogicSource(inputPath, item.Offset, item.Size, item.MainType, item.SubType,
                    verifiedSubTypes.Contains(item.SubType)))
                .ToArray();
            await WriteImageAsync(sources, outputPath, options, cancellationToken).ConfigureAwait(false);
            return FirmwareOpResult.Ok(
                $"Converted AMLogic v{(uint)image.Version} to v{(uint)options.Version} with {options.Alignment}-byte alignment.",
                entries: [.. sources.Select(GetEntryName)]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    public Task<FirmwareOpResult> VerifyAsync(string filePath, CancellationToken cancellationToken = default)
        => VerifyAsync(filePath, verifyPartitionHashes: true, cancellationToken);

    public async Task<FirmwareOpResult> VerifyAsync(
        string filePath,
        bool verifyPartitionHashes,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var image = await ParseAsync(filePath, cancellationToken).ConfigureAwait(false);
            await VerifyImageAsync(filePath, image, verifyPartitionHashes, cancellationToken).ConfigureAwait(false);
            var hashSummary = verifyPartitionHashes ? " and partition SHA-1 records" : string.Empty;
            return FirmwareOpResult.Ok($"AMLogic v{(uint)image.Version} CRC{hashSummary} verified for {image.Items.Count} items.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    private static async Task<AmlImage> ParseAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length < 12)
        {
            throw new InvalidDataException("AMLogic header is truncated.");
        }

        var prefix = new byte[12];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(8)) != Magic)
        {
            throw new InvalidDataException("AMLogic magic is invalid.");
        }

        var versionValue = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(4));
        if (versionValue is not ((uint)AmlogicImageVersion.V1) and not ((uint)AmlogicImageVersion.V2))
        {
            throw new InvalidDataException($"Unsupported AMLogic image version: {versionValue}.");
        }

        var version = (AmlogicImageVersion)versionValue;
        var headerSize = GetHeaderSize(version);
        var itemSize = GetItemRecordSize(version);
        var header = new byte[headerSize];
        prefix.CopyTo(header, 0);
        await stream.ReadExactlyAsync(header.AsMemory(prefix.Length), cancellationToken).ConfigureAwait(false);
        var alignment = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
        if (alignment is not (4 or 8))
        {
            throw new InvalidDataException($"Unsupported AMLogic item alignment: {alignment}.");
        }
        if (count == 0 || count > MaximumItems)
        {
            throw new InvalidDataException("AMLogic item count is outside the limit.");
        }

        var tableLength = checked((long)count * itemSize);
        FileExtraction.ValidateRange(headerSize, tableLength, stream.Length, "AMLogic item table");
        var items = new List<AmlItem>(checked((int)count));
        var record = new byte[itemSize];
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.ReadExactlyAsync(record, cancellationToken).ConfigureAwait(false);
            var offset = version == AmlogicImageVersion.V1
                ? BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(16))
                : BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(8));
            var size = version == AmlogicImageVersion.V1
                ? BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(24))
                : BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(16));
            var nameOffset = version == AmlogicImageVersion.V1 ? 32 : 24;
            var nameSize = version == AmlogicImageVersion.V1 ? 32 : 256;
            var verifyOffset = version == AmlogicImageVersion.V1 ? 96 : 536;
            var main = ReadName(record.AsSpan(nameOffset, nameSize));
            var sub = ReadName(record.AsSpan(nameOffset + nameSize, nameSize));
            var verify = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(verifyOffset));
            items.Add(new AmlItem(index, checked((long)offset), checked((long)size), main, sub,
                verify != 0 || main.Equals("VERIFY", StringComparison.Ordinal)));
        }

        return new AmlImage(
            version,
            BinaryPrimitives.ReadUInt32LittleEndian(header),
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(12)),
            alignment,
            headerSize,
            itemSize,
            items,
            stream.Length);
    }

    private static async Task VerifyImageAsync(
        string path,
        AmlImage image,
        bool verifyPartitionHashes,
        CancellationToken cancellationToken)
    {
        if (image.DeclaredSize != checked((ulong)image.Length))
        {
            throw new InvalidDataException($"AMLogic image size declares {image.DeclaredSize}, actual length is {image.Length}.");
        }

        var tableEnd = checked(image.HeaderSize + (long)image.Items.Count * image.ItemRecordSize);
        foreach (var item in image.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Offset < tableEnd || item.Offset % image.Alignment != 0)
            {
                throw new InvalidDataException($"AMLogic item '{GetEntryName(item)}' is misaligned or overlaps metadata.");
            }
            if (item.Size > MaximumItemBytes)
            {
                throw new InvalidDataException($"AMLogic item '{GetEntryName(item)}' exceeds the size limit.");
            }
            FileExtraction.ValidateRange(item.Offset, item.Size, image.Length, $"AMLogic item '{GetEntryName(item)}'");
        }

        var ranges = image.Items.OrderBy(item => item.Offset).ThenBy(item => item.Size).ToArray();
        for (var index = 1; index < ranges.Length; index++)
        {
            var previous = ranges[index - 1];
            var current = ranges[index];
            var exactDuplicate = previous.Offset == current.Offset && previous.Size == current.Size;
            if (!exactDuplicate && current.Offset < checked(previous.Offset + previous.Size))
            {
                throw new InvalidDataException($"AMLogic items '{GetEntryName(previous)}' and '{GetEntryName(current)}' overlap.");
            }
        }

        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var computedCrc = await ComputeCrcAsync(input, image.Alignment, cancellationToken).ConfigureAwait(false);
        if (computedCrc != image.Crc32)
        {
            throw new InvalidDataException($"AMLogic CRC mismatch: stored 0x{image.Crc32:X8}, computed 0x{computedCrc:X8}.");
        }

        if (!verifyPartitionHashes)
        {
            return;
        }

        var payloadBySubType = image.Items.Where(item => !item.IsVerify)
            .GroupBy(item => item.SubType, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var verifyItem in image.Items.Where(item => item.IsVerify))
        {
            if (verifyItem.Size != VerifyDataSize)
            {
                throw new InvalidDataException($"AMLogic VERIFY item '{verifyItem.SubType}' must contain exactly {VerifyDataSize} bytes.");
            }
            if (!payloadBySubType.TryGetValue(verifyItem.SubType, out var matches) || matches.Length != 1)
            {
                throw new InvalidDataException($"AMLogic VERIFY item '{verifyItem.SubType}' does not identify exactly one payload item.");
            }

            input.Position = verifyItem.Offset;
            var verifyData = new byte[VerifyDataSize];
            await input.ReadExactlyAsync(verifyData, cancellationToken).ConfigureAwait(false);
            var expected = ParseVerifyDigest(verifyData, verifyItem.SubType);
            input.Position = matches[0].Offset;
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            await CopyExactlyAsync(input, Stream.Null, matches[0].Size, sha1, cancellationToken).ConfigureAwait(false);
            var actual = sha1.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                throw new InvalidDataException($"AMLogic SHA-1 mismatch for '{GetEntryName(matches[0])}'.");
            }
        }
    }

    private static async Task WriteImageAsync(
        IReadOnlyList<AmlogicSource> sources,
        string outputPath,
        AmlogicPackOptions options,
        CancellationToken cancellationToken)
    {
        if (sources.Count == 0 || sources.Count > MaximumItems)
        {
            throw new InvalidDataException("AMLogic input item count is outside the limit.");
        }

        var itemCount = checked(sources.Count + sources.Count(source => source.Verify));
        if (itemCount > MaximumItems)
        {
            throw new InvalidDataException("AMLogic input item count is outside the limit after VERIFY records are added.");
        }

        var headerSize = GetHeaderSize(options.Version);
        var recordSize = GetItemRecordSize(options.Version);
        var position = Align(checked(headerSize + (long)itemCount * recordSize), options.Alignment);
        var planned = new List<PlannedItem>(itemCount);
        foreach (var source in sources)
        {
            ValidateSource(source, options.Version);
            position = Align(position, options.Alignment);
            planned.Add(new PlannedItem(source, position, source.Size, false));
            position = checked(position + source.Size);
            if (source.Verify)
            {
                position = Align(position, options.Alignment);
                planned.Add(new PlannedItem(source, position, VerifyDataSize, true));
                position = checked(position + VerifyDataSize);
            }
        }

        var imageLength = position;
        await FileExtraction.WriteAtomicAsync(outputPath, async output =>
        {
            output.SetLength(imageLength);
            var header = new byte[headerSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)options.Version);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), Magic);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(12), checked((ulong)imageLength));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), options.Alignment);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), checked((uint)itemCount));
            output.Position = 0;
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);

            for (var index = 0; index < planned.Count; index++)
            {
                var record = CreateItemRecord(options.Version, planned[index], index);
                await output.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            }

            var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var item in planned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Position = item.Offset;
                if (item.IsVerify)
                {
                    var digest = hashes[item.Source.SubType];
                    var text = Encoding.ASCII.GetBytes($"sha1sum {Convert.ToHexStringLower(digest)}");
                    await output.WriteAsync(text, cancellationToken).ConfigureAwait(false);
                    await output.WriteAsync(new byte[VerifyDataSize - text.Length], cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await using var input = new FileStream(item.Source.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                input.Position = item.Source.SourceOffset;
                using var sha1 = item.Source.Verify ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
                await CopyExactlyAsync(input, output, item.Size, sha1, cancellationToken).ConfigureAwait(false);
                if (sha1 is not null)
                {
                    hashes[item.Source.SubType] = sha1.GetHashAndReset();
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            var crc = await ComputeCrcAsync(output, options.Alignment, cancellationToken).ConfigureAwait(false);
            output.Position = 0;
            var crcBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(crcBytes, crc);
            await output.WriteAsync(crcBytes, cancellationToken).ConfigureAwait(false);
        }, overwrite: true, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] CreateItemRecord(AmlogicImageVersion version, PlannedItem item, int id)
    {
        var record = new byte[GetItemRecordSize(version)];
        BinaryPrimitives.WriteUInt32LittleEndian(record, checked((uint)id));
        if (version == AmlogicImageVersion.V1)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), checked((ulong)item.Offset));
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), checked((ulong)item.Size));
            WriteName(record.AsSpan(32, 32), item.IsVerify ? "VERIFY" : item.Source.MainType);
            WriteName(record.AsSpan(64, 32), item.Source.SubType);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(96), item.IsVerify ? 1U : 0U);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), checked((ulong)item.Offset));
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), checked((ulong)item.Size));
            WriteName(record.AsSpan(24, 256), item.IsVerify ? "VERIFY" : item.Source.MainType);
            WriteName(record.AsSpan(280, 256), item.Source.SubType);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(536), item.IsVerify ? 1U : 0U);
        }
        return record;
    }

    private static AmlogicSource CreateFileSource(string path, bool verify)
    {
        var name = Path.GetFileName(path);
        var separator = name.LastIndexOf('.');
        if (separator <= 0 || separator == name.Length - 1)
        {
            throw new InvalidDataException($"AMLogic item '{name}' must use sub.main naming.");
        }
        return new AmlogicSource(path, 0, new FileInfo(path).Length, name[(separator + 1)..], name[..separator], verify);
    }

    private static void ValidateSource(AmlogicSource source, AmlogicImageVersion version)
    {
        if (source.Size < 0 || source.Size > MaximumItemBytes)
        {
            throw new InvalidDataException($"AMLogic item '{GetEntryName(source)}' exceeds the size limit.");
        }
        var capacity = version == AmlogicImageVersion.V1 ? 32 : 256;
        ValidateName(source.MainType, capacity, "main type");
        ValidateName(source.SubType, capacity, "sub type");
    }

    private static void ValidateName(string value, int capacity, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\0', '/', '\\']) >= 0 || Encoding.UTF8.GetByteCount(value) >= capacity)
        {
            throw new InvalidDataException($"AMLogic {description} '{value}' is invalid for the selected version.");
        }
    }

    private static void ValidatePackOptions(AmlogicPackOptions options)
    {
        if (options.Version is not AmlogicImageVersion.V1 and not AmlogicImageVersion.V2)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "AMLogic version must be v1 or v2.");
        }
        if (options.Alignment is not (4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "AMLogic alignment must be 4 or 8 bytes.");
        }
    }

    private static byte[] ParseVerifyDigest(ReadOnlySpan<byte> data, string subType)
    {
        var terminator = data.IndexOf((byte)0);
        var text = Encoding.ASCII.GetString(terminator >= 0 ? data[..terminator] : data).TrimEnd();
        const string prefix = "sha1sum ";
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || text.Length != prefix.Length + 40)
        {
            throw new InvalidDataException($"AMLogic VERIFY item '{subType}' has an invalid SHA-1 record.");
        }
        try
        {
            return Convert.FromHexString(text[prefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"AMLogic VERIFY item '{subType}' has an invalid SHA-1 digest.", ex);
        }
    }

    private static async Task<uint> ComputeCrcAsync(Stream stream, long offset, CancellationToken cancellationToken)
    {
        stream.Position = offset;
        var buffer = new byte[128 * 1024];
        var crc = uint.MaxValue;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            foreach (var value in buffer.AsSpan(0, read))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320U ^ (crc >> 1) : crc >> 1;
                }
            }
        }
        return crc;
    }

    private static async Task CopyExactlyAsync(
        Stream input,
        Stream output,
        long length,
        IncrementalHash? hash,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        while (length > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("AMLogic item data is truncated.");
            }
            hash?.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            length -= read;
        }
    }

    private static int GetHeaderSize(AmlogicImageVersion version) => version == AmlogicImageVersion.V1 ? 64 : 72;
    private static int GetItemRecordSize(AmlogicImageVersion version) => version == AmlogicImageVersion.V1 ? 128 : 576;
    private static long Align(long value, uint alignment) => checked((value + alignment - 1) / alignment * alignment);
    private static string GetEntryName(AmlItem item) => $"{item.SubType}.{item.MainType}";
    private static string GetEntryName(AmlogicSource source) => $"{source.SubType}.{source.MainType}";

    private static string ReadName(ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        var value = Encoding.UTF8.GetString(terminator >= 0 ? bytes[..terminator] : bytes);
        ValidateName(value, bytes.Length, "type name");
        return value;
    }

    private static void WriteName(Span<byte> destination, string value)
    {
        destination.Clear();
        var count = Encoding.UTF8.GetBytes(value, destination);
        if (count >= destination.Length)
        {
            throw new InvalidDataException($"AMLogic type name '{value}' is too long.");
        }
    }

    private sealed record AmlImage(
        AmlogicImageVersion Version,
        uint Crc32,
        ulong DeclaredSize,
        uint Alignment,
        int HeaderSize,
        int ItemRecordSize,
        List<AmlItem> Items,
        long Length);

    private sealed record AmlItem(int Id, long Offset, long Size, string MainType, string SubType, bool IsVerify);
    private sealed record AmlogicSource(string Path, long SourceOffset, long Size, string MainType, string SubType, bool Verify);
    private sealed record PlannedItem(AmlogicSource Source, long Offset, long Size, bool IsVerify);
}
