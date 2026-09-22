using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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

/// <summary>Options for rebuilding an existing A/B OTA around a replacement Payload.</summary>
public sealed record OtaZipRebuildOptions
{
    public required string PayloadPath { get; init; }
    public IReadOnlyDictionary<string, string> ReplacementFiles { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public string CertificatePath { get; init; } = "";
    public string SigningKeyPath { get; init; } = "";
    public bool VerifyOutput { get; init; } = true;
}

/// <summary>Result of a verified, atomically published A/B OTA rebuild.</summary>
public sealed record OtaZipRebuildResult(
    string OutputPath,
    IReadOnlyList<string> ReplacedEntries,
    bool OuterSignatureApplied,
    OtaZipVerificationResult Verification);

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
    private const ulong MaximumPayloadMetadataLength = 64UL * 1024 * 1024;
    private const int PropertyRangeReservation = 512;

    /// <summary>
    /// Rebuilds an existing A/B OTA around a replacement Payload, regenerates properties and offset metadata,
    /// optionally signs the complete ZIP, verifies it, and atomically publishes it.
    /// </summary>
    public static Task<OtaZipRebuildResult> RebuildAsync(
        string inputPath,
        string outputPath,
        OtaZipRebuildOptions options,
        CancellationToken cancellationToken = default)
        => RebuildCoreAsync(inputPath, outputPath, options, null, cancellationToken);

    internal static Task<OtaZipRebuildResult> RebuildAsync(
        string inputPath,
        string outputPath,
        OtaZipRebuildOptions options,
        IDeliverySigningKeyProvider signingKeys,
        CancellationToken cancellationToken = default)
        => RebuildCoreAsync(inputPath, outputPath, options, signingKeys, cancellationToken);

    private static async Task<OtaZipRebuildResult> RebuildCoreAsync(
        string inputPath,
        string outputPath,
        OtaZipRebuildOptions options,
        IDeliverySigningKeyProvider? signingKeys,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("OTA ZIP was not found.", inputPath);
        }
        if (!File.Exists(options.PayloadPath))
        {
            throw new FileNotFoundException("Replacement Payload was not found.", options.PayloadPath);
        }
        if ((options.CertificatePath.Length == 0) != (options.SigningKeyPath.Length == 0))
        {
            throw new InvalidDataException("OTA whole-file signing requires both a certificate and a private key.");
        }

        var replacements = NormalizeReplacements(options.ReplacementFiles);
        foreach (var generated in new[] { "payload.bin", "payload_properties.txt", "META-INF/com/android/metadata", "META-INF/com/android/otacert" })
        {
            if (replacements.ContainsKey(generated))
            {
                throw new InvalidDataException($"OTA rebuild owns generated entry '{generated}'.");
            }
        }

        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var suffix = ".tmp-" + Guid.NewGuid().ToString("N");
        var staged = output + suffix + ".zip";
        var provisional = output + suffix + ".provisional.zip";
        var propertiesPath = output + suffix + ".properties";
        var metadataPath = output + suffix + ".metadata";
        var certificatePath = output + suffix + ".otacert";
        try
        {
            var payloadProperties = await CreatePayloadPropertiesAsync(options.PayloadPath, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(propertiesPath, payloadProperties.Text, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            var metadataBase = ReadMetadataBase(inputPath);
            await File.WriteAllTextAsync(
                metadataPath,
                BuildMetadata(metadataBase, "", ""),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            replacements["payload.bin"] = options.PayloadPath;
            replacements["payload_properties.txt"] = propertiesPath;
            replacements["META-INF/com/android/metadata"] = metadataPath;
            if (options.CertificatePath.Length > 0)
            {
                using var certificate = DeliveryPackageBuilder.LoadCertificate(options.CertificatePath);
                await File.WriteAllTextAsync(
                    certificatePath,
                    certificate.ExportCertificatePem(),
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
                replacements["META-INF/com/android/otacert"] = certificatePath;
            }

            await PatchAsync(
                inputPath,
                provisional,
                new OtaZipPatchOptions { ReplacementFiles = replacements, AllowPayloadReplacement = true },
                cancellationToken).ConfigureAwait(false);
            var expectedRanges = DeliveryPackageBuilder.ReadStoredRanges(provisional);
            var payloadRange = RequiredRange(expectedRanges, "payload.bin");
            var propertiesRange = RequiredRange(expectedRanges, "payload_properties.txt");
            var metadataRange = RequiredRange(expectedRanges, "META-INF/com/android/metadata");
            var streaming =
                $"payload.bin:{payloadRange.Offset}:{payloadRange.Size},payload_properties.txt:{propertiesRange.Offset}:{propertiesRange.Size},metadata:{metadataRange.Offset}:{metadataRange.Size}";
            var all = $"payload_metadata.bin:{payloadRange.Offset}:{payloadProperties.MetadataSize},{streaming}";
            await File.WriteAllTextAsync(
                metadataPath,
                BuildMetadata(metadataBase, streaming, all),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            await PatchAsync(
                inputPath,
                staged,
                new OtaZipPatchOptions { ReplacementFiles = replacements, AllowPayloadReplacement = true },
                cancellationToken).ConfigureAwait(false);
            var actualRanges = DeliveryPackageBuilder.ReadStoredRanges(staged);
            foreach (var entry in new[] { "payload.bin", "payload_properties.txt", "META-INF/com/android/metadata" })
            {
                if (RequiredRange(actualRanges, entry) != RequiredRange(expectedRanges, entry))
                {
                    throw new InvalidDataException($"OTA ZIP entry offset changed while finalizing {entry} metadata.");
                }
            }

            var verification = Verify(staged);
            if (options.VerifyOutput && !verification.IsValid)
            {
                throw new InvalidDataException("Rebuilt OTA verification failed: " + string.Join("; ", verification.Errors));
            }
            var signed = options.SigningKeyPath.Length > 0;
            if (signed)
            {
                await DeliveryPackageBuilder.SignAsync(
                    staged,
                    new DeliveryPackageOptions
                    {
                        CertificatePath = options.CertificatePath,
                        SigningKeyPath = options.SigningKeyPath
                    },
                    cancellationToken,
                    signingKeys).ConfigureAwait(false);
                verification = Verify(staged);
                if (options.VerifyOutput && !verification.IsValid)
                {
                    throw new InvalidDataException("Signed OTA verification failed: " + string.Join("; ", verification.Errors));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staged, output, overwrite: true);
            return new(
                output,
                [.. replacements.Keys.Order(StringComparer.Ordinal)],
                signed,
                verification);
        }
        finally
        {
            File.Delete(staged);
            File.Delete(provisional);
            File.Delete(propertiesPath);
            File.Delete(metadataPath);
            File.Delete(certificatePath);
        }
    }

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
            else if (BinaryPrimitives.ReadUInt64BigEndian(header[4..12]) is 2 or 3)
            {
                try
                {
                    using var manifestStream = payload.Open();
                    var metadata = PayloadParser.GetPayloadMetadata(manifestStream);
                    properties["PAYLOAD_MANIFEST_PARTITIONS"] = metadata.Manifest.Partitions.Count.ToString();
                    properties["PAYLOAD_MANIFEST_LENGTH"] = metadata.ManifestLength.ToString();
                    var actualMetadataSize = checked((long)metadata.ManifestLength + metadata.MetadataSignatureLength + 24);
                    if (properties.TryGetValue("METADATA_SIZE", out var metadataSizeText)
                        && (!long.TryParse(metadataSizeText, out var declaredMetadataSize)
                            || declaredMetadataSize != actualMetadataSize))
                        errors.Add($"METADATA_SIZE={metadataSizeText} does not equal the CrAU metadata prefix ({actualMetadataSize}).");
                    if (metadata.Manifest.Partitions.Count == 0) warnings.Add("Payload manifest contains no partition updates.");
                    if (properties.TryGetValue("METADATA_HASH", out var metadataHash)
                        && metadataHash.Length > 0)
                    {
                        using var metadataInput = payload.Open();
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        CopyExactly(metadataInput, new HashingStream(hash), actualMetadataSize);
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

    private static async Task<(string Text, ulong MetadataSize)> CreatePayloadPropertiesAsync(
        string payloadPath,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length < 24 || input.Length > MaximumEntryLength)
        {
            throw new InvalidDataException("Replacement Payload length is outside the supported range.");
        }
        var header = new byte[24];
        await input.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("CrAU"u8)
            || BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(4, 8)) is not (2 or 3))
        {
            throw new InvalidDataException("Replacement Payload has an invalid CrAU header or version.");
        }
        var manifestLength = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12, 8));
        var signatureLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
        var metadataSize = checked(24UL + manifestLength + signatureLength);
        if (manifestLength == 0 || metadataSize > MaximumPayloadMetadataLength || metadataSize > (ulong)input.Length)
        {
            throw new InvalidDataException("Replacement Payload metadata length is outside the supported range.");
        }

        input.Position = 0;
        using var metadataHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await CopyExactlyAsync(input, new HashingStream(metadataHash), checked((long)metadataSize), cancellationToken)
            .ConfigureAwait(false);
        input.Position = 0;
        var fileHash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        var text =
            $"FILE_HASH={Convert.ToBase64String(fileHash)}\n" +
            $"FILE_SIZE={input.Length}\n" +
            $"METADATA_HASH={Convert.ToBase64String(metadataHash.GetHashAndReset())}\n" +
            $"METADATA_SIZE={metadataSize}\n";
        return (text, metadataSize);
    }

    private static string ReadMetadataBase(string inputPath)
    {
        using var archive = ZipFile.OpenRead(inputPath);
        var entry = archive.GetEntry("META-INF/com/android/metadata");
        if (entry is null)
        {
            return "ota-type=AB\n";
        }
        if (entry.Length > 1024 * 1024)
        {
            throw new InvalidDataException("OTA metadata exceeds 1 MiB.");
        }
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith("ota-streaming-property-files=", StringComparison.Ordinal)
                && !line.StartsWith("ota-property-files=", StringComparison.Ordinal));
        return string.Join('\n', lines) + "\n";
    }

    private static string BuildMetadata(string metadataBase, string streaming, string all)
    {
        if (streaming.Length > PropertyRangeReservation || all.Length > PropertyRangeReservation)
        {
            throw new InvalidDataException("OTA property ranges exceed reserved metadata space.");
        }
        return metadataBase
            + "ota-streaming-property-files=" + streaming.PadRight(PropertyRangeReservation) + "\n"
            + "ota-property-files=" + all.PadRight(PropertyRangeReservation) + "\n";
    }

    private static (long Offset, ulong Size) RequiredRange(
        IReadOnlyDictionary<string, (long Offset, ulong Size)> ranges,
        string entry)
        => ranges.TryGetValue(entry, out var range)
            ? range
            : throw new InvalidDataException($"Rebuilt OTA is missing required stored entry: {entry}");

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

    private static async Task CopyExactlyAsync(
        Stream input,
        Stream output,
        long count,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of Payload while hashing metadata.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
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
