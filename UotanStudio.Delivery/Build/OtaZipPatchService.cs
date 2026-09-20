using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Parser;

namespace UotanStudio.Services.Delivery;

/// <summary>Options for patching an existing A/B OTA ZIP.</summary>
public sealed record OtaZipPatchOptions
{
    /// <summary>Maps ZIP member names to replacement files.</summary>
    public IReadOnlyDictionary<string, string> ReplacementFiles { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Allows replacing payload.bin only when its companion properties are replaced too.</summary>
    public bool AllowPayloadReplacement { get; init; }
}

/// <summary>Result of a safe existing OTA ZIP patch.</summary>
public sealed record OtaZipPatchResult(
    string OutputPath,
    IReadOnlyList<string> ReplacedEntries,
    bool OuterSignatureInvalidated);

/// <summary>Structural and property-level verification result for an OTA ZIP.</summary>
public sealed record OtaZipVerificationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, string> Properties);

/// <summary>Provides bounded patching and layered verification for existing OTA ZIP files.</summary>
public static class OtaZipPatchService
{
    private const long MaximumEntryLength = 16L * 1024 * 1024 * 1024;

    /// <summary>Patches selected ZIP members and atomically publishes the new archive.</summary>
    public static async Task<OtaZipPatchResult> PatchAsync(string inputPath, string outputPath,
        OtaZipPatchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("OTA ZIP was not found.", inputPath);

        var replacements = NormalizeReplacements(options.ReplacementFiles);
        var payloadReplacement = replacements.ContainsKey("payload.bin");
        if (payloadReplacement && !options.AllowPayloadReplacement)
            throw new InvalidDataException("Replacing payload.bin requires AllowPayloadReplacement.");
        if (payloadReplacement && !replacements.ContainsKey("payload_properties.txt"))
            throw new InvalidDataException("Replacing payload.bin also requires payload_properties.txt.");

        var output = Path.GetFullPath(outputPath);
        var temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var source = ZipFile.OpenRead(inputPath);
                using var archive = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);
                var copied = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in source.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = NormalizeEntryName(entry.FullName);
                    if (!copied.Add(name))
                        throw new InvalidDataException($"Duplicate OTA ZIP entry: {name}");
                    if (entry.Length > MaximumEntryLength)
                        throw new InvalidDataException($"OTA ZIP entry is too large: {name}");
                    var targetEntry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                    await using var destination = targetEntry.Open();
                    if (replacements.TryGetValue(name, out var replacement))
                        await CopyFileAsync(replacement, destination, cancellationToken).ConfigureAwait(false);
                    else
                    {
                        await using var content = entry.Open();
                        await content.CopyToAsync(destination, 65536, cancellationToken).ConfigureAwait(false);
                    }
                }

                foreach (var replacement in replacements)
                {
                    if (copied.Contains(replacement.Key))
                        continue;
                    var targetEntry = archive.CreateEntry(replacement.Key, CompressionLevel.NoCompression);
                    await using var destination = targetEntry.Open();
                    await CopyFileAsync(replacement.Value, destination, cancellationToken).ConfigureAwait(false);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite: true);
            return new(output, [.. replacements.Keys], OuterSignatureInvalidated: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>Verifies ZIP structure, A/B payload members and payload properties.</summary>
    public static OtaZipVerificationResult Verify(string zipPath)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entries = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                var name = NormalizeEntryName(entry.FullName);
                if (!entries.Add(name)) errors.Add($"Duplicate ZIP entry: {name}");
                if (entry.Length > MaximumEntryLength) errors.Add($"ZIP entry exceeds limit: {name}");
            }

            var payload = archive.GetEntry("payload.bin");
            var propertyEntry = archive.GetEntry("payload_properties.txt");
            if (payload is null)
            {
                warnings.Add("payload.bin is absent; this may be a Recovery ZIP rather than an A/B OTA.");
                return new(errors.Count == 0, errors, warnings, properties);
            }
            if (propertyEntry is null)
            {
                errors.Add("payload_properties.txt is missing.");
                return new(false, errors, warnings, properties);
            }

            using (var reader = new StreamReader(propertyEntry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                var parsed = ParseProperties(reader.ReadToEnd());
                foreach (var pair in parsed) properties[pair.Key] = pair.Value;
            }
            using var payloadStream = payload.Open();
            Span<byte> header = stackalloc byte[24];
            payloadStream.ReadExactly(header);
            if (!header[..4].SequenceEqual("CrAU"u8)) errors.Add("payload.bin has an invalid CrAU magic.");
            else if (BinaryPrimitives.ReadUInt64LittleEndian(header[4..12]) is 2 or 3)
            {
                try
                {
                    using var manifestStream = payload.Open();
                    var metadata = PayloadParser.GetPayloadMetadata(manifestStream);
                    properties["PAYLOAD_MANIFEST_PARTITIONS"] = metadata.Manifest.Partitions.Count.ToString();
                    properties["PAYLOAD_MANIFEST_LENGTH"] = metadata.ManifestLength.ToString();
                    if (properties.TryGetValue("METADATA_SIZE", out var metadataSizeText)
                        && long.TryParse(metadataSizeText, out var declaredMetadataSize)
                        && declaredMetadataSize != checked((long)metadata.ManifestLength + 24))
                        errors.Add($"METADATA_SIZE={declaredMetadataSize} does not equal manifest length plus CrAU header ({metadata.ManifestLength + 24}).");
                    if (metadata.Manifest.Partitions.Count == 0) warnings.Add("Payload manifest contains no partition updates.");
                    if (properties.TryGetValue("METADATA_HASH", out var metadataHash)
                        && metadataHash.Length > 0)
                    {
                        using var metadataInput = payload.Open();
                        SkipExactly(metadataInput, 24);
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        CopyExactly(metadataInput, new HashingStream(hash), checked((long)metadata.ManifestLength));
                        var actualMetadataHash = Convert.ToBase64String(hash.GetHashAndReset());
                        if (!string.Equals(actualMetadataHash, metadataHash, StringComparison.Ordinal))
                            errors.Add("METADATA_HASH does not match the CrAU manifest bytes.");
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
                {
                    errors.Add($"Payload manifest validation failed: {ex.Message}");
                }
            }
            else
            {
                warnings.Add("Payload manifest parsing skipped because the CrAU version is not supported by the managed parser.");
            }
            var fileSize = payload.Length;
            if (properties.TryGetValue("FILE_SIZE", out var fileSizeText) && long.TryParse(fileSizeText, out var declaredSize) && declaredSize != fileSize)
                errors.Add($"FILE_SIZE={declaredSize} does not match payload.bin length {fileSize}.");
            if (properties.TryGetValue("FILE_HASH", out var declaredHash) && declaredHash.Length > 0)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var hashInput = payload.Open();
                hashInput.CopyTo(new HashingStream(hash));
                var actualHash = Convert.ToBase64String(hash.GetHashAndReset());
                if (!string.Equals(actualHash, declaredHash, StringComparison.Ordinal))
                    errors.Add("FILE_HASH does not match payload.bin.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            errors.Add(ex.Message);
        }
        return new(errors.Count == 0, errors, warnings, properties);
    }

    private static Dictionary<string, string> NormalizeReplacements(IReadOnlyDictionary<string, string> replacements)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in replacements)
        {
            var name = NormalizeEntryName(pair.Key);
            if (!File.Exists(pair.Value)) throw new FileNotFoundException("OTA replacement file was not found.", pair.Value);
            result.Add(name, pair.Value);
        }
        return result;
    }

    private static string NormalizeEntryName(string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"Unsafe OTA ZIP entry name: {name}");
        return normalized;
    }

    private static Dictionary<string, string> ParseProperties(string content)
        => content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

    private static async Task CopyFileAsync(string path, Stream destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(destination, 65536, cancellationToken).ConfigureAwait(false);
    }

    private static void SkipExactly(Stream input, long count)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0) throw new EndOfStreamException("Unexpected end of Payload while skipping CrAU header.");
            count -= read;
        }
    }

    private static void CopyExactly(Stream input, Stream output, long count)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0) throw new EndOfStreamException("Unexpected end of Payload while reading manifest.");
            output.Write(buffer, 0, read);
            count -= read;
        }
    }

    private sealed class HashingStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            hash.AppendData(buffer, offset, count);
            return Task.CompletedTask;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            hash.AppendData(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }
}
